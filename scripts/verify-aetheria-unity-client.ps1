param(
    [string] $ProjectPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "Aetheria.Unity")
)

$ErrorActionPreference = "Stop"

function Assert-Equal([object] $Actual, [object] $Expected, [string] $Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
}

$manifest = Get-Content (Join-Path $ProjectPath "Packages/manifest.json") -Raw | ConvertFrom-Json
$lock = Get-Content (Join-Path $ProjectPath "Packages/packages-lock.json") -Raw | ConvertFrom-Json
$expected = @{
    "org.gamecult.cultlib" = @(
        "https://github.com/GameCult/CultLib.git?path=/unity/org.gamecult.cultlib#b9cbd7561d96b01a770273891ea9144d98a538bc",
        "b9cbd7561d96b01a770273891ea9144d98a538bc")
    "org.gamecult.eve.surface" = @(
        "https://github.com/GameCult/Eve.git?path=/packages/org.gamecult.eve.surface#3ede0799abde0bf21a4b691d99dae73ce2294f4b",
        "3ede0799abde0bf21a4b691d99dae73ce2294f4b")
    "org.gamecult.eve.unity-scene" = @(
        "https://github.com/GameCult/EveUnity.git?path=/packages/org.gamecult.eve.unity-scene#bdc942db2137c02d8f3f02deeec8ceb3f858cef0",
        "bdc942db2137c02d8f3f02deeec8ceb3f858cef0")
    "org.gamecult.eve.unity-uitoolkit" = @(
        "https://github.com/GameCult/EveUnity.git?path=/packages/org.gamecult.eve.unity-uitoolkit#bdc942db2137c02d8f3f02deeec8ceb3f858cef0",
        "bdc942db2137c02d8f3f02deeec8ceb3f858cef0")
}

foreach ($packageName in $expected.Keys) {
    $dependency = $manifest.dependencies.$packageName
    $resolved = $lock.dependencies.$packageName
    Assert-Equal $dependency $expected[$packageName][0] "$packageName manifest ref"
    Assert-Equal $resolved.version $dependency "$packageName lock ref"
    Assert-Equal $resolved.hash $expected[$packageName][1] "$packageName resolved hash"
}

$cultLibPackage = Get-ChildItem (Join-Path $ProjectPath "Library/PackageCache") -Directory |
    Where-Object Name -Like "org.gamecult.cultlib@*" |
    Where-Object { (Get-Content (Join-Path $_.FullName "package.json") -Raw | ConvertFrom-Json).version -eq "1.0.56" } |
    Select-Object -First 1
if (-not $cultLibPackage) { throw "Resolved CultLib 1.0.56 package is missing from Library/PackageCache." }

$meshAssembly = Join-Path $cultLibPackage.FullName "Runtime/Plugins/GameCult.Mesh.dll"
if (-not (Test-Path $meshAssembly)) { throw "Resolved GameCult.Mesh.dll is missing." }
$cacheAssembly = Join-Path $cultLibPackage.FullName "Runtime/Plugins/GameCult.Caching.MessagePack.dll"
if (-not (Test-Path $cacheAssembly)) { throw "Resolved GameCult.Caching.MessagePack.dll is missing." }
$coreCacheAssembly = Join-Path $cultLibPackage.FullName "Runtime/Plugins/GameCult.Caching.dll"
if (-not (Test-Path $coreCacheAssembly)) { throw "Resolved GameCult.Caching.dll is missing." }
$cacheApi = & rg -a -o "cultcache\.store\.v4\.directory-content-addressed-pages|ReadPersistedGeneration" $cacheAssembly 2>&1
$transactionApi = & rg -a -o "SnapshotForCommit" $coreCacheAssembly 2>&1
if ($LASTEXITCODE -ne 0 -or
    -not ($cacheApi -contains "cultcache.store.v4.directory-content-addressed-pages") -or
    -not ($cacheApi -contains "ReadPersistedGeneration") -or
    -not ($transactionApi -contains "SnapshotForCommit")) {
    throw "Resolved CultLib package does not contain the sealed transaction and coherent v4 generation APIs."
}
$meshApi = & rg -a -o "CultMesh[A-Za-z]+" $meshAssembly 2>&1
if ($LASTEXITCODE -ne 0) { throw "Unable to inspect GameCult.Mesh.dll metadata.`n$meshApi" }
$requiredMeshApiNames = @(
    "CultMeshBodyPublicationDocument",
    "CultMeshBodyPublicationHandle",
    "CultMeshFrameBodyPublisher",
    "CultMeshTcpContentServer",
    "CultMeshHttpsContentTransportConnector",
    "CultMeshAuthorityTrustPolicy",
    "CultMeshSessionContentProvider")
foreach ($apiName in $requiredMeshApiNames) {
    if (-not ($meshApi | Where-Object { $_ -eq $apiName })) {
        throw "Resolved GameCult.Mesh.dll does not expose $apiName."
    }
}

$surfacePackage = Get-ChildItem (Join-Path $ProjectPath "Library/PackageCache") -Directory |
    Where-Object Name -Like "org.gamecult.eve.surface@*" |
    Where-Object { (Get-Content (Join-Path $_.FullName "package.json") -Raw | ConvertFrom-Json).version -eq "0.3.5" } |
    Select-Object -First 1
if (-not $surfacePackage) { throw "Resolved Eve surface 0.3.5 package is missing from Library/PackageCache." }
$inputContractSource = Get-Content (Join-Path $surfacePackage.FullName "Runtime/EveInputCapabilityDocument.cs") -Raw
foreach ($requiredInputContract in @("PayloadKeys", "CurrentValue", "ActionBar", "IconRef")) {
    if ($inputContractSource -notmatch $requiredInputContract) {
        throw "Resolved Eve surface package is missing input contract '$requiredInputContract'."
    }
}
$receiptContractSource = Get-Content (Join-Path $surfacePackage.FullName "Runtime/EveProviderDocuments.cs") -Raw
if ($receiptContractSource -notmatch [regex]::Escape("AuthorityRuntimeId")) {
    throw "Resolved Eve surface package cannot preserve navigation authority identity."
}
if ($receiptContractSource -notmatch [regex]::Escape("InvocationHash") -or
    -not (Test-Path (Join-Path $surfacePackage.FullName "Runtime/EveCommandInvocationHash.cs"))) {
    throw "Resolved Eve surface package cannot bind receipts to immutable command invocations."
}

$scenePackage = Get-ChildItem (Join-Path $ProjectPath "Library/PackageCache") -Directory |
    Where-Object Name -Like "org.gamecult.eve.unity-scene@*" |
    Where-Object { (Get-Content (Join-Path $_.FullName "package.json") -Raw | ConvertFrom-Json).version -eq "0.3.126" } |
    Select-Object -First 1
if (-not $scenePackage) { throw "Resolved Eve Unity scene 0.3.126 package is missing from Library/PackageCache." }
$advertisedInputSource = Get-Content (Join-Path $scenePackage.FullName "Runtime/EveUnityAdvertisedInputAction.cs") -Raw
$inputDriverSource = Get-Content (Join-Path $scenePackage.FullName "Runtime/EveUnityPlayableWorldInputDriver.cs") -Raw
$actionBarSource = Get-Content (Join-Path $scenePackage.FullName "Runtime/EveUnityInputActionBar.cs") -Raw
foreach ($requiredInputContract in @("view-direction.v1", "BuildViewDirectionPayload", "SubmitChangedPerformedActions", "scalar.v1", "BuildScalarPayload", "EveUnityInputActionBar")) {
    if (($advertisedInputSource + $inputDriverSource + $actionBarSource) -notmatch [regex]::Escape($requiredInputContract)) {
        throw "Resolved Eve Unity scene package is missing generic input lowering '$requiredInputContract'."
    }
}

$navigationSource = (Get-Content (Join-Path $scenePackage.FullName "Runtime/EveUnityPlayableWorldClientBootstrap.cs") -Raw) +
    (Get-Content (Join-Path $scenePackage.FullName "Runtime/CultMesh/EveUnityCultMeshPlayableWorldProvider.cs") -Raw) +
    (Get-Content (Join-Path $scenePackage.FullName "Runtime/CultMesh/EveUnityCultMeshProviderDiscovery.cs") -Raw) +
    (Get-Content (Join-Path $scenePackage.FullName "Runtime/EveUnityPlayableWorldLiveClient.cs") -Raw)
foreach ($requiredNavigation in @(
    "receipt.Navigation",
    "NavigateAsync",
    "RendezvousEndpoints",
    "AuthorityRuntimeId",
    "requiredAuthorityRuntimeId",
    "Eve Unity Candidate Presentation",
    "PresentationSwap",
    "FinalizeNavigation",
    "_navigationRouteCommitted",
    "_connectionLeaseCount",
    "_ownedPresentation")) {
    if ($navigationSource -notmatch [regex]::Escape($requiredNavigation)) {
        throw "Resolved Eve Unity scene package is missing provider-owned navigation '$requiredNavigation'."
    }
}

$sceneSinkSource = Get-Content (Join-Path $scenePackage.FullName "Runtime/EveUnityGameObjectPlayableWorldSceneSink.cs") -Raw
$presentedEntitySource = Get-Content (Join-Path $scenePackage.FullName "Runtime/EveUnityPresentedEntities.cs") -Raw
foreach ($requiredProjectionBoundary in @(
    "IEveUnityPlayableWorldSceneSink",
    "IEveUnityEntityGenerationSink",
    "IEveUnityPresentedEntityRegistry",
    "void ApplyGeneration(EveUnityPresentedEntityGeneration generation)",
    "public Vector3 Position { get; }")) {
    if (($sceneSinkSource + $presentedEntitySource) -notmatch [regex]::Escape($requiredProjectionBoundary)) {
        throw "Resolved Eve Unity scene package is missing read-only presentation boundary '$requiredProjectionBoundary'."
    }
}
foreach ($forbiddenWriteback in @(
    "EveSurfaceCommandRequest",
    "IEveUnitySurfaceCommandTransport",
    "EveUnityCultMeshPlayableWorldProvider")) {
    if (($sceneSinkSource + $presentedEntitySource) -match [regex]::Escape($forbiddenWriteback)) {
        throw "Unity scene projection gained a forbidden provider writeback dependency '$forbiddenWriteback'."
    }
}

$assemblies = Get-ChildItem (Join-Path $ProjectPath "Library/ScriptAssemblies") -Filter *.dll -ErrorAction Stop |
    Select-Object -ExpandProperty Name
$forbidden = $assemblies | Where-Object { $_ -match "^(Aetheria|GameCult\.Aetheria)(\.|$)|ServerShared" }
if ($forbidden) { throw "Aetheria gameplay/ServerShared assemblies entered the Unity client: $($forbidden -join ', ')" }

$bootstrapSource = Get-Content (Join-Path $ProjectPath "Assets/AetheriaUnityClient.cs") -Raw
foreach ($required in @("EVEUNITY_RENDEZVOUS_ENDPOINT", "EveUnityCultMeshPlayableWorldProvider", "ConfigureProvider")) {
    if ($bootstrapSource -notmatch [regex]::Escape($required)) { throw "Client bootstrap is missing discovery input '$required'." }
}
foreach ($forbiddenInput in @("AetheriaRuntimeVerseSchemas", "AetheriaRuntimeVerseRecordKeys", "productSchema", "recordKey")) {
    if ($bootstrapSource -match [regex]::Escape($forbiddenInput)) { throw "Client bootstrap depends on forbidden product input '$forbiddenInput'." }
}
foreach ($forbiddenUnityUi in @("OnGUI(", "GUI.Box", "UnityEngine.GUI")) {
    if ($bootstrapSource -match [regex]::Escape($forbiddenUnityUi)) { throw "Client bootstrap contains forbidden Unity-authored UI '$forbiddenUnityUi'." }
}
if ($bootstrapSource -notmatch [regex]::Escape('?? "aetheria.hangar"')) {
    throw "The standalone Unity client must default to the daemon-owned Hangar surface."
}

$editorBootstrapSource = Get-Content (Join-Path $ProjectPath "Assets/Editor/AetheriaDaemonDevelopmentWindow.cs") -Raw
if ($editorBootstrapSource -notmatch [regex]::Escape('SetEnvironmentVariable("EVEUNITY_SURFACE_ID", "aetheria.hangar")')) {
    throw "Unity editor development must enter through the daemon-owned Hangar surface."
}

$uiToolkitPackage = Get-ChildItem (Join-Path $ProjectPath "Library/PackageCache") -Directory |
    Where-Object Name -Like "org.gamecult.eve.unity-uitoolkit@*" |
    Where-Object { (Get-Content (Join-Path $_.FullName "package.json") -Raw | ConvertFrom-Json).version -eq "0.1.9" } |
    Select-Object -First 1
if (-not $uiToolkitPackage) { throw "Resolved Eve Unity UI Toolkit 0.1.9 package is missing from Library/PackageCache." }
$uiToolkitLowererSource = Get-Content (Join-Path $uiToolkitPackage.FullName "Runtime/EveUiToolkitSurfaceLowerer.cs") -Raw
if ($uiToolkitLowererSource -notmatch 'unity-uitoolkit-.*Guid\.NewGuid') {
    throw "Resolved Eve Unity UI Toolkit commands do not mint invocation-scoped idempotency keys."
}
$receiptTransportSource = Get-Content (Join-Path $scenePackage.FullName "Runtime/CultMesh/EveUnityCultMeshLiveProviderTransport.cs") -Raw
foreach ($requiredReceiptFinality in @("_pendingCommands", "IsTerminalReceiptState", 'string.Equals(state, "pending"', "EveCommandInvocationHash.Compute(request)")) {
    if ($receiptTransportSource -notmatch [regex]::Escape($requiredReceiptFinality)) {
        throw "Resolved Eve Unity scene package is missing receipt finality boundary '$requiredReceiptFinality'."
    }
}
if ($uiToolkitLowererSource -match 'preview\s*==\s*null\s*\|\|\s*!preview\.IsValid') {
    throw "Unity inventory preview is suppressing a provider-owned drop command."
}

$launcherSource = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) "scripts/run-aetheria-unity.ps1") -Raw
if ($launcherSource -notmatch 'EVEUNITY_SURFACE_ID\s*=\s*"aetheria\.hangar"') {
    throw "Released launcher must enter through the daemon-owned Hangar surface."
}
if ($launcherSource -match '--terminus-scenario') {
    throw "Released launcher must not create product state through a Terminus proof-scenario flag."
}
foreach ($lifecycleBoundary in @('--lifecycle-pipe', 'NamedPipeClientStream', 'WriteLine("shutdown")', 'WaitForExit($waitMs)')) {
    if ($launcherSource -notmatch [regex]::Escape($lifecycleBoundary)) {
        throw "Released launcher is missing daemon lifecycle boundary '$lifecycleBoundary'."
    }
}
if ($launcherSource -match '"run"\s*,\s*"--project"' -or
    $launcherSource -notmatch [regex]::Escape('Aetheria.State.Daemon.dll') -or
    $launcherSource -notmatch [regex]::Escape('dotnet build $daemonProject')) {
    throw "Released launcher must build explicitly and own the direct dotnet daemon process, not a dotnet-run wrapper."
}
$editorDaemonSource = Get-Content (Join-Path $ProjectPath "Assets/Editor/AetheriaDaemonDevelopmentWindow.cs") -Raw
foreach ($lifecycleBoundary in @('--lifecycle-pipe', 'NamedPipeClientStream', 'RequestDaemonShutdown', 'MatchesProcessInstance', 'StartTime.ToUniversalTime().Ticks', 'WriteAllLines')) {
    if ($editorDaemonSource -notmatch [regex]::Escape($lifecycleBoundary)) {
        throw "Unity editor daemon owner is missing reattach-safe lifecycle boundary '$lifecycleBoundary'."
    }
}

Write-Host "Aetheria Unity client dependency and bootstrap verification passed."
