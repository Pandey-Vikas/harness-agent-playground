# Harness Agent Playground launcher
# Usage:
#   Right-click this file  ->  "Run with PowerShell"
#   Or from a terminal:    .\start.ps1
#                         .\start.ps1 -Setup   (force the setup wizard even if .env exists)

param(
    [switch]$Setup
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host ""
Write-Host "===== Harness Agent Playground launcher =====" -ForegroundColor Cyan
Write-Host ""

# 1. Prerequisite checks
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    Write-Host "Node.js was not found on PATH. Install Node.js 22+ from https://nodejs.org." -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}
$nodeVersion = (& node --version).Trim()
Write-Host "Node $nodeVersion  ($($node.Source))" -ForegroundColor Gray

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host ".NET SDK was not found on PATH. Install .NET 10 SDK from https://dot.net." -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}
$dotnetVersion = (& dotnet --version).Trim()
Write-Host ".NET SDK $dotnetVersion  ($($dotnet.Source))" -ForegroundColor Gray

# 2. Kill any leftover Harness processes — both port owners AND any stale
#    dotnet/node processes still starting up that haven't bound their port yet.
function Stop-HarnessProcesses {
    $killedAny = $false

    foreach ($p in @(3000, 3100, 5099)) {
        $conns = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
        foreach ($conn in $conns) {
            try {
                $proc = Get-Process -Id $conn.OwningProcess -ErrorAction Stop
                Write-Host "  Stopping PID $($proc.Id) ($($proc.ProcessName)) on port $p" -ForegroundColor Yellow
                Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue
                $killedAny = $true
            } catch { }
        }
    }

    # Also match by command line — catches processes that haven't bound their port yet
    # and any stale wizard/setup that survived a previous run.
    try {
        $procs = Get-CimInstance Win32_Process -Filter "Name = 'node.exe' OR Name = 'dotnet.exe'" -ErrorAction SilentlyContinue
        foreach ($p in $procs) {
            $cl = $p.CommandLine
            if (-not $cl) { continue }
            if ($cl -match 'HarnessAgentHost' -or
                $cl -match 'ui[\\/]server\.js' -or
                $cl -match 'ui[\\/]setup[\\/]setup-server\.js') {
                $preview = $cl.Substring(0, [Math]::Min(90, $cl.Length))
                Write-Host "  Stopping PID $($p.ProcessId) ($($p.Name)): $preview" -ForegroundColor Yellow
                Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
                $killedAny = $true
            }
        }
    } catch { }

    if ($killedAny) { Start-Sleep -Milliseconds 600 }
}

Write-Host "Cleaning up any stale Harness processes..." -ForegroundColor Cyan
Stop-HarnessProcesses

# 3. Setup wizard branch
function Test-EnvConfigured {
    $envPath = Join-Path $root '.env'
    if (-not (Test-Path $envPath)) { return $false }
    $lines = Get-Content -LiteralPath $envPath -ErrorAction SilentlyContinue | Where-Object { $_ -and -not $_.StartsWith('#') -and $_.Contains('=') }
    if (-not $lines) { return $false }
    $keys = $lines | ForEach-Object { ($_ -split '=', 2)[0].Trim() }
    return ($keys -contains 'FOUNDRY_PROJECT_ENDPOINT')
}

$launchWizard = $Setup -or (-not (Test-EnvConfigured))
if ($launchWizard) {
    if ($Setup) {
        Write-Host "-Setup flag passed. Launching setup wizard..." -ForegroundColor Yellow
    } else {
        Write-Host ".env missing FOUNDRY_PROJECT_ENDPOINT. Launching setup wizard first..." -ForegroundColor Yellow
    }
    Start-Job -ScriptBlock {
        param($url)
        for ($i = 0; $i -lt 30; $i++) {
            Start-Sleep -Milliseconds 500
            try {
                $ok = Test-NetConnection -ComputerName 'localhost' -Port 3100 -InformationLevel Quiet -WarningAction SilentlyContinue
                if ($ok) { Start-Process $url; return }
            } catch { }
        }
    } -ArgumentList 'http://localhost:3100/' | Out-Null

    Write-Host ""
    Write-Host "Setup wizard on http://localhost:3100/" -ForegroundColor Green
    Write-Host "Press Ctrl+C in this window to stop." -ForegroundColor Green
    Write-Host ""
    $env:AZURE_LOGIN_EXPERIENCE_V2 = 'off'
    & node --disable-warning=ExperimentalWarning ui/setup/setup-server.js
    exit 0
}

# 4. Load .env into the current session so both children see FOUNDRY_* vars
Get-Content -LiteralPath (Join-Path $root '.env') |
    Where-Object { $_ -and -not $_.StartsWith('#') -and $_.Contains('=') } |
    ForEach-Object {
        $pair = $_ -split '=', 2
        [Environment]::SetEnvironmentVariable($pair[0].Trim(), $pair[1].Trim(), 'Process')
    }

# 5. Launch the .NET Harness host in the background
Write-Host "Starting HarnessAgentHost (.NET) on http://127.0.0.1:5099/ ..." -ForegroundColor Cyan
$dotnetProc = Start-Process -FilePath 'dotnet' -ArgumentList 'run', '--project', 'agent/HarnessAgentHost', '-c', 'Release', '--no-launch-profile' -PassThru -NoNewWindow -WorkingDirectory $root

# 6. Wait for the .NET host to answer /health
$deadline = (Get-Date).AddSeconds(90)
$ready = $false
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -Uri 'http://127.0.0.1:5099/health' -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
        if ($r.StatusCode -eq 200) { $ready = $true; break }
    } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $ready) {
    Write-Host "HarnessAgentHost failed to become healthy in 90s. Check the console for errors." -ForegroundColor Yellow
} else {
    Write-Host "HarnessAgentHost is healthy." -ForegroundColor Green
}

# 7. Open the browser once the UI is up
Start-Job -ScriptBlock {
    param($url)
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $ok = Test-NetConnection -ComputerName 'localhost' -Port 3000 -InformationLevel Quiet -WarningAction SilentlyContinue
            if ($ok) { Start-Process $url; return }
        } catch { }
    }
} -ArgumentList 'http://localhost:3000/' | Out-Null

# 8. Start Node UI in the foreground. When Ctrl+C fires, kill the .NET child.
Write-Host ""
Write-Host "UI on http://localhost:3000/ (Ctrl+C to stop everything)" -ForegroundColor Green
Write-Host ""
try {
    & node --disable-warning=ExperimentalWarning --env-file-if-exists=.env ui/server.js
} finally {
    if ($dotnetProc -and -not $dotnetProc.HasExited) {
        Write-Host "Stopping HarnessAgentHost (PID $($dotnetProc.Id))..." -ForegroundColor Yellow
        Stop-Process -Id $dotnetProc.Id -Force -ErrorAction SilentlyContinue
    }
}
