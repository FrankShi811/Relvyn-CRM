[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Version,
  [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') { throw "Invalid release version: $Version" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist\source' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$workRoot = [IO.Path]::GetFullPath((Join-Path $root 'work'))
$stage = Join-Path $workRoot "g\$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$stage = [IO.Path]::GetFullPath($stage)
if (-not $stage.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Bridge source staging path must stay below the workspace work directory.'
}

$pnpm = (Get-Command pnpm -ErrorAction Stop).Source
$archive = Join-Path $OutputDirectory "AI-Sales-OS-WhatsApp-Bridge-$Version-source.zip"
function Get-LongPathSha256([string]$Path) {
  $extendedPath = if ($Path.StartsWith('\\?\')) { $Path } else { '\\?\' + $Path }
  $stream = [IO.File]::Open($extendedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
  try {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $sha.Dispose() }
  }
  finally { $stream.Dispose() }
}
try {
  & $pnpm --dir (Join-Path $root 'bridge') --filter waflow-whatsapp-bridge deploy --legacy $stage
  if ($LASTEXITCODE -ne 0) { throw 'pnpm deploy failed while collecting complete Bridge dependency sources.' }

  # pnpm creates junctions that point back into .pnpm. The real package source
  # directories already live inside that store, so remove only the redundant
  # reparse-point directory entries before hashing and archiving. This avoids
  # duplicate trees and Windows ZIP traversal failures without deleting targets.
  $reparseDirectories = @(Get-ChildItem -LiteralPath $stage -Recurse -Force -Directory | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
  } | Sort-Object { $_.FullName.Length } -Descending)
  foreach ($directory in $reparseDirectories) { [IO.Directory]::Delete($directory.FullName, $false) }

  Copy-Item -LiteralPath (Join-Path $root 'bridge\pnpm-lock.yaml') -Destination (Join-Path $stage 'pnpm-lock.yaml') -Force
  Copy-Item -LiteralPath (Join-Path $root 'bridge\LICENSE.md') -Destination (Join-Path $stage 'LICENSE.md') -Force
  Copy-Item -LiteralPath (Join-Path $root 'licenses\third-party\LIBSIGNAL-6.0.0-GPL-3.0.txt') -Destination (Join-Path $stage 'COPYING') -Force
  Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $stage 'THIRD_PARTY_NOTICES.md') -Force
  Copy-Item -LiteralPath (Join-Path $root 'docs\BRIDGE_GPL_COMPLIANCE.md') -Destination (Join-Path $stage 'INSTALL.md') -Force

  $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName)
  $entries = foreach ($file in $files) {
    [ordered]@{
      path = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
      sha256 = Get-LongPathSha256 $file.FullName
      bytes = $file.Length
    }
  }
  $manifest = [ordered]@{
    schemaVersion = 1
    releaseVersion = $Version
    nodeVersion = '22.23.1'
    pnpmVersion = '10.14.0'
    fileCount = $entries.Count
    files = @($entries)
  }
  [IO.File]::WriteAllText(
    (Join-Path $stage 'SOURCE-MANIFEST.json'),
    ($manifest | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

  if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
  $tar = (Get-Command tar.exe -ErrorAction Stop).Source
  & $tar -a -c -f $archive -C $stage .
  if ($LASTEXITCODE -ne 0) { throw 'ZIP creation failed for the Bridge corresponding source.' }
  if (-not (Test-Path -LiteralPath $archive)) { throw 'Bridge corresponding-source archive was not created.' }
  $archiveItem = Get-Item -LiteralPath $archive
  $archiveHash = Get-FileHash -Algorithm SHA256 -LiteralPath $archive
  Write-Host "PASS Bridge corresponding source: $($archiveItem.FullName)"
  Write-Host "Files: $($entries.Count)"
  Write-Host "Bytes: $($archiveItem.Length)"
  Write-Host "SHA256: $($archiveHash.Hash)"
}
finally {
  if (Test-Path -LiteralPath $stage) {
    try { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction Stop }
    catch { Write-Warning "Temporary Bridge source staging directory could not be fully removed: $stage" }
  }
}
