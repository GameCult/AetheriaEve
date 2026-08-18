param(
    [string] $CultLibRoot = "",
    [string] $EveRoot = "",
    [string] $NodePath = "",
    [string] $ChromePath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectsRoot = Split-Path -Parent $repoRoot
if ([string]::IsNullOrWhiteSpace($CultLibRoot)) { $CultLibRoot = Join-Path $projectsRoot "CultLib" }
if ([string]::IsNullOrWhiteSpace($EveRoot)) { $EveRoot = Join-Path $projectsRoot "Eve" }

foreach ($dependency in @(
    @{ Name = "CultLib"; Path = $CultLibRoot },
    @{ Name = "Eve"; Path = $EveRoot }
)) {
    if (-not (Test-Path -LiteralPath $dependency.Path -PathType Container)) {
        throw "$($dependency.Name) root does not exist: $($dependency.Path)"
    }
}

if ([string]::IsNullOrWhiteSpace($NodePath)) {
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) {
        $NodePath = $node.Source
    } else {
        $NodePath = Join-Path $env:ProgramFiles "nodejs\node.exe"
    }
}
if (-not (Test-Path -LiteralPath $NodePath -PathType Leaf)) {
    throw "Node.js was not found. Pass -NodePath explicitly."
}

if ([string]::IsNullOrWhiteSpace($ChromePath)) {
    $ChromePath = Join-Path $env:ProgramFiles "Google\Chrome\Application\chrome.exe"
}
if (-not (Test-Path -LiteralPath $ChromePath -PathType Leaf)) {
    throw "Chrome was not found. Pass -ChromePath explicitly."
}

$previousChromePath = $env:CHROME_PATH
try {
    $env:CHROME_PATH = $ChromePath
    & $NodePath (Join-Path $PSScriptRoot "verify-aetheria-browser-provider.mjs") `
        --cultlib-root $CultLibRoot `
        --eve-root $EveRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Aetheria browser provider witness failed with exit code $LASTEXITCODE."
    }
} finally {
    $env:CHROME_PATH = $previousChromePath
}

Write-Host "Aetheria daemon -> CultMesh -> Chromium Eve surface and command witness passed."
