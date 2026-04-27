# QTP QB Bridge — Phase 3 Deploy Runbook

Use this during the install window. Every step has: **what to run**, **what you should see**, **what to do if it fails**, and (where relevant) an explicit **rollback point**.

**Total time budget:** 20 min. If you hit the 30-min mark without finishing Step 8, rollback and regroup.

**Target host:** J-DC2 (10.100.15.3)
**Prereq:** you are Domain Admin, and you have an RDP session to J-DC2

---

## Before You Start

### Gather secrets

From Vaultwarden, have these 4 values ready in a scratchpad:

| Prompt | Vaultwarden entry | Notes |
|---|---|---|
| `QBBRIDGE_BEARER_TOKEN` | `QTP QB Bridge for InTime — REST Bearer` | 64-char hex |
| `QBWC_PASSWORD` | `QTP QB Bridge for InTime — QBWC` | The one you set earlier |
| `INTIME_API_BASE_URL` | (no entry — typed) | `https://10.100.15.21:4443` (LAN via stunnel) |
| `INTIME_API_BEARER_TOKEN` | same as the Bearer entry | Same value as #1 |

**TLS path:** bridge → stunnel on J-Staging → NestJS localhost:4000. stunnel uses a self-signed cert (SAN-locked to 10.100.15.21, 100.105.72.33, j-staging-frontend). The installer auto-imports `bridge-cert.pem` to `LocalMachine\Root` so the bridge's HttpClient trusts it. J-DC2 is **not** on Tailscale, so the bridge talks to J-Staging over LAN.

### Confirm staging backend + stunnel are up

From j-staging-frontend (Tailscale):

```bash
ssh bnelson@100.105.72.33 'curl -sk https://localhost:4443/api/qb-payment-sync/health'
```

Expect: `{"service":"qb-payment-sync","status":"ok","enabled":true,...}`
If not: **stop.** Bridge will have nothing to talk to. Check `systemctl status stunnel4` on J-Staging.

---

## Step 1 — RDP in + open PowerShell as Admin

**Action:**
```
RDP to J-DC2 → right-click PowerShell → Run as Administrator
```

**You'll see:** `PS C:\Windows\system32>` prompt

**If it fails:** check your VPN / Tailscale / RDP credentials. No system state has changed.

---

## Step 2 — Copy the ZIP to J-DC2

**Action:** the ZIP is already on `\\j-dc2\qbinterfacetest\Intime\qtp-qbbridge-1.0.0-win-x64.zip` (dropped from staging). Pull it to `C:\Temp`:

```powershell
New-Item -ItemType Directory -Path C:\Temp -Force | Out-Null
Copy-Item '\\j-dc2\qbinterfacetest\Intime\qtp-qbbridge-1.0.0-win-x64.zip' C:\Temp\
Expand-Archive C:\Temp\qtp-qbbridge-1.0.0-win-x64.zip -DestinationPath C:\Temp\ -Force
cd C:\Temp\qtp-qbbridge-1.0.0
dir
```

**You'll see:** 8 files in the listing — `QBBridge.Service.exe`, `install.ps1`, `uninstall.ps1`, `preflight.ps1`, `QTPBridge.qwc`, `appsettings.json`, `bridge-cert.pem`, plus a `docs\` folder.

**If it fails:** can't read the share → verify `qbinterfacetest` is reachable; can't write to `C:\Temp` → use `D:\Temp` instead (D: has more space anyway).

**Rollback point:** none yet — no system state has changed.

---

## Step 3 — Run pre-flight

**Action:**
```powershell
powershell -ExecutionPolicy Bypass -File .\preflight.ps1
```

**You should see:** lots of `[ OK ]` lines, ending with either
- `ALL GREEN — ready to install.` → proceed to Step 4
- `READY with N warning(s) — install will succeed.` → read the warnings, decide if acceptable, proceed
- `BLOCKED by N failure(s) — fix before running install.ps1.` → **stop; fix the blockers**

**Common failures and fixes:**
| Failure | Fix |
|---|---|
| `TCP 8444 already bound by <process>` | stop the offending process; often a previous install left the service running → run `uninstall.ps1` first |
| `QuickBooks Web Connector not found` | download from Intuit's site (free), install, re-run preflight |
| `QuickBooksDB34 service status=Stopped` | `Start-Service QuickBooksDB34` |
| `Not running as Administrator` | close PS, re-open as Admin |
| `Company file not found at D:\Shares\Quickbooks\...QBW` | confirm the filename with Janeth; pass `-CompanyFile 'D:\...\FILE.QBW'` |

**Rollback point:** still none — pre-flight is read-only. You can walk away.

---

## Step 4 — Install

**Action:**
```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

The script will prompt for the 4 secrets. Paste each one (they're accepted as SecureString so you won't see them echo back — this is normal).

**You'll see:** 9 `==>` stages, each with `[ok]` lines:

```
==> Pre-flight checks                      [ok] Host: J-DC2 / binary found / secrets loaded
==> Ensure local user: svc_qbbridge        [ok] Created svc_qbbridge
==> Grant "Log on as a service" right      [ok] Right granted
==> Deploy binaries to D:\Services\QBBridge [ok] Binaries copied + ACLs set
==> Import InTime backend TLS cert         [ok] Imported InTime backend cert, thumbprint: 7A47D8ED...
==> TLS certificate (self-signed) for REST [ok] Created cert, thumbprint: XXXX...
==> Set machine-level environment variables [ok] Env vars set (6)
==> Register Windows service: QTPQBBridge  [ok] Service registered with auto-restart
==> Firewall rule: inbound TCP 8444        [ok] Rule created, source=Tailscale CGNAT
==> Start service QTPQBBridge              [ok] Service is RUNNING
==> Self-test: /health                     [ok] Health OK: {...}
```

**Ends with:** "Install complete. Next steps:" box

**If Install step shows `[!!]` anywhere:**
- `Service status: Stopped` → run `Get-Content D:\Services\QBBridge\logs\qbbridge-*.log -Tail 30` to see why; most likely a bad env var value
- `Health check failed` → same; check logs

### 🚨 Rollback point #1 — install.ps1 failed

If install didn't complete cleanly, run:
```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Everything created in Step 4 is gone. System state = pre-Step-4. Figure out what was wrong, re-run preflight.

---

## Step 5 — Verify bridge is healthy

**Action (local, on J-DC2):**
```powershell
curl.exe -sk https://localhost:8444/health
```

**You should see:**
```json
{"status":"ok","service":"qtp-qb-bridge","version":"1.0.0","sessionsActive":0,"uptime":"00:00:XX"}
```

**Remote verification (from J-Staging over LAN — J-DC2 is NOT on Tailscale):**
```bash
ssh bnelson@100.105.72.33 'curl -sk https://10.100.15.3:8444/health'
```

Same response.

**Verify the InTime backend cert was imported** (so outbound TLS will work):
```powershell
Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -like '*j-staging-frontend*' }
```
You should see one cert with `Subject=CN=j-staging-frontend` and the SHA-256 thumbprint matching the one from J-Staging's `/etc/stunnel/bridge-cert.pem`.

**If it fails:**
- Locally (on J-DC2) but not remote → firewall rule issue; re-run install.ps1 (idempotent)
- Locally also fails → service crashed on startup; `Get-Service QTPQBBridge` + tail the log
- Cert not imported → manually run `Import-Certificate -FilePath .\bridge-cert.pem -CertStoreLocation Cert:\LocalMachine\Root` from the extracted ZIP folder

---

## Step 6 — Register bridge in QBWC

**Action:** Open QuickBooks Web Connector. Start menu → type "QuickBooks Web Connector" → enter.

In QBWC:
1. **File → Add an application**
2. Navigate to `C:\Temp\qtp-qbbridge-1.0.0\QTPBridge.qwc`
3. Click **Open**
4. QBWC pops **Authorize New Web Service** dialog → click **OK**
5. QBWC now lists **QTP QB Bridge for InTime** — type the `QBWC_PASSWORD` in the Password column for that row
6. When prompted "Do you want QBWC to remember this password?" → **Yes**

**You'll see:** the app listed with status `Last Result: (blank)` and `Auto-Run: unchecked`

### 🚨 Rollback point #2 — something wrong in QBWC

If QBWC rejects the .qwc file or errors during Authorize: File → Remove Application (from the QBWC menu). Then inspect the .qwc XML. Nothing bad happened on J-DC2 itself.

---

## Step 7 — Authorize in QuickBooks

**Action:** Open QuickBooks Enterprise. Open `JNJ Services Inc.QBW` company file.

QB will pop a dialog titled **"QuickBooks - Application Certificate"**:

1. Choose **"Yes, always allow access, even if QuickBooks is not running"**
2. Choose **"Allow this application to read and modify this company file"** (read-only is fine for v1; modify supports Phase 4)
3. Check the **"Allow this application to access personal data such as Social Security Numbers and customer credit card info"** box only if Janeth says payment card data is needed (otherwise leave unchecked)
4. Click **Continue**
5. Click **Done** on the confirmation screen

**Where this is stored:** inside the `.qbw` file itself. Survives QBWC reinstall / J-DC2 reimage.

**To revoke later:** QB → Edit → Preferences → Integrated Applications → Company Preferences → select the app → Remove.

### 🚨 Rollback point #3 — QB authorization refused / wrong account

If you're not logged in as a user with admin rights on the .qbw, QB won't let you grant access. Log in as Admin, retry Step 7. No other state changes.

---

## Step 8 — First manual sync

**Action:** Back in QBWC:
1. Check the box next to **QTP QB Bridge for InTime**
2. Re-enter password if prompted
3. Click **Update Selected**

**You'll see:** a progress bar, then either:
- ✅ Green check with `Last Result: Success` + a percentage / count in the log pane → **proceed**
- ❌ Red X with `Last Result: Error` → check the QBWC log (Open → Open Log File) and the bridge log (`Get-Content D:\Services\QBBridge\logs\qbbridge-*.log -Tail 50`)

**On success, watch the bridge log:**
```powershell
Get-Content D:\Services\QBBridge\logs\qbbridge-*.log -Tail 30
```

Expected lines:
```
QBWC session opened: ticket=... queries queued=2 since=2025-01-01T...
sendRequestXML: returning qbXML for ticket ...
Posted N invoices to InTime
Posted M payments to InTime
Closing session ...: OK: N invoices, M payments synced from 2025-01-01 onward
```

### 🚨 Rollback point #4 — sync failed or produced garbage data

If the first sync failed or wrote obviously wrong data to InTime:
1. **Stop QBWC** from re-running: uncheck the Auto-Run box
2. **Check the audit log:** on staging InTime backend,
   ```sql
   SELECT TOP 5 * FROM dbo.QBPaymentSyncLog
   WHERE SourceFile LIKE 'qbwc-bridge:%' ORDER BY SyncStartedAt DESC;
   ```
3. **If bad data landed:** you can clear it with the SQL in ROLLBACK.md under "Re-install after rollback"
4. **If you want to fully back out:** run `uninstall.ps1` on J-DC2, and in QBWC: File → Remove Application

---

## Step 9 — Verify in InTime Reconciliation Report

**Action:** from any browser with access to staging:
`https://<staging-intime-ui>/dashboard/accounting/qb-reconciliation`

1. Pick **Quarter = current quarter** (or whatever has recent activity)
2. Click **Run Report**
3. Look at the **QB Status**, **QB Paid Date**, **QB Paid Amount**, **QB Balance** columns

**You should see:** real data in rows where QB has recorded payments since the bridge's `since` date. Rows with no QB activity still show blank QB-side values (correct — those invoices haven't been touched in QB since the filter date).

**If all rows still show blank** on rows you know are paid in QB:
- Check `SELECT MAX(QBLastSyncDate) FROM dbo.QBInvoices` — should be within the last minute
- If it IS recent but columns are still null, parser isn't pulling a field it should. Capture the qbXML (turn on debug logging) and send to me.

---

## Step 10 — Set the daily schedule

**Action:** In QBWC, select the bridge app:
1. Click **Edit** (or right-click → Properties)
2. Set **Interval** to `1440` (minutes = 24 hours)
3. Click **OK**
4. Check the **Auto-Run** checkbox

**Recommended timing:** set the interval at **2 AM EST** so the first scheduled run fires then. To do this: wait until 2 AM exactly, then check Auto-Run.

If that's inconvenient, set the interval now and QBWC will just fire 24h from whenever you checked the box.

---

## ✅ Post-Deploy Checklist

Before you disconnect:

- [ ] `Get-Service QTPQBBridge` shows Running
- [ ] `/health` returns 200 over Tailscale from outside J-DC2
- [ ] QBWC shows **Last Result: Success** for the app
- [ ] QB Integrated Apps lists the bridge with "always allow" enabled
- [ ] One manual sync has completed; InTime Recon Report shows live QB data
- [ ] QBWC Auto-Run is checked and interval is 1440
- [ ] Install log saved somewhere (copy the PowerShell transcript if you were running `Start-Transcript`)
- [ ] Vaultwarden updated with a note under the QBWC Password entry: "installed on J-DC2 YYYY-MM-DD"

---

## If You Abort Partway

**Best single command to undo everything:**
```powershell
cd C:\Temp\qtp-qbbridge-1.0.0
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Then the 2 QBWC/QB manual steps (listed in ROLLBACK.md).

Total rollback time: ~3 minutes. System state returns exactly to pre-Step-4.

---

## After-Deploy — What To Do Tomorrow

The first scheduled run will happen ~24h after you checked Auto-Run. Next morning:

1. Check `D:\Services\QBBridge\logs\qbbridge-YYYY-MM-DD.log` — expect an entry from overnight
2. Check `SELECT MAX(QBLastSyncDate) FROM dbo.QBInvoices` — should show overnight timestamp
3. If both of those look good after 3 consecutive nights → move to production InTime backend (same bridge; just update `INTIME_API_BASE_URL` to prod)

---

## When You'll Want Me Again

- Before enabling Phase 4 (writeback) — I need to add writeback qbXML verbs behind a DRY_RUN flag and walk through the test plan with you
- If any of the parsed fields don't map the way you + Tonya want them (e.g. QB Status mapping from `Paid`/`Open`/`Void` to something more granular)
- When Janeth sends her CSV sample → we wire the translator on top of the existing CSV watcher path (independent of the bridge)
