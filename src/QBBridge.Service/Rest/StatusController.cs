using Microsoft.AspNetCore.Mvc;
using QBBridge.Service.Workflow;

namespace QBBridge.Service.Rest;

[ApiController]
[Route("status")]
public sealed class StatusController : ControllerBase
{
    private readonly SessionStore _sessions;

    public StatusController(SessionStore sessions) => _sessions = sessions;

    [HttpGet]
    public IActionResult Get()
    {
        var active = _sessions.All()
            .Select(s => new
            {
                s.Ticket,
                s.StartedAt,
                s.CompletedRequests,
                pendingRequests = s.PendingRequests.Count,
                s.InvoicesUpdated,
                s.PaymentsProcessed,
                s.LastSyncDate,
                lastError = s.LastError,
            })
            .ToList();

        return Ok(new
        {
            sessionsActive = active.Count,
            sessions = active,
        });
    }
}
