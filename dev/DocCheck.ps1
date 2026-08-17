[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$problems = [System.Collections.Generic.List[string]]::new()
$checks = 0

function Add-Check {
    param(
        [bool]$Condition,
        [string]$Failure
    )

    $script:checks++
    if (-not $Condition) {
        $script:problems.Add($Failure)
    }
}

function Read-RepoText {
    param([string]$RelativePath)
    return [IO.File]::ReadAllText((Join-Path $repoRoot $RelativePath))
}

function Get-RepoRelativePath {
    param([string]$Path)

    # Path.GetRelativePath is absent from Windows PowerShell 5.1's .NET Framework.
    # Every caller enumerates beneath repoRoot, so a checked prefix trim is both simpler
    # and portable across powershell.exe and pwsh.
    $root = [IO.Path]::GetFullPath($repoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($prefix.Length)
    }
    return $full
}

function Match-One {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Description
    )

    $match = [Text.RegularExpressions.Regex]::Match($Text, $Pattern)
    Add-Check $match.Success "Could not find $Description"
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

Write-Host ''
Write-Host 'Vintage Horizons documentation checks'
Write-Host "root: $repoRoot"
Write-Host ''

$required = @(
    'AGENTS.md',
    'CLAUDE.md',
    'STATUS.md',
    'CHANGELOG.md',
    'dev/ARCHITECTURE.md',
    'dev/GOTCHAS.md',
    'dev/TODO.md',
    'dev/WIRE_HISTORY.md',
    'dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md',
    'dev/sessions/INDEX.md',
    'dev/sessions/TEMPLATE.md',
    'dev/history/DONE.md',
    'dev/archive/README.md'
)

foreach ($relative in $required) {
    Add-Check (Test-Path -LiteralPath (Join-Path $repoRoot $relative) -PathType Leaf) "Required document is missing: $relative"
}

$modInfoPath = Join-Path $repoRoot 'VintageHorizons/modinfo.json'
$modInfo = Get-Content -LiteralPath $modInfoPath -Raw | ConvertFrom-Json
$modVersion = [string]$modInfo.version

$projectText = Read-RepoText 'VintageHorizons/VintageHorizons.csproj'
$projectVersion = Match-One $projectText '<Version>([^<]+)</Version>' 'the project Version'
$statusText = Read-RepoText 'STATUS.md'
$statusVersion = Match-One $statusText '\*\*Mod version:\*\*\s*`([^`]+)`' 'STATUS mod version'

Add-Check ($modVersion -eq $projectVersion) "modinfo.json version $modVersion does not match csproj version $projectVersion"
Add-Check ($modVersion -eq $statusVersion) "modinfo.json version $modVersion does not match STATUS version $statusVersion"

$sourceCount = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'VintageHorizons/src') -Recurse -File -Filter '*.cs').Count
$statusSourceCount = Match-One $statusText '\*\*Source files:\*\*\s*`(\d+)`' 'STATUS source-file count'
Add-Check ($sourceCount -eq [int]$statusSourceCount) "STATUS says $statusSourceCount source files, but the repository has $sourceCount"

$protocolText = Read-RepoText 'VintageHorizons/src/Net/LodAssistProtocol.cs'
$storeText = Read-RepoText 'VintageHorizons/src/Storage/LodStore.cs'
$protocol = Match-One $protocolText 'public\s+const\s+int\s+Protocol\s*=\s*(\d+)' 'assist Protocol constant'
$blobFormat = Match-One $storeText 'const\s+byte\s+BlobFormatVersion\s*=\s*(\d+)' 'blob format constant'
$schema = Match-One $storeText 'const\s+string\s+SchemaVersion\s*=\s*"([^"]+)"' 'schema version constant'

$statusProtocol = Match-One $statusText '\*\*Assist protocol:\*\*\s*`([^`]+)`' 'STATUS assist protocol'
$statusBlob = Match-One $statusText '\*\*Blob format:\*\*\s*`([^`]+)`' 'STATUS blob format'
$statusSchema = Match-One $statusText '\*\*Database schema:\*\*\s*`([^`]+)`' 'STATUS database schema'

$wireText = Read-RepoText 'dev/WIRE_HISTORY.md'
$wireProtocol = Match-One $wireText '\*\*Assist protocol:\*\*\s*`([^`]+)`' 'wire-ledger assist protocol'
$wireBlob = Match-One $wireText '\*\*Section blob format:\*\*\s*`([^`]+)`' 'wire-ledger blob format'
$wireSchema = Match-One $wireText '\*\*Database schema meaning:\*\*\s*`([^`]+)`' 'wire-ledger database schema'

Add-Check ($protocol -eq $statusProtocol -and $protocol -eq $wireProtocol) "Assist protocol disagrees: code=$protocol status=$statusProtocol ledger=$wireProtocol"
Add-Check ($blobFormat -eq $statusBlob -and $blobFormat -eq $wireBlob) "Blob format disagrees: code=$blobFormat status=$statusBlob ledger=$wireBlob"
Add-Check ($schema -eq $statusSchema -and $schema -eq $wireSchema) "Database schema disagrees: code=$schema status=$statusSchema ledger=$wireSchema"

$changelogText = Read-RepoText 'CHANGELOG.md'
$escapedVersion = [Text.RegularExpressions.Regex]::Escape($modVersion)
Add-Check ([Text.RegularExpressions.Regex]::IsMatch($changelogText, "(?m)^## \[$escapedVersion\]")) "CHANGELOG.md has no entry for current version $modVersion"

$sessionIndex = Read-RepoText 'dev/sessions/INDEX.md'
$sessionFiles = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dev/sessions') -File -Filter 'SESSION_*.md'
foreach ($session in $sessionFiles) {
    if ($session.BaseName -notmatch '^SESSION_(\d+)$') { continue }
    $number = $Matches[1]
    Add-Check ([Text.RegularExpressions.Regex]::IsMatch($sessionIndex, "(?m)^\|\s*$number\s*\|")) "Session $number has no row in dev/sessions/INDEX.md"
}

$livingMarkdown = Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter '*.md' | Where-Object {
    $_.FullName -notmatch '[\\/](\.git|bin|obj)[\\/]' -and
    $_.FullName -notmatch '[\\/]dev[\\/]archive[\\/]'
}

$linkPattern = [Text.RegularExpressions.Regex]::new('!?(?:\[[^\]]*\])\(([^)]+)\)')
foreach ($document in $livingMarkdown) {
    $text = [IO.File]::ReadAllText($document.FullName)
    foreach ($match in $linkPattern.Matches($text)) {
        $target = $match.Groups[1].Value.Trim().Trim('<', '>')
        if ($target -match '^(https?|mailto|app):' -or $target.StartsWith('#')) { continue }

        $target = ($target -split '#', 2)[0]
        if ([string]::IsNullOrWhiteSpace($target)) { continue }

        $target = [Uri]::UnescapeDataString($target)
        $resolved = [IO.Path]::GetFullPath((Join-Path $document.DirectoryName $target))
        $relativeDoc = Get-RepoRelativePath $document.FullName
        Add-Check (Test-Path -LiteralPath $resolved) "Broken Markdown pointer in ${relativeDoc}: $target"

        if ($target -match '(^|[\\/])dev[\\/]archive[\\/]') {
            $problems.Add("Living document links into the frozen archive: $relativeDoc -> $target")
        }
    }
}

$archiveMarkdown = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dev/archive') -Recurse -File -Filter '*.md' | Where-Object { $_.Name -ne 'README.md' }
foreach ($document in $archiveMarkdown) {
    $firstBlock = ([IO.File]::ReadAllLines($document.FullName) | Select-Object -First 5) -join "`n"
    $relative = Get-RepoRelativePath $document.FullName
    Add-Check ($firstBlock -match 'ARCHIVED' -and $firstBlock -match 'SUPERSEDED') "Archived document lacks the required banner: $relative"
}

$shaderFiles = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'VintageHorizons/assets') -Recurse -File | Where-Object { $_.Extension -in '.vsh', '.fsh' }
foreach ($shader in $shaderFiles) {
    $bytes = [IO.File]::ReadAllBytes($shader.FullName)
    $nonAscii = $bytes | Where-Object { $_ -gt 127 } | Select-Object -First 1
    $relative = Get-RepoRelativePath $shader.FullName
    Add-Check ($null -eq $nonAscii) "Shader contains a non-ASCII byte: $relative"
}

$textExtensions = @('.md', '.cs', '.csproj', '.json', '.ps1', '.sh', '.txt', '.yml', '.yaml', '.vsh', '.fsh')
$personalPrefix = 'C:' + [IO.Path]::DirectorySeparatorChar + 'Users' + [IO.Path]::DirectorySeparatorChar
$publicationFiles = Get-ChildItem -LiteralPath $repoRoot -Recurse -File | Where-Object {
    $_.Extension -in $textExtensions -and $_.FullName -notmatch '[\\/](\.git|bin|obj)[\\/]'
}
foreach ($file in $publicationFiles) {
    $text = [IO.File]::ReadAllText($file.FullName)
    $relative = Get-RepoRelativePath $file.FullName
    Add-Check ($text.IndexOf($personalPrefix, [StringComparison]::OrdinalIgnoreCase) -lt 0) "Tracked text contains a personal Windows path: $relative"
}

Add-Check ($statusText.Contains('dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md')) 'STATUS.md does not point to the active performance plan'
Add-Check ((Read-RepoText 'dev/TODO.md').Contains('dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md')) 'dev/TODO.md does not point to the active performance plan'

if ($problems.Count -gt 0) {
    Write-Host "$checks checks, $($problems.Count) failures" -ForegroundColor Red
    foreach ($problem in $problems) {
        Write-Host "  x $problem" -ForegroundColor Red
    }
    Write-Host ''
    exit 1
}

Write-Host "$checks checks passed" -ForegroundColor Green
Write-Host ''
