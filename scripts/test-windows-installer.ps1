[CmdletBinding()]
param(
  [string]$InstallerPath = '',
  [string]$QaDirectory = '',
  [string]$ExpectedVersion = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Add-Type -AssemblyName System.Drawing
if (-not ('WindowsTaskbarIconProbe' -as [type])) {
  Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowsTaskbarIconProbe
{
    private const uint WmGetIcon = 0x007F;
    private const uint SmtoAbortIfHung = 0x0002;

    public delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

    public static long FindMainWindow(int processId)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows(delegate(IntPtr windowHandle, IntPtr parameter)
        {
            uint ownerProcessId;
            GetWindowThreadProcessId(windowHandle, out ownerProcessId);
            if (ownerProcessId != (uint)processId || !IsWindowVisible(windowHandle)) return true;

            StringBuilder title = new StringBuilder(512);
            GetWindowText(windowHandle, title, title.Capacity);
            string text = title.ToString();
            if (text.StartsWith("AI Sales OS ", StringComparison.Ordinal) &&
                text.IndexOf("WhatsApp", StringComparison.Ordinal) >= 0)
            {
                match = windowHandle;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return match.ToInt64();
    }

    public static string GetTitle(long rawWindowHandle)
    {
        StringBuilder title = new StringBuilder(512);
        GetWindowText(new IntPtr(rawWindowHandle), title, title.Capacity);
        return title.ToString();
    }

    public static long GetIcon(long rawWindowHandle, int iconKind)
    {
        IntPtr iconHandle;
        SendMessageTimeout(
            new IntPtr(rawWindowHandle),
            WmGetIcon,
            new IntPtr(iconKind),
            IntPtr.Zero,
            SmtoAbortIfHung,
            2000,
            out iconHandle);
        return iconHandle.ToInt64();
    }

    public static uint GetWindowDpi(long rawWindowHandle)
    {
        return GetDpiForWindow(new IntPtr(rawWindowHandle));
    }

    public static int GetMetricForDpi(int metric, uint dpi)
    {
        return GetSystemMetricsForDpi(metric, dpi);
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder title, int maxLength);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wordParameter,
        IntPtr longParameter,
        uint flags,
        uint timeoutMilliseconds,
        out IntPtr result);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int metric, uint dpi);
}
'@
}

function Get-BitmapPixelHash([Drawing.Bitmap]$Bitmap) {
  $pixels = [byte[]]::new($Bitmap.Width * $Bitmap.Height * 4)
  $offset = 0
  for ($y = 0; $y -lt $Bitmap.Height; $y++) {
    for ($x = 0; $x -lt $Bitmap.Width; $x++) {
      $pixel = $Bitmap.GetPixel($x, $y)
      $pixels[$offset++] = $pixel.A
      $pixels[$offset++] = $pixel.R
      $pixels[$offset++] = $pixel.G
      $pixels[$offset++] = $pixel.B
    }
  }
  $sha = [Security.Cryptography.SHA256]::Create()
  try { [BitConverter]::ToString($sha.ComputeHash($pixels)).Replace('-', '') }
  finally { $sha.Dispose() }
}

function Test-WindowIconMatchesBrand([long]$IconHandle, [string]$Kind) {
  if ($IconHandle -eq 0) { throw "Main window did not expose its $Kind taskbar icon through WM_GETICON." }
  $borrowedIcon = [Drawing.Icon]::FromHandle([IntPtr]::new($IconHandle))
  $icon = $borrowedIcon.Clone()
  try {
    $actual = $icon.ToBitmap()
    try {
      if ($actual.Width -ne $actual.Height) {
        throw "Main window $Kind taskbar icon is not square: $($actual.Width)x$($actual.Height)."
      }
      $expectedPath = Join-Path $root "desktop\WAFlow.Desktop\Assets\Icons\AI-Sales-OS-$($actual.Width).png"
      if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
        throw "No protected brand reference exists for the runtime $Kind taskbar icon size: $expectedPath"
      }
      $expected = [Drawing.Bitmap]::FromFile($expectedPath)
      try {
        $actualHash = Get-BitmapPixelHash $actual
        $expectedHash = Get-BitmapPixelHash $expected
        if ($actual.Width -ne $expected.Width -or
            $actual.Height -ne $expected.Height -or
            $actualHash -ne $expectedHash) {
          throw "Main window $Kind taskbar icon does not match the protected brand asset. size=$($actual.Width)x$($actual.Height) actual=$actualHash expected=$expectedHash"
        }
        [pscustomobject]@{
          Kind = $Kind
          Size = "$($actual.Width)x$($actual.Height)"
          PixelSha256 = $actualHash
        }
      }
      finally { $expected.Dispose() }
    }
    finally { $actual.Dispose() }
  }
  finally { $icon.Dispose() }
}

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
$stableBrandIconPath = Join-Path $env:LOCALAPPDATA 'WAFlow\shell\AI-Sales-OS-D945B52D252F.ico'
$stableBrandIconBackup = if (Test-Path -LiteralPath $stableBrandIconPath) {
  [IO.File]::ReadAllBytes($stableBrandIconPath)
}
else { $null }
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
  $previousSingleInstanceScope = $env:WAFLOW_SINGLE_INSTANCE_SCOPE
  try {
    $env:WAFLOW_DATABASE_PATH = $qaDatabase
    $env:WAFLOW_SINGLE_INSTANCE_SCOPE = "windows-installer-qa-$([Guid]::NewGuid().ToString('N'))"
    $app = Start-Process -FilePath $appPath -PassThru
    $observedMainWindowTitle = ''
    $mainWindowHandle = 0L
    $processAlive = $true
    $mainWindowMatched = $false
    $startupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
      Start-Sleep -Milliseconds 500
      $app.Refresh()
      $processAlive = -not $app.HasExited
      if ($processAlive) {
        $mainWindowHandle = [WindowsTaskbarIconProbe]::FindMainWindow($app.Id)
        $mainWindowMatched = $mainWindowHandle -ne 0
        if ($mainWindowMatched) {
          $observedMainWindowTitle = [WindowsTaskbarIconProbe]::GetTitle($mainWindowHandle)
        }
      }
    } while ($processAlive -and -not $mainWindowMatched -and [DateTimeOffset]::UtcNow -lt $startupDeadline)
    $applicationStarted = $processAlive -and $mainWindowMatched
    if (-not $applicationStarted) {
      $observedState = if ($app.HasExited) {
        "process exited with code $($app.ExitCode)"
      }
      else {
        "unexpected top-level window '$observedMainWindowTitle' (processAlive=$processAlive; mainWindowMatched=$mainWindowMatched)"
      }
      if (-not $app.HasExited) {
        Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
        $app.WaitForExit(5000) | Out-Null
      }
      throw "Installed application did not reach its main window; a startup error dialog may be blocking it: $observedState"
    }
    # The HWND becomes visible before WPF raises Loaded. Give the application
    # time to replace WPF's provisional small icon with the protected
    # per-monitor-DPI big/small handles before sampling WM_GETICON.
    Start-Sleep -Seconds 2
    $taskbarSmall2Handle = [WindowsTaskbarIconProbe]::GetIcon($mainWindowHandle, 2)
    $taskbarBigHandle = [WindowsTaskbarIconProbe]::GetIcon($mainWindowHandle, 1)
    $taskbarSmall2 = Test-WindowIconMatchesBrand $taskbarSmall2Handle 'small2'
    $taskbarBig = Test-WindowIconMatchesBrand $taskbarBigHandle 'big'
    $windowDpi = [WindowsTaskbarIconProbe]::GetWindowDpi($mainWindowHandle)
    $expectedTaskbarSmallSize = [WindowsTaskbarIconProbe]::GetMetricForDpi(49, $windowDpi)
    $expectedTaskbarBigSize = [WindowsTaskbarIconProbe]::GetMetricForDpi(11, $windowDpi)
    Write-Host "Taskbar icon probe: dpi=$windowDpi smallHandle=$taskbarSmall2Handle bigHandle=$taskbarBigHandle small=$($taskbarSmall2.Size)/$expectedTaskbarSmallSize big=$($taskbarBig.Size)/$expectedTaskbarBigSize"
    if ($taskbarSmall2.Size -ne "$expectedTaskbarSmallSize`x$expectedTaskbarSmallSize") {
      throw "Main window small2 taskbar icon has the wrong per-DPI size. dpi=$windowDpi actual=$($taskbarSmall2.Size) expected=$expectedTaskbarSmallSize"
    }
    if ($taskbarBig.Size -ne "$expectedTaskbarBigSize`x$expectedTaskbarBigSize") {
      throw "Main window big taskbar icon has the wrong per-DPI size. dpi=$windowDpi actual=$($taskbarBig.Size) expected=$expectedTaskbarBigSize"
    }
    $taskbarIconVerified = $true
    if (-not $app.HasExited) {
      $app.CloseMainWindow() | Out-Null
      if (-not $app.WaitForExit(5000)) { Stop-Process -Id $app.Id -Force }
    }
  }
  finally {
    $env:WAFLOW_DATABASE_PATH = $previousDatabaseOverride
    $env:WAFLOW_SINGLE_INSTANCE_SCOPE = $previousSingleInstanceScope
  }
  $qaProcesses = Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($QaDirectory, [StringComparison]::OrdinalIgnoreCase)
  }
  foreach ($process in $qaProcesses) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 2

  $shell = New-Object -ComObject WScript.Shell
  $sourceBrandIconPath = Join-Path $root 'desktop\WAFlow.Desktop\Assets\AI-Sales-OS.ico'
  if (-not (Test-Path -LiteralPath $stableBrandIconPath -PathType Leaf)) {
    throw "The application did not materialize its stable Windows shell icon: $stableBrandIconPath"
  }
  $materializedBrandIconHash = (Get-FileHash -LiteralPath $stableBrandIconPath -Algorithm SHA256).Hash
  $sourceBrandIconHash = (Get-FileHash -LiteralPath $sourceBrandIconPath -Algorithm SHA256).Hash
  if ($materializedBrandIconHash -ne $sourceBrandIconHash) {
    throw "Materialized Windows shell icon hash mismatch. actual=$materializedBrandIconHash expected=$sourceBrandIconHash"
  }
  $shortcutIconLocations = @()
  $shortcutProtectedIconFrames = @()
  $shortcutTargets = foreach ($shortcutPath in $shortcutPaths) {
    if (-not (Test-Path -LiteralPath $shortcutPath)) {
      throw "Installed application did not create the shortcut: $shortcutPath"
    }
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if (-not $shortcut.TargetPath.StartsWith($QaDirectory, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Installed shortcut points outside the QA application: $shortcutPath -> $($shortcut.TargetPath)"
    }
    $shortcutIconPath = ([string]$shortcut.IconLocation).Split(',')[0].Trim('"')
    if (-not $shortcutIconPath.Equals($stableBrandIconPath, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Installed shortcut does not use the stable brand icon: $shortcutPath -> $($shortcut.IconLocation)"
    }
    $shortcutIconLocations += $shortcut.IconLocation
    # ExtractAssociatedIcon treats a .lnk as a file-type icon on clean CI
    # runners and can return the generic shortcut document instead of resolving
    # IconLocation. Validate the exact icon file referenced by the shortcut at
    # the desktop sizes Windows uses at 100% and 125% DPI. Together with the
    # exact IconLocation assertion above, this verifies the user-visible brand
    # source without confusing the Shell's shortcut overlay for the app icon.
    foreach ($shellSize in @(32, 40)) {
      $shellIcon = [Drawing.Icon]::new($stableBrandIconPath, $shellSize, $shellSize)
      try {
        $shellBitmap = $shellIcon.ToBitmap()
        try {
          $expectedShellIconPath = Join-Path $root "desktop\WAFlow.Desktop\Assets\Icons\AI-Sales-OS-$shellSize.png"
          if (-not (Test-Path -LiteralPath $expectedShellIconPath -PathType Leaf)) {
            throw "No protected brand reference exists for the Shell shortcut icon size: $expectedShellIconPath"
          }
          $expectedShellBitmap = [Drawing.Bitmap]::FromFile($expectedShellIconPath)
          try {
            $actualShellHash = Get-BitmapPixelHash $shellBitmap
            $expectedShellHash = Get-BitmapPixelHash $expectedShellBitmap
            if ($shellBitmap.Width -ne $expectedShellBitmap.Width -or
                $shellBitmap.Height -ne $expectedShellBitmap.Height -or
                $actualShellHash -ne $expectedShellHash) {
              throw "The shortcut's protected icon file has the wrong frame. path=$shortcutPath size=$($shellBitmap.Width)x$($shellBitmap.Height) actual=$actualShellHash expected=$expectedShellHash"
            }
            $shortcutProtectedIconFrames += "$shortcutPath=$($shellBitmap.Width)x$($shellBitmap.Height):$actualShellHash"
          }
          finally { $expectedShellBitmap.Dispose() }
        }
        finally { $shellBitmap.Dispose() }
      }
      finally { $shellIcon.Dispose() }
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
    ApplicationStarted = $applicationStarted
    MainWindowTitle = $observedMainWindowTitle
    WindowDpi = $windowDpi
    TaskbarIconVerified = $taskbarIconVerified
    TaskbarSmall2Size = $taskbarSmall2.Size
    TaskbarSmall2PixelSha256 = $taskbarSmall2.PixelSha256
    TaskbarBigSize = $taskbarBig.Size
    TaskbarBigPixelSha256 = $taskbarBig.PixelSha256
    MaterializedBrandIconPath = $stableBrandIconPath
    MaterializedBrandIconSha256 = $materializedBrandIconHash
    BridgeCompanionPresent = $bridgeCompanionPresent
    ShortcutTargets = $shortcutTargets -join '; '
    ShortcutIconLocations = $shortcutIconLocations -join '; '
    ShortcutProtectedIconFrames = $shortcutProtectedIconFrames -join '; '
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
  if (-not $result.TaskbarIconVerified) { throw 'Installer smoke test did not verify the live main-window taskbar icon.' }
  if (-not $result.ShortcutsVerified) { throw 'Installer smoke test did not verify both Windows shortcuts.' }
  if (-not $result.BridgeCompanionPresent) { throw 'Installer smoke test did not verify the separate GPL Bridge companion.' }
  if (-not $result.DatabasePreservationPassed) { throw 'Installer smoke test changed an existing user SQLite database.' }
  if ($result.UninstallExit -ne 0) { throw "Installer smoke uninstall failed with exit code $($result.UninstallExit)." }
  if ($result.QaDirectoryStillExists) { throw "Installer smoke uninstall left the QA directory behind: $QaDirectory" }
  if (Test-Path -LiteralPath $qaDataDirectory) { [IO.Directory]::Delete($qaDataDirectory, $true) }
}
finally {
  $remainingQaProcesses = Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and
    $_.ExecutablePath.StartsWith($QaDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
  }
  foreach ($process in $remainingQaProcesses) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
  }
  foreach ($shortcutPath in $shortcutPaths) {
    if ($shortcutBackups.ContainsKey($shortcutPath)) {
      [IO.Directory]::CreateDirectory((Split-Path -Parent $shortcutPath)) | Out-Null
      [IO.File]::WriteAllBytes($shortcutPath, $shortcutBackups[$shortcutPath])
    }
    elseif (Test-Path -LiteralPath $shortcutPath) {
      Remove-Item -LiteralPath $shortcutPath -Force
    }
  }
  if ($null -ne $stableBrandIconBackup) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $stableBrandIconPath)) | Out-Null
    [IO.File]::WriteAllBytes($stableBrandIconPath, $stableBrandIconBackup)
  }
  elseif (Test-Path -LiteralPath $stableBrandIconPath) {
    Remove-Item -LiteralPath $stableBrandIconPath -Force
  }
}
