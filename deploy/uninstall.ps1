#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Full rollback of QTP QB Bridge from J-DC2. Reverses install.ps1 completely.
.DESCRIPTION
    Stops and removes the Windows service, deletes the binaries, removes the
    firewall rule, deletes the service account, removes machine env vars, and
    deletes the TLS cert. QBWC app registration must be removed manually from
    QBWC UI (File -> Remove application) since that's a user-data concern.
#>

[CmdletBinding()]
param(
    [string] $InstallRoot = 'D:\Services\QBBridge',
    [string] $ServiceName = 'QTPQBBridge',
    [string] $ServiceAccount = 'svc_qbbridge',
    [string] $LogDir = 'D:\Services\QBBridge\logs',
    [switch] $KeepLogs
)

$ErrorActionPreference = 'Continue'
function Write-Stage($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)    { Write-Host "    [ok] $m" -ForegroundColor Green }

Write-Stage "Stop + delete service $ServiceName"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Ok 'Service removed'
} else { Write-Ok 'Service not present' }

Write-Stage 'Remove firewall rule'
Get-NetFirewallRule -DisplayName 'QTP QB Bridge (REST in)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Ok 'Rule removed (or was not present)'

Write-Stage 'Remove TLS certificate'
Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq 'CN=qtp-qbbridge.j-dc2.local' } | ForEach-Object {
    Remove-Item $_.PSPath -Force
    Write-Ok "Deleted cert $($_.Thumbprint)"
}

Write-Stage 'Remove machine env vars'
@('QBBRIDGE_BEARER_TOKEN','QBWC_PASSWORD','INTIME_API_BASE_URL','INTIME_API_BEARER_TOKEN','QBBRIDGE_TLS_THUMBPRINT','QBBRIDGE_LOG_DIR') | ForEach-Object {
    [Environment]::SetEnvironmentVariable($_, $null, 'Machine')
    Write-Ok "Cleared $_"
}

Write-Stage "Remove binaries at $InstallRoot"
if (Test-Path $InstallRoot) {
    if ($KeepLogs -and (Test-Path $LogDir)) {
        Get-ChildItem $InstallRoot -Exclude (Split-Path $LogDir -Leaf) | Remove-Item -Recurse -Force
        Write-Ok "Binaries removed, logs kept at $LogDir"
    } else {
        Remove-Item $InstallRoot -Recurse -Force
        Write-Ok "Entire $InstallRoot removed"
    }
}

Write-Stage "Remove local user $ServiceAccount"
if (Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue) {
    Remove-LocalUser -Name $ServiceAccount
    Write-Ok "User removed"
} else { Write-Ok 'User not present' }

Write-Host "`n══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Uninstall complete.  MANUAL step still needed:" -ForegroundColor Cyan
Write-Host "   Open QBWC → select 'QTP QB Bridge for InTime' → File → Remove application"
Write-Host "   (This clears QBWC's own stored credentials for the app)"
Write-Host "══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
