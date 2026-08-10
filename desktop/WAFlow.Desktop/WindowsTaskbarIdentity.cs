using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;

namespace WAFlow.Desktop;

internal static class WindowsTaskbarIdentity
{
    internal const string AppUserModelId = "AI.Sales.OS.Desktop";
    internal const string BrandIconFileName = "AI-Sales-OS-D945B52D252F.ico";
    internal const string BrandIconSha256 = "D945B52D252FBE72A44C985E6426C00967E8C35FE046973204B21309DD8037A4";

    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;
    private const ushort VtLpwstr = 31;

    private static readonly PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static readonly PropertyKey RelaunchCommandKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 2);
    private static readonly PropertyKey RelaunchIconResourceKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3);

    private static IntPtr _largeIcon;
    private static IntPtr _smallIcon;

    internal static void InitializeProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        if (result < 0) Debug.WriteLine($"Unable to assign AppUserModelID: 0x{result:X8}");
    }

    internal static void ApplyWindowIcon(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        var windowHandle = new WindowInteropHelper(window).Handle;
        var executablePath = Environment.ProcessPath;
        if (windowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(executablePath)) return;

        var iconPath = ResolveIconPath(executablePath);
        ApplyWindowIdentity(windowHandle, executablePath, iconPath);
        ReleaseWindowIcon();

        var largeIcons = new IntPtr[1];
        var smallIcons = new IntPtr[1];
        if (ExtractIconEx(iconPath, 0, largeIcons, smallIcons, 1) == 0) return;

        _largeIcon = largeIcons[0];
        _smallIcon = smallIcons[0];

        if (_largeIcon != IntPtr.Zero)
        {
            SendMessage(windowHandle, WmSetIcon, new IntPtr(IconBig), _largeIcon);
        }

        if (_smallIcon != IntPtr.Zero)
        {
            SendMessage(windowHandle, WmSetIcon, new IntPtr(IconSmall), _smallIcon);
            SendMessage(windowHandle, WmSetIcon, new IntPtr(IconSmall2), _smallIcon);
        }
    }

    internal static string ResolveIconPath(string executablePath)
    {
        try
        {
            var shellDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WAFlow",
                "shell");
            var iconPath = Path.Combine(shellDirectory, BrandIconFileName);
            if (File.Exists(iconPath) && GetFileSha256(iconPath) == BrandIconSha256) return iconPath;

            var resource = Application.GetResourceStream(
                new Uri("/AISalesOS;component/Assets/AI-Sales-OS.ico", UriKind.Relative));
            if (resource is null) return executablePath;
            using var input = resource.Stream;
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            var iconBytes = buffer.ToArray();
            if (Convert.ToHexString(SHA256.HashData(iconBytes)) != BrandIconSha256)
                throw new InvalidDataException("The embedded Windows brand icon does not match its protected hash.");

            Directory.CreateDirectory(shellDirectory);
            var temporaryPath = iconPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, iconBytes);
                File.Move(temporaryPath, iconPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return iconPath;
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Unable to materialize the stable taskbar icon; using the executable icon: {error}");
        }

        return executablePath;
    }

    private static string GetFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ApplyWindowIdentity(IntPtr windowHandle, string executablePath, string iconPath)
    {
        var interfaceId = typeof(IPropertyStore).GUID;
        var result = SHGetPropertyStoreForWindow(windowHandle, ref interfaceId, out var propertyStore);
        if (result < 0 || propertyStore is null)
        {
            Debug.WriteLine($"Unable to open taskbar property store: 0x{result:X8}");
            return;
        }

        try
        {
            SetStringProperty(propertyStore, AppUserModelIdKey, AppUserModelId);
            SetStringProperty(propertyStore, RelaunchCommandKey, $"\"{executablePath}\"");
            SetStringProperty(propertyStore, RelaunchIconResourceKey, $"{iconPath},0");
            result = propertyStore.Commit();
            if (result < 0) Debug.WriteLine($"Unable to commit taskbar identity: 0x{result:X8}");
        }
        finally
        {
            Marshal.FinalReleaseComObject(propertyStore);
        }
    }

    private static void SetStringProperty(IPropertyStore propertyStore, PropertyKey key, string text)
    {
        var value = new PropVariant
        {
            VariantType = VtLpwstr,
            PointerValue = Marshal.StringToCoTaskMemUni(text)
        };
        try
        {
            var result = propertyStore.SetValue(ref key, ref value);
            if (result < 0) Debug.WriteLine($"Unable to set taskbar property {key.PropertyId}: 0x{result:X8}");
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    internal static void ReleaseWindowIcon()
    {
        if (_largeIcon != IntPtr.Zero)
        {
            DestroyIcon(_largeIcon);
            _largeIcon = IntPtr.Zero;
        }

        if (_smallIcon != IntPtr.Zero)
        {
            DestroyIcon(_smallIcon);
            _smallIcon = IntPtr.Zero;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string fileName,
        int iconIndex,
        IntPtr[] largeIcons,
        IntPtr[] smallIcons,
        uint iconCount);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr windowHandle,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint propertyCount);
        [PreserveSig] int GetAt(uint propertyIndex, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
