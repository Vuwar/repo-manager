<#
.SYNOPSIS
    Builds and installs RepoManager for the current user. Rerun to update in place.

.DESCRIPTION
    1. Builds the web UI and publishes RepoManager.exe and devm.exe (self-contained, single file).
    2. Stops a running RepoManager (its services stop and are restored on the next start).
    3. Copies both files to %LOCALAPPDATA%\RepoManager\bin and adds that folder to the user PATH.
    4. Creates a Start Menu shortcut and (unless -NoAutostart) starts RepoManager in the tray at login.
    5. Unless -NoClaude: registers the 'repomanager' MCP server, the PreToolUse hook and the CLAUDE.md block
       (devm claude-setup; settings.json is backed up first).
    6. Starts RepoManager.

.PARAMETER NoBuild     Use the existing files in artifacts\publish.
.PARAMETER NoAutostart Do not start RepoManager at login.
.PARAMETER NoClaude    Do not touch the Claude Code configuration.
.PARAMETER NoStart     Do not start RepoManager at the end.
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoAutostart,
    [switch]$NoClaude,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$publish = Join-Path $repo 'artifacts\publish'
$dataDir = Join-Path $env:LOCALAPPDATA 'RepoManager'
$bin = Join-Path $dataDir 'bin'
$appExe = Join-Path $bin 'RepoManager.exe'
$devmExe = Join-Path $bin 'devm.exe'
$taskName = 'RepoManager'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (-not $NoBuild) {
    Step 'Building web UI'
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'npm not found. Install Node.js or use -NoBuild.' }
    Push-Location (Join-Path $repo 'web')
    try {
        if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' } }
        npm run build; if ($LASTEXITCODE) { throw 'web build failed' }
    } finally { Pop-Location }

    Step 'Publishing RepoManager.exe and devm.exe'
    foreach ($proj in 'src\RepoManager.App\RepoManager.App.csproj', 'src\RepoManager.Cli\RepoManager.Cli.csproj') {
        dotnet publish (Join-Path $repo $proj) -c Release -p:PublishSingle=true -o $publish --nologo -v q
        if ($LASTEXITCODE) { throw "dotnet publish $proj failed" }
    }
}
foreach ($f in 'RepoManager.exe', 'devm.exe') {
    if (-not (Test-Path (Join-Path $publish $f))) { throw "$f not found in $publish (run without -NoBuild)" }
}

Step 'Stopping a running RepoManager'
$infoPath = Join-Path $dataDir 'daemon.json'
if (Test-Path $infoPath) {
    try {
        $info = Get-Content $infoPath -Raw | ConvertFrom-Json
        $token = (Get-Content (Join-Path $dataDir 'token') -Raw).Trim()
        Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($info.port)/api/app/quit" -Headers @{ 'X-RepoManager-Token' = $token } -ContentType 'application/json' -Body '{}' -TimeoutSec 5 | Out-Null
        $proc = Get-Process -Id $info.pid -ErrorAction SilentlyContinue
        if ($proc) {
            if (-not $proc.WaitForExit(30000)) { Stop-Process -Id $info.pid -Force }
        }
        Write-Host '    stopped'
    } catch {
        Write-Host "    not reachable ($($_.Exception.Message))"
    }
}
Get-Process -Name RepoManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe } | Stop-Process -Force

Step "Installing to $bin"
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item (Join-Path $publish 'RepoManager.exe') $appExe -Force
Copy-Item (Join-Path $publish 'devm.exe') $devmExe -Force

Step 'Adding bin folder to the user PATH'
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($userPath -split ';' | Where-Object { $_ })
if ($parts -notcontains $bin) {
    [Environment]::SetEnvironmentVariable('Path', (($parts + $bin) -join ';'), 'User')
    Write-Host '    added (open a new terminal to use devm)'
} else { Write-Host '    already present' }
$env:Path = "$env:Path;$bin"

Step 'Creating Start Menu shortcut'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'RepoManager.lnk'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = $appExe
$lnk.WorkingDirectory = $bin
$lnk.Description = 'Manage dev servers across your repos'
$lnk.IconLocation = "$appExe,0"
$lnk.Save()

if (-not $NoAutostart) {
    Step 'Registering autostart at login'
    try {
        $action = New-ScheduledTaskAction -Execute $appExe -Argument '--background' -WorkingDirectory $bin
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
        $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
        Write-Host '    Task Scheduler task registered'
    } catch {
        # Some machines block task registration without admin rights: fall back to the Run key.
        Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $taskName -Value "`"$appExe`" --background"
        Write-Host "    Task Scheduler not available ($($_.Exception.Message)); using the HKCU Run key"
    }
}

if (-not $NoClaude) {
    Step 'Connecting Claude Code (MCP server, hook, CLAUDE.md)'
    & $devmExe claude-setup --devm $devmExe
}

if (-not $NoStart) {
    Step 'Starting RepoManager'
    Start-Process $appExe -WorkingDirectory $bin
}

Write-Host ''
Write-Host 'RepoManager installed.' -ForegroundColor Green
Write-Host "  App:  Start Menu > RepoManager (or $appExe)"
Write-Host '  CLI:  devm help   (in a new terminal)'
Write-Host '  Add your repos in the app, or: devm scan C:\path\to\repos  then  devm add <path>'
