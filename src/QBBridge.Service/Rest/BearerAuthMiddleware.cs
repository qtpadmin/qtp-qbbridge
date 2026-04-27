namespace QBBridge.Service.Rest;

public sealed class BearerAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _token;
    private readonly ILogger<BearerAuthMiddleware> _log;

    public BearerAuthMiddleware(RequestDelegate next, ILogger<BearerAuthMiddleware> log)
    {
        _next = next;
        _log = log;
        _token = Environment.GetEnvironmentVariable("QBBRIDGE_BEARER_TOKEN")
            ?? throw new InvalidOperationException("QBBRIDGE_BEARER_TOKEN env var not set");
    }

    public async Task Invoke(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";

        // /health is unauthenticated so upstream monitors can probe it
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        // SOAP callback from QBWC uses its own auth (username/password in authenticate()).
        if (path.StartsWith("/qbwc", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        // Everything else requires a bearer token
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(auth.Substring(7).Trim(), _token, StringComparison.Ordinal))
        {
            _log.LogWarning("Rejected unauthenticated request to {Path} from {IP}",
                path, ctx.Connection.RemoteIpAddress);
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("unauthorized");
            return;
        }

        await _next(ctx);
    }
}
