using QBBridge.Service.Workflow;

namespace QBBridge.Service.Soap;

public sealed class QbwcService : IQbwcService
{
    // Per-cycle caps: never queue more than this many writes per QBWC cycle.
    // Keeps sessions short and bounds blast radius if something goes wrong.
    private const int MaxCustomerAddsPerCycle = 100;
    private const int MaxVendorAddsPerCycle = 100;
    private const int MaxClaimAddsPerCycle = 100;

    // qbXML requestID space:
    //   1-99       reserved for read queries (Invoice, ReceivePayment)
    //   1000-1999  CustomerAdd (Phase 1: top-level customers)
    //   2000-2999  VendorAdd (Phase 2: contractors)
    //   3000-3999  CustomerAdd with ParentRef (Phase 3: sub-customers / claim jobs)
    private const int CustomerAddBaseRequestId = 1000;
    private const int VendorAddBaseRequestId = 2000;
    private const int SubcustomerAddBaseRequestId = 3000;

    private readonly SessionStore _sessions;
    private readonly QbxmlBuilder _builder;
    private readonly QbxmlParser _parser;
    private readonly IntimeApiClient _intime;
    private readonly ILogger<QbwcService> _log;
    private readonly string _expectedUser;
    private readonly string _expectedPass;
    private readonly bool _writebackCustomersEnabled;
    private readonly bool _writebackContractorsEnabled;
    private readonly bool _writebackClaimsEnabled;
    private readonly bool _writebackDryRun;

    public QbwcService(
        SessionStore sessions,
        QbxmlBuilder builder,
        QbxmlParser parser,
        IntimeApiClient intime,
        IConfiguration config,
        ILogger<QbwcService> log)
    {
        _sessions = sessions;
        _builder = builder;
        _parser = parser;
        _intime = intime;
        _log = log;
        _expectedUser = config["QBWC_USERNAME"] ?? "qbjnjadmin";
        _expectedPass = Environment.GetEnvironmentVariable("QBWC_PASSWORD")
            ?? throw new InvalidOperationException(
                "QBWC_PASSWORD env var is not set. Set it via Windows Credential Manager / Machine env var.");
        _writebackCustomersEnabled = string.Equals(
            Environment.GetEnvironmentVariable("WRITEBACK_CUSTOMERS_ENABLED"),
            "true", StringComparison.OrdinalIgnoreCase);
        _writebackContractorsEnabled = string.Equals(
            Environment.GetEnvironmentVariable("WRITEBACK_CONTRACTORS_ENABLED"),
            "true", StringComparison.OrdinalIgnoreCase);
        _writebackClaimsEnabled = string.Equals(
            Environment.GetEnvironmentVariable("WRITEBACK_CLAIMS_ENABLED"),
            "true", StringComparison.OrdinalIgnoreCase);
        _writebackDryRun = string.Equals(
            Environment.GetEnvironmentVariable("WRITEBACK_DRY_RUN"),
            "true", StringComparison.OrdinalIgnoreCase);
    }

    public string[] authenticate(string strUserName, string strPassword)
    {
        if (strUserName != _expectedUser || strPassword != _expectedPass)
        {
            _log.LogWarning("QBWC authenticate failed for user {User}", strUserName);
            return new[] { "", "nvu" };
        }

        var session = new SessionState
        {
            Ticket = Guid.NewGuid().ToString("N"),
        };

        // Queue up the reads we want QBWC to run this cycle.
        // v1: Invoice + ReceivePayment since last known sync date (from InTime state).
        var lastSync = _intime.GetLastSyncDateAsync().GetAwaiter().GetResult() ?? DateTime.UtcNow.AddDays(-30);
        session.PendingRequests.Enqueue(_builder.InvoiceQuery(lastSync));
        session.PendingRequests.Enqueue(_builder.ReceivePaymentQuery(lastSync));
        session.LastSyncDate = lastSync;

        // Phase 1 write-back: queue CustomerAdd requests for any pending QBCustomers rows.
        // Disabled by default; flip via WRITEBACK_CUSTOMERS_ENABLED=true on J-DC2 machine env.
        // Dry-run mode (WRITEBACK_DRY_RUN=true) logs the qbXML payload but skips queueing,
        // so we can eyeball the first cycle's output before actually pushing to QB.
        if (_writebackCustomersEnabled)
        {
            try
            {
                var pendingCustomers = _intime
                    .GetPendingCustomersAsync(MaxCustomerAddsPerCycle)
                    .GetAwaiter().GetResult();

                int requestId = CustomerAddBaseRequestId;
                int queued = 0;
                foreach (var pc in pendingCustomers)
                {
                    string xml;
                    try { xml = _builder.CustomerAdd(pc, requestId); }
                    catch (Exception ex)
                    {
                        // Bad input row (e.g. null FullName) — ack as skipped so it doesn't block the queue.
                        _log.LogWarning(ex, "CustomerAdd build failed for QBCustomersID={Id}; skipping", pc.QbCustomersId);
                        _intime.AckCustomerAsync(pc.QbCustomersId, null, pc.FullName, "skipped", ex.Message)
                            .GetAwaiter().GetResult();
                        continue;
                    }

                    if (_writebackDryRun)
                    {
                        _log.LogInformation(
                            "DRY-RUN CustomerAdd payload for QBCustomersID={Id} ({Name}):\n{Xml}",
                            pc.QbCustomersId, pc.FullName, xml);
                        continue;
                    }

                    session.PendingRequests.Enqueue(xml);
                    session.PendingAcks[requestId] = new PendingWriteback("customer", pc.QbCustomersId, pc.FullName);
                    requestId++;
                    queued++;
                }

                if (queued > 0)
                    _log.LogInformation("Phase 1 write-back: queued {N} CustomerAdd requests", queued);
                else if (_writebackDryRun)
                    _log.LogInformation("Phase 1 write-back: DRY-RUN, {N} payloads logged", pendingCustomers.Count);
                else
                    _log.LogInformation("Phase 1 write-back: no pending customers");
            }
            catch (Exception ex)
            {
                // Failure to fetch pending shouldn't block the read-side bridge.
                _log.LogError(ex, "Write-back queueing failed; continuing with read-only sync this cycle");
            }
        }

        // Phase 2 write-back: vendors. Same shape as Phase 1, separate env flag so
        // contractors can be activated independently of customers (or dry-run together).
        if (_writebackContractorsEnabled)
        {
            try
            {
                var pendingContractors = _intime
                    .GetPendingContractorsAsync(MaxVendorAddsPerCycle)
                    .GetAwaiter().GetResult();

                int requestId = VendorAddBaseRequestId;
                int queued = 0;
                foreach (var pv in pendingContractors)
                {
                    string xml;
                    try { xml = _builder.VendorAdd(pv, requestId); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "VendorAdd build failed for QBContractorsID={Id}; skipping", pv.QbContractorsId);
                        _intime.AckContractorAsync(pv.QbContractorsId, null, pv.FullName, "skipped", ex.Message)
                            .GetAwaiter().GetResult();
                        continue;
                    }

                    if (_writebackDryRun)
                    {
                        _log.LogInformation(
                            "DRY-RUN VendorAdd payload for QBContractorsID={Id} ({Name}):\n{Xml}",
                            pv.QbContractorsId, pv.FullName, xml);
                        continue;
                    }

                    session.PendingRequests.Enqueue(xml);
                    session.PendingAcks[requestId] = new PendingWriteback("contractor", pv.QbContractorsId, pv.FullName);
                    requestId++;
                    queued++;
                }

                if (queued > 0)
                    _log.LogInformation("Phase 2 write-back: queued {N} VendorAdd requests", queued);
                else if (_writebackDryRun)
                    _log.LogInformation("Phase 2 write-back: DRY-RUN, {N} payloads logged", pendingContractors.Count);
                else
                    _log.LogInformation("Phase 2 write-back: no pending contractors");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Phase 2 write-back queueing failed; continuing");
            }
        }

        // Phase 3 write-back: claims as sub-customers. Queued AFTER customers
        // (Phase 1) so parent customers go in first within the same cycle.
        if (_writebackClaimsEnabled)
        {
            try
            {
                var pendingClaims = _intime
                    .GetPendingClaimsAsync(MaxClaimAddsPerCycle)
                    .GetAwaiter().GetResult();

                int requestId = SubcustomerAddBaseRequestId;
                int queued = 0;
                foreach (var pcl in pendingClaims)
                {
                    string xml;
                    try { xml = _builder.SubcustomerAdd(pcl, requestId); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "SubcustomerAdd build failed for QBClaimsID={Id}; skipping", pcl.QbClaimsId);
                        _intime.AckClaimAsync(pcl.QbClaimsId, null, pcl.ClaimName, "skipped", ex.Message)
                            .GetAwaiter().GetResult();
                        continue;
                    }

                    if (_writebackDryRun)
                    {
                        _log.LogInformation(
                            "DRY-RUN SubcustomerAdd payload for QBClaimsID={Id} ({Parent}:{Name}):\n{Xml}",
                            pcl.QbClaimsId, pcl.ParentCustomerName, pcl.ClaimName, xml);
                        continue;
                    }

                    session.PendingRequests.Enqueue(xml);
                    session.PendingAcks[requestId] = new PendingWriteback("claim", pcl.QbClaimsId, pcl.ClaimName);
                    requestId++;
                    queued++;
                }

                if (queued > 0)
                    _log.LogInformation("Phase 3 write-back: queued {N} SubcustomerAdd requests", queued);
                else if (_writebackDryRun)
                    _log.LogInformation("Phase 3 write-back: DRY-RUN, {N} payloads logged", pendingClaims.Count);
                else
                    _log.LogInformation("Phase 3 write-back: no pending claims");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Phase 3 write-back queueing failed; continuing");
            }
        }

        _sessions.Add(session);

        _log.LogInformation(
            "QBWC session opened: ticket={Ticket}, queries queued={Count}, since={Since:O}, writebackEnabled={Wb}, dryRun={Dry}",
            session.Ticket, session.PendingRequests.Count, lastSync, _writebackCustomersEnabled, _writebackDryRun);

        // Empty company-file string = use whatever QBWC has open. Explicit path
        // also works but locks us to one file — leave blank for portability.
        return new[] { session.Ticket, "" };
    }

    public string sendRequestXML(
        string ticket,
        string strHCPResponse,
        string strCompanyFileName,
        string qbXMLCountry,
        int qbXMLMajorVers,
        int qbXMLMinorVers)
    {
        var session = _sessions.Get(ticket);
        if (session is null)
        {
            _log.LogWarning("sendRequestXML: unknown ticket {Ticket}", ticket);
            return "";
        }

        if (!session.PendingRequests.TryDequeue(out var next))
        {
            _log.LogInformation("sendRequestXML: no more requests for ticket {Ticket}", ticket);
            return ""; // tells QBWC we're done for this cycle
        }

        _log.LogDebug("sendRequestXML: returning qbXML for ticket {Ticket}", ticket);
        return next;
    }

    public int receiveResponseXML(string ticket, string response, string hresult, string message)
    {
        var session = _sessions.Get(ticket);
        if (session is null)
        {
            _log.LogWarning("receiveResponseXML: unknown ticket {Ticket}", ticket);
            return -1;
        }

        if (!string.IsNullOrEmpty(hresult))
        {
            session.LastError = $"{hresult}: {message}";
            _log.LogError("QB returned error for ticket {Ticket}: {Err}", ticket, session.LastError);
            return -1;
        }

        try
        {
            var result = _parser.ParseResponse(response);
            if (result.Invoices.Count > 0)
            {
                _intime.PostInvoicesAsync(result.Invoices).GetAwaiter().GetResult();
                session.InvoicesUpdated += result.Invoices.Count;
            }
            if (result.Payments.Count > 0)
            {
                _intime.PostPaymentsAsync(result.Payments).GetAwaiter().GetResult();
                session.PaymentsProcessed += result.Payments.Count;
            }

            // Write-back acks. Each *AddRs comes back with the requestID we set,
            // which we use to look up the InTime row that needs ImportFlag flipped.
            foreach (var add in result.AddResults)
            {
                PendingWriteback? pending = null;
                if (add.RequestId.HasValue)
                    session.PendingAcks.TryGetValue(add.RequestId.Value, out pending);

                if (pending is null)
                {
                    _log.LogWarning(
                        "{Verb} response missing pending-ack mapping (requestID={Rid}, ok={Ok}); cannot ack InTime",
                        add.Verb, add.RequestId, add.Ok);
                    continue;
                }

                if (pending.Kind == "customer")
                {
                    var status = add.Ok ? "ok" : "qb_error";
                    _intime.AckCustomerAsync(pending.InTimeId, add.ListId, add.FullName ?? pending.FullName, status, add.Error)
                        .GetAwaiter().GetResult();
                    if (add.Ok) session.CustomersAdded++;
                    else session.CustomersFailed++;
                    if (!add.Ok)
                        _log.LogWarning("CustomerAdd failed for QBCustomersID={Id} ({Name}): {Err}",
                            pending.InTimeId, pending.FullName, add.Error);
                }
                else if (pending.Kind == "contractor")
                {
                    var status = add.Ok ? "ok" : "qb_error";
                    _intime.AckContractorAsync(pending.InTimeId, add.ListId, add.FullName ?? pending.FullName, status, add.Error)
                        .GetAwaiter().GetResult();
                    if (add.Ok) session.ContractorsAdded++;
                    else session.ContractorsFailed++;
                    if (!add.Ok)
                        _log.LogWarning("VendorAdd failed for QBContractorsID={Id} ({Name}): {Err}",
                            pending.InTimeId, pending.FullName, add.Error);
                }
                else if (pending.Kind == "claim")
                {
                    var status = add.Ok ? "ok" : "qb_error";
                    _intime.AckClaimAsync(pending.InTimeId, add.ListId, add.FullName ?? pending.FullName, status, add.Error)
                        .GetAwaiter().GetResult();
                    if (add.Ok) session.ClaimsAdded++;
                    else session.ClaimsFailed++;
                    if (!add.Ok)
                        _log.LogWarning("SubcustomerAdd failed for QBClaimsID={Id} ({Name}): {Err}",
                            pending.InTimeId, pending.FullName, add.Error);
                }

                session.PendingAcks.Remove(add.RequestId!.Value);
            }

            session.CompletedRequests++;
            var total = session.CompletedRequests + session.PendingRequests.Count;
            var pct = total == 0 ? 100 : (int)Math.Round(100.0 * session.CompletedRequests / total);
            return Math.Clamp(pct, 1, 99); // never return 100 mid-cycle — QBWC uses that as "done"
        }
        catch (Exception ex)
        {
            session.LastError = ex.Message;
            _log.LogError(ex, "Parse/post failure for ticket {Ticket}", ticket);
            return -1;
        }
    }

    public string connectionError(string ticket, string hresult, string message)
    {
        _log.LogError("QBWC reports connection error for ticket {Ticket}: {H} {M}", ticket, hresult, message);
        return "done"; // give up this cycle; QBWC will retry on next schedule
    }

    public string getLastError(string ticket)
    {
        var s = _sessions.Get(ticket);
        return s?.LastError ?? "unknown error";
    }

    public string closeConnection(string ticket)
    {
        var s = _sessions.Get(ticket);
        if (s is null) return "unknown ticket";

        var summary = $"OK: {s.InvoicesUpdated} invoices, {s.PaymentsProcessed} payments, " +
                      $"{s.CustomersAdded}/{s.CustomersFailed} customers (added/failed), " +
                      $"{s.ContractorsAdded}/{s.ContractorsFailed} contractors, " +
                      $"{s.ClaimsAdded}/{s.ClaimsFailed} claims since {s.LastSyncDate:yyyy-MM-dd}";
        _log.LogInformation("Closing session {Ticket}: {Summary}", ticket, summary);

        _intime.RecordSyncSummaryAsync(s).GetAwaiter().GetResult();
        _sessions.Remove(ticket);
        return summary;
    }

    public string serverVersion() => "1.0.0";
    public string clientVersion(string strVersion) => ""; // accept any QBWC version
}
