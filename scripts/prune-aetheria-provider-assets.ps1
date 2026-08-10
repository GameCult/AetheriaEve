param(
  [switch] $Apply
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$assetRoot = [System.IO.Path]::GetFullPath((Join-Path $root "Aetheria.Assets.Unity\Assets"))
$expectedRoot = Join-Path $root "Aetheria.Assets.Unity\Assets"
if (-not [System.IO.Path]::Equals($assetRoot, $expectedRoot)) {
  throw "Refusing to prune unexpected asset root: $assetRoot"
}

$closurePath = Join-Path $root "Aetheria.Assets.Unity\Build\provider-asset-dependencies.txt"
if (-not (Test-Path -LiteralPath $closurePath)) {
  throw "Provider dependency closure does not exist: $closurePath"
}

$keep = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$renderSettingsRoot = Join-Path $assetRoot "Settings"
$renderSettings = if (Test-Path -LiteralPath $renderSettingsRoot) {
  @(Get-ChildItem -LiteralPath $renderSettingsRoot -Recurse -File | ForEach-Object {
    $_.FullName.Substring($assetRoot.Length + 1)
  })
} else { @() }
$paths = @(Get-Content -LiteralPath $closurePath | ForEach-Object {
  ($_ -replace '^Assets/', '').Replace('/', '\')
}) + $renderSettings + @(
  'Editor\AetheriaGravityFogVerifier.cs',
  'Editor\AetheriaStardustContinuityVerifier.cs',
  'Editor\EveAssetBundleBuilder.cs',
  'Editor\EveEnvironmentProfileMigrator.cs',
  'Shaders\PackFloat.cginc',
  'Shaders\Volumetric.cginc'
)

foreach ($relativePath in $paths) {
  [void] $keep.Add($relativePath)
  [void] $keep.Add("$relativePath.meta")
  $parent = Split-Path $relativePath -Parent
  while ($parent) {
    [void] $keep.Add("$parent.meta")
    $parent = Split-Path $parent -Parent
  }
}

$remove = @(Get-ChildItem -LiteralPath $assetRoot -Recurse -File | Where-Object {
  -not $keep.Contains($_.FullName.Substring($assetRoot.Length + 1))
})
Write-Host "Provider asset closure keeps $($keep.Count) paths; $($remove.Count) files are outside it."
if (-not $Apply) {
  $remove | ForEach-Object { $_.FullName.Substring($assetRoot.Length + 1) }
  exit 0
}

foreach ($file in $remove) {
  if (-not $file.FullName.StartsWith("$assetRoot\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove path outside asset root: $($file.FullName)"
  }
  Remove-Item -LiteralPath $file.FullName -Force
}

Get-ChildItem -LiteralPath $assetRoot -Recurse -Directory |
  Sort-Object FullName -Descending |
  Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
  Remove-Item -Force

Write-Host "Removed $($remove.Count) files outside the provider dependency closure."
