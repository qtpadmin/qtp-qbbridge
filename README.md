# QTP QB Bridge

QuickBooks Web Connector ↔ InTime reconciliation bridge.

Lets the InTime reconciliation report read live QuickBooks Desktop invoice and payment data without QODBC, without exposing QB to the network, and without changes to the QB company file. The bridge runs as a Windows service on the QB host (J-DC2 in JNJ's environment), accepts SOAP callbacks from the locally-installed QuickBooks Web Connector, queries QB via qbXML, and POSTs the parsed results to the InTime backend over a Tailscale-restricted TLS endpoint.

## Status

**v1.0 — operational on JNJ staging since 2026-04-26.** First real-data sync persisted to `InTime_Staging.dbo.QBInvoices` at `2026-04-26T17:52:54.800Z`. Schedule: 24h via QBWC + QBWCMonitor service (Intuit's official always-on wakeup). Production cutover gated on a multi-day clean soak.

## Architecture (Option C — pure QBWC/qbXML)

```
QuickBooks Desktop  ←→  QBWC (J-DC2)  ──SOAP/HTTP──→  Bridge (J-DC2)
                                                         │
                                                         │ HTTPS (TLS 1.3, bearer)
                                                         ▼
                                              stunnel (J-Staging :4443)
                                                         │
                                                         │ HTTP loopback
                                                         ▼
                                              InTime backend (J-Staging :4000)
                                                         │
                                                         ▼
                                              SQL Server (InTime_Staging)
```

- Two ports on the bridge: `127.0.0.1+[::1]:8443` (SOAP for QBWC, plain HTTP, localhost-only) and `0.0.0.0:8444` (REST HTTPS, Tailscale-CGNAT-restricted via Windows firewall)
- Self-contained .NET 8 win-x64 single-file binary — no .NET install on J-DC2 required
- QBWC password lives in a Windows machine env var (`QBWC_PASSWORD`); REST bearer in `QBBRIDGE_BEARER_TOKEN`
- Outbound TLS to InTime via stunnel terminating at `https://10.100.15.21:4443/api/qb-payment-sync/*` (TLS 1.2 floor, leaf cert in J-DC2's `LocalMachine\Root`)

Why Option C over QODBC or hybrid: QB-side schema validation, no third-party driver on a domain controller, full audit trail in QB's Integrated Applications, Intuit-supported protocol with backward compat. Build cost was ~5-7 days; safety floor is meaningfully higher.

## Layout

```
src/
  QBBridge.Service/      — ASP.NET Core service (SoapCore + REST + Serilog)
    Program.cs           — Kestrel + DI + middleware wiring
    Soap/
      IQbwcService.cs    — 8-verb QBWC SOAP contract
      QbwcService.cs     — session state + qbXML orchestration
      QbxmlBuilder.cs    — InvoiceQueryRq / ReceivePaymentQueryRq builders (qbXML 13.0)
      QbxmlParser.cs     — XLinq-based, partial-field-tolerant
    Rest/
      BearerAuthMiddleware.cs   — token check for non-/health, non-/qbwc paths
      HealthController.cs       — /health (no auth)
      StatusController.cs       — /status (bearer)
    Workflow/
      SessionState.cs           — in-mem session store (1h purge)
      IntimeApiClient.cs        — typed HttpClient → InTime ingest endpoints
  QBBridge.Tests/        — 25 xUnit tests (parser fixtures, builder schema)

deploy/
  install.ps1            — idempotent installer (user, service, cert, firewall, env vars)
  uninstall.ps1          — full clean rollback
  preflight.ps1          — read-only readiness check
  QTPBridge.qwc          — QBWC app registration manifest
  bridge-cert.pem        — stunnel TLS cert for outbound trust (leaf, public-key-only equivalent)

docs/
  INSTALL.md             — guided 7-step install
  DEPLOY_RUNBOOK.md      — checklist with per-step rollback points
  OPERATIONS.md          — day-2: logs, status, rotation, troubleshooting
  ROLLBACK.md            — full removal under 5 min
```

## Build

Self-contained win-x64 single-file via Docker (no .NET on host needed):

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test --nologo -c Release    # expect 25/25

docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet publish src/QBBridge.Service/QBBridge.Service.csproj \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o publish/win-x64
```

Output: `publish/win-x64/QBBridge.Service.exe` (~104 MB).

## Install on J-DC2

See `docs/DEPLOY_RUNBOOK.md`. Short version:

1. Pre-flight: `.\preflight.ps1` — must be all green (QBWC installed, company file present, ports free, network reachable)
2. Set 4 env vars in the PowerShell session: `QBBRIDGE_BEARER_TOKEN`, `QBWC_PASSWORD`, `INTIME_API_BASE_URL`, `INTIME_API_BEARER_TOKEN`
3. `.\install.ps1` (RunAsAdministrator) — creates `svc_qbbridge`, deploys binaries to `D:\Services\QBBridge\`, generates self-signed cert, registers Windows service, opens firewall :8444 to Tailscale CGNAT only
4. In QuickBooks Web Connector: File → Add Application → `QTPBridge.qwc`; QB prompts for trust → Yes Always Allow Even When Closed
5. Click Update Selected — first sync. Watch `D:\Services\QBBridge\logs\qbbridge-*.log`
6. Edit the app → set Run Every N Minutes = 1440, Auto-Run = on (most installations get this from the .qwc Scheduler block automatically)

`QBWCMonitor` Windows service (installed with QBWC) keeps the schedule firing even when nobody is logged in.

## Documentation

- `MSP_AUTOPILOT_*` references in the parent ITSupport repo describe the InTime backend side
- claude-mem observations #3247 (qtp-qbbridge) and #3248 (jnj-intime) capture the 5-bug debugging session that led to v1.0
- Session log: `/home/bnelson/ITSupport/SESSIONS/projects/qtp-qbbridge/SESSION_qbwc-bridge.md`
