param(
    [string] $CultLibRoot = "",
    [string] $EveRoot = "",
    [string] $YmirRoot = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectsRoot = Split-Path -Parent $repoRoot
if ([string]::IsNullOrWhiteSpace($CultLibRoot)) { $CultLibRoot = Join-Path $projectsRoot "CultLib" }
if ([string]::IsNullOrWhiteSpace($EveRoot)) { $EveRoot = Join-Path $projectsRoot "Eve" }
if ([string]::IsNullOrWhiteSpace($YmirRoot)) { $YmirRoot = Join-Path $projectsRoot "Ymir" }
$project = Join-Path $repoRoot "Aetheria.State.Daemon.Smoke\Aetheria.State.Daemon.Smoke.csproj"
$progressionProject = Join-Path $repoRoot "Aetheria.State.ProgressionSmoke\Aetheria.State.ProgressionSmoke.csproj"

& (Join-Path $PSScriptRoot "verify-portable-game-framework.ps1") -Root $repoRoot

foreach ($dependency in @(
    @{ Name = "CultLib"; Path = $CultLibRoot },
    @{ Name = "Eve"; Path = $EveRoot },
    @{ Name = "Ymir"; Path = $YmirRoot }
)) {
    if (-not (Test-Path -LiteralPath $dependency.Path -PathType Container)) {
        throw "$($dependency.Name) root does not exist: $($dependency.Path)"
    }
}

$ymirSession = Join-Path $YmirRoot "src\Ymir.Core\YmirSession.cs"
$box2DProject = Join-Path $YmirRoot "src\Ymir.Box3D\Ymir.Box3D.csproj"
$box2DSource = Join-Path $YmirRoot "extern\box3d\CMakeLists.txt"
if (-not (Test-Path -LiteralPath $ymirSession -PathType Leaf) -or
    -not (Test-Path -LiteralPath $box2DProject -PathType Leaf)) {
    throw "Ymir does not expose the retained Box2D session API required by Aetheria. Use the current Aetheria integration revision."
}
if (-not (Test-Path -LiteralPath $box2DSource -PathType Leaf)) {
    throw "Ymir's pinned Box2D submodule is missing. Run 'git -C $YmirRoot submodule update --init --recursive'."
}

$properties = @(
    "-p:CultLibRoot=$CultLibRoot",
    "-p:EveRoot=$EveRoot",
    "-p:YmirRoot=$YmirRoot"
)

& dotnet restore $project @properties
if ($LASTEXITCODE -ne 0) {
    throw "Aetheria daemon dependency restore failed with exit code $LASTEXITCODE."
}

& dotnet build $project @properties --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Aetheria daemon smoke build failed with exit code $LASTEXITCODE."
}
$smokeAssembly = Join-Path $repoRoot "Aetheria.State.Daemon.Smoke\bin\Debug\net10.0\Aetheria.State.Daemon.Smoke.dll"
& dotnet $smokeAssembly
if ($LASTEXITCODE -ne 0) { throw "Aetheria daemon smoke failed with exit code $LASTEXITCODE." }

& dotnet build $progressionProject @properties
if ($LASTEXITCODE -ne 0) {
    throw "Aetheria progression Verse smoke build failed with exit code $LASTEXITCODE."
}
$progressionAssembly = Join-Path $repoRoot "Aetheria.State.ProgressionSmoke\bin\Debug\net10.0\Aetheria.State.ProgressionSmoke.dll"
& dotnet $progressionAssembly
if ($LASTEXITCODE -ne 0) { throw "Aetheria progression Verse smoke failed with exit code $LASTEXITCODE." }

Write-Host "Aetheria daemon dependency-root and simulation verification passed."
