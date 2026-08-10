[CmdletBinding()]
param(
  [string]$PackageRoot = '',
  [string]$SourceArchive = '',
  [switch]$ReleaseGate
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$errors = [Collections.Generic.List[string]]::new()
function Add-ComplianceError([string]$Message) { $script:errors.Add($Message) }
function Require-File([string]$Path, [string]$Label) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Add-ComplianceError "Missing ${Label}: $Path" }
}
function Get-Sha([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }

foreach ($file in @('LICENSE', 'EULA.md', 'PRIVACY.md', 'THIRD_PARTY_NOTICES.md')) {
  Require-File (Join-Path $root $file) $file
}
Require-File (Join-Path $root 'bridge\LICENSE.md') 'Bridge GPL license declaration'
Require-File (Join-Path $root 'docs\BRIDGE_GPL_COMPLIANCE.md') 'Bridge installation information'
Require-File (Join-Path $root 'licenses\third-party\LIBSIGNAL-6.0.0-GPL-3.0.txt') 'GPL-3.0 license text'

$bridgePackage = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\package.json') | ConvertFrom-Json
if ($bridgePackage.license -ne 'GPL-3.0-only') { Add-ComplianceError 'bridge/package.json must declare GPL-3.0-only.' }
$bridgeLock = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\pnpm-lock.yaml')
if ($bridgeLock -notmatch 'libsignal@6\.0\.0') { Add-ComplianceError 'Expected resolved libsignal@6.0.0 evidence is missing from bridge/pnpm-lock.yaml.' }

$coreProject = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\WAFlow.Core.csproj')
if ($coreProject -match 'WAFlow\.WhatsApp\.Bridge\.exe') { Add-ComplianceError 'The GPL Bridge must not be embedded into WAFlow.Core or AISalesOS.exe.' }
$bridgeClient = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppBridgeClient.cs')
if ($bridgeClient -notmatch 'AI_SALES_OS_WHATSAPP_BRIDGE_PATH' -or
    $bridgeClient -match 'GetManifestResourceStream\("WAFlow\.WhatsApp\.Bridge\.exe"\)') {
  Add-ComplianceError 'Desktop must prefer the documented user-replaceable Bridge path and must not extract an embedded Bridge.'
}
foreach ($buildScript in @('scripts\build-desktop.ps1', 'scripts\build-velopack-release.ps1')) {
  $text = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root $buildScript)
  if ($text -notmatch "Copy-Item.*WAFlow\.WhatsApp\.Bridge\.exe|bridgeExe") {
    Add-ComplianceError "$buildScript must package the Bridge as a separate companion file."
  }
}

$bridgeSources = @(Get-ChildItem (Join-Path $root 'bridge\src'), (Join-Path $root 'bridge\scripts') -Recurse -File -Include '*.mjs','*.cjs')
foreach ($source in $bridgeSources) {
  $text = Get-Content -Raw -Encoding utf8 -LiteralPath $source.FullName
  if (-not $text.StartsWith('// SPDX-License-Identifier: GPL-3.0-only')) {
    Add-ComplianceError "Bridge source is missing GPL SPDX header: $($source.FullName.Substring($root.Length + 1))"
  }
}

$requirements = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'compliance\release-requirements.json') | ConvertFrom-Json
foreach ($requirement in $requirements.requirements) {
  if ($requirement.status -ne 'resolved') { Add-ComplianceError "Release requirement remains open: $($requirement.id)" }
}

$brandManifestPath = Join-Path $root 'docs\brand\generation-records\brand-assets-manifest.json'
Require-File $brandManifestPath 'brand provenance manifest'
if (Test-Path -LiteralPath $brandManifestPath) {
  $brand = Get-Content -Raw -Encoding utf8 -LiteralPath $brandManifestPath | ConvertFrom-Json
  foreach ($entry in @(
    [pscustomobject]@{ path = $brand.prompt; sha256 = $brand.promptSha256 },
    [pscustomobject]@{ path = $brand.originalMaster; sha256 = $brand.originalMasterSha256 }
  ) + @($brand.assets)) {
    $assetPath = Join-Path $root ([string]$entry.path)
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
      Add-ComplianceError "Brand provenance file is missing: $($entry.path)"
    }
    elseif ((Get-Sha $assetPath) -ne [string]$entry.sha256) {
      Add-ComplianceError "Brand provenance hash mismatch: $($entry.path)"
    }
  }
}

if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) {
  $PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
  foreach ($file in @(
    'AISalesOS.exe',
    'WAFlow.WhatsApp.Bridge.exe',
    'LICENSE',
    'EULA.md',
    'PRIVACY.md',
    'THIRD_PARTY_NOTICES.md',
    'BRIDGE_GPL_COMPLIANCE.md',
    'licenses\third-party\LIBSIGNAL-6.0.0-GPL-3.0.txt'
  )) { Require-File (Join-Path $PackageRoot $file) "packaged $file" }
}

if (-not [string]::IsNullOrWhiteSpace($SourceArchive)) {
  $SourceArchive = [IO.Path]::GetFullPath($SourceArchive)
  Require-File $SourceArchive 'Bridge corresponding-source archive'
  if (Test-Path -LiteralPath $SourceArchive) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($SourceArchive)
    try {
      $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/').TrimStart('.', '/') })
      foreach ($pattern in @(
        'package.json',
        'pnpm-lock.yaml',
        'COPYING',
        'INSTALL.md',
        'SOURCE-MANIFEST.json',
        'src/index.mjs',
        'scripts/build-sea.mjs',
        'node_modules/.pnpm/libsignal@6.0.0/node_modules/libsignal/package.json'
      )) {
        if ($names -notcontains $pattern) { Add-ComplianceError "Bridge source archive is missing $pattern" }
      }
    }
    finally { $zip.Dispose() }
  }
}
elseif ($ReleaseGate) {
  Add-ComplianceError 'ReleaseGate requires the Bridge corresponding-source archive path.'
}

if ($errors.Count -gt 0) {
  foreach ($message in $errors) { Write-Error $message -ErrorAction Continue }
  throw "Compliance checks failed with $($errors.Count) error(s)."
}

Write-Host "PASS compliance checks. Release gate: $ReleaseGate"
