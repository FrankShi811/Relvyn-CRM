using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace WAFlow.Desktop;

internal sealed class DesktopSingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly CancellationTokenSource _activationCancellation = new();
    private Task? _activationListener;
    private bool _ownsMutex;

    private DesktopSingleInstance(Mutex mutex, EventWaitHandle activationEvent, bool ownsMutex)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimary => _ownsMutex;

    public static DesktopSingleInstance Acquire(string? isolationScope = null)
    {
        var scope = BuildUserScope(isolationScope);
        var mutex = new Mutex(false, $"Local\\AISalesOS.Desktop.{scope}");
        var activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            $"Local\\AISalesOS.Desktop.Activate.{scope}");
        var ownsMutex = false;
        try
        {
            ownsMutex = mutex.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }
        return new DesktopSingleInstance(mutex, activationEvent, ownsMutex);
    }

    public void SignalPrimary() => _activationEvent.Set();

    public void StartActivationListener(Dispatcher dispatcher, Func<Window?> windowProvider)
    {
        if (!IsPrimary || _activationListener is not null) return;
        _activationListener = Task.Factory.StartNew(
            () =>
            {
                var handles = new WaitHandle[] { _activationEvent, _activationCancellation.Token.WaitHandle };
                while (WaitHandle.WaitAny(handles) == 0)
                {
                    dispatcher.BeginInvoke(() => Activate(windowProvider()));
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static void Activate(Window? window)
    {
        if (window is null) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private static string BuildUserScope(string? isolationScope)
    {
        string identity;
        try
        {
            identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch
        {
            identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
        if (!string.IsNullOrWhiteSpace(isolationScope))
        {
            identity += $"\nqa:{isolationScope.Trim()}";
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }

    public void Dispose()
    {
        _activationCancellation.Cancel();
        _activationEvent.Set();
        try { _activationListener?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _activationCancellation.Dispose();
        _activationEvent.Dispose();
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }
}
