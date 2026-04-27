<#
.SYNOPSIS
    Pre-flight readiness check for QTP QB Bridge install on J-DC2.

.DESCRIPTION
    Read-only. Makes ZERO changes. Verifies every condition install.ps1
    needs so you know before you start whether the real install will
    succeed. Run as Administrator so the checks can see service + cert
    state accurately.

    Exits 0 on all-green, 1 if any REQUIRED check fails. Warnings don't
    fail the exit code (they just flag things install.ps1 will re-handle).
#>

[CmdletBinding()]
param(
    [string] $InstallRoot = 'D:\Services\QBBridge',
    [int]    $RestPort = 8444,
    [int]    $SoapPort = 8443,
    [string] $ServiceAccount = 'svc_qbbridge',
    [string] $ServiceName = 'QTPQBBridge',
    [string] $CompanyFile = 'D:\Shares\Quickbooks\JNJ Services Inc.QBW'
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$Failures  = @()
$Warnings  = @()

function Check-Pass($label, $detail = '') {
    Write-Host "  [ OK ] $label" -ForegroundColor Green -NoNewline
    if ($detail) { Write-Host "  $detail" -ForegroundColor DarkGray } else { Write-Host "" }
}
function Check-Fail($label, $why) {
    Write-Host "  [FAIL] $label" -ForegroundColor Red -NoNewline
    Write-Host "  $why" -ForegroundColor DarkRed
    $script:Failures += "$label — $why"
}
function Check-Warn($label, $why) {
    Write-Host "  [WARN] $label" -ForegroundColor Yellow -NoNewline
    Write-Host "  $why" -ForegroundColor DarkYellow
    $script:Warnings += "$label — $why"
}

Write-Host "`n═════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " QTP QB Bridge — Pre-Flight Check" -ForegroundColor Cyan
Write-Host " Host: $env:COMPUTERNAME   User: $env:USERNAME   $(Get-Date)" -ForegroundColor Cyan
Write-Host "═════════════════════════════════════════════════════════════" -ForegroundColor Cyan

# ─────────────────────────────────────────────────────────────
Write-Host "`n[1] HOST & OS" -ForegroundColor White

if ($env:COMPUTERNAME -like 'J-DC2*') {
    Check-Pass 'Correct host (J-DC2)'
} else {
    Check-Warn 'Not on J-DC2' "hostname=$env:COMPUTERNAME — install.ps1 will re-prompt"
}

$os = Get-ComputerInfo | Select-Object OsName, OsVersion, OsArchitecture
if ($os.OsArchitecture -eq '64-bit') {
    Check-Pass 'OS is 64-bit' "$($os.OsName) $($os.OsVersion)"
} else {
    Check-Fail 'OS is 64-bit' "architecture=$($os.OsArchitecture), bridge is win-x64 only"
}

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) { Check-Pass 'Running as Administrator' } else { Check-Fail 'Not running as Administrator' 'install.ps1 requires admin to create users, services, certs' }

# ─────────────────────────────────────────────────────────────
Write-Host "`n[2] QUICKBOOKS" -ForegroundColor White

$qbSvc = Get-Service -Name 'QuickBooksDB34' -ErrorAction SilentlyContinue
if ($qbSvc -and $qbSvc.Status -eq 'Running') {
    Check-Pass 'QuickBooksDB34 service' "status=$($qbSvc.Status)"
} elseif ($qbSvc) {
    Check-Fail 'QuickBooksDB34 service' "present but status=$($qbSvc.Status); start it first"
} else {
    Check-Fail 'QuickBooksDB34 service' 'not found; is QB Enterprise 2024 installed?'
}

# QBWC binary check — QBWC installs itself under C:\Program Files (x86)\Common Files\Intuit\QuickBooks
$qbwcCandidates = @(
    'C:\Program Files (x86)\Common Files\Intuit\QuickBooks\QBWebConnector\QBWebConnector.exe',
    'C:\Program Files\Common Files\Intuit\QuickBooks\QBWebConnector\QBWebConnector.exe',
    'C:\Program Files (x86)\Common Files\Intuit\QuickBooks\QBWebConnector.exe',
    'C:\Program Files\Common Files\Intuit\QuickBooks\QBWebConnector.exe',
    'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\QuickBooks\QuickBooks Web Connector.lnk'
)
$qbwcFound = $qbwcCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($qbwcFound) {
    Check-Pass 'QuickBooks Web Connector installed' $qbwcFound
} else {
    Check-Fail 'QuickBooks Web Connector' 'not found; download free installer from Intuit and re-run'
}

if (Test-Path $CompanyFile) {
    $info = Get-Item $CompanyFile
    Check-Pass 'Company file exists' "$($info.Length.ToString('N0')) bytes, modified $($info.LastWriteTime)"
} else {
    Check-Fail 'Company file' "not found at $CompanyFile"
}

# ─────────────────────────────────────────────────────────────
Write-Host "`n[3] NETWORK" -ForegroundColor White

foreach ($port in @($RestPort, $SoapPort)) {
    $bound = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue | Where-Object { $_.State -in @('Listen','Established') }
    if (-not $bound) {
        Check-Pass "TCP $port free"
    } else {
        $owner = try { (Get-Process -Id $bound[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName } catch { 'unknown' }
        Check-Fail "TCP $port" "already bound by $owner (PID $($bound[0].OwningProcess))"
    }
}

# Tailscale check
$tsSvc = Get-Service -Name 'Tailscale' -ErrorAction SilentlyContinue
if ($tsSvc -and $tsSvc.Status -eq 'Running') {
    Check-Pass 'Tailscale service' "status=$($tsSvc.Status)"
} elseif ($tsSvc) {
    Check-Warn 'Tailscale service' "present but status=$($tsSvc.Status); start before deploy for remote REST access"
} else {
    Check-Warn 'Tailscale service' 'not found; bridge will still work locally but remote InTime backend reach will fail'
}

# Probe staging InTime (best-effort)
try {
    $test = Invoke-WebRequest -Uri 'http://10.100.15.21:4000/api/qb-payment-sync/health' -UseBasicParsing -TimeoutSec 3 -SkipCertificateCheck -ErrorAction SilentlyContinue
    if ($test.StatusCode -eq 200) {
        Check-Pass 'Staging backend reachable' 'http://10.100.15.21:4000/api/qb-payment-sync/health = 200'
    } else {
        Check-Warn 'Staging backend' "responded $($test.StatusCode) — will retry on real run"
    }
} catch {
    try {
        [Net.ServicePointManager]::ServerCertificateValidationCallback = {$true}
        $test = Invoke-WebRequest -Uri 'http://10.100.15.21:4000/api/qb-payment-sync/health' -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
        Check-Pass 'Staging backend reachable (ps5.1)' "= $($test.StatusCode)"
    } catch {
        Check-Warn 'Staging backend' "unreachable from J-DC2 — $($_.Exception.Message.Split([char]10)[0])"
    }
}

# ─────────────────────────────────────────────────────────────
Write-Host "`n[4] FILESYSTEM" -ForegroundColor White

$installParent = Split-Path $InstallRoot -Parent
if (Test-Path $installParent) {
    try {
        $probe = Join-Path $installParent "preflight-$([guid]::NewGuid().ToString('N')).tmp"
        "x" | Set-Content $probe -ErrorAction Stop
        Remove-Item $probe -Force
        Check-Pass "Writable: $installParent"
    } catch {
        Check-Fail "Writable: $installParent" $_.Exception.Message
    }
} else {
    Check-Warn "$installParent" 'does not exist yet (install.ps1 will create it)'
}

$dDrive = Get-PSDrive -Name D -ErrorAction SilentlyContinue
if ($dDrive) {
    $freeGB = [math]::Round($dDrive.Free / 1GB, 1)
    if ($freeGB -ge 1) {
        Check-Pass 'D: drive free space' "$freeGB GB (need ~200 MB for binary + logs)"
    } else {
        Check-Fail 'D: drive free space' "only $freeGB GB free"
    }
} else {
    Check-Warn 'D: drive' 'not found; install.ps1 defaults to D:\Services\QBBridge, pass -InstallRoot to override'
}

# ─────────────────────────────────────────────────────────────
Write-Host "`n[5] PRE-EXISTING STATE (install.ps1 will handle, but heads-up)" -ForegroundColor White

if (Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue) {
    Check-Warn "Local user $ServiceAccount" 'already exists — install.ps1 will rotate password, which is fine'
} else {
    Check-Pass "Local user $ServiceAccount" 'not present (install.ps1 will create)'
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Check-Warn "Service $ServiceName" 'already installed — install.ps1 will remove + re-register'
} else {
    Check-Pass "Service $ServiceName" 'not present (install.ps1 will register)'
}

$certExisting = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq 'CN=qtp-qbbridge.j-dc2.local' }
if ($certExisting) {
    Check-Warn 'Bridge TLS cert' "already exists (thumbprint=$($certExisting[0].Thumbprint)) — install.ps1 will reuse"
} else {
    Check-Pass 'Bridge TLS cert' 'not present (install.ps1 will create)'
}

if (Get-NetFirewallRule -DisplayName 'QTP QB Bridge (REST in)' -ErrorAction SilentlyContinue) {
    Check-Warn 'Firewall rule' 'already exists — install.ps1 will replace'
} else {
    Check-Pass 'Firewall rule' 'not present (install.ps1 will create)'
}

# ─────────────────────────────────────────────────────────────
Write-Host "`n[6] DEPLOY PACKAGE (run from the extracted folder)" -ForegroundColor White

$exe = Join-Path $PSScriptRoot 'QBBridge.Service.exe'
$installScript = Join-Path $PSScriptRoot 'install.ps1'
$qwc = Join-Path $PSScriptRoot 'QTPBridge.qwc'

if (Test-Path $exe) {
    $size = (Get-Item $exe).Length
    if ($size -gt 50MB) { Check-Pass 'QBBridge.Service.exe' "$([math]::Round($size/1MB,0)) MB (self-contained .NET 8)" }
    else { Check-Warn 'QBBridge.Service.exe' "suspiciously small ($size bytes)" }
} else {
    Check-Fail 'QBBridge.Service.exe' 'not found next to preflight.ps1 — did you extract the ZIP?'
}

if (Test-Path $installScript) { Check-Pass 'install.ps1' } else { Check-Fail 'install.ps1' 'missing' }
if (Test-Path $qwc) { Check-Pass 'QTPBridge.qwc' } else { Check-Fail 'QTPBridge.qwc' 'missing' }

# ─────────────────────────────────────────────────────────────
Write-Host "`n═════════════════════════════════════════════════════════════" -ForegroundColor Cyan

if ($Failures.Count -eq 0 -and $Warnings.Count -eq 0) {
    Write-Host " ALL GREEN — ready to install." -ForegroundColor Green
    Write-Host "   Run: powershell -ExecutionPolicy Bypass -File .\install.ps1"
    exit 0
} elseif ($Failures.Count -eq 0) {
    Write-Host " READY with $($Warnings.Count) warning(s) — install will succeed." -ForegroundColor Yellow
    Write-Host "   Warnings:" -ForegroundColor Yellow
    $Warnings | ForEach-Object { Write-Host "     • $_" -ForegroundColor Yellow }
    Write-Host "   Run: powershell -ExecutionPolicy Bypass -File .\install.ps1"
    exit 0
} else {
    Write-Host " BLOCKED by $($Failures.Count) failure(s) — fix before running install.ps1." -ForegroundColor Red
    $Failures | ForEach-Object { Write-Host "     ✗ $_" -ForegroundColor Red }
    if ($Warnings.Count -gt 0) {
        Write-Host "   Also saw $($Warnings.Count) warning(s) (install.ps1 will handle):" -ForegroundColor Yellow
        $Warnings | ForEach-Object { Write-Host "     • $_" -ForegroundColor Yellow }
    }
    exit 1
}
