#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the QTP QB Bridge Windows service on J-DC2.

.DESCRIPTION
    One-shot installer. Creates the service account, places binaries,
    generates a TLS cert, creates the Windows service, opens a firewall
    rule, and sets the three env vars the bridge needs at runtime.

    All operations are idempotent — safe to re-run if a step fails.

.NOTES
    Run this script ONCE on J-DC2 as a Domain Admin (required to create
    the local user + install the service).

    Required environment at install time (script will prompt if missing):
      - $env:QBBRIDGE_BEARER_TOKEN   (for REST auth; bridge + InTime share this)
      - $env:QBWC_PASSWORD           (what QBWC presents to the bridge)
      - $env:INTIME_API_BASE_URL     (e.g. https://10.100.15.21:4443 for staging via stunnel)
      - $env:INTIME_API_BEARER_TOKEN (the BRIDGE_INGEST_BEARER_TOKEN from InTime .env)

    The INSTALL source ZIP must be extracted to .\ (current directory)
    before running this script, with QBBridge.Service.exe at the root.
#>

[CmdletBinding()]
param(
    [string] $InstallRoot = 'D:\Services\QBBridge',
    [string] $ServiceName = 'QTPQBBridge',
    [string] $ServiceAccount = 'svc_qbbridge',
    [string] $LogDir = 'D:\Services\QBBridge\logs',
    [int]    $RestPort = 8444,
    [string] $TailscaleInterfaceName = 'Tailscale'
)

# Phase 3 fix: load System.Web BEFORE [System.Web.Security.Membership] is referenced (PS 5.1 does not auto-load it).
Add-Type -AssemblyName System.Web

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Stage($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "    [ok] $msg" -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    [!!] $msg" -ForegroundColor Yellow }

# ─────────────────────────────────────────────────────────────
# 0. Sanity — we are on J-DC2 and binaries exist
# ─────────────────────────────────────────────────────────────
Write-Stage 'Pre-flight checks'

if ($env:COMPUTERNAME -notlike 'J-DC2*') {
    Write-Warn2 "Host is $env:COMPUTERNAME (expected J-DC2). Continue only if you know what you are doing."
    $confirm = Read-Host 'Type YES to continue'
    if ($confirm -ne 'YES') { throw 'Aborted by operator.' }
}
Write-Ok "Host: $env:COMPUTERNAME"

$exePath = Join-Path $PSScriptRoot 'QBBridge.Service.exe'
if (-not (Test-Path $exePath)) { throw "QBBridge.Service.exe not found next to install.ps1 ($exePath)" }
Write-Ok "Found bridge binary: $exePath"

# Prompt for required secrets if not already in env
$reqEnv = @('QBBRIDGE_BEARER_TOKEN','QBWC_PASSWORD','INTIME_API_BASE_URL','INTIME_API_BEARER_TOKEN')
foreach ($v in $reqEnv) {
    if (-not [Environment]::GetEnvironmentVariable($v, 'Process')) {
        $sec = Read-Host -AsSecureString "Enter $v (will be stored as a MACHINE env var)"
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
        [Environment]::SetEnvironmentVariable($v, $plain, 'Process')
    }
}
Write-Ok 'Secrets loaded into process env'

# ─────────────────────────────────────────────────────────────
# 1. Create local service account (idempotent)
# ─────────────────────────────────────────────────────────────
Write-Stage "Ensure local user: $ServiceAccount"

$existing = Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
if (-not $existing) {
    $randomPw = [System.Web.Security.Membership]::GeneratePassword(24, 6)
    # Fallback if System.Web not loaded
    if (-not $randomPw) {
        Add-Type -AssemblyName System.Web
        $randomPw = [System.Web.Security.Membership]::GeneratePassword(24, 6)
    }
    $secPw = ConvertTo-SecureString $randomPw -AsPlainText -Force
    New-LocalUser -Name $ServiceAccount `
                  -Password $secPw `
                  -FullName 'QTP QB Bridge service account' `
                  -Description 'Runs QTPQBBridge Windows service. DO NOT DELETE.' `
                  -PasswordNeverExpires `
                  -UserMayNotChangePassword | Out-Null
    Write-Ok "Created $ServiceAccount (random password, not saved anywhere — service re-creates as needed)"
    $script:PlainPw = $randomPw
} else {
    Write-Ok "$ServiceAccount already exists"
    $resetPw = [System.Web.Security.Membership]::GeneratePassword(24, 6)
    if (-not $resetPw) {
        Add-Type -AssemblyName System.Web
        $resetPw = [System.Web.Security.Membership]::GeneratePassword(24, 6)
    }
    $secPw = ConvertTo-SecureString $resetPw -AsPlainText -Force
    Set-LocalUser -Name $ServiceAccount -Password $secPw
    Write-Ok 'Password rotated'
    $script:PlainPw = $resetPw
}

# Grant "Log on as a service" right
Write-Stage 'Grant "Log on as a service" right'
$sid = (Get-LocalUser -Name $ServiceAccount).SID.Value
$tmp = New-TemporaryFile
secedit /export /cfg $tmp.FullName | Out-Null
$seceditText = Get-Content $tmp.FullName -Raw
if ($seceditText -notmatch "SeServiceLogonRight.*$sid") {
    $seceditText = $seceditText -replace '(SeServiceLogonRight = [^\r\n]+)', "`$1,*$sid"
    $seceditText | Set-Content $tmp.FullName
    secedit /configure /db "$env:WINDIR\security\local.sdb" /cfg $tmp.FullName /areas USER_RIGHTS | Out-Null
    Write-Ok 'Right granted'
} else {
    Write-Ok 'Right already granted'
}
Remove-Item $tmp.FullName

# ─────────────────────────────────────────────────────────────
# 2. Deploy binaries
# ─────────────────────────────────────────────────────────────
Write-Stage "Deploy binaries to $InstallRoot"

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

Copy-Item (Join-Path $PSScriptRoot 'QBBridge.Service.exe') $InstallRoot -Force
if (Test-Path (Join-Path $PSScriptRoot 'appsettings.json')) {
    Copy-Item (Join-Path $PSScriptRoot 'appsettings.json') $InstallRoot -Force
}

# ACL: grant the service account Read+Execute on install dir, Full on log dir
icacls $InstallRoot /grant "${ServiceAccount}:(RX)"  /T | Out-Null
icacls $LogDir      /grant "${ServiceAccount}:(M)"   /T | Out-Null
Write-Ok "Binaries copied + ACLs set"

# ─────────────────────────────────────────────────────────────
# 2b. Import InTime backend TLS cert (for outbound HTTPS trust)
# ─────────────────────────────────────────────────────────────
# The bridge POSTs to https://<staging-ip>:4443 which is fronted by stunnel
# with a self-signed cert. Import it to LocalMachine\Root so the bridge's
# HttpClient trusts it. Leaf cert (non-CA) with SAN-locked to the InTime
# staging host(s) — Windows still validates SAN on every connection, so
# this trust is scoped to the staging endpoint only.
Write-Stage 'Import InTime backend TLS cert (for outbound HTTPS from bridge)'

$intimeCert = Join-Path $PSScriptRoot 'bridge-cert.pem'
if (-not (Test-Path $intimeCert)) {
    Write-Warn2 "bridge-cert.pem not found next to install.ps1 — skipping import."
    Write-Warn2 "Bridge outbound TLS to InTime will FAIL unless cert is in LocalMachine\Root."
} else {
    $imported = Import-Certificate -FilePath $intimeCert -CertStoreLocation Cert:\LocalMachine\Root
    Write-Ok "Imported InTime backend cert, thumbprint: $($imported.Thumbprint)"
    Write-Ok "To remove: Remove-Item Cert:\LocalMachine\Root\$($imported.Thumbprint)"
}

# ─────────────────────────────────────────────────────────────
# 3. Generate / reuse TLS cert
# ─────────────────────────────────────────────────────────────
Write-Stage 'TLS certificate (self-signed) for REST port'

$certSubject = 'CN=qtp-qbbridge.j-dc2.local'
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $certSubject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -DnsName 'qtp-qbbridge.j-dc2.local','j-dc2','j-dc2.jnjservices.local','localhost' `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -Subject $certSubject `
        -KeyUsage DigitalSignature,KeyEncipherment `
        -KeyLength 2048 `
        -NotAfter (Get-Date).AddYears(5) `
        -KeyExportPolicy NonExportable
    Write-Ok "Created cert, thumbprint: $($cert.Thumbprint)"
} else {
    Write-Ok "Reusing cert, thumbprint: $($cert.Thumbprint)"
}

# Give the service account read access to the private key.
# Modern self-signed certs default to CNG keys (NOT legacy CSP), so CspKeyContainerInfo
# is empty. Use GetRSAPrivateKey().Key.UniqueName (CNG) with the Crypto\Keys path, and
# grant by SID to avoid AD vs local-account name lookup ambiguity on a DC.
$rsaKey   = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$keyName  = $rsaKey.Key.UniqueName
$keyPath  = Join-Path $env:ALLUSERSPROFILE "Microsoft\Crypto\Keys\$keyName"
if (Test-Path $keyPath) {
    $svcSid = (Get-LocalUser -Name $ServiceAccount).SID.Value
    icacls $keyPath /grant "*${svcSid}:R" | Out-Null
    Write-Ok "CNG private-key ACL granted to $ServiceAccount (SID $svcSid)"
} else {
    Write-Warn2 "CNG key file not found at $keyPath — REST :8444 TLS may fail (NTE_BAD_KEYSET / 0x8009030D)"
}

# ─────────────────────────────────────────────────────────────
# 4. Machine env vars the service reads at startup
# ─────────────────────────────────────────────────────────────
Write-Stage 'Set machine-level environment variables'

[Environment]::SetEnvironmentVariable('QBBRIDGE_BEARER_TOKEN',  $env:QBBRIDGE_BEARER_TOKEN,  'Machine')
[Environment]::SetEnvironmentVariable('QBWC_PASSWORD',          $env:QBWC_PASSWORD,          'Machine')
[Environment]::SetEnvironmentVariable('INTIME_API_BASE_URL',    $env:INTIME_API_BASE_URL,    'Machine')
[Environment]::SetEnvironmentVariable('INTIME_API_BEARER_TOKEN', $env:INTIME_API_BEARER_TOKEN,'Machine')
[Environment]::SetEnvironmentVariable('QBBRIDGE_TLS_THUMBPRINT', $cert.Thumbprint,            'Machine')
[Environment]::SetEnvironmentVariable('QBBRIDGE_LOG_DIR',        $LogDir,                     'Machine')
Write-Ok 'Env vars set (6)'

# ─────────────────────────────────────────────────────────────
# 5. Create / update the Windows service
# ─────────────────────────────────────────────────────────────
Write-Stage "Register Windows service: $ServiceName"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Ok 'Removed existing service to re-register cleanly'
}

$exeFull = Join-Path $InstallRoot 'QBBridge.Service.exe'
New-Service `
    -Name $ServiceName `
    -BinaryPathName "`"$exeFull`"" `
    -DisplayName 'QTP QB Bridge for InTime' `
    -Description 'Syncs QuickBooks data to the InTime reconciliation report via QBWC (QuickBooks Web Connector). Runs on QBWC schedule (24h).' `
    -StartupType Automatic `
    -Credential (New-Object PSCredential(".\$ServiceAccount", (ConvertTo-SecureString $script:PlainPw -AsPlainText -Force))) | Out-Null

# Recovery: restart on crash, 60s delay, up to 3 times then keep retrying
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Write-Ok 'Service registered with auto-restart on crash'

# ─────────────────────────────────────────────────────────────
# 6. Firewall — inbound REST port on Tailscale interface only
# ─────────────────────────────────────────────────────────────
Write-Stage "Firewall rule: inbound TCP $RestPort (Tailscale only)"

$ruleName = 'QTP QB Bridge (REST in)'
if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
    Remove-NetFirewallRule -DisplayName $ruleName
}
New-NetFirewallRule `
    -DisplayName $ruleName `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort $RestPort `
    -Action Allow `
    -Profile Any `
    -Program $exeFull `
    -RemoteAddress '100.64.0.0/10' | Out-Null   # Tailscale CGNAT range only
Write-Ok "Rule created, source=Tailscale CGNAT (100.64.0.0/10)"

# ─────────────────────────────────────────────────────────────
# 7. Start
# ─────────────────────────────────────────────────────────────
Write-Stage "Start service $ServiceName"
Start-Service $ServiceName
Start-Sleep -Seconds 3
$svc = Get-Service $ServiceName
if ($svc.Status -eq 'Running') {
    Write-Ok "Service is RUNNING"
} else {
    Write-Warn2 "Service status: $($svc.Status) — check logs at $LogDir"
}

# ─────────────────────────────────────────────────────────────
# 8. Self-test
# ─────────────────────────────────────────────────────────────
Write-Stage 'Self-test: /health'
try {
    # -SkipCertificateCheck is on PowerShell 7+; 5.1 fallback below
    $resp = Invoke-RestMethod -Uri "https://localhost:$RestPort/health" -SkipCertificateCheck -TimeoutSec 5
    Write-Ok "Health OK: $($resp | ConvertTo-Json -Compress)"
} catch {
    try {
        [Net.ServicePointManager]::ServerCertificateValidationCallback = {$true}
        $resp = Invoke-RestMethod -Uri "https://localhost:$RestPort/health" -TimeoutSec 5
        Write-Ok "Health OK (ps5.1): $($resp | ConvertTo-Json -Compress)"
    } catch {
        Write-Warn2 "Health check failed: $($_.Exception.Message)"
        Write-Warn2 "Check logs: $LogDir"
    }
}

Write-Host "`n══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Install complete. Next steps:" -ForegroundColor Cyan
Write-Host "──────────────────────────────────────────────────────────────" -ForegroundColor Cyan
Write-Host "  1. Open QuickBooks Web Connector (Start menu → QBWC)"
Write-Host "  2. File → Add an application → browse to QTPBridge.qwc"
Write-Host "  3. QB will prompt: choose 'Yes, always allow (even when QB closed)'"
Write-Host "  4. In QBWC, check the app and click 'Update Selected' to test"
Write-Host "  5. Set Interval to 1440 min (24 hr) for scheduled runs"
Write-Host "  6. Watch logs: Get-Content $LogDir\qbbridge-*.log -Wait -Tail 20"
Write-Host ""
