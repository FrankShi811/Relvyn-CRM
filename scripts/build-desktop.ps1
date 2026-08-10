[CmdletBinding()]
param(
  [ValidateSet('win-x64')]
  [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$localDotnet = Join-Path $root 'work\dotnet8\dotnet.exe'
$dotnet = $env:WAFLOW_DOTNET_PATH
if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet)) {
  $dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$work = Join-Path $root 'work'
$publish = Join-Path $work "publish\$Runtime-$([Guid]::NewGuid().ToString('N'))"
$rootOutput = Join-Path $root 'AI Sales OS.exe'
$rootBridgeOutput = Join-Path $root 'WAFlow.WhatsApp.Bridge.exe'
$bridgeBuild = Join-Path $root 'bridge\scripts\build-sea.mjs'

$env:DOTNET_CLI_HOME = $work
$env:NUGET_PACKAGES = Join-Path $work 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (Test-Path -LiteralPath $bridgeBuild) {
  $node = $env:WAFLOW_NODE_PATH
  if (-not $node -or -not (Test-Path -LiteralPath $node)) {
    $bundledNode = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
    if (Test-Path -LiteralPath $bundledNode) { $node = $bundledNode }
  }
  if (-not $node -or -not (Test-Path -LiteralPath $node)) {
    $nodeCommand = Get-Command node -ErrorAction SilentlyContinue
    if ($nodeCommand) { $node = $nodeCommand.Source }
  }
  if (-not $node -or -not (Test-Path -LiteralPath $node)) { throw 'Node.js is required to build the companion WhatsApp Bridge. Set WAFLOW_NODE_PATH.' }
  & $node $bridgeBuild
  if ($LASTEXITCODE -ne 0) { throw 'AI Sales OS WhatsApp bridge SEA build failed.' }
}

& $dotnet publish (Join-Path $root 'desktop\WAFlow.Desktop\WAFlow.Desktop.csproj') `
  -c Release -r $Runtime --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'AI Sales OS desktop publish failed.' }

$publishedExe = Join-Path $publish 'AISalesOS.exe'
$bridgeExe = Join-Path $root 'bridge\dist\WAFlow.WhatsApp.Bridge.exe'
if (-not (Test-Path -LiteralPath $bridgeExe)) { throw 'Built WhatsApp Bridge executable is missing.' }
Copy-Item -LiteralPath $bridgeExe -Destination (Join-Path $publish 'WAFlow.WhatsApp.Bridge.exe') -Force
try {
  Copy-Item -LiteralPath $publishedExe -Destination $rootOutput -Force
  Copy-Item -LiteralPath $bridgeExe -Destination $rootBridgeOutput -Force
}
catch [System.IO.IOException] {
  throw 'AI Sales OS.exe is currently running. Close the application and rebuild so the canonical EXE can be overwritten.'
}
$file = Get-Item -LiteralPath $rootOutput
$hash = Get-FileHash -LiteralPath $rootOutput -Algorithm SHA256
Write-Host "Created: $($file.FullName)"
Write-Host "Size: $([Math]::Round($file.Length / 1MB, 2)) MB"
Write-Host "SHA256: $($hash.Hash)"
try { Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction Stop }
catch { Write-Warning "Temporary publish directory could not be removed: $publish" }
