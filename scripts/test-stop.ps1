#Requires -Version 5.1

<#
.SYNOPSIS
Stop sandbox test instances on Windows, by pidfile, after proving the process is ours.

.DESCRIPTION
scripts/test-stop.sh cannot do this on Windows: it identifies a process through
/proc/<pid>/cmdline, which does not exist here, so it refuses to signal anything and
clears the pidfile instead - leaving the instance running. A benchmark whose runner is
interrupted leaves exactly that behind, and a dedicated server holds well over a gigabyte
while it waits for a client that will never reconnect.

The two hard rules from the shell script still apply and are enforced below:
  - Never match on process name or argument pattern. The user runs their own game
    concurrently, and a name match has burned this project before.
  - Never trust a pidfile on liveness alone. A crashed instance leaves its pidfile behind
    and Windows recycles PIDs, so the recycled PID could be the user's own game. The
    command line must contain the sandbox data path before any signal is sent.

Termination is not graceful: a dedicated server exposes no window to close and its
in-game /stop needs a connected client. The sandbox world is disposable test data, which
is the trade this script makes deliberately. Prefer letting the benchmark runner finish,
because it sends /stop through the client it already owns.
#>

[CmdletBinding()]
param(
    [ValidateSet('client', 'server', 'all')]
    [string]$Target = 'all'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sandbox = Join-Path $repoRoot '.testdata'
$failed = 0

function Stop-One {
    param([string]$Label, [string]$PidFile)

    if (-not (Test-Path -LiteralPath $PidFile)) {
        Write-Host "${Label}: no pidfile, nothing to stop"
        return
    }

    $raw = (Get-Content -LiteralPath $PidFile -Raw).Trim()
    if ($raw -notmatch '^\d+$') {
        Write-Host "${Label}: pidfile is not a pid; clearing it"
        Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
        return
    }

    $record = Get-CimInstance Win32_Process -Filter "ProcessId=$raw" -ErrorAction SilentlyContinue
    if ($null -eq $record) {
        Write-Host "${Label}: pid $raw already gone"
        Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
        return
    }

    $commandLine = [string]$record.CommandLine
    if (-not $commandLine.ToLowerInvariant().Contains($sandbox.ToLowerInvariant())) {
        Write-Warning "${Label}: pid $raw is live but is NOT a sandbox process (recycled pid?) - refusing to signal it; clearing stale pidfile"
        Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
        return
    }

    Stop-Process -Id ([int]$raw) -Force -ErrorAction Stop
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-CimInstance Win32_Process -Filter "ProcessId=$raw" -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }

    if (Get-CimInstance Win32_Process -Filter "ProcessId=$raw" -ErrorAction SilentlyContinue) {
        Write-Warning "${Label}: pid $raw did not exit after 10s; pidfile kept, check it manually"
        $script:failed = 1
        return
    }

    Write-Host "${Label}: pid $raw stopped"
    Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
}

# A run that dies before writing its pidfile, or after clearing it, leaves a live instance
# that nothing points at. Identity here is the sandbox data path itself, which the user's
# own game can never contain, so this stays inside the never-match-by-name rule.
function Stop-Orphans {
    $found = 0
    $names = @('dotnet.exe', 'Vintagestory.exe', 'VintagestoryServer.exe')
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $names -contains $_.Name } |
        Where-Object { ([string]$_.CommandLine).ToLowerInvariant().Contains($sandbox.ToLowerInvariant()) } |
        ForEach-Object {
            $found++
            Write-Host "orphan $($_.Name) pid $($_.ProcessId): stopping"
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
    if ($found -eq 0) { Write-Host 'no orphaned sandbox instances' }
}

switch ($Target) {
    'client' { Stop-One 'test client' (Join-Path $sandbox 'test-instance.pid') }
    'server' { Stop-One 'test server' (Join-Path $sandbox 'server\server.pid') }
    'orphans' { Stop-Orphans }
    'all' {
        Stop-One 'test client' (Join-Path $sandbox 'test-instance.pid')
        Stop-One 'test server' (Join-Path $sandbox 'server\server.pid')
        Stop-One 'integrated client' (Join-Path $sandbox 'integrated\test-instance.pid')
        Stop-Orphans
    }
}

exit $failed
