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
    [switch]$ServerMod,
    [switch]$DisableStats,
    [string]$AutoCommand,
    [ValidateSet('Any', 'Warm', 'Cold')]
    [string]$ClientCache = 'Any',
    [ValidateSet('Any', 'Warm', 'Cold')]
    [string]$ServerCache = 'Any',
    [string]$ServerConfig,
    [string]$RequireServerText,
    [switch]$RequireGenerationComplete,
    [switch]$RequireMipConvergence,
    [switch]$RequireMipRecovery,
    [switch]$RequireNoPersistedMips,
    [switch]$InterruptWhenPersistedMip,
    [ValidateRange(10, 1800)]
    [int]$InterruptTimeout = 180,
    [ValidateRange(0, 256)]
    [int]$RequireAssistPeakInFlight = 0,
    [switch]$IntegratedSingleplayer,
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$WorldName = 'vhbench-integrated',
    [switch]$RequireLocalOfferRetry,
    [switch]$ReuseServer,
    [switch]$Watch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sandboxRoot = Join-Path $repoRoot '.testdata'
$sandbox = if ($IntegratedSingleplayer) { Join-Path $sandboxRoot 'integrated' } else { $sandboxRoot }
$game = if ($env:VINTAGE_STORY) { $env:VINTAGE_STORY } else { Join-Path $env:APPDATA 'Vintagestory' }
$routePath = if ($Route) { [IO.Path]::GetFullPath($Route) } else { Join-Path $repoRoot 'bench/routes/vhsurvival.txt' }
$serverConfigPath = if ($ServerConfig) { [IO.Path]::GetFullPath($ServerConfig) } else { $null }
$benchOut = Join-Path $sandbox 'bench'
$clientMods = Join-Path $sandbox 'Mods'
$serverData = Join-Path $sandbox 'server'
$serverMods = Join-Path $serverData 'Mods'
$clientPidFile = Join-Path $sandbox 'test-instance.pid'
$serverPidFile = Join-Path $serverData 'server.pid'
$clientCacheDir = Join-Path $sandbox 'ModData\vintagehorizons'
$serverCacheDir = if ($IntegratedSingleplayer) { $clientCacheDir } else {
    Join-Path $serverData 'ModData\vintagehorizons'
}
$clientMainLog = Join-Path $sandbox 'Logs\client-main.log'

function Assert-UnderSandbox {
    param([string]$Path)
    $root = [IO.Path]::GetFullPath($sandbox).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem operation outside the test sandbox: $full"
    }
}

function Assert-FreshAssembly {
    param(
        [string]$Assembly,
        [string]$ProjectFile,
        [string]$SourceDirectory
    )

    $artifact = Get-Item -LiteralPath $Assembly -ErrorAction Stop
    $inputs = @((Get-Item -LiteralPath $ProjectFile -ErrorAction Stop))
    $inputs += @(Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File -Filter '*.cs' -ErrorAction Stop)
    $newest = $inputs | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($newest.LastWriteTimeUtc -gt $artifact.LastWriteTimeUtc) {
        $relativeInput = [IO.Path]::GetRelativePath($repoRoot, $newest.FullName)
        $relativeArtifact = [IO.Path]::GetRelativePath($repoRoot, $artifact.FullName)
        throw "Benchmark assembly is stale: $relativeInput is newer than $relativeArtifact. Build Debug before running."
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

function Get-ClientCacheFiles {
    if (-not (Test-Path -LiteralPath $clientCacheDir -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $clientCacheDir -Filter '*.db' -File -ErrorAction Stop |
        Where-Object { -not $_.BaseName.EndsWith('-server', [StringComparison]::OrdinalIgnoreCase) })
}

function Get-ServerCacheFiles {
    if (-not (Test-Path -LiteralPath $serverCacheDir -PathType Container)) { return @() }
    $files = @(Get-ChildItem -LiteralPath $serverCacheDir -Filter '*.db' -File -ErrorAction Stop)
    if ($IntegratedSingleplayer) {
        return @($files | Where-Object {
            $_.BaseName.EndsWith('-server', [StringComparison]::OrdinalIgnoreCase)
        })
    }
    return $files
}

function Assert-RequestedClientCacheState {
    param([IO.FileInfo[]]$Files)

    if ($ClientCache -eq 'Warm' -and $Files.Count -eq 0) {
        throw "Warm-cache benchmark requested, but $clientCacheDir contains no .db files. Populate the isolated cache first."
    }
    if ($ClientCache -eq 'Cold' -and $Files.Count -ne 0) {
        throw "Cold-cache benchmark requested, but $clientCacheDir contains $($Files.Count) .db file(s). Move them to a recoverable sandbox archive first."
    }
}

function Assert-RequestedServerCacheState {
    param([object[]]$Files)

    if ($ServerCache -eq 'Warm' -and $Files.Count -eq 0) {
        throw "Warm server-cache benchmark requested, but $serverCacheDir contains no .db files. Populate the isolated server cache first."
    }
    if ($ServerCache -eq 'Cold' -and $Files.Count -ne 0) {
        throw "Cold server-cache benchmark requested, but $serverCacheDir contains $($Files.Count) .db file(s). Move them to a recoverable sandbox archive first."
    }
}

function Get-ReportedCachedSectionCount {
    if (-not (Test-Path -LiteralPath $clientMainLog -PathType Leaf)) { return $null }
    $logText = Get-Content -LiteralPath $clientMainLog -Raw -ErrorAction Stop
    $found = [regex]::Matches(
        $logText,
        'Level finalized\. LOD capture active \(render distance: [^,]+, (?<count>\d+) sections from cache')
    if ($found.Count -eq 0) { return $null }
    return [int64]$found[$found.Count - 1].Groups['count'].Value
}

function Get-ReportedPersistedMipObligations {
    if (-not (Test-Path -LiteralPath $clientMainLog -PathType Leaf)) { return $null }
    $logText = Get-Content -LiteralPath $clientMainLog -Raw -ErrorAction Stop
    $found = [regex]::Matches(
        $logText, '\[vintagehorizons\] LOD cache: [^\r\n]+ \((?<count>\d+) persisted mip obligations\)')
    if ($found.Count -eq 0) { return $null }
    return [int64]$found[$found.Count - 1].Groups['count'].Value
}

function Get-ReportedServerCachedSectionCount {
    if (-not (Test-Path -LiteralPath $serverOut -PathType Leaf)) { return $null }
    $logText = Get-Content -LiteralPath $serverOut -Raw -ErrorAction Stop
    $found = [regex]::Matches(
        $logText,
        'Server LOD capture active \((?<count>\d+) sections from cache\)')
    if ($found.Count -eq 0) { return $null }
    return [int64]$found[$found.Count - 1].Groups['count'].Value
}

function Get-CompletedGenerationRecord {
    if (-not (Test-Path -LiteralPath $serverOut -PathType Leaf)) { return $null }
    $line = @(Get-Content -LiteralPath $serverOut -ErrorAction Stop |
        Where-Object { $_.Contains('Generation finished around block ', [StringComparison]::Ordinal) } |
        Select-Object -Last 1)[0]
    if (-not $line) { return $null }

    $summaryPattern =
        'Generation finished around block (?<x>-?\d+),(?<z>-?\d+): (?<total>\d+) columns - ' +
        '(?<generated>\d+) generated, (?<loaded>\d+) loaded from the savegame, ' +
        '(?<frontier>\d+) skipped on the frontier, (?<height>\d+) without a height map, ' +
        '(?<timedOut>\d+) timed out'
    $summary = [regex]::Match($line, $summaryPattern)
    $verified = [regex]::Match($line,
        'Verified (?<preserved>\d+)/(?<checked>\d+) sampled absent positions still absent')
    if (-not $summary.Success -or -not $verified.Success) { return $null }

    return [ordered]@{
        line = $line
        centreBlockX = [int64]$summary.Groups['x'].Value
        centreBlockZ = [int64]$summary.Groups['z'].Value
        totalColumns = [int64]$summary.Groups['total'].Value
        generated = [int64]$summary.Groups['generated'].Value
        loaded = [int64]$summary.Groups['loaded'].Value
        skippedFrontier = [int64]$summary.Groups['frontier'].Value
        withoutHeightMap = [int64]$summary.Groups['height'].Value
        timedOut = [int64]$summary.Groups['timedOut'].Value
        preservedAbsent = [int64]$verified.Groups['preserved'].Value
        checkedAbsent = [int64]$verified.Groups['checked'].Value
    }
}

function Get-ClientAssistRecord {
    if (-not (Test-Path -LiteralPath $clientMainLog -PathType Leaf)) { return $null }
    $logText = Get-Content -LiteralPath $clientMainLog -Raw -ErrorAction Stop
    $assistPattern =
        'server assist: (?<offered>\d+) offered, (?<remote>\d+) remote-only, ' +
        '(?<wanted>\d+) wanted by view, (?<requested>\d+) requested, ' +
        '(?<received>\d+) received, (?<installed>\d+) installed, ' +
        '(?<inFlight>\d+) in flight, peak (?<peak>\d+), (?<declined>\d+) declined'
    $found = [regex]::Matches($logText, $assistPattern)
    if ($found.Count -eq 0) { return $null }

    $record = [ordered]@{
        offered = 0L
        remoteOnly = 0L
        wantedByView = 0L
        requested = 0L
        received = 0L
        installed = 0L
        inFlight = 0L
        peakInFlight = 0L
        declined = 0L
    }
    foreach ($match in $found) {
        $record.offered = [Math]::Max($record.offered, [int64]$match.Groups['offered'].Value)
        $record.remoteOnly = [Math]::Max($record.remoteOnly, [int64]$match.Groups['remote'].Value)
        $record.wantedByView = [Math]::Max($record.wantedByView, [int64]$match.Groups['wanted'].Value)
        $record.requested = [Math]::Max($record.requested, [int64]$match.Groups['requested'].Value)
        $record.received = [Math]::Max($record.received, [int64]$match.Groups['received'].Value)
        $record.installed = [Math]::Max($record.installed, [int64]$match.Groups['installed'].Value)
        $record.inFlight = [Math]::Max($record.inFlight, [int64]$match.Groups['inFlight'].Value)
        $record.peakInFlight = [Math]::Max($record.peakInFlight, [int64]$match.Groups['peak'].Value)
        $record.declined = [Math]::Max($record.declined, [int64]$match.Groups['declined'].Value)
    }
    return $record
}

function Get-ClientLocalOfferRecord {
    if (-not (Test-Path -LiteralPath $clientMainLog -PathType Leaf)) { return $null }
    $logText = Get-Content -LiteralPath $clientMainLog -Raw -ErrorAction Stop
    $pattern =
        'local sibling offers: (?<discovered>\d+) discovered, ' +
        '(?<misses>\d+) retryable misses, (?<accepted>\d+) accepted, ' +
        '(?<installed>\d+) installed, (?<remote>\d+) remote-only, (?<wanted>\d+) wanted'
    $found = [regex]::Matches($logText, $pattern)
    if ($found.Count -eq 0) { return $null }

    $last = $found[$found.Count - 1]
    return [ordered]@{
        discovered = [int64]$last.Groups['discovered'].Value
        retryableMisses = [int64]$last.Groups['misses'].Value
        accepted = [int64]$last.Groups['accepted'].Value
        installed = [int64]$last.Groups['installed'].Value
        remoteOnly = [int64]$last.Groups['remote'].Value
        wanted = [int64]$last.Groups['wanted'].Value
    }
}

function Get-ClientMipConvergenceRecord {
    if (-not (Test-Path -LiteralPath $clientMainLog -PathType Leaf)) { return $null }
    $logText = Get-Content -LiteralPath $clientMainLog -Raw -ErrorAction Stop

    $worldPattern =
        '(?<line>[^\r\n]*?, (?<pendingColumns>\d+) pending, worker: ' +
        '(?<pendingCaptures>\d+) captures / (?<pendingMeshes>\d+) meshes / ' +
        '(?<pendingMips>\d+) mips queued / (?<captureErrors>\d+)\+' +
        '(?<meshErrors>\d+)\+(?<mipErrors>\d+) errors, ' +
        '(?<awaitingMip>\d+) awaiting mip \((?<mipInFlight>\d+) in flight\), ' +
        '(?<renderDirty>\d+) render-dirty, (?<unsaved>\d+) unsaved)'
    $worldMatches = [regex]::Matches($logText, $worldPattern)
    if ($worldMatches.Count -eq 0) { return $null }
    $world = $worldMatches[$worldMatches.Count - 1]

    $captureMatches = [regex]::Matches(
        $logText,
        'capture publish budget: \d+ items/[\d.]+ MiB, (?<queued>\d+) queued/')
    $storageMatches = [regex]::Matches(
        $logText,
        'storage thread: (?<backlog>\d+) write backlog, \d+ written, ' +
        '(?<writeErrors>\d+) write errors, \d+ read, ' +
        '(?<loadsInFlight>\d+) async loads in flight, (?<readErrors>\d+) read errors')

    return [ordered]@{
        line = $world.Groups['line'].Value
        pendingColumns = [int64]$world.Groups['pendingColumns'].Value
        pendingCaptures = [int64]$world.Groups['pendingCaptures'].Value
        pendingMeshes = [int64]$world.Groups['pendingMeshes'].Value
        pendingMips = [int64]$world.Groups['pendingMips'].Value
        captureErrors = [int64]$world.Groups['captureErrors'].Value
        meshErrors = [int64]$world.Groups['meshErrors'].Value
        mipErrors = [int64]$world.Groups['mipErrors'].Value
        awaitingMip = [int64]$world.Groups['awaitingMip'].Value
        mipInFlight = [int64]$world.Groups['mipInFlight'].Value
        renderDirty = [int64]$world.Groups['renderDirty'].Value
        unsaved = [int64]$world.Groups['unsaved'].Value
        pendingCaptureResults = if ($captureMatches.Count -gt 0) {
            [int64]$captureMatches[$captureMatches.Count - 1].Groups['queued'].Value
        } else { $null }
        storageWriteBacklog = if ($storageMatches.Count -gt 0) {
            [int64]$storageMatches[$storageMatches.Count - 1].Groups['backlog'].Value
        } else { $null }
        storageWriteErrors = if ($storageMatches.Count -gt 0) {
            [int64]$storageMatches[$storageMatches.Count - 1].Groups['writeErrors'].Value
        } else { $null }
        asyncLoadsInFlight = if ($storageMatches.Count -gt 0) {
            [int64]$storageMatches[$storageMatches.Count - 1].Groups['loadsInFlight'].Value
        } else { $null }
        storageReadErrors = if ($storageMatches.Count -gt 0) {
            [int64]$storageMatches[$storageMatches.Count - 1].Groups['readErrors'].Value
        } else { $null }
    }
}

function Get-CacheRecord {
    param([string]$Directory)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $Directory -Filter '*.db' -File -ErrorAction Stop | ForEach-Object {
        [ordered]@{
            name = $_.Name
            bytes = $_.Length
            lastWriteUtc = $_.LastWriteTimeUtc.ToString('o')
        }
    })
}

function Get-LastLogLineContaining {
    param([string]$Path, [string]$Needle)
    if (-not $Needle -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return @(Get-Content -LiteralPath $Path -ErrorAction Stop |
        Where-Object { $_.Contains($Needle, [StringComparison]::Ordinal) } | Select-Object -Last 1)[0]
}

if ($IntegratedSingleplayer -and $ServerMod) {
    throw '-ServerMod is implicit in -IntegratedSingleplayer; do not pass both.'
}
if ($IntegratedSingleplayer -and $ReuseServer) {
    throw '-ReuseServer is only for a separate dedicated server, not integrated singleplayer.'
}
if ($serverConfigPath -and -not ($ServerMod -or $IntegratedSingleplayer)) {
    throw '-ServerConfig requires -ServerMod or -IntegratedSingleplayer so the pinned settings have a consumer.'
}
if ($ServerCache -ne 'Any' -and -not ($ServerMod -or $IntegratedSingleplayer)) {
    throw '-ServerCache requires a modded dedicated or integrated server.'
}
if ($RequireGenerationComplete -and -not ($ServerMod -or $IntegratedSingleplayer)) {
    throw '-RequireGenerationComplete requires a modded dedicated or integrated server.'
}
if ($RequireMipConvergence -and $DisableStats) {
    throw '-RequireMipConvergence requires stats so the final pipeline state is observable.'
}
if ($RequireMipConvergence -and $Cooldown -lt 30) {
    throw '-RequireMipConvergence requires at least 30 seconds of cooldown for a final stats sample.'
}
if ($RequireMipRecovery -and -not $RequireMipConvergence) {
    throw '-RequireMipRecovery requires -RequireMipConvergence.'
}
if ($RequireNoPersistedMips -and -not $RequireMipConvergence) {
    throw '-RequireNoPersistedMips requires -RequireMipConvergence.'
}
if ($RequireMipRecovery -and $RequireNoPersistedMips) {
    throw '-RequireMipRecovery and -RequireNoPersistedMips are mutually exclusive.'
}
if ($InterruptWhenPersistedMip -and $RequireMipConvergence) {
    throw '-InterruptWhenPersistedMip is the pre-restart crash phase and cannot also require final convergence.'
}
if ($InterruptWhenPersistedMip -and $ReuseServer) {
    throw '-InterruptWhenPersistedMip must start the isolated server that the recovery run will explicitly reuse.'
}
if ($RequireAssistPeakInFlight -gt 0 -and -not $ServerMod) {
    throw '-RequireAssistPeakInFlight requires -ServerMod.'
}
if ($RequireLocalOfferRetry -and -not $IntegratedSingleplayer) {
    throw '-RequireLocalOfferRetry requires -IntegratedSingleplayer.'
}

$requiredInputs = @(
    (Join-Path $game 'Vintagestory.dll'),
    (Join-Path $game 'VintagestoryServer.dll'),
    $routePath,
    (Join-Path $repoRoot 'VintageHorizons/bin/Debug/net10.0/Mods/vintagehorizons'),
    (Join-Path $repoRoot 'bench/VintageHorizonsBench/bin/Debug/net10.0/Mods/vintagehorizonsbench')
)
if ($serverConfigPath) { $requiredInputs += $serverConfigPath }
foreach ($required in $requiredInputs) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required benchmark input is missing: $required" }
}
Assert-FreshAssembly `
    (Join-Path $repoRoot 'VintageHorizons/bin/Debug/net10.0/Mods/vintagehorizons/VintageHorizons.dll') `
    (Join-Path $repoRoot 'VintageHorizons/VintageHorizons.csproj') `
    (Join-Path $repoRoot 'VintageHorizons/src')
Assert-FreshAssembly `
    (Join-Path $repoRoot 'bench/VintageHorizonsBench/bin/Debug/net10.0/Mods/vintagehorizonsbench/VintageHorizonsBench.dll') `
    (Join-Path $repoRoot 'bench/VintageHorizonsBench/VintageHorizonsBench.csproj') `
    (Join-Path $repoRoot 'bench/VintageHorizonsBench/src')
$prelaunchCacheFiles = @(Get-ClientCacheFiles)
Assert-RequestedClientCacheState $prelaunchCacheFiles
$prelaunchCacheRecord = @($prelaunchCacheFiles | ForEach-Object {
    [ordered]@{
        name = $_.Name
        bytes = $_.Length
        lastWriteUtc = $_.LastWriteTimeUtc.ToString('o')
    }
})
$prelaunchServerCacheFiles = @(Get-ServerCacheFiles)
$prelaunchServerCacheRecord = @($prelaunchServerCacheFiles | ForEach-Object {
    [ordered]@{
        name = $_.Name
        bytes = $_.Length
        lastWriteUtc = $_.LastWriteTimeUtc.ToString('o')
    }
})
Assert-RequestedServerCacheState $prelaunchServerCacheRecord
if ($RequireMipRecovery) {
    if ($prelaunchCacheFiles.Count -ne 1) {
        throw "Mip recovery requires exactly one prelaunch client cache, found $($prelaunchCacheFiles.Count)."
    }
}

$existingClient = Get-SandboxProcess $clientPidFile
if ($null -ne $existingClient) { throw "Sandbox client $($existingClient.Id) is already running." }
$server = Get-SandboxProcess $serverPidFile
if ($IntegratedSingleplayer -and $null -ne $server) {
    throw "Integrated sandbox has an unexpected separate server process $($server.Id)."
}
if ($null -ne $server -and -not $ReuseServer) {
    throw "Sandbox server $($server.Id) is already running. Pass -ReuseServer only when intentionally recovering an interrupted benchmark."
}
if ($null -ne $server -and $serverConfigPath) {
    throw 'A pinned -ServerConfig cannot be applied to a reused server. Start a fresh isolated server.'
}
New-Item -ItemType Directory -Path $sandbox, $benchOut, $serverData, (Join-Path $sandbox 'tmp'), (Join-Path $serverData 'tmp') -Force | Out-Null

foreach ($modsPath in @($clientMods)) {
    Assert-UnderSandbox $modsPath
    if (Test-Path -LiteralPath $modsPath) { Remove-Item -LiteralPath $modsPath -Recurse -Force }
    New-Item -ItemType Directory -Path $modsPath | Out-Null
}
if (-not $IntegratedSingleplayer -and $null -eq $server) {
    Assert-UnderSandbox $serverMods
    if (Test-Path -LiteralPath $serverMods) { Remove-Item -LiteralPath $serverMods -Recurse -Force }
    New-Item -ItemType Directory -Path $serverMods | Out-Null
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'VintageHorizons/bin/Debug/net10.0/Mods/vintagehorizons') -Destination $clientMods -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'bench/VintageHorizonsBench/bin/Debug/net10.0/Mods/vintagehorizonsbench') -Destination $clientMods -Recurse
if (-not $IntegratedSingleplayer -and $null -eq $server -and $ServerMod) {
    Copy-Item -LiteralPath (Join-Path $repoRoot 'VintageHorizons/bin/Debug/net10.0/Mods/vintagehorizons') -Destination $serverMods -Recurse
}
if ($serverConfigPath) {
    $serverConfigDir = if ($IntegratedSingleplayer) {
        Join-Path $sandbox 'ModConfig'
    } else { Join-Path $serverData 'ModConfig' }
    $serverConfigTarget = Join-Path $serverConfigDir 'vintagehorizons-server.json'
    Assert-UnderSandbox $serverConfigTarget
    New-Item -ItemType Directory -Path $serverConfigDir -Force | Out-Null
    Copy-Item -LiteralPath $serverConfigPath -Destination $serverConfigTarget -Force
}

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
$scenario = Join-Path $benchOut "$Label-scenario.json"
$mipInterruptMarker = Join-Path $benchOut "$Label-mip-persisted"
$mipInterruptRelease = Join-Path $benchOut "$Label-mip-release"
$localOfferMissMarker = Join-Path $benchOut "$Label-local-offer-miss"
$localOfferInstallMarker = Join-Path $benchOut "$Label-local-offer-installed"
foreach ($artifact in @(
    $done, $csv, $scenario, $mipInterruptMarker, $mipInterruptRelease,
    $localOfferMissMarker, $localOfferInstallMarker)) {
    if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact -Force }
}

$serverOut = if ($IntegratedSingleplayer) {
    Join-Path $sandbox 'Logs\server-main.log'
} else { Join-Path $serverData 'console.log' }
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
$statsEnabled = if ($DisableStats) { '0' } else { '1' }
if (-not $IntegratedSingleplayer -and $null -eq $server) {
    foreach ($log in @($serverOut, $serverErr)) {
        if (Test-Path -LiteralPath $log) { Move-Item -LiteralPath $log -Destination "$log.prev" -Force }
    }
    $server = Start-Process -FilePath 'dotnet' -ArgumentList $serverArgs -WorkingDirectory $game -PassThru `
        -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr -WindowStyle Hidden `
        -Environment @{
            TEMP = (Join-Path $serverData 'tmp')
            TMP = (Join-Path $serverData 'tmp')
            VINTAGEHORIZONS_STATS = $statsEnabled
        }
    Set-Content -LiteralPath $serverPidFile -Value $server.Id
    Write-Host "Test server started: PID $($server.Id), port $Port"

    if (-not (Wait-ForText $serverOut 'Dedicated Server now running' 180 $server)) {
        throw "Server did not become ready. Inspect $serverOut and $serverErr"
    }
} elseif (-not $IntegratedSingleplayer) {
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
    VHBENCH_STOP_SERVER = if ($IntegratedSingleplayer) { '0' } else { '1' }
    VINTAGEHORIZONS_STATS = $statsEnabled
    VINTAGEHORIZONS_AUTOUNPAUSE = '1'
}
if ($AutoCommand) {
    $clientEnvironment.VINTAGEHORIZONS_AUTOCMD = $AutoCommand
    $clientEnvironment.VINTAGEHORIZONS_CREATIVE = '1'
}
if ($InterruptWhenPersistedMip) {
    $clientEnvironment.VINTAGEHORIZONS_INTERRUPT_MIP_MARKER = $mipInterruptMarker
    $clientEnvironment.VINTAGEHORIZONS_INTERRUPT_MIP_RELEASE = $mipInterruptRelease
}
if ($RequireLocalOfferRetry) {
    $clientEnvironment.VINTAGEHORIZONS_TEST_LOCAL_OFFER_MISS_MARKER = $localOfferMissMarker
    $clientEnvironment.VINTAGEHORIZONS_TEST_LOCAL_OFFER_INSTALL_MARKER = $localOfferInstallMarker
}
$clientArgs = @(
    'Vintagestory.dll',
    "--dataPath `"$sandbox`"",
    "--addModPath `"$clientMods`""
)
if ($IntegratedSingleplayer) {
    $clientArgs += @('-o', $WorldName, '-p', 'preset-surviveandbuild')
} else {
    $clientArgs += @('-c', "localhost:$Port")
}
$client = Start-Process -FilePath 'dotnet' -ArgumentList $clientArgs -WorkingDirectory $game -PassThru `
    -RedirectStandardOutput $clientOut -RedirectStandardError $clientErr -Environment $clientEnvironment
Set-Content -LiteralPath $clientPidFile -Value $client.Id
if ($IntegratedSingleplayer) {
    Write-Host "Integrated test client started: PID $($client.Id), world $WorldName, isolated data at $sandbox"
} else {
    Write-Host "Test client started: PID $($client.Id), isolated data at $sandbox"
}

$waypoints = @(Get-Content -LiteralPath $routePath | Where-Object { $_ -notmatch '^\s*(#|$)' }).Count
$startupBudget = if ($IntegratedSingleplayer) { 360 } else { 180 }
$budget = [int]($waypoints * ([Math]::Max(1, $Laps) + [Math]::Max(0, $WarmupLaps)) * ([Math]::Max($Settle, $SettleMax) + $Measure + 15) + [Math]::Max(0, $Cooldown) + $startupBudget)

try {
    if ($InterruptWhenPersistedMip) {
        $deadline = [DateTime]::UtcNow.AddSeconds($InterruptTimeout)
        while (-not (Test-Path -LiteralPath $mipInterruptMarker -PathType Leaf)) {
            if ($client.HasExited) { throw 'Client exited before a persisted mip obligation was observed.' }
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "No persisted mip obligation appeared within $InterruptTimeout seconds."
            }
            Start-Sleep -Milliseconds 100
        }

        $observedMipWrite = (Get-Content -LiteralPath $mipInterruptMarker -Raw).Trim()
        $verifiedClient = Get-SandboxProcess $clientPidFile
        if ($null -eq $verifiedClient -or $verifiedClient.Id -ne $client.Id) {
            throw 'The client PID no longer resolves to the launched sandbox process; refusing interruption.'
        }

        # This is the one deliberate non-graceful path: interruption recovery cannot be
        # established by CloseMainWindow. The exact PID has just been checked against the
        # private sandbox command line, and no process-name search is used.
        $verifiedClient.Kill()
        if (-not $verifiedClient.WaitForExit(30000)) {
            throw "Sandbox client PID $($verifiedClient.Id) did not exit after deliberate interruption."
        }
        Set-Content -LiteralPath $mipInterruptRelease -Value 'client interrupted' -Encoding ascii
        Remove-Item -LiteralPath $clientPidFile -Force -ErrorAction SilentlyContinue

        $scenarioRecord = [ordered]@{
            label = $Label
            route = [IO.Path]::GetRelativePath($repoRoot, $routePath).Replace('\', '/')
            intentionalClientInterruption = $true
            interruptionTrigger = 'persisted ApplyToParent row'
            persistedMipWrite = $observedMipWrite
            durableObligationRetained = $true
            integratedSingleplayer = [bool]$IntegratedSingleplayer
            worldName = if ($IntegratedSingleplayer) { $WorldName } else { $null }
            prelaunchClientCacheFiles = $prelaunchCacheRecord
            prelaunchServerCacheFiles = $prelaunchServerCacheRecord
            interruptedUtc = [DateTime]::UtcNow.ToString('o')
        }
        $scenarioRecord | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $scenario -Encoding utf8

        Write-Host 'Sandbox client interrupted after a durable mip obligation was written.'
        Write-Host "Scenario proof: $scenario"
        if ($IntegratedSingleplayer) {
            Write-Host 'The integrated process stopped; recovery must reopen the same isolated world.'
        } else {
            Write-Host 'The isolated server remains running for the required -ReuseServer recovery phase.'
        }
        return
    }

    if (-not (Wait-ForFile $done $budget $client)) {
        throw "Benchmark did not finish within ${budget}s. Inspect $sandbox\Logs\client-main.log and $clientOut"
    }

    $reportedCachedSections = Get-ReportedCachedSectionCount
    $reportedServerCachedSections = Get-ReportedServerCachedSectionCount
    $reportedServerLine = Get-LastLogLineContaining $serverOut $RequireServerText
    $generationRecord = Get-CompletedGenerationRecord
    $assistRecord = Get-ClientAssistRecord
    $localOfferRecord = Get-ClientLocalOfferRecord
    $mipConvergenceRecord = Get-ClientMipConvergenceRecord
    $reportedPersistedMipObligations = Get-ReportedPersistedMipObligations
    $scenarioRecord = [ordered]@{
        label = $Label
        route = [IO.Path]::GetRelativePath($repoRoot, $routePath).Replace('\', '/')
        integratedSingleplayer = [bool]$IntegratedSingleplayer
        worldName = if ($IntegratedSingleplayer) { $WorldName } else { $null }
        clientCacheRequirement = $ClientCache
        serverCacheRequirement = $ServerCache
        prelaunchClientCacheFiles = $prelaunchCacheRecord
        prelaunchServerCacheFiles = $prelaunchServerCacheRecord
        reportedSectionsFromCache = $reportedCachedSections
        reportedServerSectionsFromCache = $reportedServerCachedSections
        serverConfig = if ($serverConfigPath) {
            [IO.Path]::GetRelativePath($repoRoot, $serverConfigPath).Replace('\', '/')
        } else { $null }
        requiredServerText = if ($RequireServerText) { $RequireServerText } else { $null }
        reportedServerLine = $reportedServerLine
        requireGenerationComplete = [bool]$RequireGenerationComplete
        generation = $generationRecord
        requireMipConvergence = [bool]$RequireMipConvergence
        mipConvergence = $mipConvergenceRecord
        requireMipRecovery = [bool]$RequireMipRecovery
        requireNoPersistedMips = [bool]$RequireNoPersistedMips
        persistedMipObligationsLoaded = $reportedPersistedMipObligations
        requiredAssistPeakInFlight = $RequireAssistPeakInFlight
        assist = $assistRecord
        requireLocalOfferRetry = [bool]$RequireLocalOfferRetry
        localOffers = $localOfferRecord
        localOfferMissKey = if (Test-Path -LiteralPath $localOfferMissMarker) {
            (Get-Content -LiteralPath $localOfferMissMarker -Raw).Trim()
        } else { $null }
        localOfferInstalledKey = if (Test-Path -LiteralPath $localOfferInstallMarker) {
            (Get-Content -LiteralPath $localOfferInstallMarker -Raw).Trim()
        } else { $null }
        settleSeconds = $Settle
        settleMaxSeconds = [Math]::Max($Settle, $SettleMax)
        measureSeconds = $Measure
        measuredLaps = [Math]::Max(1, $Laps)
        warmupLaps = [Math]::Max(0, $WarmupLaps)
        cooldownSeconds = [Math]::Max(0, $Cooldown)
        serverMod = [bool]$ServerMod
        statsEnabled = -not [bool]$DisableStats
        completedUtc = [DateTime]::UtcNow.ToString('o')
    }
    $scenarioRecord | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $scenario -Encoding utf8

    if ($ClientCache -ne 'Any' -and $null -eq $reportedCachedSections) {
        throw "The client log did not report Vintage Horizons' join-time cache count; cache-state proof failed. Inspect $clientMainLog"
    }
    if ($ClientCache -eq 'Warm' -and $reportedCachedSections -le 0) {
        throw "Warm-cache benchmark requested, but the active world reported 0 sections from cache. See $scenario"
    }
    if ($ClientCache -eq 'Cold' -and $reportedCachedSections -ne 0) {
        throw "Cold-cache benchmark requested, but the active world reported $reportedCachedSections sections from cache. See $scenario"
    }
    if ($ServerCache -ne 'Any' -and $null -eq $reportedServerCachedSections) {
        throw "The server log did not report Vintage Horizons' active cache count; server cache-state proof failed. See $scenario"
    }
    if ($ServerCache -eq 'Warm' -and $reportedServerCachedSections -le 0) {
        throw "Warm server-cache benchmark requested, but the active world reported 0 sections from cache. See $scenario"
    }
    if ($ServerCache -eq 'Cold' -and $reportedServerCachedSections -ne 0) {
        throw "Cold server-cache benchmark requested, but the active world reported $reportedServerCachedSections sections from cache. See $scenario"
    }
    if ($RequireServerText -and $null -eq $reportedServerLine) {
        throw "The server log did not contain the required completion text '$RequireServerText'. See $scenario and $serverOut"
    }
    if ($RequireGenerationComplete) {
        if ($null -eq $generationRecord) {
            throw "The server log did not contain a parseable generation completion record. See $scenario and $serverOut"
        }
        if ($generationRecord.generated -le 0) {
            throw "Generation completed without transiently generating any absent columns. See $scenario"
        }
        if ($generationRecord.timedOut -ne 0 -or $generationRecord.withoutHeightMap -ne 0) {
            throw "Generation completed with timeouts or unusable height maps. See $scenario"
        }
        if ($generationRecord.checkedAbsent -le 0 -or
            $generationRecord.preservedAbsent -ne $generationRecord.checkedAbsent) {
            throw "Generation did not prove that sampled absent positions remained absent. See $scenario"
        }
    }
    if ($RequireMipConvergence) {
        if ($null -eq $mipConvergenceRecord -or
            $null -eq $mipConvergenceRecord.pendingCaptureResults -or
            $null -eq $mipConvergenceRecord.storageWriteBacklog) {
            throw "The client log did not contain a complete pipeline-convergence record. See $scenario and $clientMainLog"
        }

        $notConverged = @(
            $mipConvergenceRecord.pendingColumns,
            $mipConvergenceRecord.pendingCaptures,
            $mipConvergenceRecord.pendingCaptureResults,
            $mipConvergenceRecord.captureErrors,
            $mipConvergenceRecord.meshErrors,
            $mipConvergenceRecord.pendingMips,
            $mipConvergenceRecord.mipErrors,
            $mipConvergenceRecord.awaitingMip,
            $mipConvergenceRecord.mipInFlight,
            $mipConvergenceRecord.unsaved,
            $mipConvergenceRecord.storageWriteBacklog,
            $mipConvergenceRecord.storageWriteErrors,
            $mipConvergenceRecord.asyncLoadsInFlight,
            $mipConvergenceRecord.storageReadErrors
        ) | Where-Object { $_ -ne 0 }

        if ($notConverged.Count -gt 0) {
            throw "The final sampled client pipeline did not converge cleanly. See $scenario and $clientMainLog"
        }
    }
    if ($RequireMipRecovery -and
        ($null -eq $reportedPersistedMipObligations -or $reportedPersistedMipObligations -le 0)) {
        throw "Mip recovery did not load a persisted ApplyToParent obligation. See $scenario and $clientMainLog"
    }
    if ($RequireNoPersistedMips -and
        ($null -eq $reportedPersistedMipObligations -or $reportedPersistedMipObligations -ne 0)) {
        throw "The fresh process still loaded a persisted ApplyToParent obligation. See $scenario and $clientMainLog"
    }
    if ($RequireAssistPeakInFlight -gt 0) {
        if ($null -eq $assistRecord) {
            throw "The client log did not contain parseable live-assist transfer telemetry. See $scenario and $clientMainLog"
        }
        if ($assistRecord.peakInFlight -lt $RequireAssistPeakInFlight) {
            throw "Live assist peaked at $($assistRecord.peakInFlight) in-flight sections, below the required $RequireAssistPeakInFlight. See $scenario"
        }
        if ($assistRecord.received -le 0 -or $assistRecord.installed -le 0) {
            throw "Live assist reached the request guard but did not receive and install a section. See $scenario"
        }
    }
    if ($RequireLocalOfferRetry) {
        if ($null -eq $localOfferRecord) {
            throw "The client log did not contain parseable local sibling-offer telemetry. See $scenario and $clientMainLog"
        }
        $localOfferComplete = $localOfferRecord.discovered -gt 0 -and
            $localOfferRecord.retryableMisses -gt 0 -and
            $localOfferRecord.accepted -gt 0 -and
            $localOfferRecord.installed -gt 0
        if (-not $localOfferComplete) {
            throw "Sibling-cache discovery, retry, acceptance, and installation did not all occur. See $scenario"
        }
        $missMarkerExists = Test-Path -LiteralPath $localOfferMissMarker -PathType Leaf
        $installMarkerExists = Test-Path -LiteralPath $localOfferInstallMarker -PathType Leaf
        if (-not $missMarkerExists -or -not $installMarkerExists) {
            throw "The sibling-cache retry markers are incomplete. See $scenario"
        }
        $missedKey = (Get-Content -LiteralPath $localOfferMissMarker -Raw).Trim()
        $installedKey = (Get-Content -LiteralPath $localOfferInstallMarker -Raw).Trim()
        if (-not $missedKey -or $missedKey -ne $installedKey) {
            throw "The sibling-cache key that missed was not the exact key later installed. See $scenario"
        }
    }

    Write-Host "Benchmark complete: $csv"
    Write-Host "Scenario proof: $scenario"
    Get-Content -LiteralPath $csv
}
finally {
    if ($InterruptWhenPersistedMip -and -not (Test-Path -LiteralPath $mipInterruptRelease)) {
        Set-Content -LiteralPath $mipInterruptRelease -Value 'runner cleanup' -Encoding ascii
    }
    Close-ClientGracefully $client
    if ($client.HasExited) { Remove-Item -LiteralPath $clientPidFile -Force -ErrorAction SilentlyContinue }

    if ($null -ne $server -and -not $InterruptWhenPersistedMip -and -not $server.HasExited) {
        if (-not $server.WaitForExit(90000)) {
            Write-Warning "Server PID $($server.Id) did not exit after the benchmark's /stop. It was NOT force-killed; the pidfile remains."
        }
    }
    if ($null -ne $server -and $server.HasExited) {
        Remove-Item -LiteralPath $serverPidFile -Force -ErrorAction SilentlyContinue
    }
}
