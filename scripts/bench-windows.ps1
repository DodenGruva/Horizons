#Requires -Version 7.0

[CmdletBinding()]
param(
    # The bench mod rewrites anything outside [A-Za-z0-9_-] to '_' when it names its own
    # output, so a label containing a dot makes this script wait five minutes for a done
    # file that was written under a different name. Refuse it in the first second instead.
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
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
    [switch]$GpuStats,
    [string]$GpuRenderer,
    [string]$GpuArena,
    [string]$GpuArenaMb,
    [string]$GpuArenaPageMb,
    # Draw opaque cached terrain from the regional arenas. Pinned for a whole run rather
    # than typed in game, because the phase gate is a controlled A/B and a comparison whose
    # switches were set by hand is how session 40 lost a run.
    [ValidateSet('0', '1')]
    [string]$GpuIndirect,
    # Pull twelve-byte opaque quad records in the vertex shader. Keep indirect drawing on
    # in both halves of the A/B so this isolates packing rather than batching.
    [ValidateSet('0', '1')]
    [string]$GpuPacked,
    # Phase 9 only: force one named GPU boundary to fail once so the fallback can be
    # observed in the isolated client. This is recorded in scenario.json with the run.
    [ValidateSet('arena', 'shader', 'depth-copy', 'draw')]
    [string]$GpuFailure,
    # Split packed section geometry into a 4x4 grid of independently culled commands.
    [ValidateSet('0', '1')]
    [string]$GpuClusters,
    # Build the private depth pyramid every frame. It hides nothing, so an A/B over this
    # measures the mechanism's cost alone - which is exactly the Phase 4 gate.
    [ValidateSet('0', '1')]
    [string]$Hzb,
    [string]$AutoCommand,
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$SandboxProfile,
    [string]$SeedSave,
    [string]$SeedClientCache,
    [string]$SeedServerCache,
    [switch]$SeedOnly,
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
    [switch]$RequireReadinessConvergence,
    [switch]$ChunkMask,
    # 25 ms is the plan's own renderer-hitch rule. A tighter maximum fails on a single
    # blocking IsChunkRendered call, which the tracker cannot preempt, so the steady-state
    # gate is p99 instead: a 2026-08-18 route measured 20.5 us average and 75 us p99.
    [ValidateRange(1, 60000)]
    [int]$ReadinessMaxPhaseMicroseconds = 25000,
    [ValidateRange(1, 60000)]
    [int]$ReadinessMaxP99Microseconds = 500,
    [ValidateRange(1, 100000)]
    [int]$ReadinessMaxWorkAgeFrames = 900,
    [switch]$ReuseServer,
    [switch]$Watch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sandboxBase = Join-Path $repoRoot '.testdata'
$sandboxRoot = if ($SandboxProfile) {
    Join-Path $sandboxBase (Join-Path 'profiles' $SandboxProfile)
} else { $sandboxBase }
$sandbox = if ($IntegratedSingleplayer) { Join-Path $sandboxRoot 'integrated' } else { $sandboxRoot }
$game = if ($env:VINTAGE_STORY) { $env:VINTAGE_STORY } else { Join-Path $env:APPDATA 'Vintagestory' }
$routePath = if ($Route) { [IO.Path]::GetFullPath($Route) } else { Join-Path $repoRoot 'bench/routes/vhsurvival.txt' }
$serverConfigPath = if ($ServerConfig) { [IO.Path]::GetFullPath($ServerConfig) } else { $null }
$benchOut = Join-Path $sandbox 'bench'
$clientMods = Join-Path $sandbox 'Mods'
$serverData = Join-Path $sandbox 'server'
$serverMods = Join-Path $serverData 'Mods'
$seedDir = Join-Path $sandboxRoot 'seed'
$clientPidFile = Join-Path $sandbox 'test-instance.pid'
$serverPidFile = Join-Path $serverData 'server.pid'
$clientCacheDir = Join-Path $sandbox 'ModData\vintagehorizons'
$serverCacheDir = if ($IntegratedSingleplayer) { $clientCacheDir } else {
    Join-Path $serverData 'ModData\vintagehorizons'
}
$clientMainLog = Join-Path $sandbox 'Logs\client-main.log'
$seedManifest = Join-Path $benchOut 'seed-profile.json'
$seedSavePath = if ($SeedSave) { [IO.Path]::GetFullPath($SeedSave) } else { $null }
$seedClientCachePath = if ($SeedClientCache) {
    [IO.Path]::GetFullPath($SeedClientCache)
} else { $null }
$seedServerCachePath = if ($SeedServerCache) {
    [IO.Path]::GetFullPath($SeedServerCache)
} else { $null }

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

function Initialize-SeedFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Target
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Seed source does not exist: $Source"
    }
    $sourceWal = Get-Item -LiteralPath "$Source-wal" -ErrorAction SilentlyContinue
    if ($null -ne $sourceWal -and $sourceWal.Length -gt 0) {
        throw "Seed source has a non-empty SQLite WAL and cannot be copied consistently: $Source-wal"
    }
    Assert-UnderSandbox $Target
    $sourceFile = Get-Item -LiteralPath $Source -ErrorAction Stop
    if (Test-Path -LiteralPath $Target -PathType Leaf) {
        $targetFile = Get-Item -LiteralPath $Target -ErrorAction Stop
        if (($targetFile.Length -ne $sourceFile.Length) -or ($targetFile.LastWriteTimeUtc -ne $sourceFile.LastWriteTimeUtc)) {
            throw "Frozen seed target differs from its source; archive the sandbox profile before reseeding: $Target"
        }
    } else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $Target) -Force | Out-Null
        Copy-Item -LiteralPath $Source -Destination $Target
        $targetFile = Get-Item -LiteralPath $Target -ErrorAction Stop
        if ($targetFile.Length -ne $sourceFile.Length) {
            throw "Seed copy length mismatch for $Target"
        }
    }

    return [ordered]@{
        name = $sourceFile.Name
        bytes = $sourceFile.Length
        lastWriteUtc = $sourceFile.LastWriteTimeUtc.ToString('o')
        sandboxTarget = [IO.Path]::GetRelativePath($repoRoot, $Target).Replace('\', '/')
    }
}

function Restore-FrozenSeedFile {
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][string]$Target
    )

    $relativeSource = ([string]$Record.sandboxTarget).Replace('/', '\')
    $source = [IO.Path]::GetFullPath((Join-Path $repoRoot $relativeSource))
    Assert-UnderSandbox $source
    Assert-UnderSandbox $Target
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Frozen seed input is missing: $source"
    }
    $sourceFile = Get-Item -LiteralPath $source -ErrorAction Stop
    if ($sourceFile.Length -ne [int64]$Record.bytes) {
        throw "Frozen seed input length changed: $source"
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $Target) -Force | Out-Null
    # These are exact working-copy sidecars below the validated profile root. A previous
    # run may leave them behind after graceful shutdown; never pair them with a restored
    # main database from an earlier point in time.
    Remove-Item -LiteralPath "$Target-wal", "$Target-shm" -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $source -Destination $Target -Force
    if ((Get-Item -LiteralPath $Target -ErrorAction Stop).Length -ne $sourceFile.Length) {
        throw "Restored working-copy length mismatch for $Target"
    }
}

function Get-SandboxProcess {
    param([string]$PidFile)
    if (-not (Test-Path -LiteralPath $PidFile)) { return $null }
    $raw = (Get-Content -LiteralPath $PidFile -Raw).Trim()
    if ($raw -notmatch '^\d+$') { Remove-Item -LiteralPath $PidFile -Force; return $null }
    $record = Get-CimInstance Win32_Process -Filter "ProcessId=$raw" -ErrorAction SilentlyContinue
    if ($null -eq $record) { Remove-Item -LiteralPath $PidFile -Force; return $null }
    if (([string]$record.CommandLine).IndexOf(
            $sandbox, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
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
            if ($text -and ([string]$text).IndexOf(
                    $Needle, [StringComparison]::Ordinal) -ge 0) { return $true }
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
        Where-Object { ([string]$_).IndexOf(
            'Generation finished around block ', [StringComparison]::Ordinal) -ge 0 } |
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

function Get-ClientReadinessRecord {
    if (-not (Test-Path -LiteralPath $clientMainLog -PathType Leaf)) { return $null }
    $logText = Get-Content -LiteralPath $clientMainLog -Raw -ErrorAction Stop

    # The tracker prints one shadow line per stats interval and resets its interval
    # counters afterwards, so rates are summed and states are read from the last sample.
    $disabled = [regex]::Matches($logText,
        'vanilla readiness: shadow (?<reason>disabled|not initialized)')
    $shadowPattern =
        'vanilla readiness: shadow (?<width>\d+)x(?<vertical>\d+)x(?<depth>\d+), ' +
        '(?<ready>\d+) ready/(?<observed>\d+) observed/(?<pending>\d+) pending/' +
        '(?<unknown>\d+) unknown, (?<candidateQueue>\d+) probes/' +
        '(?<observationQueue>\d+) observations queued, oldest work (?<age>\d+) frames, ' +
        '(?<kib>[\d.,]+) KiB arrays, interval (?<probes>\d+) probes ' +
        '\((?<trueResults>\d+) true/(?<falseResults>\d+) false\), ' +
        '(?<readyTransitions>\d+) ready/(?<lostTransitions>\d+) lost transitions, ' +
        '(?<errors>\d+) errors, (?<windowChanges>\d+) window changes/(?<resizes>\d+) resizes, ' +
        '(?<fullRevalidations>\d+) full revalidations, ' +
        '(?<staleCommitted>\d+) stale committed found/(?<countRepairs>\d+) count repairs, ' +
        '(?<emptyChunks>\d+) drawn-but-empty/' +
        '(?<noGeometryChunks>\d+) drawn-without-geometry chunks, ' +
        'owned without geometry (?<ownedNoGeometry>\d+) at ' +
        '(?<ownedNoGeometryNear>-?[\d.,]+)-(?<ownedNoGeometryFar>-?[\d.,]+) blocks ' +
        'per Y (?<ownedNoGeometryPerY>[\d/]+), ' +
        '(?<ownershipDenied>\d+) ownership denied no-geometry/' +
        '(?<ownershipDeniedRange>\d+) denied beyond view distance/' +
        '(?<maskResyncs>\d+) mask resyncs, ' +
        'events (?<accepted>\d+) accepted/(?<coalesced>\d+) coalesced/(?<dropped>\d+) dropped, ' +
        'sweeps (?<sweepsAccepted>\d+) accepted/(?<sweepsCoalesced>\d+) coalesced, ' +
        'columns (?<columnsTracked>\d+) tracked/(?<columnsFull>\d+) full/(?<columnsPartial>\d+) partial, ' +
        'max (?<maxReadyPerColumn>\d+)/(?<verticalCells>\d+) ready per column, ' +
        'ready per Y (?<readyPerY>[\d/]+), ' +
        'nearest incomplete (?<nearestIncomplete>\d+) blocks/unready (?<nearestUnready>\d+) blocks, ' +
        'handoff (?<handoff>\d+) blocks \((?<handoffSource>[^,]+), radial (?<radialHandoff>\d+)\), ' +
        'mask (?<mask>[^,]+), (?<ownedSkipped>\d+) owned draws skipped'
    $found = [regex]::Matches($logText, $shadowPattern)
    if ($found.Count -eq 0) {
        if ($disabled.Count -eq 0) { return $null }
        return [ordered]@{
            samples = 0L
            disabledSamples = [int64]$disabled.Count
            disabledReason = $disabled[$disabled.Count - 1].Groups['reason'].Value
        }
    }

    $last = $found[$found.Count - 1]
    $record = [ordered]@{
        samples = [int64]$found.Count
        disabledSamples = [int64]$disabled.Count
        disabledReason = if ($disabled.Count -gt 0) {
            $disabled[$disabled.Count - 1].Groups['reason'].Value
        } else { $null }
        activeWidth = [int64]$last.Groups['width'].Value
        activeVerticalChunks = [int64]$last.Groups['vertical'].Value
        activeDepth = [int64]$last.Groups['depth'].Value
        trackedKiB = [double]$last.Groups['kib'].Value
        committedReadyCells = [int64]$last.Groups['ready'].Value
        observedCells = [int64]$last.Groups['observed'].Value
        pendingCells = [int64]$last.Groups['pending'].Value
        unknownCells = [int64]$last.Groups['unknown'].Value
        columnsTracked = [int64]$last.Groups['columnsTracked'].Value
        fullyReadyColumns = [int64]$last.Groups['columnsFull'].Value
        partiallyReadyColumns = [int64]$last.Groups['columnsPartial'].Value
        maxReadyPerColumn = [int64]$last.Groups['maxReadyPerColumn'].Value
        verticalCellsPerColumn = [int64]$last.Groups['verticalCells'].Value
        readyCellsPerY = $last.Groups['readyPerY'].Value
        nearestIncompleteBlocks = [int64]$last.Groups['nearestIncomplete'].Value
        nearestUnreadyBlocks = [int64]$last.Groups['nearestUnready'].Value
        radialHandoffBlocks = [int64]$last.Groups['radialHandoff'].Value
        handoffBlocks = [int64]$last.Groups['handoff'].Value
        handoffSource = $last.Groups['handoffSource'].Value
        maskState = $last.Groups['mask'].Value
        maxOwnedDrawsSkipped = 0L
        maskFailedSamples = 0L
        maskOffSamples = 0L
        radialFallbackSamples = 0L
        minHandoffBlocks = [int64]::MaxValue
        maxHandoffBlocks = 0L
        minNearestIncompleteBlocks = [int64]::MaxValue
        maxNearestIncompleteBlocks = 0L
        peakFullyReadyColumns = 0L
        probes = 0L
        trueResults = 0L
        falseResults = 0L
        readyTransitions = 0L
        lostTransitions = 0L
        probeErrors = 0L
        windowChanges = 0L
        resizes = 0L
        eventsAccepted = 0L
        eventsCoalesced = 0L
        eventsDropped = 0L
        sweepCandidates = 0L
        maxCandidateQueue = 0L
        maxObservationQueue = 0L
        maxOldestWorkFrames = 0L
        confirmationOnlyIntervals = 0L
    }
    foreach ($match in $found) {
        $record.probes += [int64]$match.Groups['probes'].Value
        $record.trueResults += [int64]$match.Groups['trueResults'].Value
        $record.falseResults += [int64]$match.Groups['falseResults'].Value
        $record.readyTransitions += [int64]$match.Groups['readyTransitions'].Value
        $record.lostTransitions += [int64]$match.Groups['lostTransitions'].Value
        $record.probeErrors += [int64]$match.Groups['errors'].Value
        $record.windowChanges += [int64]$match.Groups['windowChanges'].Value
        $record.resizes += [int64]$match.Groups['resizes'].Value
        $record.eventsAccepted += [int64]$match.Groups['accepted'].Value
        $record.eventsCoalesced += [int64]$match.Groups['coalesced'].Value
        $record.eventsDropped += [int64]$match.Groups['dropped'].Value
        $record.sweepCandidates += [int64]$match.Groups['sweepsAccepted'].Value
        $record.maxCandidateQueue = [Math]::Max(
            $record.maxCandidateQueue, [int64]$match.Groups['candidateQueue'].Value)
        $record.maxObservationQueue = [Math]::Max(
            $record.maxObservationQueue, [int64]$match.Groups['observationQueue'].Value)
        $record.maxOldestWorkFrames = [Math]::Max(
            $record.maxOldestWorkFrames, [int64]$match.Groups['age'].Value)
        if ($match.Groups['handoffSource'].Value -ne 'readiness') { $record.radialFallbackSamples++ }
        if ($match.Groups['mask'].Value -eq 'failed') { $record.maskFailedSamples++ }
        $record.maxOwnedDrawsSkipped = [Math]::Max(
            $record.maxOwnedDrawsSkipped, [int64]$match.Groups['ownedSkipped'].Value)
        if ($match.Groups['mask'].Value -eq 'off') { $record.maskOffSamples++ }
        $record.minHandoffBlocks = [Math]::Min(
            $record.minHandoffBlocks, [int64]$match.Groups['handoff'].Value)
        $record.maxHandoffBlocks = [Math]::Max(
            $record.maxHandoffBlocks, [int64]$match.Groups['handoff'].Value)
        $record.minNearestIncompleteBlocks = [Math]::Min(
            $record.minNearestIncompleteBlocks, [int64]$match.Groups['nearestIncomplete'].Value)
        $record.maxNearestIncompleteBlocks = [Math]::Max(
            $record.maxNearestIncompleteBlocks, [int64]$match.Groups['nearestIncomplete'].Value)
        $record.peakFullyReadyColumns = [Math]::Max(
            $record.peakFullyReadyColumns, [int64]$match.Groups['columnsFull'].Value)
        # An interval that probed heavily, returned nothing but true, and still held
        # pending cells spent its whole budget re-confirming ownership it already had.
        # That is the ready-only maintenance defect found on 2026-08-18.
        if ([int64]$match.Groups['probes'].Value -gt 1000 -and
            [int64]$match.Groups['falseResults'].Value -eq 0 -and
            [int64]$match.Groups['pending'].Value -gt 0) {
            $record.confirmationOnlyIntervals++
        }
    }

    # Phase cost and allocation come from the surrounding render telemetry lines. They are
    # formatted with the client's own culture, so plain casts parse them the same way.
    $phase = [regex]::Matches($logText, 'readiness shadow (?<avg>[\d.,]+)/(?<max>[\d.,]+) \|')
    foreach ($match in $phase) {
        $record.avgPhaseMicroseconds = [Math]::Max(
            [double]($record.avgPhaseMicroseconds), [double]$match.Groups['avg'].Value)
        $record.maxPhaseMicroseconds = [Math]::Max(
            [double]($record.maxPhaseMicroseconds), [double]$match.Groups['max'].Value)
    }
    $percentile = [regex]::Matches($logText,
        'render p95/p99/max us:[^\r\n]*? readiness (?<p95>\d+)/(?<p99>\d+)/(?<max>\d+) \|')
    foreach ($match in $percentile) {
        $record.p95PhaseMicroseconds = [Math]::Max(
            [int64]($record.p95PhaseMicroseconds), [int64]$match.Groups['p95'].Value)
        $record.p99PhaseMicroseconds = [Math]::Max(
            [int64]($record.p99PhaseMicroseconds), [int64]$match.Groups['p99'].Value)
    }
    $allocation = [regex]::Matches($logText,
        'render allocation interval MiB/max KiB:[^\r\n]*? readiness (?<mib>[\d.,]+)/(?<kib>[\d.,]+) \|')
    foreach ($match in $allocation) {
        $record.maxIntervalAllocatedMiB = [Math]::Max(
            [double]($record.maxIntervalAllocatedMiB), [double]$match.Groups['mib'].Value)
        $record.maxCallAllocatedKiB = [Math]::Max(
            [double]($record.maxCallAllocatedKiB), [double]$match.Groups['kib'].Value)
    }
    return $record
}

function Get-ClientGpuRenderRecord {
    if (-not (Test-Path -LiteralPath $clientMainLog -PathType Leaf)) { return $null }
    $lines = @(Get-Content -LiteralPath $clientMainLog -ErrorAction Stop)
    $probeLine = @($lines | Where-Object {
        ([string]$_).IndexOf('[VintageHorizons] GPU probe:', [StringComparison]::Ordinal) -ge 0
    } | Select-Object -Last 1)[0]

    $timerPattern =
        'delayed GPU pass p95/p99/max us: opaque (?<opaqueP95>\d+)/(?<opaqueP99>\d+)/(?<opaqueMax>\d+) ' +
        'over (?<opaqueSamples>\d+) samples \| ' +
        'split near (?<splitNearP95>\d+)/(?<splitNearP99>\d+)/(?<splitNearMax>\d+) ' +
        'over (?<splitNearSamples>\d+) \| ' +
        'split far (?<splitFarP95>\d+)/(?<splitFarP99>\d+)/(?<splitFarMax>\d+) ' +
        'over (?<splitFarSamples>\d+) \| ' +
        'water (?<waterP95>\d+)/(?<waterP99>\d+)/(?<waterMax>\d+) ' +
        'over (?<waterSamples>\d+); (?<pending>\d+) pending, (?<skips>\d+) ring-full skips, ' +
        '(?<conflicts>\d+) time-query target conflicts, timing (?<state>active|inactive)'
    $timerSamples = @()
    foreach ($line in $lines) {
        $match = [regex]::Match($line, $timerPattern)
        if (-not $match.Success) { continue }
        $timerSamples += [ordered]@{
            line = $line
            opaqueP95Microseconds = [int64]$match.Groups['opaqueP95'].Value
            opaqueP99Microseconds = [int64]$match.Groups['opaqueP99'].Value
            opaqueMaxMicroseconds = [int64]$match.Groups['opaqueMax'].Value
            opaqueSamples = [int64]$match.Groups['opaqueSamples'].Value
            splitNearP95Microseconds = [int64]$match.Groups['splitNearP95'].Value
            splitNearP99Microseconds = [int64]$match.Groups['splitNearP99'].Value
            splitNearMaxMicroseconds = [int64]$match.Groups['splitNearMax'].Value
            splitNearSamples = [int64]$match.Groups['splitNearSamples'].Value
            splitFarP95Microseconds = [int64]$match.Groups['splitFarP95'].Value
            splitFarP99Microseconds = [int64]$match.Groups['splitFarP99'].Value
            splitFarMaxMicroseconds = [int64]$match.Groups['splitFarMax'].Value
            splitFarSamples = [int64]$match.Groups['splitFarSamples'].Value
            waterP95Microseconds = [int64]$match.Groups['waterP95'].Value
            waterP99Microseconds = [int64]$match.Groups['waterP99'].Value
            waterMaxMicroseconds = [int64]$match.Groups['waterMax'].Value
            waterSamples = [int64]$match.Groups['waterSamples'].Value
            pending = [int64]$match.Groups['pending'].Value
            ringFullSkips = [int64]$match.Groups['skips'].Value
            targetConflicts = [int64]$match.Groups['conflicts'].Value
            timingActive = $match.Groups['state'].Value -eq 'active'
        }
    }

    $cullTimerPattern =
        'delayed GPU cull p95/p99/max us: ordinary ' +
        '(?<ordinaryP95>\d+)/(?<ordinaryP99>\d+)/(?<ordinaryMax>\d+) ' +
        'over (?<ordinarySamples>\d+) samples \| ' +
        'split near (?<splitNearP95>\d+)/(?<splitNearP99>\d+)/(?<splitNearMax>\d+) ' +
        'over (?<splitNearSamples>\d+) \| ' +
        'split far (?<splitFarP95>\d+)/(?<splitFarP99>\d+)/(?<splitFarMax>\d+) ' +
        'over (?<splitFarSamples>\d+)'
    $cullTimerSamples = @()
    foreach ($line in $lines) {
        $match = [regex]::Match($line, $cullTimerPattern)
        if (-not $match.Success) { continue }
        $cullTimerSamples += [ordered]@{
            line = $line
            ordinaryCullP95Microseconds = [int64]$match.Groups['ordinaryP95'].Value
            ordinaryCullP99Microseconds = [int64]$match.Groups['ordinaryP99'].Value
            ordinaryCullMaxMicroseconds = [int64]$match.Groups['ordinaryMax'].Value
            ordinaryCullSamples = [int64]$match.Groups['ordinarySamples'].Value
            splitNearCullP95Microseconds = [int64]$match.Groups['splitNearP95'].Value
            splitNearCullP99Microseconds = [int64]$match.Groups['splitNearP99'].Value
            splitNearCullMaxMicroseconds = [int64]$match.Groups['splitNearMax'].Value
            splitNearCullSamples = [int64]$match.Groups['splitNearSamples'].Value
            splitFarCullP95Microseconds = [int64]$match.Groups['splitFarP95'].Value
            splitFarCullP99Microseconds = [int64]$match.Groups['splitFarP99'].Value
            splitFarCullMaxMicroseconds = [int64]$match.Groups['splitFarMax'].Value
            splitFarCullSamples = [int64]$match.Groups['splitFarSamples'].Value
        }
    }

    $uploadPattern =
        'render gpu calls p95/p99/max us: upload (?<uploadP95>\d+)/(?<uploadP99>\d+)/(?<uploadMax>\d+) \| ' +
        'dispose (?<disposeP95>\d+)/(?<disposeP99>\d+)/(?<disposeMax>\d+)'
    $uploadSamples = @()
    foreach ($line in $lines) {
        $match = [regex]::Match($line, $uploadPattern)
        if (-not $match.Success) { continue }
        $uploadSamples += [ordered]@{
            line = $line
            uploadP95Microseconds = [int64]$match.Groups['uploadP95'].Value
            uploadP99Microseconds = [int64]$match.Groups['uploadP99'].Value
            uploadMaxMicroseconds = [int64]$match.Groups['uploadMax'].Value
            disposeP95Microseconds = [int64]$match.Groups['disposeP95'].Value
            disposeP99Microseconds = [int64]$match.Groups['disposeP99'].Value
            disposeMaxMicroseconds = [int64]$match.Groups['disposeMax'].Value
        }
    }

    $arenaPattern =
        'gpu arena shadow: (?:retaining (?<retention>[^;]+); )?sections (?<sections>\d+) live[^|]*\| ' +
        'vertices (?<vertexLive>[\d.,]+)/(?<vertexCapacity>[\d.,]+) MiB[^|]*\| ' +
        'indices (?<indexLive>[\d.,]+)/(?<indexCapacity>[\d.,]+) MiB[^|]*\| ' +
        'packed (?<packedLive>[\d.,]+)/(?<packedCapacity>[\d.,]+) MiB[^|]*\| ' +
        'clusters (?<clusterLive>[\d.,]+)/(?<clusterCapacity>[\d.,]+) MiB'
    $arenaSamples = @()
    foreach ($line in $lines) {
        $match = [regex]::Match($line, $arenaPattern)
        if (-not $match.Success) { continue }
        $arenaSamples += [ordered]@{
            line = $line
            retention = if ($match.Groups['retention'].Success) {
                $match.Groups['retention'].Value
            } else { $null }
            liveSections = [int64]$match.Groups['sections'].Value
            vertexLiveMiB = [double]::Parse(
                $match.Groups['vertexLive'].Value.Replace(',', '.'),
                [Globalization.CultureInfo]::InvariantCulture)
            vertexCapacityMiB = [double]::Parse(
                $match.Groups['vertexCapacity'].Value.Replace(',', '.'),
                [Globalization.CultureInfo]::InvariantCulture)
            indexLiveMiB = [double]::Parse(
                $match.Groups['indexLive'].Value.Replace(',', '.'),
                [Globalization.CultureInfo]::InvariantCulture)
            indexCapacityMiB = [double]::Parse(
                $match.Groups['indexCapacity'].Value.Replace(',', '.'),
                [Globalization.CultureInfo]::InvariantCulture)
            packedLiveMiB = [double]::Parse(
                $match.Groups['packedLive'].Value.Replace(',', '.'),
                [Globalization.CultureInfo]::InvariantCulture)
            packedCapacityMiB = [double]::Parse(
                $match.Groups['packedCapacity'].Value.Replace(',', '.'),
                [Globalization.CultureInfo]::InvariantCulture)
            clusterLiveMiB = [double]::Parse(
                $match.Groups['clusterLive'].Value.Replace(',', '.'),
                [Globalization.CultureInfo]::InvariantCulture)
            clusterCapacityMiB = [double]::Parse(
                $match.Groups['clusterCapacity'].Value.Replace(',', '.'),
                [Globalization.CultureInfo]::InvariantCulture)
        }
    }

    $drawPattern =
        'render draw interval: opaque (?<opaqueCalls>\d+) calls, ' +
        '(?<opaqueVertices>\d+) vertices/(?<opaqueIndices>\d+) indices \| ' +
        'water (?<waterCalls>\d+) calls, (?<waterVertices>\d+) vertices/' +
        '(?<waterIndices>\d+) indices \| live geometry (?<liveMiB>[\d.,]+) MiB, ' +
        'opaque (?<liveOpaqueVertices>\d+)/(?<liveOpaqueIndices>\d+), ' +
        'water (?<liveWaterVertices>\d+)/(?<liveWaterIndices>\d+)'
    $drawSamples = @()
    foreach ($line in $lines) {
        $match = [regex]::Match($line, $drawPattern)
        if (-not $match.Success) { continue }
        $drawSamples += [ordered]@{
            line = $line
            opaqueCalls = [int64]$match.Groups['opaqueCalls'].Value
            opaqueVertices = [int64]$match.Groups['opaqueVertices'].Value
            opaqueIndices = [int64]$match.Groups['opaqueIndices'].Value
            waterCalls = [int64]$match.Groups['waterCalls'].Value
            waterVertices = [int64]$match.Groups['waterVertices'].Value
            waterIndices = [int64]$match.Groups['waterIndices'].Value
            liveGeometryMiB = [double]::Parse(
                $match.Groups['liveMiB'].Value.Replace(',', '.'),
                [Globalization.CultureInfo]::InvariantCulture)
            liveOpaqueVertices = [int64]$match.Groups['liveOpaqueVertices'].Value
            liveOpaqueIndices = [int64]$match.Groups['liveOpaqueIndices'].Value
            liveWaterVertices = [int64]$match.Groups['liveWaterVertices'].Value
            liveWaterIndices = [int64]$match.Groups['liveWaterIndices'].Value
        }
    }

    if ((-not $probeLine) -and $timerSamples.Count -eq 0 -and
        $cullTimerSamples.Count -eq 0 -and $drawSamples.Count -eq 0 -and
        $uploadSamples.Count -eq 0 -and $arenaSamples.Count -eq 0) {
        return $null
    }
    return [ordered]@{
        probeLine = $probeLine
        timerSamples = $timerSamples
        cullTimerSamples = $cullTimerSamples
        uploadSamples = $uploadSamples
        arenaSamples = $arenaSamples
        drawSamples = $drawSamples
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
        Where-Object { ([string]$_).IndexOf($Needle, [StringComparison]::Ordinal) -ge 0 } |
        Select-Object -Last 1)[0]
}

if ($IntegratedSingleplayer -and $ServerMod) {
    throw '-ServerMod is implicit in -IntegratedSingleplayer; do not pass both.'
}
if ($IntegratedSingleplayer -and $ReuseServer) {
    throw '-ReuseServer is only for a separate dedicated server, not integrated singleplayer.'
}
if (($SeedSave -or $SeedClientCache -or $SeedServerCache) -and -not $SandboxProfile) {
    throw 'Seed files require -SandboxProfile so the default benchmark world is never replaced.'
}
if ($IntegratedSingleplayer -and ($SeedSave -or $SeedServerCache)) {
    throw 'Seeded save/server-cache support currently targets the isolated dedicated-server path.'
}
if (($SeedClientCache -or $SeedServerCache) -and -not $SeedSave) {
    throw 'Seeded caches require -SeedSave so their world identity cannot be applied alone.'
}
if ($SeedOnly -and -not $SeedSave) {
    throw '-SeedOnly requires -SeedSave.'
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
$seedRecords = [ordered]@{
    save = $null
    clientCache = $null
    serverCache = $null
}
$profileHasSeed = Test-Path -LiteralPath $seedManifest -PathType Leaf
$seedClientProcess = if ($seedSavePath -or $profileHasSeed) {
    Get-SandboxProcess $clientPidFile
} else { $null }
$seedServerProcess = if ($seedSavePath -or $profileHasSeed) {
    Get-SandboxProcess $serverPidFile
} else { $null }
if (($seedSavePath -or $profileHasSeed) -and ($null -ne $seedClientProcess -or $null -ne $seedServerProcess)) {
    throw 'A seeded sandbox process is still running; refusing to copy benchmark inputs.'
}
if ($seedSavePath) {
    $seedRecords.save = Initialize-SeedFile `
        $seedSavePath (Join-Path $seedDir ([IO.Path]::GetFileName($seedSavePath)))
}
if ($seedClientCachePath) {
    $seedRecords.clientCache = Initialize-SeedFile `
        $seedClientCachePath (Join-Path $seedDir ([IO.Path]::GetFileName($seedClientCachePath)))
}
if ($seedServerCachePath) {
    $seedRecords.serverCache = Initialize-SeedFile `
        $seedServerCachePath (Join-Path $seedDir ([IO.Path]::GetFileName($seedServerCachePath)))
}
if ($SeedOnly) {
    New-Item -ItemType Directory -Path $benchOut -Force | Out-Null
    [ordered]@{
        sandboxProfile = $SandboxProfile
        seedFiles = $seedRecords
        seededUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $seedManifest -Encoding utf8
    Write-Host "Seeded frozen sandbox profile '$SandboxProfile'."
    Write-Host "Seed proof: $seedManifest"
    return
}
if (-not $seedSavePath -and $profileHasSeed) {
    $savedSeedManifest = Get-Content -LiteralPath $seedManifest -Raw | ConvertFrom-Json
    $seedRecords = $savedSeedManifest.seedFiles
}
if ($null -ne $seedRecords.save) {
    Restore-FrozenSeedFile $seedRecords.save (Join-Path $serverData 'Saves\default.vcdbs')
}
if ($null -ne $seedRecords.clientCache) {
    Restore-FrozenSeedFile $seedRecords.clientCache `
        (Join-Path $clientCacheDir ([string]$seedRecords.clientCache.name))
}
if ($null -ne $seedRecords.serverCache) {
    Restore-FrozenSeedFile $seedRecords.serverCache `
        (Join-Path $serverCacheDir ([string]$seedRecords.serverCache.name))
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
    VINTAGEHORIZONS_GPU_STATS = if ($GpuStats) { '1' } else { '0' }
    VINTAGEHORIZONS_AUTOUNPAUSE = '1'
}
# Pinned in both directions since 0.3.17, when the mask became the default. Leaving the
# variable unset would let the saved client setting decide, so a run without -ChunkMask
# would measure whatever the sandbox happened to have saved rather than the radial path.
$clientEnvironment.VINTAGEHORIZONS_CHUNK_MASK = if ($ChunkMask) { '1' } else { '0' }
# Set before the client starts, so the measurement shadow exists from the renderer's first
# frame and mirrors every section as it is published. The in-game switch has to backfill by
# re-meshing instead, which a fixed-route run has no time to finish.
if ($GpuRenderer) {
    $clientEnvironment.VINTAGEHORIZONS_GPU_RENDERER = $GpuRenderer
}
if ($GpuArena) {
    $clientEnvironment.VINTAGEHORIZONS_GPU_ARENA = $GpuArena
}
if ($GpuArenaMb) {
    $clientEnvironment.VINTAGEHORIZONS_GPU_ARENA_MB = $GpuArenaMb
}
if ($GpuArenaPageMb) {
    $clientEnvironment.VINTAGEHORIZONS_GPU_ARENA_PAGE_MB = $GpuArenaPageMb
}
if ($GpuIndirect) {
    $clientEnvironment.VINTAGEHORIZONS_GPU_INDIRECT = $GpuIndirect
}
if ($GpuPacked) {
    $clientEnvironment.VINTAGEHORIZONS_GPU_PACKED = $GpuPacked
}
if ($GpuFailure) {
    $clientEnvironment.VINTAGEHORIZONS_GPU_INJECT_FAILURE = $GpuFailure
}
if ($GpuClusters) {
    $clientEnvironment.VINTAGEHORIZONS_GPU_CLUSTERS = $GpuClusters
}
if ($Hzb) {
    $clientEnvironment.VINTAGEHORIZONS_DEPTH_PYRAMID = $Hzb
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
    $readinessRecord = Get-ClientReadinessRecord
    $gpuRenderRecord = Get-ClientGpuRenderRecord
    $reportedPersistedMipObligations = Get-ReportedPersistedMipObligations
    $scenarioRecord = [ordered]@{
        label = $Label
        route = [IO.Path]::GetRelativePath($repoRoot, $routePath).Replace('\', '/')
        integratedSingleplayer = [bool]$IntegratedSingleplayer
        worldName = if ($IntegratedSingleplayer) { $WorldName } else { $null }
        sandboxProfile = if ($SandboxProfile) { $SandboxProfile } else { $null }
        seedFiles = $seedRecords
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
        requireReadinessConvergence = [bool]$RequireReadinessConvergence
        chunkMask = [bool]$ChunkMask
        readinessMaxPhaseMicroseconds = $ReadinessMaxPhaseMicroseconds
        readinessMaxP99Microseconds = $ReadinessMaxP99Microseconds
        readinessMaxWorkAgeFrames = $ReadinessMaxWorkAgeFrames
        readiness = $readinessRecord
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
        gpuStatsEnabled = [bool]$GpuStats
        autoCommand = if ($AutoCommand) { $AutoCommand } else { $null }
        gpuRenderer = if ($GpuRenderer) { $GpuRenderer } else { $null }
        gpuArena = if ($GpuArena) { $GpuArena } else { $null }
        gpuArenaMb = if ($GpuArenaMb) { $GpuArenaMb } else { $null }
        gpuArenaPageMb = if ($GpuArenaPageMb) { $GpuArenaPageMb } else { $null }
        gpuIndirect = if ($GpuIndirect) { $GpuIndirect } else { $null }
        gpuPacked = if ($GpuPacked) { $GpuPacked } else { $null }
        gpuFailure = if ($GpuFailure) { $GpuFailure } else { $null }
        gpuClusters = if ($GpuClusters) { $GpuClusters } else { $null }
        hzb = if ($Hzb) { $Hzb } else { $null }
        gpuRender = $gpuRenderRecord
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

    if ($RequireReadinessConvergence) {
        if ($null -eq $readinessRecord) {
            throw "The client log did not contain parseable vanilla-readiness telemetry. See $scenario and $clientMainLog"
        }
        if ($readinessRecord.samples -le 0 -or $readinessRecord.disabledSamples -gt 0) {
            throw "The vanilla-readiness shadow tracker reported '$($readinessRecord.disabledReason)' state instead of tracking. See $scenario and $clientMainLog"
        }
        if ($readinessRecord.probeErrors -ne 0) {
            throw "The readiness tracker recorded $($readinessRecord.probeErrors) probe errors; every one leaves a cell cache-owned. See $scenario"
        }
        if ($readinessRecord.eventsDropped -ne 0) {
            throw "The readiness tracker dropped $($readinessRecord.eventsDropped) candidate events, so its queues were undersized. See $scenario"
        }
        # Committed cells are state; transitions are an interval rate that is legitimately
        # zero once a stationary route's world has settled. Gate on the state, and report
        # the rate. A 2026-08-18 stationary route held 1,608 committed cells across seven
        # intervals without a single new transition.
        if ($readinessRecord.committedReadyCells -le 0) {
            throw "The readiness tracker holds no committed vanilla-ready cell, so the run proves nothing about convergence. See $scenario"
        }
        if ($ChunkMask) {
            if ($readinessRecord.maskFailedSamples -gt 0) {
                throw "The chunk ownership mask reported failure and fell back to the handoff radius. See $scenario and $clientMainLog"
            }
            if ($readinessRecord.maskState -eq 'off' -or $readinessRecord.maskState -eq 'pending') {
                throw "The chunk ownership mask was requested but never became active (ended '$($readinessRecord.maskState)'). See $scenario"
            }
        }
        elseif ($readinessRecord.handoffSource -notin @('readiness', 'mask')) {
            throw "The near handoff ended the run on the radial fallback rather than measured readiness. See $scenario"
        }
        if (-not $ChunkMask -and $readinessRecord.handoffBlocks -gt $readinessRecord.nearestIncompleteBlocks) {
            throw "The applied handoff of $($readinessRecord.handoffBlocks) blocks reaches past the nearest column that is not wholly owned at $($readinessRecord.nearestIncompleteBlocks) blocks. See $scenario"
        }
        if ($readinessRecord.confirmationOnlyIntervals -gt 0) {
            throw "$($readinessRecord.confirmationOnlyIntervals) readiness intervals spent their whole probe budget re-confirming ready cells while pending cells were never probed. See $scenario"
        }
        if ($readinessRecord.maxOldestWorkFrames -gt $ReadinessMaxWorkAgeFrames) {
            throw "Readiness work waited $($readinessRecord.maxOldestWorkFrames) frames, above the required $ReadinessMaxWorkAgeFrames. See $scenario"
        }
        if ($null -ne $readinessRecord.maxPhaseMicroseconds -and
            $readinessRecord.maxPhaseMicroseconds -gt $ReadinessMaxPhaseMicroseconds) {
            throw "The readiness phase reached $($readinessRecord.maxPhaseMicroseconds)us, above the required $ReadinessMaxPhaseMicroseconds. See $scenario"
        }
        if ($null -ne $readinessRecord.p99PhaseMicroseconds -and
            $readinessRecord.p99PhaseMicroseconds -gt $ReadinessMaxP99Microseconds) {
            throw "Readiness p99 frame cost reached $($readinessRecord.p99PhaseMicroseconds)us, above the required $ReadinessMaxP99Microseconds. See $scenario"
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
