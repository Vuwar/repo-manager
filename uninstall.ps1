<#
.SYNOPSIS
    Removes RepoManager for the current user: undoes every step of install.ps1.

.PARAMETER RemoveData  Also delete %LOCALAPPDATA%\RepoManager (registry of projects, state, logs).
.PARAMETER NoClaude    Leave the Claude Code configuration alone.
#>
[CmdletBinding()]
param(
    [switch]$RemoveData,
    [switch]$NoClaude
)

$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:LOCALAPPDATA 'RepoManager'
$bin = Join-Path $dataDir 'bin'
$appExe = Join-Path $bin 'RepoManager.exe'
$devmExe = Join-Path $bin 'devm.exe'
$taskName = 'RepoManager'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

Step 'Stopping RepoManager'
$infoPath = Join-Path $dataDir 'daemon.json'
if (Test-Path $infoPath) {
    try {
        $info = Get-Content $infoPath -Raw | ConvertFrom-Json
        $token = (Get-Content (Join-Path $dataDir 'token') -Raw).Trim()
        Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($info.port)/api/app/quit" -Headers @{ 'X-RepoManager-Token' = $token } -ContentType 'application/json' -Body '{}' -TimeoutSec 5 | Out-Null
        $proc = Get-Process -Id $info.pid -ErrorAction SilentlyContinue
        if ($proc -and -not $proc.WaitForExit(30000)) { Stop-Process -Id $info.pid -Force }
    } catch { }
}
Get-Process -Name RepoManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe } | Stop-Process -Force

if (-not $NoClaude -and (Test-Path $devmExe)) {
    Step 'Disconnecting Claude Code'
    & $devmExe claude-remove
}

Step 'Removing autostart'
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $taskName -ErrorAction SilentlyContinue

Step 'Removing Start Menu shortcut'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'RepoManager.lnk'
if (Test-Path $shortcut) { [IO.File]::Delete($shortcut) }

Step 'Removing bin folder from the user PATH'
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($userPath -split ';' | Where-Object { $_ -and $_ -ne $bin })
[Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'User')

Step 'Removing program files'
if (Test-Path $bin) { [IO.Directory]::Delete($bin, $true) }

if ($RemoveData -and (Test-Path $dataDir)) {
    Step "Removing data in $dataDir"
    [IO.Directory]::Delete($dataDir, $true)
}

Write-Host 'RepoManager removed.' -ForegroundColor Green
if (-not $RemoveData) { Write-Host "Project list, state and logs kept in $dataDir (use -RemoveData to delete)." }
