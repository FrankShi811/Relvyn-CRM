[CmdletBinding()]
param(
  [string]$MasterPath = '',
  [string]$PromptPath = '',
  [string]$GeneratedAtUtc = '2026-08-10T05:35:12Z'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $MasterPath) {
  $MasterPath = Join-Path $root 'docs\brand\generation-records\ai-sales-os-brand-master-original-20260810-133512.png'
}
if (-not $PromptPath) {
  $PromptPath = Join-Path $root 'docs\brand\generation-records\ai-sales-os-brand-master-prompt-20260810-133512.md'
}
$MasterPath = [IO.Path]::GetFullPath($MasterPath)
$PromptPath = [IO.Path]::GetFullPath($PromptPath)
if (-not (Test-Path -LiteralPath $MasterPath -PathType Leaf)) { throw "Brand master is missing: $MasterPath" }
if (-not (Test-Path -LiteralPath $PromptPath -PathType Leaf)) { throw "Brand prompt is missing: $PromptPath" }

Add-Type -AssemblyName System.Drawing

function Get-CanonicalTextSha256([string]$Path) {
  $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false))
  $canonicalText = $text.Replace("`r`n", "`n").Replace("`r", "`n")
  $bytes = [Text.UTF8Encoding]::new($false).GetBytes($canonicalText)
  $sha = [Security.Cryptography.SHA256]::Create()
  try { return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') }
  finally { $sha.Dispose() }
}

function New-RoundedMaster([Drawing.Image]$Source, [int]$Size) {
  $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $bitmap.SetResolution(96, 96)
  $graphics = [Drawing.Graphics]::FromImage($bitmap)
  try {
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
    $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $radius = [Math]::Round($Size * 0.165)
    $diameter = $radius * 2
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    try {
      $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
      $path.AddArc($Size - $diameter, 0, $diameter, $diameter, 270, 90)
      $path.AddArc($Size - $diameter, $Size - $diameter, $diameter, $diameter, 0, 90)
      $path.AddArc(0, $Size - $diameter, $diameter, $diameter, 90, 90)
      $path.CloseFigure()
      $graphics.SetClip($path)
      $graphics.DrawImage($Source, [Drawing.Rectangle]::new(0, 0, $Size, $Size))
    }
    finally { $path.Dispose() }
  }
  finally { $graphics.Dispose() }
  return $bitmap
}

function Save-Png([Drawing.Bitmap]$Bitmap, [string]$Path) {
  [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
  $Bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
}

function Get-RelativeAssetPath([string]$BasePath, [string]$Path) {
  $baseUri = [Uri]::new($BasePath.TrimEnd('\') + '\')
  $pathUri = [Uri]::new($Path)
  return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('\', '/')
}

function Get-PngBytes([Drawing.Bitmap]$Bitmap) {
  $stream = [IO.MemoryStream]::new()
  try {
    $Bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    return $stream.ToArray()
  }
  finally { $stream.Dispose() }
}

function Write-PngIcon([string]$Path, [hashtable]$Images) {
  $orderedSizes = @($Images.Keys | ForEach-Object { [int]$_ } | Sort-Object)
  $payloads = foreach ($size in $orderedSizes) { Get-PngBytes $Images[$size] }
  $headerLength = 6 + (16 * $orderedSizes.Count)
  $offset = $headerLength
  $stream = [IO.File]::Open($Path, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
  $writer = [IO.BinaryWriter]::new($stream)
  try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$orderedSizes.Count)
    for ($index = 0; $index -lt $orderedSizes.Count; $index++) {
      $size = $orderedSizes[$index]
      $payload = $payloads[$index]
      $iconDimension = if ($size -ge 256) { 0 } else { $size }
      $writer.Write([byte]$iconDimension)
      $writer.Write([byte]$iconDimension)
      $writer.Write([byte]0)
      $writer.Write([byte]0)
      $writer.Write([uint16]1)
      $writer.Write([uint16]32)
      $writer.Write([uint32]$payload.Length)
      $writer.Write([uint32]$offset)
      $offset += $payload.Length
    }
    foreach ($payload in $payloads) { $writer.Write($payload) }
  }
  finally {
    $writer.Dispose()
    $stream.Dispose()
  }
}

$desktopAssets = Join-Path $root 'desktop\WAFlow.Desktop\Assets'
$iconDirectory = Join-Path $desktopAssets 'Icons'
$pwaAssets = Join-Path $root 'pwa\public'
$recordDirectory = Join-Path $root 'docs\brand\generation-records'
[IO.Directory]::CreateDirectory($iconDirectory) | Out-Null
[IO.Directory]::CreateDirectory($pwaAssets) | Out-Null
[IO.Directory]::CreateDirectory($recordDirectory) | Out-Null

$source = [Drawing.Image]::FromFile($MasterPath)
$bitmaps = @{}
try {
  foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 192, 256, 512, 1024)) {
    $bitmaps[$size] = New-RoundedMaster $source $size
  }
  Save-Png $bitmaps[1024] (Join-Path $desktopAssets 'AI-Sales-OS.png')
  foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    Save-Png $bitmaps[$size] (Join-Path $iconDirectory "AI-Sales-OS-$size.png")
  }
  Save-Png $bitmaps[192] (Join-Path $pwaAssets 'pwa-192.png')
  Save-Png $bitmaps[512] (Join-Path $pwaAssets 'pwa-512.png')
  $icoImages = @{}
  foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) { $icoImages[$size] = $bitmaps[$size] }
  Write-PngIcon (Join-Path $desktopAssets 'AI-Sales-OS.ico') $icoImages
}
finally {
  foreach ($bitmap in $bitmaps.Values) { $bitmap.Dispose() }
  $source.Dispose()
}

$assetPaths = @(
  'desktop/WAFlow.Desktop/Assets/AI-Sales-OS.png',
  'desktop/WAFlow.Desktop/Assets/AI-Sales-OS.ico',
  'pwa/public/pwa-192.png',
  'pwa/public/pwa-512.png'
) + (@(16, 20, 24, 32, 40, 48, 64, 128, 256) | ForEach-Object {
  "desktop/WAFlow.Desktop/Assets/Icons/AI-Sales-OS-$_.png"
})

$manifest = [ordered]@{
  schemaVersion = 1
  generatedAtUtc = $GeneratedAtUtc
  sourceTool = 'OpenAI host-native image generation tool; model selected by host runtime'
  sourceUsedReferenceImages = $false
  projectOwnerAuthorization = 'The project owner explicitly authorized replacement with newly generated assets on 2026-08-10.'
  prompt = Get-RelativeAssetPath $root $PromptPath
  promptSha256 = Get-CanonicalTextSha256 $PromptPath
  promptHashNormalization = 'UTF-8 without BOM; CRLF and CR line endings normalized to LF before SHA-256'
  originalMaster = Get-RelativeAssetPath $root $MasterPath
  originalMasterSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $MasterPath).Hash
  derivation = 'Rounded-square alpha mask and deterministic high-quality downscaling through scripts/generate-brand-assets.ps1; no third-party brand assets or reference images used.'
  assets = @($assetPaths | ForEach-Object {
    $absolute = Join-Path $root $_
    [ordered]@{
      path = $_
      sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $absolute).Hash
      bytes = (Get-Item -LiteralPath $absolute).Length
    }
  })
}
$manifestPath = Join-Path $recordDirectory 'brand-assets-manifest.json'
[IO.File]::WriteAllText(
  $manifestPath,
  ($manifest | ConvertTo-Json -Depth 6),
  [Text.UTF8Encoding]::new($false))

Write-Host "PASS generated original AI Sales OS brand assets from $MasterPath"
Write-Host "Manifest: $manifestPath"
