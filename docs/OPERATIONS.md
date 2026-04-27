# QTP QB Bridge — Operations Guide

## Where things live on J-DC2

| Item | Path |
|---|---|
| Binary | `D:\Services\QBBridge\QBBridge.Service.exe` |
| Config | `D:\Services\QBBridge\appsettings.json` |
| Logs | `D:\Services\QBBridge\logs\qbbridge-YYYY-MM-DD.log` (30-day retention) |
| Service name | `QTPQBBridge` |
| Service account | `svc_qbbridge` (local, non-admin) |
| REST port | 8444 HTTPS (Tailscale-only) |
| SOAP port | 8443 HTTP (localhost-only, for QBWC) |
| TLS cert | `CN=qtp-qbbridge.j-dc2.local` in `LocalMachine\My` |

## Quick commands

### Status

```powershell
Get-Service QTPQBBridge
curl.exe -sk https://localhost:8444/health
```

### Start / stop / restart

```powershell
Start-Service QTPQBBridge
Stop-Service  QTPQBBridge
Restart-Service QTPQBBridge
```

### Watch live logs

```powershell
Get-Content D:\Services\QBBridge\logs\qbbridge-*.log -Wait -Tail 50
```

### Trigger a sync manually (outside of QBWC schedule)

Open QBWC → check the app → enter password → click **Update Selected**.

### Check last sync timestamp

```bash
# from any machine with Tailscale access to InTime staging backend:
curl -sH "Authorization: Bearer <token>" \
  https://100.105.72.33:4000/api/qb-payment-sync/last-sync
```

### Check what happened in the last 10 runs

```sql
SELECT TOP 10 SourceFile, Status, SyncStartedAt, SyncFinishedAt,
       RowsRead, InvoicesUpdated, InvoicesNotFound, RowsSkipped, ErrorMessage
FROM dbo.QBPaymentSyncLog
WHERE SourceFile LIKE 'qbwc-bridge:%'
ORDER BY SyncStartedAt DESC;
```

## Schedule tuning

QBWC owns the schedule, not the bridge.

- **Change interval:** QBWC → select app → set Interval (minutes) → OK.
- **Pause runs without uninstalling:** QBWC → uncheck "Auto-Run" on the app. Re-check to resume.
- **Change run time of day:** QBWC runs every N minutes from when you *set* the interval. To pin 2 AM daily, set the interval at 2 AM (or stop/start QBWC at 2 AM once to reset the clock).

## Common issues

### Health endpoint returns 502 / connection refused

Service is stopped or the TLS cert was removed. `Get-Service QTPQBBridge`; check logs. Re-run `install.ps1` — it's safe to re-run, creates nothing it already finds.

### Bridge health OK but QBWC says "Auth failed"

`QBWC_PASSWORD` env var doesn't match what QBWC has stored. Either:
- Update the stored password in QBWC (right-click app → Update Password) to match the machine env var
- Or update the machine env var to match QBWC's stored password: `setx /M QBWC_PASSWORD "..."` then `Restart-Service QTPQBBridge`

### Bridge runs, QBWC logs "0 new data", but we know QB has new invoices

Check `getLastSyncDate` return value — if always null, the delta filter is always 30d. Run this in SSMS:

```sql
SELECT MAX(QBLastSyncDate) FROM dbo.QBInvoices WHERE QBLastSyncDate IS NOT NULL;
```

If it's returning a future date, QBWC is querying "since the future" and finding nothing. Fix by `UPDATE dbo.QBInvoices SET QBLastSyncDate = '2025-01-01' WHERE QBLastSyncDate > GETDATE()` and re-run.

### Event log entries

Service startup events land in Application log:

```powershell
Get-WinEvent -LogName Application -MaxEvents 20 | Where-Object { $_.ProviderName -eq 'QTPQBBridge' }
```

## Secrets rotation

All secrets are machine env vars — nothing in source files. To rotate:

```powershell
# Rotate the bridge bearer token (must update InTime .env at the same time!)
$new = -join ((1..32) | ForEach-Object { "{0:x2}" -f (Get-Random -Max 256) })
[Environment]::SetEnvironmentVariable('QBBRIDGE_BEARER_TOKEN', $new, 'Machine')
[Environment]::SetEnvironmentVariable('INTIME_API_BEARER_TOKEN', $new, 'Machine')
Restart-Service QTPQBBridge
# Then: update staging + prod InTime .env BRIDGE_INGEST_BEARER_TOKEN and restart backends
```

Do **not** echo tokens back. Save to Vaultwarden as you rotate.

## Updating to a new bridge version

1. Stop the service: `Stop-Service QTPQBBridge`
2. Copy new `QBBridge.Service.exe` over the existing one
3. `Start-Service QTPQBBridge`
4. Verify `/health` reports new version number

No reinstall needed for binary updates. Only re-run `install.ps1` if cert, service account, firewall, or env vars change.

## Backup considerations

- **Bridge binary** — not backed up; re-deployable from the release ZIP anytime
- **Logs** — not backed up; rotation keeps 30d locally
- **QBWC app config** — stored in QBWC's own DB under the running user's profile; survives QBWC reinstall. If you reimage J-DC2, re-add via `QTPBridge.qwc`.
- **QuickBooks integration grant** — stored inside the `.qbw` file itself. A .qbw restore from backup re-grants the app automatically.
