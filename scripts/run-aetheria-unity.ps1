param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [int] $Port = 3076,
  [string] $State = "",
  [string[]] $OdinDiscoveryEndpoint = @(),
  [string[]] $OdinRootP256 = @(),
  [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "Aetheria.Unity"
$assetProject = Join-Path $root "Aetheria.Assets.Unity"
$assetBundleRoot = Join-Path $assetProject "Build\EveAssets"
$artifacts = Join-Path $project "Build\Logs"
$clientExe = Join-Path $project "Build\Windows\Aetheria.exe"
$daemonProject = Join-Path $root "Aetheria.State.Daemon\Aetheria.State.Daemon.csproj"
$daemonDll = Join-Path $root "Aetheria.State.Daemon\bin\Debug\net10.0\Aetheria.State.Daemon.dll"
$importProject = Join-Path $root "Aetheria.State.Import\Aetheria.State.Import.csproj"
$state = if ([string]::IsNullOrWhiteSpace($State)) {
  Join-Path $project "Build\aetheria-unity.cc"
} else {
  [System.IO.Path]::GetFullPath($State)
}
$stateRecords = "$state.records"
New-Item -ItemType Directory -Force $artifacts | Out-Null

if (-not (Test-Path $stateRecords)) {
  dotnet run --project $importProject -- $root $state
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path $stateRecords)) {
    throw "Aetheria typed state import failed with exit code $LASTEXITCODE"
  }
}

if (-not $SkipBuild) {
  $env:AETHERIA_ASSET_CATALOG_STATE = $state
  $env:AETHERIA_EVE_BUNDLE_OUTPUT = Join-Path $assetBundleRoot "StandaloneWindows64"
  $assetLog = Join-Path $artifacts "asset-bundles.log"
  $assets = Start-Process $UnityExe -ArgumentList @(
    "-batchmode", "-quit", "-projectPath", $assetProject,
    "-cacheServerEnableDownload", "false", "-cacheServerEnableUpload", "false",
    "-executeMethod", "Aetheria.Editor.EveAssetBundleBuilder.BuildWindows",
    "-logFile", $assetLog
  ) -PassThru -WindowStyle Hidden
  Write-Host "Asset build PID: $($assets.Id)"
  Write-Host "Asset build log: $assetLog"
  if (-not $assets.WaitForExit(600000)) { Stop-Process $assets.Id -Force; throw "Asset build timed out." }
  if ($assets.ExitCode -ne 0) { Get-Content $assetLog -Tail 120; throw "Asset build failed." }

  $clientLog = Join-Path $artifacts "client-build.log"
  $build = Start-Process $UnityExe -ArgumentList @(
    "-batchmode", "-quit", "-projectPath", $project,
    "-cacheServerEnableDownload", "false", "-cacheServerEnableUpload", "false",
    "-executeMethod", "AetheriaUnityBuild.BuildWindows",
    "-logFile", $clientLog
  ) -PassThru -WindowStyle Hidden
  Write-Host "Client build PID: $($build.Id)"
  Write-Host "Client build log: $clientLog"
  if (-not $build.WaitForExit(600000)) { Stop-Process $build.Id -Force; throw "Client build timed out." }
  if ($build.ExitCode -ne 0 -or -not (Test-Path $clientExe)) { Get-Content $clientLog -Tail 120; throw "Client build failed." }
}

if (-not (Test-Path $clientExe)) { throw "Client executable not found. Run without -SkipBuild first." }

dotnet build $daemonProject
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $daemonDll)) {
  throw "Aetheria daemon build failed with exit code $LASTEXITCODE."
}

function Stop-AetheriaDaemon([System.Diagnostics.Process] $Daemon, [string] $PipeName) {
  if ($null -eq $Daemon -or $Daemon.HasExited) { return }

  $pipe = $null
  $writer = $null
  $requested = $false
  try {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $PipeName, [System.IO.Pipes.PipeDirection]::Out)
    $pipe.Connect(2000)
    $writer = [System.IO.StreamWriter]::new($pipe)
    $writer.AutoFlush = $true
    $writer.WriteLine("shutdown")
    $requested = $true
  }
  catch {
    Write-Warning "Could not request graceful Aetheria daemon shutdown: $($_.Exception.Message)"
  }
  finally {
    if ($null -ne $writer) { $writer.Dispose() }
    elseif ($null -ne $pipe) { $pipe.Dispose() }
  }

  $waitMs = if ($requested) { 5000 } else { 250 }
  if ($Daemon.WaitForExit($waitMs)) {
    $Daemon.WaitForExit()
    return
  }

  Write-Warning "Aetheria daemon did not checkpoint and exit within $waitMs ms; escalating termination."
  Stop-Process -Id $Daemon.Id -Force
  if (-not $Daemon.WaitForExit(5000)) {
    throw "Aetheria daemon PID $($Daemon.Id) survived graceful shutdown and forced termination."
  }
  $Daemon.WaitForExit()
}

$daemonLog = Join-Path $artifacts "daemon.log"
$lifecyclePipe = "aetheria-unity-daemon-$([guid]::NewGuid().ToString('N'))"
$daemonArguments = @(
  $daemonDll,
  "--root", $root,
  "--state", $state,
  "--client-cultmesh-host", "127.0.0.1",
  "--client-cultmesh-advertise-host", "127.0.0.1",
  "--client-cultmesh-port", $Port,
  "--lifecycle-pipe", $lifecyclePipe,
  "--asset-bundle-root", $assetBundleRoot,
  "--tick-interval-ms", 20,
  "--fixed-delta-ms", 20,
  "--no-odin-announcements"
)
foreach ($endpoint in $OdinDiscoveryEndpoint) {
  if (-not [string]::IsNullOrWhiteSpace($endpoint)) {
    $daemonArguments += @("--odin-discovery-endpoint", $endpoint.Trim())
  }
}
foreach ($rootKey in $OdinRootP256) {
  if (-not [string]::IsNullOrWhiteSpace($rootKey)) {
    $daemonArguments += @("--odin-root-p256", $rootKey.Trim())
  }
}
$daemon = Start-Process dotnet -ArgumentList $daemonArguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $daemonLog -RedirectStandardError "$daemonLog.error"
Write-Host "Daemon PID: $($daemon.Id)"
Write-Host "Daemon log: $daemonLog"

try {
  $ready = $false
  # A cold daemon build can take several minutes because the state schema
  # generator and the Ymir/CultMesh dependency graph compile before Program
  # publishes the endpoint. Readiness belongs to the endpoint, not an
  # arbitrary 30-second process lifetime.
  for ($i = 0; $i -lt 600; $i++) {
    if ($daemon.HasExited) { throw "Aetheria daemon exited. See $daemonLog" }
    if ((Test-Path $daemonLog) -and (Select-String $daemonLog -Pattern "Aetheria client CultMesh endpoint: cultnet\+tcp://127.0.0.1:$Port" -Quiet)) { $ready = $true; break }
    Start-Sleep -Milliseconds 500
  }
  if (-not $ready) { throw "Aetheria daemon did not publish its Eve endpoint." }
  $env:EVEUNITY_RENDEZVOUS_ENDPOINT = "cultnet+tcp://127.0.0.1:$Port"
  $env:EVEUNITY_SURFACE_ID = "aetheria.hangar"
  $env:AETHERIA_ODIN_ROOT_P256 = ($OdinRootP256 | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ";"
  $client = Start-Process $clientExe -ArgumentList "-force-d3d11" -PassThru
  Write-Host "Client PID: $($client.Id)"
  Write-Host "Close the client window to stop the daemon."
  $client.WaitForExit()
}
finally {
  Stop-AetheriaDaemon $daemon $lifecyclePipe
}
