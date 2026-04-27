using Microsoft.AspNetCore.Mvc;
using QBBridge.Service.Workflow;

namespace QBBridge.Service.Rest;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private readonly SessionStore _sessions;

    public HealthController(SessionStore sessions) => _sessions = sessions;

    [HttpGet]
    public IActionResult Get()
    {
        _sessions.PurgeStale(TimeSpan.FromHours(1));

        return Ok(new
        {
            status = "ok",
            service = "qtp-qb-bridge",
            version = "1.0.0",
            sessionsActive = _sessions.All().Count(),
            uptime = (DateTime.UtcNow - Process.StartTime).ToString(),
        });
    }

    private static class Process
    {
        public static DateTime StartTime { get; } = DateTime.UtcNow;
    }
}
