#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Label,
    [string]$Route,
    [double]$Settle = 20,
    [double]$SettleMax = 90,
    [double]$Measure = 10,
    [int]$Laps = 1,
    [int]$WarmupLaps = 1,
    [double]$Cooldown = 0,
    [int]$Port = 42425,
    [switch]$ReuseServer,
    [switch]$Watch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sandbox = Join-Path $repoRoot '.testdata'
$game = if ($env:VINTAGE_STORY) { $env:VINTAGE_STORY } else { Join-Path $env:APPDATA 'Vintagestory' }
$routePath = if ($Route) { [IO.Path]::GetFullPath($Route) } else { Join-Path $repoRoot 'bench/routes/vhsurvival.txt' }
$benchOut = Join-Path $sandbox 'bench'
$clientMods = Join-Path $sandbox 'Mods'
$serverData = Join-Path $sandbox 'server'
$serverMods = Join-Path $serverData 'Mods'
$clientPidFile = Join-Path $sandbox 'test-instance.pid'
$serverPidFile = Join-Path $serverData 'server.pid'

function Assert-UnderSandbox {
    param([string]$Path)
    $root = [IO.Path]::GetFullPath($sandbox).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem operation outside the test sandbox: $full"
    }
}

function Get-SandboxProcess {
    param([string]$PidFile)
    if (-not (Test-Path -LiteralPath $PidFile)) { return $null }
    $raw = (Get-Content -LiteralPath $PidFile -Raw).Trim()
    if ($raw -notmatch '^\d+$') { Remove-Item -LiteralPath $PidFile -Force; return $null }
    $record = Get-CimInstance Win32_Process -Filter "ProcessId=$raw" -ErrorAction SilentlyContinue
    if ($null -eq $record) { Remove-Item -LiteralPath $PidFile -Force; return $null }
    if (-not ([string]$record.CommandLine).Contains($sandbox, [StringComparison]::OrdinalIgnoreCase)) {
        throw "PID $raw is live but is not a Vintage Horizons sandbox process; refusing to signal it."
    }
    return Get-Process -Id ([int]$raw) -ErrorAction Stop
}

function Assert-NotRunning {
    foreach ($pidFile in @($clientPidFile, $serverPidFile)) {
        $process = Get-SandboxProcess $pidFile
        if ($null -ne $process) { throw "Sandbox process $($process.Id) is already running. Close it cleanly before another benchmark." }
    }
}

function Wait-ForText {
    param([string]$Path, [string]$Needle, [int]$TimeoutSeconds, [Diagnostics.Process]$Process)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $Path) {
            $text = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
            if ($text -and $text.Contains($Needle, [StringComparison]::Ordinal)) { return $true }
        }
        if ($Process.HasExited) { return $false }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Wait-ForFile {
    param([string]$Path, [int]$TimeoutSeconds, [Diagnostics.Process]$Process)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $Path -PathType Leaf) { return $true }
        if ($Process.HasExited) { return $false }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Close-ClientGracefully {
    param([Diagnostics.Process]$Process)
    if ($null -eq $Process -or $Process.HasExited) { return }
    $null = $Process.CloseMainWindow()
    if (-not $Process.WaitForExit(90000)) {
        Write-Warning "Client PID $($Process.Id) did not exit after its window was closed. It was NOT force-killed; the pidfile remains."
    }
}

foreach ($required in @(
    (Join-Path $game 'Vintagestory.dll'),
    (Join-Path $game 'VintagestoryServer.dll'),
    $routePath,
    (Join-Path $repoRoot 'VintageHorizons/bin/Debug/net10.0/Mods/vintagehorizons'),
    (Join-Path $repoRoot 'bench/VintageHorizonsBench/bin/Debug/net10.0/Mods/vintagehorizonsbench')
)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required benchmark input is missing: $required" }
}

$existingClient = Get-SandboxProcess $clientPidFile
if ($null -ne $existingClient) { throw "Sandbox client $($existingClient.Id) is already running." }
$server = Get-SandboxProcess $serverPidFile
if ($null -ne $server -and -not $ReuseServer) {
    throw "Sandbox server $($server.Id) is already running. Pass -ReuseServer only when intentionally recovering an interrupted benchmark."
}
New-Item -ItemType Directory -Path $sandbox, $benchOut, $serverData, (Join-Path $sandbox 'tmp'), (Join-Path $serverData 'tmp') -Force | Out-Null

foreach ($modsPath in @($clientMods)) {
    Assert-UnderSandbox $modsPath
    if (Test-Path -LiteralPath $modsPath) { Remove-Item -LiteralPath $modsPath -Recurse -Force }
    New-Item -ItemType Directory -Path $modsPath | Out-Null
}
if ($null -eq $server) {
    Assert-UnderSandbox $serverMods
    if (Test-Path -LiteralPath $serverMods) { Remove-Item -LiteralPath $serverMods -Recurse -Force }
    New-Item -ItemType Directory -Path $serverMods | Out-Null
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'VintageHorizons/bin/Debug/net10.0/Mods/vintagehorizons') -Destination $clientMods -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'bench/VintageHorizonsBench/bin/Debug/net10.0/Mods/vintagehorizonsbench') -Destination $clientMods -Recurse

$settingsPath = Join-Path $sandbox 'clientsettings.json'
$settings = if (Test-Path -LiteralPath $settingsPath) {
    Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable
} else { @{} }
if (-not $settings.ContainsKey('intSettings')) { $settings.intSettings = @{} }
if (-not $settings.ContainsKey('stringSettings')) { $settings.stringSettings = @{} }

# A brand-new dataPath has no session and stops at the login screen. Copy only the
# authentication fields from the user's normal profile into this gitignored sandbox;
# never print them and never alter the source profile. This is equivalent to logging in
# once inside the isolated client, without putting credentials through UI automation.
$realSettingsPath = Join-Path $env:APPDATA 'VintagestoryData/clientsettings.json'
if (Test-Path -LiteralPath $realSettingsPath) {
    $realSettings = Get-Content -LiteralPath $realSettingsPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($key in @('mptoken', 'playername', 'playeruid', 'sessionkey', 'sessionsignature', 'useremail')) {
        if ($realSettings.stringSettings.ContainsKey($key)) {
            $settings.stringSettings[$key] = $realSettings.stringSettings[$key]
        }
    }
}
$settings.intSettings.vsyncMode = if ($Watch) { 1 } else { 0 }
$settings.intSettings.maxFps = if ($Watch) { 60 } else { 0 }
$settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8

$done = Join-Path $benchOut "$Label.done"
$csv = Join-Path $benchOut "$Label.csv"
foreach ($artifact in @($done, $csv)) { if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact -Force } }

$serverOut = Join-Path $serverData 'console.log'
$serverErr = Join-Path $serverData 'console.err.log'
$clientOut = Join-Path $sandbox 'launch.log'
$clientErr = Join-Path $sandbox 'launch.err.log'
foreach ($log in @($clientOut, $clientErr)) {
    if (Test-Path -LiteralPath $log) { Move-Item -LiteralPath $log -Destination "$log.prev" -Force }
}

$serverArgs = @(
    'VintagestoryServer.dll',
    "--dataPath `"$serverData`"",
    "--withconfig=`"{ Port: $Port, VerifyPlayerAuth: false, WhitelistMode: 'off', AdvertiseServer: false, DefaultRoleCode: 'admin' }`""
)
if ($null -eq $server) {
    foreach ($log in @($serverOut, $serverErr)) {
        if (Test-Path -LiteralPath $log) { Move-Item -LiteralPath $log -Destination "$log.prev" -Force }
    }
    $server = Start-Process -FilePath 'dotnet' -ArgumentList $serverArgs -WorkingDirectory $game -PassThru `
        -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr -WindowStyle Hidden `
        -Environment @{ TEMP = (Join-Path $serverData 'tmp'); TMP = (Join-Path $serverData 'tmp') }
    Set-Content -LiteralPath $serverPidFile -Value $server.Id
    Write-Host "Test server started: PID $($server.Id), port $Port"

    if (-not (Wait-ForText $serverOut 'Dedicated Server now running' 180 $server)) {
        throw "Server did not become ready. Inspect $serverOut and $serverErr"
    }
} else {
    Write-Host "Reusing isolated test server: PID $($server.Id), port $Port"
}

$clientEnvironment = @{
    TEMP = (Join-Path $sandbox 'tmp')
    TMP = (Join-Path $sandbox 'tmp')
    VHBENCH_ROUTE = $routePath
    VHBENCH_LABEL = $Label
    VHBENCH_OUT = $benchOut
    VHBENCH_SETTLE = $Settle.ToString([Globalization.CultureInfo]::InvariantCulture)
    VHBENCH_SETTLE_MAX = ([Math]::Max($Settle, $SettleMax)).ToString([Globalization.CultureInfo]::InvariantCulture)
    VHBENCH_MEASURE = $Measure.ToString([Globalization.CultureInfo]::InvariantCulture)
    VHBENCH_LAPS = ([Math]::Max(1, $Laps)).ToString()
    VHBENCH_WARMUP_LAPS = ([Math]::Max(0, $WarmupLaps)).ToString()
    VHBENCH_COOLDOWN = ([Math]::Max(0, $Cooldown)).ToString([Globalization.CultureInfo]::InvariantCulture)
    VHBENCH_STOP_SERVER = '1'
    VINTAGEHORIZONS_STATS = '1'
    VINTAGEHORIZONS_AUTOUNPAUSE = '1'
}
$clientArgs = @(
    'Vintagestory.dll',
    "--dataPath `"$sandbox`"",
    "--addModPath `"$clientMods`"",
    '-c', "localhost:$Port"
)
$client = Start-Process -FilePath 'dotnet' -ArgumentList $clientArgs -WorkingDirectory $game -PassThru `
    -RedirectStandardOutput $clientOut -RedirectStandardError $clientErr -Environment $clientEnvironment
Set-Content -LiteralPath $clientPidFile -Value $client.Id
Write-Host "Test client started: PID $($client.Id), isolated data at $sandbox"

$waypoints = @(Get-Content -LiteralPath $routePath | Where-Object { $_ -notmatch '^\s*(#|$)' }).Count
$budget = [int]($waypoints * ([Math]::Max(1, $Laps) + [Math]::Max(0, $WarmupLaps)) * ([Math]::Max($Settle, $SettleMax) + $Measure + 15) + [Math]::Max(0, $Cooldown) + 180)

try {
    if (-not (Wait-ForFile $done $budget $client)) {
        throw "Benchmark did not finish within ${budget}s. Inspect $sandbox\Logs\client-main.log and $clientOut"
    }
    Write-Host "Benchmark complete: $csv"
    Get-Content -LiteralPath $csv
}
finally {
    Close-ClientGracefully $client
    if ($client.HasExited) { Remove-Item -LiteralPath $clientPidFile -Force -ErrorAction SilentlyContinue }

    if (-not $server.HasExited) {
        if (-not $server.WaitForExit(90000)) {
            Write-Warning "Server PID $($server.Id) did not exit after the benchmark's /stop. It was NOT force-killed; the pidfile remains."
        }
    }
    if ($server.HasExited) { Remove-Item -LiteralPath $serverPidFile -Force -ErrorAction SilentlyContinue }
}
