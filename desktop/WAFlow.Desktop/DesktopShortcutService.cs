using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Velopack.Locators;
using Velopack.Windows;

namespace WAFlow.Desktop;

/// <summary>
/// Velopack normally creates shortcuts during setup. This startup guard also
/// repairs the desktop and Start menu shortcuts after every installed update,
/// including machines where Windows removed or lost the previous shortcut.
/// Portable builds are intentionally left untouched.
/// </summary>
internal static class DesktopShortcutService
{
    private const string ShortcutFileName = "AI Sales OS.lnk";
    private const uint ShellChangeUpdateItem = 0x00002000;
    private const uint ShellChangeNotifyPathUnicode = 0x0005;
    private const uint ShellChangeNotifyFlushNoWait = 0x2000;

    internal static void EnsureForInstalledApp()
    {
        if (!OperatingSystem.IsWindows() || !VelopackLocator.IsCurrentSet)
        {
            return;
        }

        var locator = VelopackLocator.Current;
        if (locator.IsPortable || locator.CurrentlyInstalledVersion is null)
        {
            return;
        }

        // Ask Velopack to recreate (not merely update) both links first. An
        // update can run while either link is missing, so updateOnly must remain
        // false.
        try
        {
#pragma warning disable CS0618
            var shortcuts = new Shortcuts(locator);
            shortcuts.CreateShortcut(
                locator.ThisExeRelativePath,
                ShortcutLocation.Desktop | ShortcutLocation.StartMenuRoot,
                updateOnly: false,
                programArguments: null,
                icon: null);
#pragma warning restore CS0618
        }
        catch (Exception error)
        {
            Trace.TraceWarning($"Velopack shortcut repair failed; using Windows fallback: {error}");
        }

        // Velopack 1.2 can return without a link when Windows removed it during
        // an in-place update. Rebuild and verify the actual .lnk files through
        // Windows Script Host so a silent API failure cannot be mistaken for
        // success.
        var processPath = Environment.ProcessPath;
        var contentDirectory = locator.AppContentDir
            ?? (processPath is null ? null : Path.GetDirectoryName(processPath))
            ?? locator.RootAppDir
            ?? throw new InvalidOperationException("The installed application directory is unavailable.");
        var relativeExePath = locator.ThisExeRelativePath
            ?? (processPath is null ? null : Path.GetFileName(processPath))
            ?? "AISalesOS.exe";
        var targetPath = Path.Combine(contentDirectory, relativeExePath);
        var iconPath = WindowsTaskbarIdentity.ResolveIconPath(targetPath);
        var desktopShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ShortcutFileName);
        var startMenuShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            ShortcutFileName);
        foreach (var shortcutPath in new[] { desktopShortcut, startMenuShortcut })
        {
            try
            {
                CreateWindowsShortcut(shortcutPath, targetPath, iconPath);
                if (!File.Exists(shortcutPath))
                    throw new IOException($"Windows did not create the shortcut: {shortcutPath}");
                NotifyShellShortcutChanged(shortcutPath, iconPath);
            }
            catch (Exception error)
            {
                // A shortcut failure must not prevent the application or an
                // update from starting. Keep a diagnostic breadcrumb for support.
                Trace.TraceWarning($"Unable to repair AI Sales OS shortcut '{shortcutPath}': {error}");
            }
        }
    }

    private static void CreateWindowsShortcut(string shortcutPath, string targetPath, string iconPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var temporaryShortcutPath = Path.Combine(
            Path.GetDirectoryName(shortcutPath)!,
            $"{Path.GetFileNameWithoutExtension(shortcutPath)}.{Guid.NewGuid():N}.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("Windows Script Host is unavailable.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Unable to create the Windows shortcut service.");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [temporaryShortcutPath]);
            if (shortcut is null)
                throw new InvalidOperationException("Windows returned an empty shortcut object.");

            var shortcutType = shortcut.GetType();
            SetShortcutProperty(shortcutType, shortcut, "TargetPath", targetPath);
            SetShortcutProperty(shortcutType, shortcut, "WorkingDirectory", Path.GetDirectoryName(targetPath)!);
            SetShortcutProperty(shortcutType, shortcut, "IconLocation", $"{iconPath},0");
            SetShortcutProperty(shortcutType, shortcut, "Description", "AI Sales OS");
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shortcut,
                args: null);
            File.Move(temporaryShortcutPath, shortcutPath, overwrite: true);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
            try { if (File.Exists(temporaryShortcutPath)) File.Delete(temporaryShortcutPath); }
            catch { }
        }
    }

    private static void NotifyShellShortcutChanged(string shortcutPath, string iconPath)
    {
        const uint flags = ShellChangeNotifyPathUnicode | ShellChangeNotifyFlushNoWait;
        SHChangeNotify(ShellChangeUpdateItem, flags, shortcutPath, null);
        SHChangeNotify(ShellChangeUpdateItem, flags, iconPath, null);
    }

    private static void SetShortcutProperty(Type shortcutType, object shortcut, string name, string value) =>
        shortcutType.InvokeMember(
            name,
            BindingFlags.SetProperty,
            binder: null,
            target: shortcut,
            args: [value]);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        string? item1,
        string? item2);
}
