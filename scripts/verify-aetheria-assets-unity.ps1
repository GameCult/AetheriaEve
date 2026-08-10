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

$manifest = Get-Content (Join-Path $project "Packages\manifest.json") -Raw
if ($manifest -notmatch 'file:\.\./\.\./Packages/org\.gamecult\.aetheria\.state') {
  throw "Asset authoring must consume the repository-owned typed state package."
}
foreach ($forbidden in @("org.gamecult.aetheria.eve-runtime", "org.gamecult.eve.unity-scene", "org.gamecult.eve.unity-uitoolkit")) {
  if ($manifest -match [regex]::Escape($forbidden)) {
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
