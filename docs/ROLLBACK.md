# QTP QB Bridge — Full Rollback Procedure

Complete removal from J-DC2 in under 5 minutes. Safe to run at any time.

## One-shot rollback

```powershell
cd C:\Temp\qtp-qbbridge     # wherever you extracted the install zip
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Adding `-KeepLogs` preserves the log folder (`D:\Services\QBBridge\logs\`) so you can forensically inspect what happened before removing.

`uninstall.ps1` does:

1. Stops and unregisters the `QTPQBBridge` Windows service
2. Removes the inbound firewall rule on TCP 8444
3. Deletes the self-signed TLS cert from `LocalMachine\My`
4. Clears all 6 machine env vars (`QBBRIDGE_BEARER_TOKEN`, `QBWC_PASSWORD`, `INTIME_API_BASE_URL`, `INTIME_API_BEARER_TOKEN`, `QBBRIDGE_TLS_THUMBPRINT`, `QBBRIDGE_LOG_DIR`)
5. Removes `D:\Services\QBBridge\` (binary + config; logs also removed unless `-KeepLogs`)
6. Removes the local `svc_qbbridge` user

## Manual step you must do in QBWC

The uninstall script does NOT touch QBWC's own database (it belongs to the interactive user, not the service). Do this manually:

1. Open **QuickBooks Web Connector**
2. Select **QTP QB Bridge for InTime**
3. **File → Remove Application** → Yes
4. Close QBWC

## Manual step you must do in QuickBooks

The integration grant lives inside the `.qbw` file. To revoke:

1. Open QuickBooks Enterprise as admin
2. Open `JNJ Services Inc.QBW`
3. **Edit → Preferences → Integrated Applications → Company Preferences**
4. Select **QTP QB Bridge for InTime**
5. Click **Remove** → Yes → OK

## Full state after rollback

| Thing | State |
|---|---|
| `QTPQBBridge` service | deleted |
| `svc_qbbridge` user | deleted |
| `D:\Services\QBBridge\` | removed |
| Firewall rule "QTP QB Bridge (REST in)" | removed |
| TLS cert CN=qtp-qbbridge.j-dc2.local | removed |
| Machine env vars (6) | cleared |
| QBWC app registration | **manually removed per above** |
| QB Integrated App grant in .qbw | **manually removed per above** |
| `QBInvoices.QBLastSyncDate` column values | left as-is (InTime data, not ours to touch) |
| `QBPaymentSyncLog` rows | left as-is (audit history) |

## Reversible, not destructive

Rolling back does not modify any existing InTime data. The columns the bridge populated (`QBStatus`, `QBPaidDate`, etc.) retain their last-synced values until you either (a) re-install the bridge and let it refresh them, or (b) manually clear them via SSMS if desired:

```sql
-- Optional: clear bridge-sourced columns if you want a clean slate
UPDATE dbo.QBInvoices
   SET QBStatus        = NULL,
       QBPaidDate      = NULL,
       QBPaidAmount    = NULL,
       QBBalance       = NULL,
       QBLastSyncDate  = NULL,
       QBLastSyncFile  = NULL
 WHERE QBLastSyncFile LIKE 'qbwc-bridge:%';
```

## Re-install after rollback

Just run `install.ps1` again. The service account gets a new random password; certs get regenerated; env vars re-prompted; binary redeployed. Same config, same behavior.
