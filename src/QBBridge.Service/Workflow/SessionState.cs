using System.Collections.Concurrent;

namespace QBBridge.Service.Workflow;

/// <summary>
/// Per-QBWC-session state. QBWC opens a session with authenticate(), keeps a ticket,
/// and we stash what's been done + what's next so sendRequestXML / receiveResponseXML
/// stay stateless across HTTP hops.
///
/// v1: in-memory only. Sessions last minutes. If the service restarts mid-session,
/// QBWC sees connectionError and retries next cycle — no data loss because we only
/// commit to the InTime DB on receiveResponseXML.
/// </summary>
public sealed class SessionState
{
    public string Ticket { get; init; } = "";
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public Queue<string> PendingRequests { get; init; } = new();
    public int TotalRequests { get; init; }
    public int CompletedRequests { get; set; }
    public int InvoicesUpdated { get; set; }
    public int PaymentsProcessed { get; set; }
    public string LastError { get; set; } = "";
    public DateTime? LastSyncDate { get; set; }
}

public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

    public void Add(SessionState s) => _sessions[s.Ticket] = s;
    public SessionState? Get(string ticket) => _sessions.TryGetValue(ticket, out var s) ? s : null;
    public void Remove(string ticket) => _sessions.TryRemove(ticket, out _);
    public IEnumerable<SessionState> All() => _sessions.Values;

    // Housekeeping: drop sessions older than 1h — stale from a QBWC crash.
    public void PurgeStale(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var kv in _sessions.Where(kv => kv.Value.StartedAt < cutoff).ToList())
            _sessions.TryRemove(kv.Key, out _);
    }
}
