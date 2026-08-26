param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $State = "",
  [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "Aetheria.Assets.Unity"
$statePath = if ([string]::IsNullOrWhiteSpace($State)) {
  Join-Path $root "Aetheria.Unity\Build\aetheria-unity.cc"
} else {
  [System.IO.Path]::GetFullPath($State)
}

if (-not (Test-Path -LiteralPath $project -PathType Container)) {
  throw "Aetheria asset-authoring project is missing: $project"
}
if (-not $SkipBuild -and -not (Test-Path -LiteralPath "$statePath.records" -PathType Container)) {
  throw "Imported Aetheria state records are missing: $statePath.records"
}

$sourceFiles = @(Get-ChildItem (Join-Path $project "Assets") -Recurse -Filter *.cs |
  ForEach-Object { $_.FullName.Substring($project.Length + 1).Replace('\', '/') })
$expectedSources = @(
  "Assets/Editor/AetheriaGravityFogVerifier.cs",
  "Assets/Editor/AetheriaStardustContinuityVerifier.cs",
  "Assets/Editor/EveAssetBundleBuilder.cs",
  "Assets/Editor/EveEnvironmentProfileMigrator.cs"
)
$unexpected = @($sourceFiles | Where-Object { $_ -notin $expectedSources })
$missing = @($expectedSources | Where-Object { $_ -notin $sourceFiles })
if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
  throw "Asset authoring source boundary failed. Unexpected: $($unexpected -join ', '); missing: $($missing -join ', ')."
}

$manifest = Get-Content (Join-Path $project "Packages\manifest.json") -Raw | ConvertFrom-Json
$lock = Get-Content (Join-Path $project "Packages\packages-lock.json") -Raw | ConvertFrom-Json
$statePackage = Get-Content (Join-Path $root "Packages\org.gamecult.aetheria.state\package.json") -Raw | ConvertFrom-Json
$cultLibRef = "https://github.com/GameCult/CultLib.git?path=/unity/org.gamecult.cultlib#334e60f1928b4212a29dd8b0d19b2c099fe6365e"
$surfaceRef = "https://github.com/GameCult/Eve.git?path=/packages/org.gamecult.eve.surface#96839ad34c8d464ef622d8bbdd5d277e1ca9d825"
if ($manifest.dependencies.'org.gamecult.aetheria.state' -ne 'file:../../Packages/org.gamecult.aetheria.state') {
  throw "Asset authoring must consume the repository-owned typed state package."
}
if ($manifest.dependencies.'org.gamecult.cultlib' -ne $cultLibRef -or
    $lock.dependencies.'org.gamecult.cultlib'.version -ne $cultLibRef -or
    $lock.dependencies.'org.gamecult.cultlib'.hash -ne '334e60f1928b4212a29dd8b0d19b2c099fe6365e') {
  throw "Asset authoring must resolve the CultLib 1.0.56 coherent-generation API commit."
}
if ($manifest.dependencies.'org.gamecult.eve.surface' -ne $surfaceRef -or
    $lock.dependencies.'org.gamecult.eve.surface'.version -ne $surfaceRef -or
    $lock.dependencies.'org.gamecult.eve.surface'.hash -ne '96839ad34c8d464ef622d8bbdd5d277e1ca9d825') {
  throw "Asset authoring must resolve the same Eve surface contract as the player client."
}
$requiredStateDependencies = @{
  'org.gamecult.cultlib' = '1.0.56'
  'org.gamecult.cultmath' = '0.1.2'
  'org.gamecult.eve.plugin-fields' = '0.2.3'
  'org.gamecult.eve.surface' = '0.3.6'
}
foreach ($dependencyName in $requiredStateDependencies.Keys) {
  if ($statePackage.dependencies.$dependencyName -ne $requiredStateDependencies[$dependencyName] -or
      $lock.dependencies.'org.gamecult.aetheria.state'.dependencies.$dependencyName -ne $requiredStateDependencies[$dependencyName]) {
    throw "Typed state package dependency '$dependencyName' is missing or stale in the asset-authoring graph."
  }
}
foreach ($forbidden in @("org.gamecult.aetheria.eve-runtime", "org.gamecult.eve.unity-scene", "org.gamecult.eve.unity-uitoolkit")) {
  if ($manifest.dependencies.PSObject.Properties.Name -contains $forbidden) {
    throw "Asset authoring gained a forbidden client/runtime dependency: $forbidden"
  }
}

if (-not $SkipBuild) {
  $log = Join-Path $project "Build\Logs\asset-verification.log"
  New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
  $env:AETHERIA_ASSET_CATALOG_STATE = $statePath
  $process = Start-Process $UnityExe -ArgumentList @(
    "-batchmode", "-quit", "-projectPath", $project,
    "-cacheServerEnableDownload", "false", "-cacheServerEnableUpload", "false",
    "-executeMethod", "Aetheria.Editor.EveAssetBundleBuilder.BuildWindows",
    "-logFile", $log
  ) -PassThru -WindowStyle Hidden
  Write-Host "Asset verification PID: $($process.Id)"
  Write-Host "Asset verification log: $log"
  if (-not $process.WaitForExit(600000)) {
    Stop-Process $process.Id -Force
    throw "Aetheria asset verification timed out."
  }
  if ($process.ExitCode -ne 0) {
    Get-Content $log -Tail 120
    throw "Aetheria asset verification failed with exit code $($process.ExitCode)."
  }
}

Write-Host "Aetheria asset-authoring boundary verification passed."
