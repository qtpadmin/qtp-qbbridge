# QTP QB Bridge — Install Guide (J-DC2)

This installs the QBWC (QuickBooks Web Connector) bridge on J-DC2 so the InTime Reconciliation Report populates QB-side data automatically on a daily schedule.

**Version:** 1.0.0 (reads only; writeback gated behind Phase 4)
**Target host:** J-DC2 (10.100.15.3) — the QuickBooks database host
**Time required:** ~15 minutes

---

## Prerequisites

Checked by `install.ps1`, but good to verify first:

- [x] Windows Server 2022 (confirmed by Phase 1 discovery)
- [x] QuickBooks Enterprise 2024 (v34) installed and licensed
- [x] `QuickBooksDB34` service is Running
- [x] `D:\Shares\Quickbooks\JNJ Services Inc.QBW` exists and opens cleanly in QB
- [x] You are logged in as a Domain Admin (required to create local user + service)
- [x] Tailscale is installed and the J-DC2 IP in the tailnet is known
- [x] **QuickBooks Web Connector** is installed — if not, download the free installer from Intuit:
      https://developer.intuit.com/app/developer/qbdesktop/docs/get-started/get-started-with-quickbooks-web-connector

---

## Files in the Install ZIP

```
qtp-qbbridge-1.0.0-win-x64.zip
├── QBBridge.Service.exe       ← self-contained .NET 8 Windows binary (99 MB)
├── appsettings.json
├── install.ps1                ← main installer (run as admin)
├── uninstall.ps1              ← full rollback
├── QTPBridge.qwc              ← QBWC registration config
└── docs/
    ├── INSTALL.md             ← this file
    ├── OPERATIONS.md          ← day-to-day ops
    └── ROLLBACK.md            ← full removal steps
```

---

## Step-by-Step

### 1. Stage the ZIP

Copy `qtp-qbbridge-1.0.0-win-x64.zip` onto J-DC2 (any method — SMB, RDP clipboard, drop in `\\j-dc2\qbinterfacetest\Intime\` and read it locally).

### 2. Extract

```powershell
cd C:\Temp
Expand-Archive .\qtp-qbbridge-1.0.0-win-x64.zip -DestinationPath qtp-qbbridge
cd .\qtp-qbbridge
```

### 3. Gather the 4 secrets from Vaultwarden

You'll be prompted for these during install. Get them ready in a scratchpad:

| Secret | Vaultwarden entry | Notes |
|---|---|---|
| **QBBRIDGE_BEARER_TOKEN** | `QTP QB Bridge for InTime — REST Bearer` | Protects inbound REST API from InTime backend |
| **QBWC_PASSWORD** | `QTP QB Bridge for InTime — QBWC` | Password QBWC presents to the bridge's SOAP endpoint |
| **INTIME_API_BASE_URL** | not in VW — plain value | `https://100.105.72.33:4000` for staging backend |
| **INTIME_API_BEARER_TOKEN** | `QTP QB Bridge for InTime — REST Bearer` | Same token as QBBRIDGE_BEARER_TOKEN in v1 |

### 4. Run the installer

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

The script will:

1. Create local user `svc_qbbridge` (random password, never stored)
2. Grant it "Log on as a service" right
3. Deploy the exe to `D:\Services\QBBridge\`
4. Generate a self-signed TLS cert (5-yr life) and register it in `LocalMachine\My`
5. Set 6 machine-level environment variables (the 4 you supplied + log-dir + cert thumbprint)
6. Register the Windows service `QTPQBBridge`, running as `svc_qbbridge`, auto-start + auto-recover on crash
7. Open inbound TCP 8444 firewall rule, source limited to Tailscale CGNAT `100.64.0.0/10`
8. Start the service
9. Run a `/health` self-test

Expect ~90 seconds total runtime. Green `[ok]` lines throughout; any `[!!]` is worth a look.

### 5. Register the bridge with QBWC

**Launch QuickBooks Web Connector** (Start menu → "QuickBooks Web Connector"):

1. **File → Add an application**
2. Browse to your extracted folder, pick `QTPBridge.qwc`
3. QBWC shows "Authorize New Web Service" dialog → click **OK**
4. Enter the **QBWC_PASSWORD** value when prompted (QBWC stores it in its own DPAPI vault)

### 6. Approve the integration *inside* QuickBooks

**Open QuickBooks Enterprise, open the company file** (`JNJ Services Inc.QBW`):

1. QB will pop up *"An application is requesting access to the company file"*
2. Choose **"Yes, always allow access, even when QuickBooks is not running"**
3. Pick **"Allow this application to read and modify personal data"** (we'll use read-only in v1, but the permission grant supports the Phase 4 writeback path)
4. Confirm

You can later view/revoke this grant: *QB → Edit → Preferences → Integrated Applications → Company Preferences*

### 7. First run (manual)

Back in **QBWC**:

1. Check the box next to **QTP QB Bridge for InTime**
2. Type the password in the password column
3. Click **Update Selected**
4. Watch the status column — green checkmark = success

Check the bridge log to confirm data flowed:

```powershell
Get-Content D:\Services\QBBridge\logs\qbbridge-*.log -Tail 30
```

You should see entries like:
```
QBWC session opened: ticket=... queries queued=2
sendRequestXML: returning qbXML ...
Posted N invoices to InTime
Closing session ...: OK: N invoices, M payments synced
```

### 8. Verify InTime received the data

From AIServer (or any machine with access to staging backend):

```bash
curl -sH "Authorization: Bearer <BRIDGE_INGEST_BEARER_TOKEN>" \
  https://100.105.72.33:4000/api/qb-payment-sync/last-sync
```

Response should be `{"lastSync": "<timestamp-within-the-last-minute>"}`.

Then open the InTime staging UI → Accounting → QB Reconciliation → **Run Report** for the current quarter. The `QBStatus`, `QBPaidDate`, `QBPaidAmount`, `QBBalance` columns should now show real data on rows where QB has recorded activity. ✅

### 9. Set the schedule

**QBWC** → select **QTP QB Bridge for InTime** → **Edit** (or right-click) → set **Interval** to `1440` min (24 hours) → **OK**.

QBWC will auto-run the sync every day at approximately the same wall-clock time as when you set the interval. To pin it to **2 AM EST**, start the schedule at 2 AM.

---

## If Something Goes Wrong

- **Install script fails partway** — re-run it. It's idempotent.
- **`/health` returns 500** — check `D:\Services\QBBridge\logs\qbbridge-*.log` for the stack trace. Most common cause: a missing env var. Re-run install to re-prompt.
- **QBWC shows "Unable to connect"** — the Windows service is down. `Get-Service QTPQBBridge`; `Start-Service QTPQBBridge`; check the log.
- **QB pops up asking for authorization on every run** — step 6 wasn't set to "always allow". Open Integrated Applications preferences and flip it.

Full troubleshooting + operational guidance in **OPERATIONS.md**.
Full rollback in **ROLLBACK.md** (runs in under 5 min).

---

**Before you leave:**

- [ ] Service `QTPQBBridge` is Running
- [ ] `QBWC` lists "QTP QB Bridge for InTime" with a saved password
- [ ] QB Integrated Applications has granted access and checked "always allow"
- [ ] One manual sync has succeeded; InTime recon report shows live QB columns
- [ ] Schedule set to 1440 min
- [ ] Vaultwarden has an entry for `svc_qbbridge` noting "random password, rotated on install — reset via install.ps1 re-run if needed"
