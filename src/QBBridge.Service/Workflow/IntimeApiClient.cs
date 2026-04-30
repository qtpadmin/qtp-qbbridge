using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using QBBridge.Service.Soap;

namespace QBBridge.Service.Workflow;

/// <summary>
/// HTTPS client to the InTime backend's /api/qb-payment-sync/ingest endpoints.
/// Bearer-token auth (shared secret, separate from user JWT).
/// </summary>
public sealed class IntimeApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<IntimeApiClient> _log;

    public IntimeApiClient(HttpClient http, IConfiguration config, ILogger<IntimeApiClient> log)
    {
        _http = http;
        _log = log;

        var baseUrl = config["INTIME_API_BASE_URL"]
            ?? throw new InvalidOperationException("INTIME_API_BASE_URL not configured");
        var token = Environment.GetEnvironmentVariable("INTIME_API_BEARER_TOKEN")
            ?? throw new InvalidOperationException("INTIME_API_BEARER_TOKEN env var not set");

        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<DateTime?> GetLastSyncDateAsync()
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<LastSyncResponse>("api/qb-payment-sync/last-sync");
            return resp?.LastSync;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch last-sync timestamp; defaulting to 30d ago");
            return null;
        }
    }

    public async Task PostInvoicesAsync(IReadOnlyList<QbInvoice> invoices)
    {
        var payload = new { source = "qbwc-bridge", invoices };
        var resp = await _http.PostAsJsonAsync("api/qb-payment-sync/ingest/invoices", payload);
        resp.EnsureSuccessStatusCode();
        _log.LogInformation("Posted {N} invoices to InTime", invoices.Count);
    }

    public async Task PostPaymentsAsync(IReadOnlyList<QbPayment> payments)
    {
        var payload = new { source = "qbwc-bridge", payments };
        var resp = await _http.PostAsJsonAsync("api/qb-payment-sync/ingest/payments", payload);
        resp.EnsureSuccessStatusCode();
        _log.LogInformation("Posted {N} payments to InTime", payments.Count);
    }

    public async Task RecordSyncSummaryAsync(SessionState session)
    {
        var payload = new
        {
            ticket = session.Ticket,
            startedAt = session.StartedAt,
            invoicesUpdated = session.InvoicesUpdated,
            paymentsProcessed = session.PaymentsProcessed,
            customersAdded = session.CustomersAdded,
            customersFailed = session.CustomersFailed,
            lastSyncDate = session.LastSyncDate,
            error = session.LastError,
        };
        try
        {
            await _http.PostAsJsonAsync("api/qb-payment-sync/sync-summary", payload);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record sync summary — non-fatal");
        }
    }

    // ─── Write-back maintenance: pull pending + ack results ──────────────

    public async Task<IReadOnlyList<PendingCustomer>> GetPendingCustomersAsync(int limit = 50)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<PendingCustomersResponse>(
                $"api/qb-payment-sync/pending/customers?limit={limit}");
            return resp?.Customers ?? new List<PendingCustomer>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch pending customers — skipping writeback this cycle");
            return new List<PendingCustomer>();
        }
    }

    public async Task AckCustomerAsync(int qbCustomersId, string? listId, string? name, string status, string? error)
    {
        var payload = new
        {
            qbCustomersId,
            qbListId = listId,
            qbName = name,
            status,
            error,
        };
        try
        {
            var resp = await _http.PostAsJsonAsync("api/qb-payment-sync/ack/customer", payload);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // Failure to ack is non-fatal: the row stays at ImportFlag=0 and will retry next cycle.
            // Worst case in QB is a duplicate-name error on the retry, which we log and skip.
            _log.LogWarning(ex, "ack/customer failed for QBCustomersID={Id}", qbCustomersId);
        }
    }

    private sealed record LastSyncResponse(DateTime? LastSync);
    private sealed record PendingCustomersResponse(List<PendingCustomer> Customers);
}

/// <summary>
/// Mirrors the JSON shape returned by GET /api/qb-payment-sync/pending/customers.
/// All fields are nullable since QBCustomers source columns are mostly nullable.
/// </summary>
public sealed record PendingCustomer(
    int QbCustomersId,
    int? CustomerId,
    string? FullName,
    string? CompanyName,
    string? FirstName,
    string? LastName,
    string? MailingName,
    string? Address1,
    string? Address2,
    string? Address3,
    string? City,
    string? StateCode,
    string? ZipCode,
    string? Phone,
    string? TollfreePhone,
    string? Fax,
    string? CustomerTypeCode,
    string? TermsCode,
    DateTime? LastChangeDate);
