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
        "https://github.com/GameCult/CultLib.git?path=/unity/org.gamecult.cultlib#cultlib-unity-v1.0.45",
        "03840d0430bc727f1322861a1ab46b396eaca860")
    "org.gamecult.eve.surface" = @(
        "https://github.com/GameCult/Eve.git?path=/packages/org.gamecult.eve.surface#eve-surface-v0.3.0",
        "fe0dddf28267decbb416325a3d0b2c62432825c2")
    "org.gamecult.eve.unity-scene" = @(
        "https://github.com/GameCult/EveUnity.git?path=/packages/org.gamecult.eve.unity-scene#50cd42137a1b94fa8c578918463ddcd822d2bfc9",
        "50cd42137a1b94fa8c578918463ddcd822d2bfc9")
    "org.gamecult.eve.unity-uitoolkit" = @(
        "https://github.com/GameCult/EveUnity.git?path=/packages/org.gamecult.eve.unity-uitoolkit#50cd42137a1b94fa8c578918463ddcd822d2bfc9",
        "50cd42137a1b94fa8c578918463ddcd822d2bfc9")
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
    Where-Object { (Get-Content (Join-Path $_.FullName "package.json") -Raw | ConvertFrom-Json).version -eq "1.0.45" } |
    Select-Object -First 1
if (-not $cultLibPackage) { throw "Resolved CultLib 1.0.45 package is missing from Library/PackageCache." }

$meshAssembly = Join-Path $cultLibPackage.FullName "Runtime/Plugins/GameCult.Mesh.dll"
if (-not (Test-Path $meshAssembly)) { throw "Resolved GameCult.Mesh.dll is missing." }
$meshApi = & rg -a -o "CultMesh[A-Za-z]+" $meshAssembly 2>&1
if ($LASTEXITCODE -ne 0) { throw "Unable to inspect GameCult.Mesh.dll metadata.`n$meshApi" }
$requiredMeshApiNames = @(
    "CultMeshBodyPublicationDocument",
    "CultMeshBodyPublicationHandle",
    "CultMeshFrameBodyPublisher",
    "CultMeshTcpContentServer",
    "CultMeshSessionContentProvider")
foreach ($apiName in $requiredMeshApiNames) {
    if (-not ($meshApi | Where-Object { $_ -eq $apiName })) {
        throw "Resolved GameCult.Mesh.dll does not expose $apiName."
    }
}

$surfacePackage = Get-ChildItem (Join-Path $ProjectPath "Library/PackageCache") -Directory |
    Where-Object Name -Like "org.gamecult.eve.surface@*" |
    Where-Object { (Get-Content (Join-Path $_.FullName "package.json") -Raw | ConvertFrom-Json).version -eq "0.3.0" } |
    Select-Object -First 1
if (-not $surfacePackage) { throw "Resolved Eve surface 0.3.0 package is missing from Library/PackageCache." }
$inputContractSource = Get-Content (Join-Path $surfacePackage.FullName "Runtime/EveInputCapabilityDocument.cs") -Raw
foreach ($requiredInputContract in @("PayloadKeys", "CurrentValue", "ActionBar", "IconRef")) {
    if ($inputContractSource -notmatch $requiredInputContract) {
        throw "Resolved Eve surface package is missing input contract '$requiredInputContract'."
    }
}

$scenePackage = Get-ChildItem (Join-Path $ProjectPath "Library/PackageCache") -Directory |
    Where-Object Name -Like "org.gamecult.eve.unity-scene@*" |
    Where-Object { (Get-Content (Join-Path $_.FullName "package.json") -Raw | ConvertFrom-Json).version -eq "0.3.104" } |
    Select-Object -First 1
if (-not $scenePackage) { throw "Resolved Eve Unity scene 0.3.104 package is missing from Library/PackageCache." }
$advertisedInputSource = Get-Content (Join-Path $scenePackage.FullName "Runtime/EveUnityAdvertisedInputAction.cs") -Raw
$inputDriverSource = Get-Content (Join-Path $scenePackage.FullName "Runtime/EveUnityPlayableWorldInputDriver.cs") -Raw
$actionBarSource = Get-Content (Join-Path $scenePackage.FullName "Runtime/EveUnityInputActionBar.cs") -Raw
foreach ($requiredInputContract in @("view-direction.v1", "BuildViewDirectionPayload", "SubmitChangedPerformedActions", "scalar.v1", "BuildScalarPayload", "EveUnityInputActionBar")) {
    if (($advertisedInputSource + $inputDriverSource + $actionBarSource) -notmatch [regex]::Escape($requiredInputContract)) {
        throw "Resolved Eve Unity scene package is missing generic input lowering '$requiredInputContract'."
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
if ($bootstrapSource -notmatch [regex]::Escape('?? "aetheria.hangar"')) {
    throw "The standalone Unity client must default to the daemon-owned Hangar surface."
}

$editorBootstrapSource = Get-Content (Join-Path $ProjectPath "Assets/Editor/AetheriaDaemonDevelopmentWindow.cs") -Raw
if ($editorBootstrapSource -notmatch [regex]::Escape('SetEnvironmentVariable("EVEUNITY_SURFACE_ID", "aetheria.hangar")')) {
    throw "Unity editor development must enter through the daemon-owned Hangar surface."
}

$uiToolkitPackage = Get-ChildItem (Join-Path $ProjectPath "Library/PackageCache") -Directory |
    Where-Object Name -Like "org.gamecult.eve.unity-uitoolkit@*" |
    Select-Object -First 1
if (-not $uiToolkitPackage) { throw "Resolved Eve Unity UI Toolkit package is missing from Library/PackageCache." }
$uiToolkitLowererSource = Get-Content (Join-Path $uiToolkitPackage.FullName "Runtime/EveUiToolkitSurfaceLowerer.cs") -Raw
if ($uiToolkitLowererSource -notmatch 'unity-uitoolkit-.*Guid\.NewGuid') {
    throw "Resolved Eve Unity UI Toolkit commands do not mint invocation-scoped idempotency keys."
}

$launcherSource = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) "scripts/run-aetheria-unity.ps1") -Raw
if ($launcherSource -notmatch 'EVEUNITY_SURFACE_ID\s*=\s*"aetheria\.hangar"') {
    throw "Released launcher must enter through the daemon-owned Hangar surface."
}
if ($launcherSource -match '--terminus-scenario') {
    throw "Released launcher must not create product state through a Terminus proof-scenario flag."
}

Write-Host "Aetheria Unity client dependency and bootstrap verification passed."
