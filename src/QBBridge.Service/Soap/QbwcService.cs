using QBBridge.Service.Workflow;

namespace QBBridge.Service.Soap;

public sealed class QbwcService : IQbwcService
{
    private readonly SessionStore _sessions;
    private readonly QbxmlBuilder _builder;
    private readonly QbxmlParser _parser;
    private readonly IntimeApiClient _intime;
    private readonly ILogger<QbwcService> _log;
    private readonly string _expectedUser;
    private readonly string _expectedPass;

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

        _sessions.Add(session);

        _log.LogInformation(
            "QBWC session opened: ticket={Ticket}, queries queued={Count}, since={Since:O}",
            session.Ticket, session.PendingRequests.Count, lastSync);

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

        var summary = $"OK: {s.InvoicesUpdated} invoices, {s.PaymentsProcessed} payments synced from {s.LastSyncDate:yyyy-MM-dd} onward";
        _log.LogInformation("Closing session {Ticket}: {Summary}", ticket, summary);

        _intime.RecordSyncSummaryAsync(s).GetAwaiter().GetResult();
        _sessions.Remove(ticket);
        return summary;
    }

    public string serverVersion() => "1.0.0";
    public string clientVersion(string strVersion) => ""; // accept any QBWC version
}
