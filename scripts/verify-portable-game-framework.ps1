param(
    [string] $Root = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$Root = (Resolve-Path -LiteralPath $Root).Path

$sourceRoots = @(
    (Join-Path $Root "Aetheria.State"),
    (Join-Path $Root "Aetheria.State.Daemon"),
    (Join-Path $Root "Packages\org.gamecult.aetheria.state\Runtime")
)
$sources = Get-ChildItem -LiteralPath $sourceRoots -Recurse -File -Filter *.cs

$forbidden = @(
    @{ Pattern = 'class\s+AetheriaRuntimeSurfaceDocument\b'; Reason = 'Aetheria must not own a clone of the Eve surface document.' },
    @{ Pattern = 'class\s+AetheriaRuntimeSurface(Component|Tree|CommandTemplate|StyleToken|EmbeddedDocumentSlot)\b'; Reason = 'Aetheria must not own cloned Eve surface members.' },
    @{ Pattern = 'gamecult\.aetheria\.runtime_surface'; Reason = 'The retired Aetheria surface schema must not return.' },
    @{ Pattern = '\b(ToPortableSurface|FromPortableSurface)\s*\('; Reason = 'Aetheria surfaces must not cross a duplicate conversion bridge.' }
)

foreach ($rule in $forbidden) {
    $matches = $sources | Select-String -Pattern $rule.Pattern
    if ($matches) {
        $locations = $matches | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
        throw "$($rule.Reason)`n$($locations -join [Environment]::NewLine)"
    }
}

$registry = Join-Path $Root "Aetheria.State\AetheriaDocumentRegistry.cs"
$canonicalRegistrations = @(Select-String -LiteralPath $registry -Pattern 'typeof\(EveSurfaceDocument\)').Count
if ($canonicalRegistrations -ne 1) {
    throw "Aetheria must register canonical EveSurfaceDocument exactly once; found $canonicalRegistrations."
}

$hangarBuilder = Join-Path $Root "Packages\org.gamecult.aetheria.state\Runtime\AetheriaRuntimeHangarSurfaceBuilder.cs"
if (-not (Select-String -LiteralPath $hangarBuilder -Quiet -Pattern 'public static EveSurfaceDocument Build\(')) {
    throw "The Hangar surface builder does not return the canonical EveSurfaceDocument."
}

$daemonProgram = Join-Path $Root "Aetheria.State.Daemon\Program.cs"
$hangarWriters = @(Select-String -LiteralPath $daemonProgram -Pattern 'MutableDocument<EveSurfaceDocument>\(AetheriaRuntimeVerseRecordKeys\.HangarSurface\)').Count
if ($hangarWriters -ne 1) {
    throw "The canonical Hangar surface must have exactly one daemon writer; found $hangarWriters."
}

Write-Host "Portable game framework boundary verification passed."
