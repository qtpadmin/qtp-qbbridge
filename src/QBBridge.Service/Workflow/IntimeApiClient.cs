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

    private sealed record LastSyncResponse(DateTime? LastSync);
}
