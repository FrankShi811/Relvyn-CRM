[CmdletBinding()]
param(
  [string]$PreviousTag = 'v5.21.1',
  [string]$Repository = 'FrankShi811/Relvyn-CRM',
  [string]$PreviousPackagePath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workRoot = [IO.Path]::GetFullPath((Join-Path $root 'work'))
$probeRoot = [IO.Path]::GetFullPath((Join-Path $workRoot "connector-upgrade-$([Guid]::NewGuid().ToString('N'))"))
if (-not $probeRoot.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
  throw "Upgrade probe directory escaped the workspace work directory: $probeRoot"
}

$dotnet = $env:WAFLOW_DOTNET_PATH
if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet)) {
  $localDotnet = Join-Path $root 'work\dotnet8\dotnet.exe'
  $sharedDotnet = 'D:\whatsapp 自动化\work\dotnet8\dotnet.exe'
  if (Test-Path -LiteralPath $localDotnet) { $dotnet = $localDotnet }
  elseif (Test-Path -LiteralPath $sharedDotnet) { $dotnet = $sharedDotnet }
  else { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
}

$previousManager = (git show "$PreviousTag`:desktop/WAFlow.Core/Services/WhatsAppConnectionManager.cs") -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Unable to read previous connector source from $PreviousTag." }
$previousBridgeClient = (git show "$PreviousTag`:desktop/WAFlow.Core/Services/WhatsAppBridgeClient.cs") -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Unable to read previous bridge client source from $PreviousTag." }
$previousWorkspace = (git show "$PreviousTag`:desktop/WAFlow.Core/Infrastructure/DataWorkspaceManager.cs") -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Unable to read previous workspace source from $PreviousTag." }
$currentManager = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppConnectionManager.cs')
$currentBridgeClient = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppBridgeClient.cs')
$currentWorkspace = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Infrastructure\DataWorkspaceManager.cs')
foreach ($invariant in @(
  'whatsapp-sessions',
  'creds.json.enc',
  'WAFlow/WhatsAppSessionKey/',
  'private const string DatabaseFileName = "waflow.db";'
)) {
  $previousCombined = $previousManager + $previousBridgeClient + $previousWorkspace
  $currentCombined = $currentManager + $currentBridgeClient + $currentWorkspace
  if (-not $previousCombined.Contains($invariant) -or -not $currentCombined.Contains($invariant)) {
    throw "Upgrade compatibility invariant changed or disappeared: $invariant"
  }
}
Write-Host "PASS  $PreviousTag and current source retain database, session and credential identities"

[IO.Directory]::CreateDirectory($probeRoot) | Out-Null
$download = Join-Path $probeRoot 'download'
$expanded = Join-Path $probeRoot 'previous-package'
$workspace = Join-Path $probeRoot 'workspace'
[IO.Directory]::CreateDirectory($download) | Out-Null
[IO.Directory]::CreateDirectory($expanded) | Out-Null
[IO.Directory]::CreateDirectory($workspace) | Out-Null

try {
  if ($PreviousPackagePath) {
    $resolvedPackage = (Resolve-Path -LiteralPath $PreviousPackagePath -ErrorAction Stop).Path
    $package = Get-Item -LiteralPath $resolvedPackage
    if ($package.Extension -ne '.nupkg') { throw "Previous package must be a Velopack .nupkg file: $resolvedPackage" }
    Write-Host "INFO  using local read-only previous package $($package.Name)"
  }
  else {
    gh release download $PreviousTag --repo $Repository --pattern '*-full.nupkg' --dir $download
    if ($LASTEXITCODE -ne 0) { throw "Unable to download previous formal package $PreviousTag from $Repository." }
    $package = Get-ChildItem -LiteralPath $download -Filter '*-full.nupkg' | Select-Object -First 1
    if (-not $package) { throw "Previous release did not contain a full Velopack package: $PreviousTag" }
  }
  [IO.Compression.ZipFile]::ExtractToDirectory($package.FullName, $expanded)
  $previousExe = Get-ChildItem -LiteralPath $expanded -Recurse -Filter 'AISalesOS.exe' | Select-Object -First 1
  if (-not $previousExe) { throw "Previous formal package did not contain AISalesOS.exe: $($package.FullName)" }

  $previousDatabaseOverride = $env:WAFLOW_DATABASE_PATH
  $previousSingleInstanceScope = $env:WAFLOW_SINGLE_INSTANCE_SCOPE
  try {
    $env:WAFLOW_DATABASE_PATH = Join-Path $workspace 'waflow.db'
    $env:WAFLOW_SINGLE_INSTANCE_SCOPE = "connector-upgrade-old-$([Guid]::NewGuid().ToString('N'))"
    $oldApp = Start-Process -FilePath $previousExe.FullName -PassThru -WindowStyle Hidden
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(35)
    do {
      Start-Sleep -Milliseconds 500
      $oldApp.Refresh()
      if ($oldApp.HasExited) { throw "Previous formal application exited during isolated workspace initialization. code=$($oldApp.ExitCode)" }
    } until ((Test-Path -LiteralPath $env:WAFLOW_DATABASE_PATH -PathType Leaf) -or [DateTimeOffset]::UtcNow -ge $deadline)
    if (-not (Test-Path -LiteralPath $env:WAFLOW_DATABASE_PATH -PathType Leaf)) { throw 'Previous formal application did not initialize its isolated database.' }
    Start-Sleep -Seconds 2
    if (-not $oldApp.CloseMainWindow()) { Stop-Process -Id $oldApp.Id -Force }
    elseif (-not $oldApp.WaitForExit(10000)) { Stop-Process -Id $oldApp.Id -Force }
  }
  finally {
    $env:WAFLOW_DATABASE_PATH = $previousDatabaseOverride
    $env:WAFLOW_SINGLE_INSTANCE_SCOPE = $previousSingleInstanceScope
    if ($oldApp -and -not $oldApp.HasExited) { Stop-Process -Id $oldApp.Id -Force -ErrorAction SilentlyContinue }
  }

  $account = 'upgrade_probe'
  $session = Join-Path $workspace "whatsapp-sessions\$account"
  [IO.Directory]::CreateDirectory($session) | Out-Null
  [IO.File]::WriteAllText((Join-Path $session 'creds.json.enc'), '{"version":1,"probe":"previous-release-session-layout"}', [Text.UTF8Encoding]::new($false))

  & $dotnet run --project (Join-Path $root 'desktop\WAFlow.SmokeTests\WAFlow.SmokeTests.csproj') -c Release --no-build -- --connector-upgrade-probe $workspace $account
  if ($LASTEXITCODE -ne 0) { throw "$PreviousTag to current connector upgrade probe failed." }
  Write-Host "PASS  $($package.Name) workspace opens in current code with database families and encrypted-session marker preserved"
}
finally {
  $resolvedProbe = [IO.Path]::GetFullPath($probeRoot)
  if ($resolvedProbe.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
      (Test-Path -LiteralPath $resolvedProbe)) {
    [IO.Directory]::Delete($resolvedProbe, $true)
  }
}
