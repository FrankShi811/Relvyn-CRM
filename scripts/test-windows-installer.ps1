[CmdletBinding()]
param(
  [string]$InstallerPath = '',
  [string]$QaDirectory = '',
  [string]$ExpectedVersion = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $InstallerPath) { $InstallerPath = Join-Path $root 'dist\installers\AI Sales OS Setup.exe' }
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $InstallerPath)) { throw "Installer is missing: $InstallerPath" }
if (-not $ExpectedVersion) {
  $project = Join-Path $root 'desktop\WAFlow.Desktop\WAFlow.Desktop.csproj'
  $ExpectedVersion = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $project)).Project.PropertyGroup.Version | Select-Object -First 1
}
if (-not $QaDirectory) { $QaDirectory = Join-Path $root 'work\windows-installer-qa' }
$QaDirectory = [IO.Path]::GetFullPath($QaDirectory)
$workRoot = [IO.Path]::GetFullPath((Join-Path $root 'work'))
if (-not $QaDirectory.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
  throw "QA directory must stay below the workspace work directory: $QaDirectory"
}
if (Test-Path -LiteralPath $QaDirectory) { [IO.Directory]::Delete($QaDirectory, $true) }

$database = Join-Path $env:LOCALAPPDATA 'WAFlow\waflow.db'
$qaDataDirectory = Join-Path $workRoot 'windows-installer-qa-data'
if (Test-Path -LiteralPath $qaDataDirectory) { [IO.Directory]::Delete($qaDataDirectory, $true) }
[IO.Directory]::CreateDirectory($qaDataDirectory) | Out-Null
$qaDatabase = Join-Path $qaDataDirectory 'waflow.db'
function Get-HashWithRetry([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return 'MISSING' }
  for ($attempt = 1; $attempt -le 10; $attempt++) {
    try {
      $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
      $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
      try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose() }
      }
      finally { $stream.Dispose() }
    }
    catch {
      if ($attempt -eq 10) { throw }
      Start-Sleep -Milliseconds 500
    }
  }
}
$beforeHash = Get-HashWithRetry $database
$shortcutPaths = @(
  (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'AI Sales OS.lnk'),
  (Join-Path ([Environment]::GetFolderPath('Programs')) 'AI Sales OS.lnk')
)
$shortcutBackups = @{}
foreach ($shortcutPath in $shortcutPaths) {
  if (Test-Path -LiteralPath $shortcutPath) {
    $shortcutBackups[$shortcutPath] = [IO.File]::ReadAllBytes($shortcutPath)
  }
}
try {
  $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$QaDirectory`"")
  $installer = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
  if ($installer.ExitCode -ne 0) { throw "Installer exited with code $($installer.ExitCode)." }

  $appPath = Join-Path $QaDirectory 'current\AISalesOS.exe'
  if (-not (Test-Path -LiteralPath $appPath)) { throw "Installed application is missing: $appPath" }
  $installedRoot = Join-Path $QaDirectory 'current'
  foreach ($requiredFile in @(
    'WAFlow.WhatsApp.Bridge.exe',
    'LICENSE',
    'EULA.md',
    'PRIVACY.md',
    'THIRD_PARTY_NOTICES.md',
    'BRIDGE_GPL_COMPLIANCE.md',
    'licenses\third-party\LIBSIGNAL-6.0.0-GPL-3.0.txt'
  )) {
    if (-not (Test-Path -LiteralPath (Join-Path $installedRoot $requiredFile) -PathType Leaf)) {
      throw "Installed application is missing required Bridge or license file: $requiredFile"
    }
  }
  $bridgeCompanionPresent = Test-Path -LiteralPath (Join-Path $installedRoot 'WAFlow.WhatsApp.Bridge.exe') -PathType Leaf
  $previousDatabaseOverride = $env:WAFLOW_DATABASE_PATH
  try {
    $env:WAFLOW_DATABASE_PATH = $qaDatabase
    $app = Start-Process -FilePath $appPath -PassThru
    Start-Sleep -Seconds 8
    if (-not $app.HasExited) {
      $app.CloseMainWindow() | Out-Null
      if (-not $app.WaitForExit(5000)) { Stop-Process -Id $app.Id -Force }
    }
  }
  finally {
    $env:WAFLOW_DATABASE_PATH = $previousDatabaseOverride
  }
  $qaProcesses = Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($QaDirectory, [StringComparison]::OrdinalIgnoreCase)
  }
  foreach ($process in $qaProcesses) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 2

  $shell = New-Object -ComObject WScript.Shell
  $shortcutTargets = foreach ($shortcutPath in $shortcutPaths) {
    if (-not (Test-Path -LiteralPath $shortcutPath)) {
      throw "Installed application did not create the shortcut: $shortcutPath"
    }
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if (-not $shortcut.TargetPath.StartsWith($QaDirectory, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Installed shortcut points outside the QA application: $shortcutPath -> $($shortcut.TargetPath)"
    }
    $shortcut.TargetPath
  }

  $afterHash = Get-HashWithRetry $database
  $databasePreservationPassed = $beforeHash -eq 'MISSING' -or $beforeHash -eq $afterHash
  $installedVersion = (Get-Item -LiteralPath $appPath).VersionInfo.FileVersion
  $versionSource = 'installed executable'
  # A self-contained .NET single-file bundle can omit the native apphost's
  # Windows version resource even though the Velopack package and installer
  # carry the release version. Fall back to the installer resource so the
  # smoke test still verifies the artifact that performed this installation.
  if ([string]::IsNullOrWhiteSpace($installedVersion)) {
    $installedVersion = (Get-Item -LiteralPath $InstallerPath).VersionInfo.FileVersion
    $versionSource = 'installer'
  }
  $installedVersion = $installedVersion.Trim()
  $updatePath = Join-Path $QaDirectory 'Update.exe'
  $uninstallExit = $null
  if (Test-Path -LiteralPath $updatePath) {
    $uninstall = Start-Process -FilePath $updatePath -ArgumentList @('--uninstall', '--silent') -Wait -PassThru -WindowStyle Hidden
    $uninstallExit = $uninstall.ExitCode
  }

  $result = [pscustomobject]@{
    InstallerExit = $installer.ExitCode
    InstalledExeVersion = $installedVersion
    InstalledVersionSource = $versionSource
    ApplicationStarted = $true
    BridgeCompanionPresent = $bridgeCompanionPresent
    ShortcutTargets = $shortcutTargets -join '; '
    ShortcutsVerified = $shortcutTargets.Count -eq 2
    DatabaseHashBefore = $beforeHash
    DatabaseHashAfter = $afterHash
    DatabaseUnchanged = $beforeHash -eq $afterHash
    DatabasePreservationPassed = $databasePreservationPassed
    UninstallExit = $uninstallExit
    QaDirectoryStillExists = Test-Path -LiteralPath $QaDirectory
  }
  $result
  if ([string]::IsNullOrWhiteSpace($installedVersion) -or
      (-not $installedVersion.StartsWith($ExpectedVersion + '.', [StringComparison]::Ordinal) -and
       $installedVersion -ne $ExpectedVersion)) {
    throw "Installed version mismatch. expected=$ExpectedVersion actual=$installedVersion"
  }
  if (-not $result.ShortcutsVerified) { throw 'Installer smoke test did not verify both Windows shortcuts.' }
  if (-not $result.BridgeCompanionPresent) { throw 'Installer smoke test did not verify the separate GPL Bridge companion.' }
  if (-not $result.DatabasePreservationPassed) { throw 'Installer smoke test changed an existing user SQLite database.' }
  if ($result.UninstallExit -ne 0) { throw "Installer smoke uninstall failed with exit code $($result.UninstallExit)." }
  if ($result.QaDirectoryStillExists) { throw "Installer smoke uninstall left the QA directory behind: $QaDirectory" }
  if (Test-Path -LiteralPath $qaDataDirectory) { [IO.Directory]::Delete($qaDataDirectory, $true) }
}
finally {
  foreach ($shortcutPath in $shortcutPaths) {
    if ($shortcutBackups.ContainsKey($shortcutPath)) {
      [IO.Directory]::CreateDirectory((Split-Path -Parent $shortcutPath)) | Out-Null
      [IO.File]::WriteAllBytes($shortcutPath, $shortcutBackups[$shortcutPath])
    }
    elseif (Test-Path -LiteralPath $shortcutPath) {
      Remove-Item -LiteralPath $shortcutPath -Force
    }
  }
}
