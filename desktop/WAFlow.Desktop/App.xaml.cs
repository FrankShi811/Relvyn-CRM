using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using System.IO;
using Velopack;
using WAFlow.Core;
using WAFlow.Core.Infrastructure;
using WAFlow.Desktop.Updates;

namespace WAFlow.Desktop;

public partial class App : Application
{
    private static readonly TimeSpan MigrationShutdownStepTimeout = TimeSpan.FromSeconds(10);
    public AppServices Services { get; private set; } = null!;
    public IApplicationUpdateService Updates { get; private set; } = null!;
    public DataWorkspaceManager DataWorkspaceManager { get; private set; } = null!;
    private DataWorkspaceMigrationResult _startupMigration = new(false, true, "");
    private DataWorkspaceLease? _workspaceLease;
    private bool _workspaceMigrationShutdownRequested;
    private DesktopSingleInstance? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetAppUserModelId(WindowsTaskbarIdentity.AppUserModelId)
            .SetAutoApplyOnStartup(false)
            .Run();

        var workspaceManager = new DataWorkspaceManager();
        var waitForProcessId = ParseWaitForProcessId(args);
        var isMigrationWorker = args.Any(argument =>
            argument.Equals("--apply-workspace-migration", StringComparison.OrdinalIgnoreCase));
        DataWorkspaceMigrationResult startupMigration;
        try
        {
            startupMigration = workspaceManager
                .ApplyPendingMigrationAsync(waitForProcessId)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception error)
        {
            LogException("workspace-migration-startup", error);
            MessageBox.Show(
                $"无法读取本地数据工作区迁移计划：\n{error.Message}\n\n程序尚未修改任何客户数据。",
                "AI Sales OS",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        if (isMigrationWorker
            && startupMigration.Attempted
            && !startupMigration.Succeeded
            && IsProcessRunning(waitForProcessId))
        {
            MessageBox.Show(
                startupMigration.Message +
                "\n\n原程序仍在运行，本次迁移辅助进程不会再启动第二个程序窗口。请更新后重新执行迁移。",
                "工作区迁移未完成",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        var singleInstance = DesktopSingleInstance.Acquire(
            Environment.GetEnvironmentVariable("WAFLOW_SINGLE_INSTANCE_SCOPE"));
        if (!singleInstance.IsPrimary)
        {
            singleInstance.SignalPrimary();
            singleInstance.Dispose();
            return;
        }
        var app = new App
        {
            DataWorkspaceManager = workspaceManager,
            _startupMigration = startupMigration,
            _singleInstance = singleInstance
        };
        app.InitializeComponent();
        app.Run();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        WindowsTaskbarIdentity.InitializeProcess();
        DesktopShortcutService.EnsureForInstalledApp();
        LocalUpdateCacheMaintenance.Run();
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        DataWorkspaceLocation? location = null;
        try
        {
            location = DataWorkspaceManager.Resolve();
            _workspaceLease = DataWorkspaceManager.AcquireLease(location.RootDirectory);
            Services = new AppServices(dataWorkspaceManager: DataWorkspaceManager);
            await Services.InitializeAsync();
            Updates = new VelopackUpdateService();
            var settings = await Services.Repository.GetAppSettingsAsync();
            ThemeManager.Apply(settings.ThemeMode);
            var main = new MainWindow(Services, Updates, settings.UiScalePercentage);
            MainWindow = main;
            main.Show();
            _singleInstance?.StartActivationListener(Dispatcher, () => MainWindow);
            if (Services.Repository.LastRecoveryNotice is { } recovery)
            {
                MessageBox.Show(
                    $"检测到本地数据库损坏，AI Sales OS 已自动恢复并保留可读取数据。\n\n" +
                    $"客户：{recovery.LeadCount} 条\nWhatsApp 会话：{recovery.ConversationCount} 个\n消息：{recovery.MessageCount} 条\n\n" +
                    $"损坏原件和恢复副本已保存在：\n{recovery.BackupDirectory}",
                    "数据库已安全恢复",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            await Services.LeadAutomation.StartAsync();
            await Services.CustomerEnrichment.StartAsync();
            await Services.Campaigns.StartAsync();
            await Services.MessagingSync.StartAsync();
            await Services.WhatsAppNumberValidation.StartAsync();

            var completion = await DataWorkspaceManager.CompletePendingMigrationAsync(
                Services.DataWorkspace.RootDirectory);
            if (!string.IsNullOrWhiteSpace(completion.Message))
            {
                MessageBox.Show(
                    completion.Message,
                    completion.SourceRetained ? "工作区已切换，原位置已保留" : "工作区迁移完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else if (_startupMigration.Attempted && !_startupMigration.Succeeded)
            {
                MessageBox.Show(
                    _startupMigration.Message,
                    "工作区迁移未完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception error)
        {
            LogException("startup", error);
            if (location is not null)
            {
                try
                {
                    await DataWorkspaceManager.RollbackAfterStartupFailureAsync(
                        location.RootDirectory,
                        error);
                }
                catch (Exception rollbackError)
                {
                    LogException("workspace-rollback", rollbackError);
                }
            }
            _workspaceLease?.Dispose();
            _workspaceLease = null;
            MessageBox.Show($"AI Sales OS 初始化失败：\n{error.Message}", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static int? ParseWaitForProcessId(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (!args[index].Equals("--wait-for-pid", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(args[index + 1], out var processId) && processId > 0)
                return processId;
        }
        return null;
    }

    private static bool IsProcessRunning(int? processId)
    {
        if (processId is not > 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void RequestWorkspaceMigrationShutdown() =>
        _workspaceMigrationShutdownRequested = true;

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("dispatcher", e.Exception);
        MessageBox.Show($"操作失败：\n{e.Exception.Message}", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LogException(string area, Exception error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Sales OS", "logs");
            Directory.CreateDirectory(directory);
            var entry = $"[{DateTimeOffset.Now:O}] {area}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "app-errors.log"), entry);
        }
        catch
        {
            // Logging must never hide the original application error.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Updates is not null)
                DisposeForExit("updates", () => Updates.DisposeAsync());
            if (Services is not null)
            {
                DisposeForExit("lead-automation", () => Services.LeadAutomation.DisposeAsync());
                DisposeForExit("customer-enrichment", () => Services.CustomerEnrichment.DisposeAsync());
                DisposeForExit("campaigns", () => Services.Campaigns.DisposeAsync());
                DisposeForExit("messaging-sync", () => Services.MessagingSync.DisposeAsync());
                DisposeForExit("whatsapp-number-validation", () => Services.WhatsAppNumberValidation.DisposeAsync());
                DisposeForExit("email", () => Services.Email.DisposeAsync());
                DisposeForExit("whatsapp", () => Services.WhatsApp.DisposeAsync());
            }
        }
        finally
        {
            _workspaceLease?.Dispose();
            _workspaceLease = null;
            _singleInstance?.Dispose();
            _singleInstance = null;
            base.OnExit(e);
        }
    }

    private void DisposeForExit(string area, Func<ValueTask> dispose)
    {
        try
        {
            var task = dispose().AsTask();
            if (_workspaceMigrationShutdownRequested
                && !task.Wait(MigrationShutdownStepTimeout))
            {
                LogException(
                    $"workspace-migration-shutdown-{area}",
                    new TimeoutException(
                        $"后台服务 {area} 未能在 {MigrationShutdownStepTimeout.TotalSeconds:0} 秒内停止；" +
                        "程序将继续退出，迁移进程会在数据库句柄释放后再开始复制。"));
                return;
            }
            task.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            LogException($"shutdown-{area}", error);
            if (!_workspaceMigrationShutdownRequested)
                throw;
        }
    }
}
