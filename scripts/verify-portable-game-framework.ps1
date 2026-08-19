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
    @{ Pattern = '\bCultMeshReactiveDocument\s*<'; Reason = 'Aetheria client mirrors are observers; document write authority must be selected explicitly at an operation boundary.' }
    @{ Pattern = '\bAetheriaRuntimeCommittedFactImporter\b'; Reason = 'Peer committed facts cannot mutate gameplay; Pilot output must enter as pre-finality candidate evidence.' }
    @{ Pattern = '\b(CumulativeImportedFactIds|CumulativeRejectedImportedFactIds|DuplicateImportedFactIds)\b'; Reason = 'Daemon frames cannot retain peer-import chronology or imply peer finality.' }
    @{ Pattern = '\bPeerCultMeshEndpoints\b|ReadOptions\(args,\s*"--peer-cultmesh-endpoint"'; Reason = 'Direct peer fact import is retired; Starbridge requires Pilot candidates and Commander selection.' }
    @{ Pattern = '\b(class|sealed\s+class)\s+(AetheriaRuntimeVerseClient|AetheriaClientState|AetheriaClient)\b'; Reason = 'Application clients must not reopen daemon .cc state or own client-side projection Verses.' }
    @{ Pattern = '\bAetheriaClient\.OpenAsync\b'; Reason = 'Application clients must connect through retained CultMesh provider identity, not a direct state-file facade.' }
    @{ Pattern = '\b(AetheriaRuntimeClientTarget|AetheriaRuntimeStateBoot|AetheriaRuntimeVerseDiscovery|AetheriaRuntimeVerseReplicaBridge|AetheriaClientTarget)\b'; Reason = 'Verse selection belongs to the daemon Hangar coordinator; client sidecars, boot selectors, and application replicas must not return.' }
    @{ Pattern = '\b(MainMenuShowVerseSettings|VerseSettingsSurfaceId)\b'; Reason = 'The player-facing Verse selector exists only in the daemon-published Hangar surface.' }
)

foreach ($rule in $forbidden) {
    $matches = $sources | Select-String -Pattern $rule.Pattern
    if ($matches) {
        $locations = $matches | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
        throw "$($rule.Reason)`n$($locations -join [Environment]::NewLine)"
    }
}

foreach ($retiredProject in @("Aetheria.State.Replica", "Aetheria.State.Unity", "Aetheria.State.Unity.Smoke")) {
    if (Test-Path -LiteralPath (Join-Path $Root $retiredProject)) {
        $liveFiles = Get-ChildItem -LiteralPath (Join-Path $Root $retiredProject) -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
        if ($liveFiles) {
            throw "$retiredProject is retired. Unity must use generic Eve/CultMesh identity; local state and replica tools cannot survive as a client architecture."
        }
    }
}

$registry = Join-Path $Root "Aetheria.State\AetheriaDocumentRegistry.cs"
$canonicalRegistrations = @(Select-String -LiteralPath $registry -Pattern 'typeof\(EveSurfaceDocument\)').Count
if ($canonicalRegistrations -ne 1) {
    throw "Aetheria must register canonical EveSurfaceDocument exactly once; found $canonicalRegistrations."
}

$runtimeRegistry = Join-Path $Root "Packages\org.gamecult.aetheria.state\Runtime\AetheriaRuntimeVerseContracts.cs"
$runtimeRegistrations = @(Select-String -LiteralPath $runtimeRegistry -Pattern 'typeof\(EveSurfaceDocument\)').Count
if ($runtimeRegistrations -ne 1) {
    throw "The runtime Verse contract registry must list EveSurfaceDocument exactly once; found $runtimeRegistrations."
}

$headlessProjectFiles = Get-ChildItem -LiteralPath $Root -Recurse -File |
    Where-Object {
        $_.Extension -in @(".csproj", ".props") -and
        $_.FullName -match '[\\/]Aetheria\.State' -and
        $_.FullName -notmatch '[\\/](obj|bin)[\\/]'
    } |
    ForEach-Object FullName
$rendererProjectReferences = if ($headlessProjectFiles) {
    Select-String -LiteralPath $headlessProjectFiles -Pattern 'EveUnityRoot|EveUnity[\\/]packages'
} else {
    @()
}
if ($rendererProjectReferences) {
    $locations = $rendererProjectReferences | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
    throw "Headless Aetheria projects must consume renderer-neutral contracts from Eve, not EveUnity.`n$($locations -join [Environment]::NewLine)"
}

$stateProject = Join-Path $Root "Aetheria.State\Aetheria.State.csproj"
if (-not (Select-String -LiteralPath $stateProject -Quiet -SimpleMatch '$(EveRoot)\packages\org.gamecult.eve.surface\GameCult.Eve.Surface.csproj')) {
    throw "Aetheria.State must resolve the canonical renderer-neutral surface contract from EveRoot."
}

$hangarBuilder = Join-Path $Root "Packages\org.gamecult.aetheria.state\Runtime\AetheriaRuntimeHangarSurfaceBuilder.cs"
if (-not (Select-String -LiteralPath $hangarBuilder -Quiet -Pattern 'public static EveSurfaceDocument Build\(')) {
    throw "The Hangar surface builder does not return the canonical EveSurfaceDocument."
}

$daemonProgram = Join-Path $Root "Aetheria.State.Daemon\Program.cs"
$daemonSource = Get-Content -LiteralPath $daemonProgram -Raw
if ($daemonSource -match 'AetheriaRuntime(ClientTarget|VerseReplicaBridge|StateBoot)') {
    throw "The daemon must own progression Verse discovery and selection; client-target sidecars and replica bridges are forbidden in its path."
}
if ($daemonSource -match 'currentFrame\?\.AccountedCommandIds') {
    throw "The hot daemon frame cannot remain the command idempotency ledger."
}
if ($daemonSource -notmatch 'DeleteDaemonCommandAsync\(commandId\)') {
    throw "Committed daemon commands must leave the transient command inbox."
}
if ($daemonSource -notmatch 'DeleteAsync<EveSurfaceCommandRequest>\(requestRecordKey\)') {
    throw "Handled Eve invocations must leave the transient inbox by their actual CultCache record identity."
}
if ($daemonSource -match 'Documents<EveCommandReceiptDocument>\(\).*ToHashSet' -or
    $daemonSource -match 'Documents<AetheriaRuntimeDaemonCommandDocument>\(\).*ToHashSet') {
    throw "Eve ingress must use indexed receipt and command identity instead of rebuilding lifetime command-id sets."
}
$compatibilityBridge = Get-Content -LiteralPath (Join-Path $Root "Aetheria.State\AetheriaEveCommandBridge.cs") -Raw
if ($compatibilityBridge -notmatch 'GetStoredDocuments<AetheriaRuntimeEveCommandDocument>' -or
    $compatibilityBridge -notmatch 'DeleteAsync<AetheriaRuntimeEveCommandDocument>\(storedCommand\.Key\)' -or
    $compatibilityBridge -notmatch 'EveReceiptForCommand\(command\.CommandId\)') {
    throw "The compatibility Eve bridge must be a receipt-indexed transient inbox, not a lifetime command ledger."
}
if ($compatibilityBridge -match 'IEnumerable<string>\?\s+accountedCommandIds') {
    throw "The compatibility Eve bridge cannot accept a cumulative command-id ledger."
}
$tickRunner = Get-Content -LiteralPath (Join-Path $Root "Packages\org.gamecult.aetheria.state\Runtime\AetheriaRuntimeDaemonTickRunner.cs") -Raw
if ($tickRunner -match 'options\.Cumulative(Applied|Rejected)CommandIds') {
    throw "Daemon ticks cannot carry lifetime command chronology through every hot frame."
}
$commandScaleSmoke = Get-Content -LiteralPath (Join-Path $Root "Aetheria.State.Daemon.Smoke\Program.cs") -Raw
if ($commandScaleSmoke -notmatch '10_000' -or $commandScaleSmoke -notmatch 'FrameSizeDoesNotGrowWithCommandChronology') {
    throw "The 10,000-command hot-frame size witness is missing."
}
$hangarWriters = @(Select-String -LiteralPath $daemonProgram -Pattern 'MutableDocument<EveSurfaceDocument>\(AetheriaRuntimeVerseRecordKeys\.HangarSurface\)').Count
if ($hangarWriters -ne 1) {
    throw "The canonical Hangar surface must have exactly one daemon writer; found $hangarWriters."
}

$progressionDocument = Join-Path $Root "Packages\org.gamecult.aetheria.state\Runtime\AetheriaRuntimeProgressionSourceDocuments.cs"
$progressionCoordinator = Join-Path $Root "Aetheria.State.Daemon\AetheriaProgressionVerseCoordinator.cs"
foreach ($required in @($progressionDocument, $progressionCoordinator)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "The daemon-owned progression Verse boundary is missing: $required"
    }
}
$progressionCoordinatorSource = Get-Content -LiteralPath $progressionCoordinator -Raw
if ($progressionCoordinatorSource -notmatch 'new\s+CultMeshSessionTarget\s*\(\s*source\.SelectedVerseId\s*,\s*authorityRuntimeId\s*\)' -or
    $progressionCoordinatorSource -notmatch 'AuthorityRuntimeIds') {
    throw "The Hangar daemon must derive an explicit Verse/provider session target from the selected Odin descriptor."
}
if ($progressionCoordinatorSource -match '_remote\.(ReadAsync|SubmitDocumentAsync)\s*<[^>]+>\s*\(\s*source\.SelectedVerseId' -or
    $progressionCoordinatorSource -match '_remote\.(ReadAsync|SubmitDocumentAsync)\s*\(\s*source\.SelectedVerseId') {
    throw "Remote Hangar traffic cannot use the Verse selector value as an ambiguous provider identity."
}
if (-not (Select-String -LiteralPath $hangarBuilder -Quiet -SimpleMatch '"control.select"') -or
    -not (Select-String -LiteralPath $hangarBuilder -Quiet -SimpleMatch 'AetheriaRuntimeHangarCommands.SelectVerse')) {
    throw "The Hangar must publish the daemon-owned progression Verse selector as an Eve control.select."
}
if (-not (Select-String -LiteralPath $hangarBuilder -Quiet -SimpleMatch 'EveInventoryInteraction.GridKind') -or
    -not (Select-String -LiteralPath $hangarBuilder -Quiet -SimpleMatch 'dropCommand.hangar') -or
    -not (Select-String -LiteralPath $hangarBuilder -Quiet -SimpleMatch 'dropCommand.equipment') -or
    -not (Select-String -LiteralPath $hangarBuilder -Quiet -SimpleMatch 'payload.expectedHangarRevision')) {
    throw "The Hangar loadout must be a daemon-published Eve inventory grid with revision-bound typed drops."
}
$electronMain = Join-Path $Root "Aetheria.Rts.Web\Electron\main.ts"
$electronSource = Get-Content -LiteralPath $electronMain -Raw
foreach ($lifecycleBoundary in @('await stopChildGracefully(ownedDaemon)', 'terminateProcessTree', 'taskkill', 'process.kill(-pid, "SIGKILL")', 'Aetheria shutdown remains incomplete')) {
    if ($electronSource -notmatch [regex]::Escape($lifecycleBoundary)) {
        throw "Electron is missing verified daemon lifecycle boundary '$lifecycleBoundary'."
    }
}
if ($electronSource -match 'daemonProcess\s*=\s*null\s*;\s*await\s+stopChildGracefully' -or
    $electronSource -match 'shutdownOwnedRuntime\(\)\.finally\(\(\)\s*=>\s*app\.quit') {
    throw "Electron cannot forget daemon ownership or quit before verified termination."
}

$hangarDocuments = Get-Content -LiteralPath (Join-Path $Root "Packages\org.gamecult.aetheria.state\Runtime\AetheriaRuntimeHangarDocuments.cs") -Raw
$hangarCoordinator = Get-Content -LiteralPath (Join-Path $Root "Aetheria.State.Daemon\AetheriaDaemonHangarCoordinator.cs") -Raw
if ($hangarDocuments -notmatch 'class\s+AetheriaHangarDraftState' -or
    $hangarCoordinator -notmatch 'SelectShipAsync' -or
    $hangarCoordinator -notmatch 'SelectModeAsync' -or
    $hangarCoordinator -notmatch 'SelectViewAsync' -or
    $hangarCoordinator -notmatch 'LaunchAsync' -or
    $daemonSource -notmatch 'AetheriaRuntimeHangarCommands\.ShowOverview' -or
    $daemonSource -match 'LaunchTerminusAsync|CanContinueTerminusAsync') {
    throw "Ship, mode, and view selection must have one typed daemon Hangar draft consumed by the generic admission path."
}
$hangarBuilderSource = Get-Content -LiteralPath $hangarBuilder -Raw
if ($hangarBuilderSource -match 'gridTemplate|gridArea|Component\([^\r\n]+,\s*"panel"' -or
    $hangarBuilderSource -match '\("enabled",\s*can(Launch|Continue)') {
    throw "The Hangar Eve graph must use portable partition/min-max geometry and semantic disabled state."
}
foreach ($modeCommand in @("SelectTerminus", "SelectStarbridge", "SelectArena")) {
    if ($daemonSource -notmatch "case\s+AetheriaRuntimeHangarCommands\.$modeCommand[\s\S]{0,300}AetheriaDaemonHangarCoordinator\.SelectModeAsync") {
        throw "Every advertised Hangar mode must mutate the daemon-owned draft."
    }
}
if ($daemonSource -notmatch 'LoadAuthorityRouteGrant' -or
    $daemonSource -notmatch 'MessagePackSerializer\.Deserialize<CultMeshVerseDescriptorMessage>' -or
    $daemonSource -match 'OdinSigningKey|odin-signing-key|CreateSignedRoute' -or
    $daemonSource -notmatch 'wss://' -or
    $daemonSource -notmatch 'https://' -or
    $daemonSource -notmatch 'requireExisting:\s*remotePublication') {
    throw "Non-loopback Aetheria publication must consume an Odin-issued typed grant and advertise its WSS/HTTPS/QUIC routes without possessing the Odin signing key."
}

Write-Host "Portable game framework boundary verification passed."
