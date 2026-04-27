using Microsoft.AspNetCore.Server.Kestrel.Core;
using QBBridge.Service.Rest;
using QBBridge.Service.Soap;
using QBBridge.Service.Workflow;
using Serilog;
using Serilog.Events;
using SoapCore;

// Serilog bootstrap — write to Windows EventLog AND a rolling file on D:\.
var logDir = Environment.GetEnvironmentVariable("QBBRIDGE_LOG_DIR") ?? "logs";
Directory.CreateDirectory(logDir);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(logDir, "qbbridge-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("QTP QB Bridge starting up");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    // Run as a Windows service when invoked by SCM; as a console app otherwise.
    builder.Host.UseWindowsService(o => o.ServiceName = "QTPQBBridge");
    builder.Host.UseSerilog();

    // Kestrel: two ports.
    // 8443 → SOAP (QBWC callback), bound to loopback for safety
    // 8444 → REST (InTime over Tailscale), bound to all interfaces with HTTPS
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenLocalhost(8443, o =>
        {
            o.Protocols = HttpProtocols.Http1;
        });

        options.ListenAnyIP(8444, o =>
        {
            o.Protocols = HttpProtocols.Http1AndHttp2;
            o.UseHttps(h =>
            {
                // Cert loaded from Windows cert store (LocalMachine\My) by thumbprint.
                // install.ps1 generates a self-signed cert and sets the thumbprint env var.
                var thumbprint = Environment.GetEnvironmentVariable("QBBRIDGE_TLS_THUMBPRINT");
                if (!string.IsNullOrWhiteSpace(thumbprint))
                {
                    using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                        System.Security.Cryptography.X509Certificates.StoreName.My,
                        System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                    store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                    var certs = store.Certificates.Find(
                        System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
                        thumbprint, validOnly: false);
                    if (certs.Count > 0) h.ServerCertificate = certs[0];
                }
                // Missing cert → Kestrel refuses to start on 8444. Intentional fail-closed.
            });
        });
    });

    // DI
    builder.Services.AddSingleton<SessionStore>();
    builder.Services.AddSingleton<QbxmlBuilder>();
    builder.Services.AddSingleton<QbxmlParser>();
    builder.Services.AddSingleton<IQbwcService, QbwcService>();
    builder.Services.AddHttpClient<IntimeApiClient>();
    builder.Services.AddControllers();
    builder.Services.AddSoapCore();

    var app = builder.Build();

    app.UseMiddleware<BearerAuthMiddleware>();
    app.UseRouting();

    // SOAP endpoint for QBWC (disambiguate to IApplicationBuilder overload)
    SoapEndpointExtensions.UseSoapEndpoint<IQbwcService>(
        (Microsoft.AspNetCore.Builder.IApplicationBuilder)app,
        "/qbwc",
        new SoapEncoderOptions(),
        SoapSerializer.XmlSerializer);

    app.MapControllers();

    Log.Information("Listening: SOAP 127.0.0.1+[::1]:8443 /qbwc   |   REST 0.0.0.0:8444 /health /status");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Fatal startup failure");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;
