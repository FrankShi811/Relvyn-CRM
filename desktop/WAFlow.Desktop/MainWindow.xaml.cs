using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;
using WAFlow.Desktop.Controls;
using WAFlow.Desktop.Pages;
using WAFlow.Desktop.Updates;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop;

public partial class MainWindow : Window
{
    private const double SidebarCollapsedWidth = 60;
    private const double SidebarExpandedWidth = 240;
    private static readonly TimeSpan SidebarExpandDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan SidebarCollapseDuration = TimeSpan.FromMilliseconds(220);
    private readonly AppServices _services;
    private readonly IApplicationUpdateService _updates;
    private readonly DashboardView _dashboard;
    private readonly LeadIntelligenceView _intelligence;
    private readonly CustomersView _customers;
    private readonly CustomerEnrichmentView _customerEnrichment;
    private readonly WhatsAppInboxView _inbox;
    private readonly EmailInboxView _email;
    private readonly CampaignsView _campaigns;
    private readonly KnowledgeBaseView _knowledge;
    private readonly AnalyticsView _analytics;
    private Button? _activeButton;
    private OnboardingState _onboardingState = new();
    private bool _onboardingReady;
    private string _currentPage = "dashboard";
    private readonly DispatcherTimer _unreadBadgeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _unreadBadgeRefreshRunning;
    private bool _unreadBadgeRefreshPending;
    private InboxUnreadTotals? _lastUnreadTotals;
    private IReadOnlyList<FrameworkElement> _sidebarRevealElements = [];
    private bool _sidebarExpanded;
    private bool _sidebarPointerInside;
    private bool _sidebarFocusFromPointer;
    private bool _sidebarKeyboardExpanded;
    private bool _sidebarKeyboardNavigationPending;
    private DropShadowEffect _sidebarShadow = null!;
    private long _sidebarMotionVersion;
    private CancellationTokenSource? _navigationMotionCancellation;
    private long _commandMotionVersion;
    private IInputElement? _focusBeforeCommandOverlay;
    private bool _initializationCompleted;

    public MainWindow(AppServices services, IApplicationUpdateService updates, int uiScalePercentage = 100)
    {
        _services = services;
        _updates = updates;
        InitializeComponent();
        if (string.Equals(Environment.GetEnvironmentVariable("WAFLOW_UI_REVIEW"), "1", StringComparison.Ordinal))
            Title += " · UI 沙盒";
        InitializeSidebarMotionState();
        ApplyUiScale(uiScalePercentage);
        GuideCatalog.ValidateCoverage();
        SidebarVersionText.Text = $"当前版本  v{ReleaseCatalog.CurrentVersion}";
        _dashboard = new DashboardView(services);
        _intelligence = new LeadIntelligenceView(services);
        _customers = new CustomersView(services);
        _customerEnrichment = new CustomerEnrichmentView(services);
        _inbox = new WhatsAppInboxView(services);
        _email = new EmailInboxView(services);
        _campaigns = new CampaignsView(services);
        _knowledge = new KnowledgeBaseView(services);
        _analytics = new AnalyticsView(services);
        _dashboard.NavigateRequested += Dashboard_NavigateRequested;
        _intelligence.ImportRequested += OpenImport;
        _intelligence.OpportunityImportRequested += OpenOpportunityImport;
        _intelligence.DataChanged += View_DataChanged;
        _customers.ImportRequested += OpenImport;
        _customers.DataChanged += View_DataChanged;
        _customerEnrichment.DataChanged += View_DataChanged;
        _customerEnrichment.ImportRequested += OpenImport;
        _customerEnrichment.SettingsRequested += CustomerEnrichment_SettingsRequested;
        _inbox.DataChanged += View_DataChanged;
        _email.DataChanged += View_DataChanged;
        _campaigns.DataChanged += View_DataChanged;
        _knowledge.DataChanged += View_DataChanged;
        _analytics.DataChanged += View_DataChanged;
        _services.Campaigns.SafetyStopped += Campaigns_SafetyStopped;
        _services.LeadAutomation.AnalysisChanged += LeadAutomation_AnalysisChanged;
        _services.WhatsAppSync.MessageSynchronized += MessagingUnreadChanged;
        _services.WhatsAppSync.SynchronizationChanged += WhatsAppSynchronizationChanged;
        _services.Email.SynchronizationChanged += EmailSynchronizationChanged;
        _unreadBadgeTimer.Tick += UnreadBadgeTimer_Tick;
        _updates.StateChanged += Updates_StateChanged;
        ApplyUpdateState(_updates.State);
        OnboardingGuide.CloseRequested += OnboardingGuide_CloseRequested;
        OnboardingGuide.FinishedRequested += OnboardingGuide_FinishedRequested;
        OnboardingGuide.SettingsRequested += OnboardingGuide_SettingsRequested;
        OnboardingGuide.GlobalRequested += OnboardingGuide_GlobalRequested;
        Loaded += MainWindow_Loaded;
        _initializationCompleted = true;
    }

    private void InitializeSidebarMotionState()
    {
        _sidebarRevealElements = FindSidebarRevealElements(SidebarHost).ToArray();
        _sidebarExpanded = false;
        _sidebarPointerInside = SidebarHost.IsMouseOver;
        _sidebarKeyboardExpanded = SidebarHost.IsKeyboardFocusWithin && !_sidebarFocusFromPointer;
        _sidebarMotionVersion++;
        SidebarHost.Width = SidebarCollapsedWidth;
        SidebarHost.Effect = null;
        _sidebarShadow = ((DropShadowEffect)FindResource("SidebarOverlayShadow")).CloneCurrentValue();
        _sidebarShadow.Opacity = 0;
        foreach (var element in _sidebarRevealElements)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 0;
            EnsureSidebarTranslateTransform(element).X = -10;
        }
        UpdateSidebarExpansionState();
    }

    private static IEnumerable<FrameworkElement> FindSidebarRevealElements(DependencyObject root)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement { Tag: "SidebarReveal" } element)
                yield return element;
            foreach (var descendant in FindSidebarRevealElements(child))
                yield return descendant;
        }
    }

    private static TranslateTransform EnsureSidebarTranslateTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } writableTranslate)
            return writableTranslate;
        var initialX = element.RenderTransform is TranslateTransform currentTranslate ? currentTranslate.X : 0;
        var translate = new TranslateTransform(initialX, 0);
        element.RenderTransform = translate;
        return translate;
    }

    private void SidebarHost_MouseEnter(object sender, MouseEventArgs e)
    {
        _sidebarPointerInside = true;
        UpdateSidebarExpansionState();
    }

    private void SidebarHost_MouseLeave(object sender, MouseEventArgs e)
    {
        _sidebarPointerInside = false;
        UpdateSidebarExpansionState();
    }

    private void SidebarHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _sidebarFocusFromPointer = true;
        _sidebarKeyboardNavigationPending = false;
        _sidebarKeyboardExpanded = false;
    }

    private void SidebarHost_PreviewMouseUp(object sender, MouseButtonEventArgs e) =>
        _sidebarFocusFromPointer = false;

    private void SidebarHost_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_sidebarFocusFromPointer && _sidebarKeyboardNavigationPending)
            _sidebarKeyboardExpanded = true;
        _sidebarKeyboardNavigationPending = false;
        UpdateSidebarExpansionState();
    }

    private void SidebarHost_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!SidebarHost.IsKeyboardFocusWithin)
                _sidebarKeyboardExpanded = false;
            UpdateSidebarExpansionState();
        });
    }

    private void UpdateSidebarExpansionState() =>
        SetSidebarExpanded(_sidebarPointerInside || _sidebarKeyboardExpanded);

    private void SetSidebarExpanded(bool expanded)
    {
        if (_sidebarExpanded == expanded) return;
        _sidebarExpanded = expanded;
        var version = ++_sidebarMotionVersion;
        if (expanded && SidebarHost.Effect is null)
        {
            _sidebarShadow.Opacity = 0;
            SidebarHost.Effect = _sidebarShadow;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            SidebarHost.BeginAnimation(WidthProperty, null);
            SidebarHost.Width = expanded ? SidebarExpandedWidth : SidebarCollapsedWidth;
            _sidebarShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            _sidebarShadow.Opacity = expanded ? 0.18 : 0;
            if (!expanded) SidebarHost.Effect = null;
            foreach (var element in _sidebarRevealElements)
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = expanded ? 1 : 0;
                var translate = EnsureSidebarTranslateTransform(element);
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.X = expanded ? 0 : -10;
            }
            return;
        }

        var duration = expanded ? SidebarExpandDuration : SidebarCollapseDuration;
        var shellEase = new SineEase { EasingMode = EasingMode.EaseInOut };
        AnimateAndCommit(
            SidebarHost,
            WidthProperty,
            expanded ? SidebarExpandedWidth : SidebarCollapsedWidth,
            duration,
            shellEase,
            TimeSpan.Zero,
            () => version == _sidebarMotionVersion);
        AnimateAndCommit(
            _sidebarShadow,
            DropShadowEffect.OpacityProperty,
            expanded ? 0.18 : 0,
            TimeSpan.FromMilliseconds(expanded ? 210 : 165),
            new SineEase { EasingMode = EasingMode.EaseOut },
            TimeSpan.Zero,
            () => version == _sidebarMotionVersion,
            () =>
            {
                if (!expanded && version == _sidebarMotionVersion)
                    SidebarHost.Effect = null;
            });

        var revealDelay = expanded ? TimeSpan.FromMilliseconds(48) : TimeSpan.Zero;
        var revealDuration = TimeSpan.FromMilliseconds(expanded ? 178 : 128);
        var revealEase = new SineEase { EasingMode = EasingMode.EaseOut };
        foreach (var element in _sidebarRevealElements)
        {
            AnimateAndCommit(
                element,
                OpacityProperty,
                expanded ? 1 : 0,
                revealDuration,
                revealEase,
                revealDelay,
                () => version == _sidebarMotionVersion);
            AnimateAndCommit(
                EnsureSidebarTranslateTransform(element),
                TranslateTransform.XProperty,
                expanded ? 0 : -10,
                revealDuration,
                revealEase,
                revealDelay,
                () => version == _sidebarMotionVersion);
        }
    }

    private static void AnimateAndCommit(
        DependencyObject target,
        DependencyProperty property,
        double destination,
        TimeSpan duration,
        IEasingFunction easing,
        TimeSpan beginTime,
        Func<bool>? isCurrent = null,
        Action? completed = null)
    {
        if (target is not IAnimatable animatable)
        {
            target.SetValue(property, destination);
            completed?.Invoke();
            return;
        }

        var current = (double)target.GetValue(property);
        animatable.BeginAnimation(property, null);
        target.SetValue(property, current);
        var animation = new DoubleAnimation(current, destination, new Duration(duration))
        {
            BeginTime = beginTime,
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (isCurrent is not null && !isCurrent()) return;
            target.SetValue(property, destination);
            animatable.BeginAnimation(property, null);
            completed?.Invoke();
        };
        animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static Task AnimateAndCommitAsync(
        DependencyObject target,
        DependencyProperty property,
        double destination,
        TimeSpan duration,
        IEasingFunction easing,
        CancellationToken cancellationToken)
    {
        if (!SystemParameters.ClientAreaAnimation || duration == TimeSpan.Zero)
        {
            if (target is IAnimatable immediateAnimatable)
                immediateAnimatable.BeginAnimation(property, null);
            target.SetValue(property, destination);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        AnimateAndCommit(
            target,
            property,
            destination,
            duration,
            easing,
            TimeSpan.Zero,
            () => !cancellationToken.IsCancellationRequested,
            () =>
            {
                registration.Dispose();
                completion.TrySetResult();
            });
        return completion.Task;
    }

    private void VersionHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_updates.State is { Stage: ApplicationUpdateStage.ReadyToInstall, CanInstall: true })
        {
            try
            {
                _updates.ApplyAndRestart();
                Application.Current.Shutdown();
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "无法安装更新", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }
        new VersionHistoryWindow(_updates) { Owner = this }.ShowDialog();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTaskbarIdentity.ApplyWindowIcon(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _navigationMotionCancellation?.Cancel();
        _navigationMotionCancellation?.Dispose();
        _sidebarMotionVersion++;
        _commandMotionVersion++;
        WindowsTaskbarIdentity.ReleaseWindowIcon();
        if (!_initializationCompleted)
        {
            base.OnClosed(e);
            return;
        }
        _services.Campaigns.SafetyStopped -= Campaigns_SafetyStopped;
        _services.LeadAutomation.AnalysisChanged -= LeadAutomation_AnalysisChanged;
        _services.WhatsAppSync.MessageSynchronized -= MessagingUnreadChanged;
        _services.WhatsAppSync.SynchronizationChanged -= WhatsAppSynchronizationChanged;
        _services.Email.SynchronizationChanged -= EmailSynchronizationChanged;
        _customerEnrichment.DataChanged -= View_DataChanged;
        _customerEnrichment.ImportRequested -= OpenImport;
        _customerEnrichment.SettingsRequested -= CustomerEnrichment_SettingsRequested;
        _unreadBadgeTimer.Stop();
        _unreadBadgeTimer.Tick -= UnreadBadgeTimer_Tick;
        _updates.StateChanged -= Updates_StateChanged;
        OnboardingGuide.CloseRequested -= OnboardingGuide_CloseRequested;
        OnboardingGuide.FinishedRequested -= OnboardingGuide_FinishedRequested;
        OnboardingGuide.SettingsRequested -= OnboardingGuide_SettingsRequested;
        OnboardingGuide.GlobalRequested -= OnboardingGuide_GlobalRequested;
        base.OnClosed(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Control templates inside the ScrollViewer join the visual tree only after the window loads.
        InitializeSidebarMotionState();
        await UpdateProviderStateAsync();
        await UpdateUnreadBadgesAsync();
        _services.DashboardUnreadDigest.QueueBackgroundRefresh();
        _unreadBadgeTimer.Start();
        await NavigateAsync("dashboard", DashboardButton);
        _onboardingState = await _services.Repository.GetOnboardingStateAsync();
        if (GuideCatalog.MigrateLegacyState(_onboardingState))
            await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
        _onboardingReady = true;
        if (!GuideCatalog.IsSeen(_onboardingState, "global"))
            OnboardingGuide.ShowGuide(GuideCatalog.Global);
        else
            await ShowModuleGuideIfNeededAsync(_currentPage);
        _updates.StartMonitoring();
    }

    private void Updates_StateChanged(object? sender, ApplicationUpdateState state) =>
        _ = Dispatcher.InvokeAsync(() => ApplyUpdateState(state));

    private void ApplyUpdateState(ApplicationUpdateState state)
    {
        SidebarUpdateIcon.SetResourceReference(TextBlock.ForegroundProperty, "Success");
        SidebarUpdateText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarMuted");
        VersionButton.BorderBrush = Brushes.Transparent;
        VersionButton.BorderThickness = new Thickness(0);
        VersionButton.ToolTip = "查看版本与更新";
        switch (state.Stage)
        {
            case ApplicationUpdateStage.Checking:
                SidebarUpdateIcon.Text = "◌";
                SidebarVersionText.Text = $"当前版本  v{state.CurrentVersion}";
                SidebarUpdateText.Text = "正在检查 GitHub Release…";
                break;
            case ApplicationUpdateStage.Downloading:
                SidebarUpdateIcon.Text = "↓";
                SidebarVersionText.Text = $"正在下载  v{state.LatestVersion}";
                SidebarUpdateText.Text = $"下载进度 {state.DownloadProgress}%";
                break;
            case ApplicationUpdateStage.ReadyToInstall:
                SidebarUpdateIcon.Text = "●";
                SidebarUpdateIcon.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
                SidebarVersionText.Text = $"新版本  v{state.LatestVersion}";
                SidebarUpdateText.Text = "已下载 · 点击更新并重启";
                SidebarUpdateText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarText");
                VersionButton.SetResourceReference(Button.BorderBrushProperty, "Warning");
                VersionButton.BorderThickness = new Thickness(2);
                VersionButton.ToolTip = "更新已下载，点击安装并重启";
                break;
            case ApplicationUpdateStage.Failed:
                SidebarUpdateIcon.Text = "!";
                SidebarUpdateIcon.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                SidebarVersionText.Text = $"当前版本  v{state.CurrentVersion}";
                SidebarUpdateText.Text = "检查失败 · 点击查看详情";
                SidebarUpdateText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarText");
                break;
            case ApplicationUpdateStage.Disabled:
                SidebarUpdateIcon.Text = "↻";
                SidebarVersionText.Text = $"当前版本  v{state.CurrentVersion}";
                SidebarUpdateText.Text = state.Message;
                break;
            default:
                SidebarUpdateIcon.Text = "✓";
                SidebarVersionText.Text = $"当前版本  v{state.CurrentVersion}";
                SidebarUpdateText.Text = state.Stage == ApplicationUpdateStage.UpToDate ? "后台持续监控 GitHub 更新" : "启动后持续监控更新";
                break;
        }
    }

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string page) await NavigateAsync(page, button);
    }

    private async void Dashboard_NavigateRequested(object? sender, string page)
    {
        var button = page switch
        {
            "intelligence" => IntelligenceButton,
            "inbox" => InboxButton,
            "email" => EmailButton,
            "broadcast" => BroadcastButton,
            "knowledge" => KnowledgeButton,
            "customers" => CustomersButton,
            "customer-enrichment" => CustomerEnrichmentButton,
            "analytics" => AnalyticsButton,
            _ => DashboardButton
        };
        await NavigateAsync(page, button);
    }

    private async Task NavigateAsync(string page, Button button)
    {
        _navigationMotionCancellation?.Cancel();
        _navigationMotionCancellation?.Dispose();
        var motionCancellation = new CancellationTokenSource();
        _navigationMotionCancellation = motionCancellation;
        var cancellationToken = motionCancellation.Token;

        _currentPage = page;
        if (_activeButton is not null)
            ApplyNavigationButtonState(_activeButton, false);
        _activeButton = button;
        ApplyNavigationButtonState(button, true);
        (object Content, string Title, string Subtitle) target = page switch
        {
            "intelligence" => ((object)_intelligence, "商机智能", "AI 评分证据、客户画像与下一步决策"),
            "customers" => ((object)_customers, "客户列表", "统一客户数据、动态字段与批量运营"),
            "customer-enrichment" => ((object)_customerEnrichment, "客户外部调查", "公开来源、主体匹配、证据事实与人工审核"),
            "inbox" => ((object)_inbox, "WhatsApp", "会话、客户资料与 AI 销售信号实时联动"),
            "email" => ((object)_email, "邮件箱", "邮件收发、历史归档与 CRM 客户资料实时联动"),
            "broadcast" => ((object)_campaigns, "多渠道自动化触达", "WhatsApp 与邮件任务、动态字段、发送节奏与分渠道审计"),
            "knowledge" => ((object)_knowledge, "知识库", "批准知识、作用域、版本、混合检索与来源审计"),
            "analytics" => ((object)_analytics, "客户智能分析", "全量客户数据、AI 商业判断、报告版本与管理层导出"),
            _ => ((object)_dashboard, "看板", "今天最值得推进的商机与动作")
        };

        try
        {
            var contentChanged = !ReferenceEquals(ContentHost.Content, target.Content);
            if (contentChanged && ContentHost.Content is not null && SystemParameters.ClientAreaAnimation)
            {
                var exitEase = new SineEase { EasingMode = EasingMode.EaseIn };
                await Task.WhenAll(
                    AnimateAndCommitAsync(ContentHost, OpacityProperty, 0, TimeSpan.FromMilliseconds(95), exitEase, cancellationToken),
                    AnimateAndCommitAsync(ContentHostTranslate, TranslateTransform.YProperty, -4, TimeSpan.FromMilliseconds(110), exitEase, cancellationToken));
            }

            cancellationToken.ThrowIfCancellationRequested();
            ContentHost.BeginAnimation(OpacityProperty, null);
            ContentHostTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            ContentHost.Opacity = contentChanged ? 0 : 1;
            ContentHostTranslate.Y = contentChanged ? 10 : 0;
            ContentHost.Content = target.Content;
            TopTitle.Text = target.Title;
            TopSubtitle.Text = target.Subtitle;
            PageGuideButton.ToolTip = $"查看“{target.Title}”的功能介绍和操作步骤";
            var refreshTask = ContentHost.Content is IRefreshableView view
                ? view.RefreshAsync()
                : Task.CompletedTask;
            var enterTask = Task.CompletedTask;
            if (contentChanged)
            {
                var enterEase = new CubicEase { EasingMode = EasingMode.EaseOut };
                enterTask = Task.WhenAll(
                    AnimateAndCommitAsync(ContentHost, OpacityProperty, 1, TimeSpan.FromMilliseconds(235), enterEase, cancellationToken),
                    AnimateAndCommitAsync(ContentHostTranslate, TranslateTransform.YProperty, 0, TimeSpan.FromMilliseconds(255), enterEase, cancellationToken));
            }
            await Task.WhenAll(enterTask, refreshTask);

            cancellationToken.ThrowIfCancellationRequested();
            if (_onboardingReady && !OnboardingGuide.IsOpen)
                await ShowModuleGuideIfNeededAsync(page);
        }
        catch (OperationCanceledException)
        {
            // A newer navigation owns the presentation state and continues from
            // the current rendered values without flashing the superseded page.
        }
        finally
        {
            if (ReferenceEquals(_navigationMotionCancellation, motionCancellation))
            {
                _navigationMotionCancellation = null;
                motionCancellation.Dispose();
            }
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await OpenSettingsAsync();
    }

    private async Task OpenSettingsAsync(bool focusCustomerEnrichment = false)
    {
        var window = new SettingsWindow(_services, _updates, focusCustomerEnrichment) { Owner = this };
        var saved = window.ShowDialog() == true;
        // Closing an owned window restores focus to the invoking button. That is
        // programmatic focus restoration, not keyboard navigation, so it must
        // not pin the hover overlay open after the pointer has already left.
        _sidebarFocusFromPointer = false;
        _sidebarKeyboardNavigationPending = false;
        _sidebarKeyboardExpanded = false;
        _sidebarPointerInside = SidebarHost.IsMouseOver;
        UpdateSidebarExpansionState();
        _onboardingState = await _services.Repository.GetOnboardingStateAsync();
        if (saved)
        {
            var settings = await _services.Repository.GetAppSettingsAsync();
            ApplyUiScale(settings.UiScalePercentage);
            RefreshShellThemeState();
            await UpdateProviderStateAsync();
            if (ContentHost.Content is IRefreshableView currentView)
                await currentView.RefreshAsync();
            await _intelligence.RefreshAiRouteAsync();
            await UpdateUnreadBadgesAsync();
        }
    }

    private void ApplyUiScale(int percentage) =>
        MainScaleHost.Scale = UiScaleManager.ToScale(percentage);

    private void ShowGuide_Click(object sender, RoutedEventArgs e) => OnboardingGuide.ShowGuide(GuideCatalog.ForModule(_currentPage));

    private async Task ShowModuleGuideIfNeededAsync(string page)
    {
        if (!_onboardingReady || OnboardingGuide.IsOpen) return;
        if (!GuideCatalog.IsSeen(_onboardingState, page))
            OnboardingGuide.ShowGuide(GuideCatalog.ForModule(page));
        await Task.CompletedTask;
    }

    private async Task MarkModuleGuideSeenAsync(string key)
    {
        GuideCatalog.MarkSeen(_onboardingState, key);
        await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
    }

    private async Task CloseGuideAsync()
    {
        var definition = OnboardingGuide.CurrentDefinition;
        OnboardingGuide.HideGuide();
        if (definition is { IsGlobal: false })
            await MarkModuleGuideSeenAsync(definition.Key);
        else
        {
            GuideCatalog.MarkSeen(_onboardingState, "global");
            await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
            await ShowModuleGuideIfNeededAsync(_currentPage);
        }
    }

    private async void OnboardingGuide_CloseRequested(object? sender, EventArgs e) => await CloseGuideAsync();

    private async void OnboardingGuide_FinishedRequested(object? sender, EventArgs e)
    {
        if (OnboardingGuide.CurrentDefinition is not { } definition) return;
        if (definition.IsGlobal)
        {
            if (!_services.DeepSeek.HasApiKey())
            {
                MessageBox.Show("请先配置 DeepSeek 或兼容 AI 接口的 API Key，并从自动拉取的列表中选择模型，再结束首次使用引导。", "需要配置 AI API", MessageBoxButton.OK, MessageBoxImage.Information);
                await OpenSettingsAsync();
                if (!_services.DeepSeek.HasApiKey()) return;
            }
            GuideCatalog.MarkSeen(_onboardingState, "global");
            await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
            OnboardingGuide.HideGuide();
            await ShowModuleGuideIfNeededAsync(_currentPage);
            return;
        }
        await MarkModuleGuideSeenAsync(definition.Key);
        OnboardingGuide.HideGuide();
    }

    private async void OnboardingGuide_SettingsRequested(object? sender, EventArgs e)
    {
        var definition = OnboardingGuide.CurrentDefinition;
        var step = OnboardingGuide.CurrentStepIndex;
        OnboardingGuide.HideGuide();
        await OpenSettingsAsync();
        if (definition is not null) OnboardingGuide.ShowGuide(definition, step);
    }

    private void OnboardingGuide_GlobalRequested(object? sender, EventArgs e) => OnboardingGuide.ShowGuide(GuideCatalog.Global);

    private async void OpenImport(object? sender, EventArgs e)
    {
        var window = new ImportWindow(_services) { Owner = this };
        if (window.ShowDialog() != true) return;
        if (ContentHost.Content is IRefreshableView currentView)
            await currentView.RefreshAsync();
        await UpdateUnreadBadgesAsync();
    }

    private async void OpenOpportunityImport(object? sender, EventArgs e)
    {
        var window = new OpportunitySupplementImportWindow(_services) { Owner = this };
        if (window.ShowDialog() != true) return;
        await _intelligence.RefreshAsync();
        _dashboard.NotifyUnreadChanged();
    }

    private async Task UpdateProviderStateAsync()
    {
        var configured = _services.DeepSeek.HasApiKey();
        var settings = await _services.Repository.GetAppSettingsAsync();
        ProviderText.Text = configured
            ? settings.UseGlobalAiConfiguration
                ? $"AI 已配置 · {settings.DeepSeekModel}"
                : $"AI 已配置 · 分板块模型（默认 {settings.DeepSeekModel}）"
            : "AI API 未配置";
        ProviderBadge.SetResourceReference(Border.BackgroundProperty, configured ? "SuccessSoft" : "WarningSoft");
        ProviderText.SetResourceReference(TextBlock.ForegroundProperty, configured ? "Success" : "Warning");
        AnimateProviderBadgeFeedback();
    }

    private void AnimateProviderBadgeFeedback()
    {
        ProviderBadge.BeginAnimation(OpacityProperty, null);
        ProviderBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProviderBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ProviderBadgeTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ProviderBadge.Opacity = SystemParameters.ClientAreaAnimation ? 0.72 : 1;
        ProviderBadgeScale.ScaleX = SystemParameters.ClientAreaAnimation ? 0.97 : 1;
        ProviderBadgeScale.ScaleY = SystemParameters.ClientAreaAnimation ? 0.97 : 1;
        ProviderBadgeTranslate.Y = SystemParameters.ClientAreaAnimation ? -3 : 0;
        if (!SystemParameters.ClientAreaAnimation) return;

        var easing = new SineEase { EasingMode = EasingMode.EaseOut };
        AnimateAndCommit(ProviderBadge, OpacityProperty, 1, TimeSpan.FromMilliseconds(180), easing, TimeSpan.Zero);
        AnimateAndCommit(ProviderBadgeScale, ScaleTransform.ScaleXProperty, 1, TimeSpan.FromMilliseconds(210), easing, TimeSpan.Zero);
        AnimateAndCommit(ProviderBadgeScale, ScaleTransform.ScaleYProperty, 1, TimeSpan.FromMilliseconds(210), easing, TimeSpan.Zero);
        AnimateAndCommit(ProviderBadgeTranslate, TranslateTransform.YProperty, 0, TimeSpan.FromMilliseconds(220), easing, TimeSpan.Zero);
    }

    private void RefreshShellThemeState()
    {
        ApplyUpdateState(_updates.State);
        foreach (var button in new[]
                 {
                     DashboardButton, IntelligenceButton, CustomersButton, InboxButton,
                     CustomerEnrichmentButton, EmailButton, BroadcastButton, KnowledgeButton, AnalyticsButton
                 })
            ApplyNavigationButtonState(button, ReferenceEquals(button, _activeButton));
        SettingsButton.SetResourceReference(Button.ForegroundProperty, "SidebarText");
    }

    private void ApplyNavigationButtonState(Button button, bool active)
    {
        button.SetResourceReference(Button.ForegroundProperty, "SidebarText");
        MotionAssist.SetIsSelected(button, active);
        button.Background = Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
    }

    private void CommandButton_Click(object sender, RoutedEventArgs e) => ToggleCommandOverlay(true);

    private void ToggleCommandOverlay(bool show)
    {
        var version = ++_commandMotionVersion;
        if (!SystemParameters.ClientAreaAnimation)
        {
            CommandOverlay.BeginAnimation(OpacityProperty, null);
            CommandPanel.BeginAnimation(OpacityProperty, null);
            CommandPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CommandPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CommandPanelTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            CommandOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            CommandOverlay.Opacity = show ? 1 : 0;
            CommandPanel.Opacity = show ? 1 : 0;
            CommandPanelScale.ScaleX = show ? 1 : 0.97;
            CommandPanelScale.ScaleY = show ? 1 : 0.97;
            CommandPanelTranslate.Y = show ? 0 : -10;
            if (show)
            {
                _focusBeforeCommandOverlay = Keyboard.FocusedElement;
                FirstQuickActionButton.Focus();
            }
            else
            {
                RestoreCommandOverlayFocus();
            }
            return;
        }

        var wasCollapsed = CommandOverlay.Visibility != Visibility.Visible;
        if (show)
        {
            _focusBeforeCommandOverlay ??= Keyboard.FocusedElement;
            CommandOverlay.Visibility = Visibility.Visible;
            if (wasCollapsed)
            {
                CommandOverlay.Opacity = 0;
                CommandPanel.Opacity = 0;
                CommandPanelScale.ScaleX = 0.97;
                CommandPanelScale.ScaleY = 0.97;
                CommandPanelTranslate.Y = -10;
            }

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            AnimateAndCommit(CommandOverlay, OpacityProperty, 1, TimeSpan.FromMilliseconds(180), easing, TimeSpan.Zero, () => version == _commandMotionVersion);
            AnimateAndCommit(CommandPanel, OpacityProperty, 1, TimeSpan.FromMilliseconds(205), easing, TimeSpan.FromMilliseconds(20), () => version == _commandMotionVersion);
            AnimateAndCommit(CommandPanelScale, ScaleTransform.ScaleXProperty, 1, TimeSpan.FromMilliseconds(245), easing, TimeSpan.Zero, () => version == _commandMotionVersion);
            AnimateAndCommit(CommandPanelScale, ScaleTransform.ScaleYProperty, 1, TimeSpan.FromMilliseconds(245), easing, TimeSpan.Zero, () => version == _commandMotionVersion);
            AnimateAndCommit(
                CommandPanelTranslate,
                TranslateTransform.YProperty,
                0,
                TimeSpan.FromMilliseconds(245),
                easing,
                TimeSpan.Zero,
                () => version == _commandMotionVersion,
                () => FirstQuickActionButton.Focus());
            return;
        }

        if (wasCollapsed) return;
        var exitEase = new SineEase { EasingMode = EasingMode.EaseIn };
        AnimateAndCommit(CommandOverlay, OpacityProperty, 0, TimeSpan.FromMilliseconds(145), exitEase, TimeSpan.Zero, () => version == _commandMotionVersion);
        AnimateAndCommit(CommandPanel, OpacityProperty, 0, TimeSpan.FromMilliseconds(130), exitEase, TimeSpan.Zero, () => version == _commandMotionVersion);
        AnimateAndCommit(CommandPanelScale, ScaleTransform.ScaleXProperty, 0.985, TimeSpan.FromMilliseconds(160), exitEase, TimeSpan.Zero, () => version == _commandMotionVersion);
        AnimateAndCommit(CommandPanelScale, ScaleTransform.ScaleYProperty, 0.985, TimeSpan.FromMilliseconds(160), exitEase, TimeSpan.Zero, () => version == _commandMotionVersion);
        AnimateAndCommit(
            CommandPanelTranslate,
            TranslateTransform.YProperty,
            -6,
            TimeSpan.FromMilliseconds(165),
            exitEase,
            TimeSpan.Zero,
            () => version == _commandMotionVersion,
            () =>
            {
                if (version != _commandMotionVersion) return;
                CommandOverlay.Visibility = Visibility.Collapsed;
                RestoreCommandOverlayFocus();
            });
    }

    private void RestoreCommandOverlayFocus()
    {
        if (_focusBeforeCommandOverlay is UIElement previous && previous.IsVisible && previous.IsEnabled)
            previous.Focus();
        _focusBeforeCommandOverlay = null;
    }

    private void CommandOverlay_MouseDown(object sender, MouseButtonEventArgs e) => ToggleCommandOverlay(false);

    private void CommandPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private async void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        ToggleCommandOverlay(false);
        switch (action)
        {
            case "import": OpenImport(this, EventArgs.Empty); break;
            case "intelligence": await NavigateAsync(action, IntelligenceButton); break;
            case "customer-enrichment": await NavigateAsync(action, CustomerEnrichmentButton); break;
            case "inbox": await NavigateAsync(action, InboxButton); break;
            case "email": await NavigateAsync(action, EmailButton); break;
            case "broadcast": await NavigateAsync(action, BroadcastButton); break;
            case "knowledge": await NavigateAsync(action, KnowledgeButton); break;
            case "analytics": await NavigateAsync(action, AnalyticsButton); break;
        }
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down)
        {
            _sidebarKeyboardNavigationPending = true;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                () => _sidebarKeyboardNavigationPending = false);
        }
        if (e.Key == Key.Escape && OnboardingGuide.IsOpen)
        {
            await CloseGuideAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && CommandOverlay.Visibility == Visibility.Visible)
        {
            ToggleCommandOverlay(false);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.K)
        {
            ToggleCommandOverlay(CommandOverlay.Visibility != Visibility.Visible);
            e.Handled = true;
            return;
        }
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var target = e.Key switch
        {
            Key.D1 => ("dashboard", DashboardButton),
            Key.D2 => ("intelligence", IntelligenceButton),
            Key.D3 => ("customers", CustomersButton),
            Key.D4 => ("customer-enrichment", CustomerEnrichmentButton),
            Key.D5 => ("inbox", InboxButton),
            Key.D6 => ("email", EmailButton),
            Key.D7 => ("broadcast", BroadcastButton),
            Key.D8 => ("knowledge", KnowledgeButton),
            Key.D9 => ("analytics", AnalyticsButton),
            _ => ((string Page, Button Button)?)null
        };
        if (target is null) return;
        await NavigateAsync(target.Value.Page, target.Value.Button);
        e.Handled = true;
    }

    private void LeadAutomation_AnalysisChanged(object? sender, LeadAnalysisAutomationEventArgs e)
    {
        QueueCurrentViewRefresh(_dashboard, _intelligence);
    }

    private async void CustomerEnrichment_SettingsRequested(object? sender, EventArgs e) =>
        await OpenSettingsAsync(focusCustomerEnrichment: true);

    private void View_DataChanged(object? sender, EventArgs e)
    {
        QueueUnreadBadgeRefresh();
        _services.DashboardUnreadDigest.QueueBackgroundRefresh();
        _dashboard.NotifyUnreadChanged();
    }

    private void MessagingUnreadChanged(object? sender, WhatsAppMessage e)
    {
        QueueUnreadBadgeRefresh();
        _services.DashboardUnreadDigest.QueueBackgroundRefresh();
        _dashboard.NotifyUnreadChanged();
    }

    private void WhatsAppSynchronizationChanged(object? sender, WhatsAppSyncProgress e)
    {
        QueueUnreadBadgeRefresh();
        if (e.State == "data" && e.Phase == "labels") QueueCurrentViewRefresh(_customers);
    }

    private void EmailSynchronizationChanged(object? sender, EmailSynchronizationState e)
    {
        QueueUnreadBadgeRefresh();
        if (e.Imported <= 0) return;
        _services.DashboardUnreadDigest.QueueBackgroundRefresh();
        _dashboard.NotifyUnreadChanged();
    }

    private void UnreadBadgeTimer_Tick(object? sender, EventArgs e) => QueueUnreadBadgeRefresh();

    private void QueueUnreadBadgeRefresh()
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_unreadBadgeRefreshRunning)
            {
                _unreadBadgeRefreshPending = true;
                return;
            }
            _unreadBadgeRefreshRunning = true;
            try
            {
                do
                {
                    _unreadBadgeRefreshPending = false;
                    await UpdateUnreadBadgesAsync();
                }
                while (_unreadBadgeRefreshPending);
            }
            catch
            {
                // The five-second reconciliation timer retries transient database reads.
            }
            finally
            {
                _unreadBadgeRefreshRunning = false;
            }
        });
    }

    private bool _currentViewRefreshRunning;
    private bool _currentViewRefreshPending;

    private void QueueCurrentViewRefresh(params IRefreshableView[] eligibleViews)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (ContentHost.Content is not IRefreshableView currentView) return;
            if (eligibleViews.Length > 0 && !eligibleViews.Contains(currentView)) return;
            if (_currentViewRefreshRunning)
            {
                _currentViewRefreshPending = true;
                return;
            }

            _currentViewRefreshRunning = true;
            try
            {
                do
                {
                    _currentViewRefreshPending = false;
                    await Task.Delay(150);
                    if (ReferenceEquals(ContentHost.Content, currentView))
                        await currentView.RefreshAsync();
                }
                while (_currentViewRefreshPending && ReferenceEquals(ContentHost.Content, currentView));
            }
            catch
            {
                // A later navigation or data event will retry the current page.
            }
            finally
            {
                _currentViewRefreshRunning = false;
            }
        });
    }

    private async Task UpdateUnreadBadgesAsync()
    {
        var totals = await _services.Repository.GetInboxUnreadTotalsAsync();
        SetUnreadBadge(WhatsAppUnreadBadge, WhatsAppUnreadText, InboxButton, totals.WhatsApp, "WhatsApp");
        SetUnreadBadge(EmailUnreadBadge, EmailUnreadText, EmailButton, totals.Email, "邮件箱");
        if (_lastUnreadTotals is not null && _lastUnreadTotals != totals)
        {
            _services.DashboardUnreadDigest.QueueBackgroundRefresh();
            _dashboard.NotifyUnreadChanged();
        }
        _lastUnreadTotals = totals;
    }

    private static void SetUnreadBadge(Border badge, TextBlock text, Button button, int count, string channel)
    {
        badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        text.Text = count > 99 ? "99+" : count.ToString();
        button.ToolTip = count > 0 ? $"{channel}：{count} 条未读消息" : $"{channel}：暂无未读消息";
    }

    private void Campaigns_SafetyStopped(object? sender, CampaignSafetyStoppedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var sent = e.Campaigns.Sum(item => item.Sent);
            var failed = e.Campaigns.Sum(item => item.Failed);
            var skipped = e.Campaigns.Sum(item => item.Skipped);
            var completed = e.Campaigns.Sum(item => item.Sent + item.Failed + item.Skipped + item.Cancelled);
            var remaining = e.Campaigns.Sum(item => item.Queued);
            var details = string.Join(Environment.NewLine, e.Campaigns.Select(item => $"• {item.Name}：已处理 {item.Progress}，成功 {item.Sent}，失败 {item.Failed}，跳过 {item.Skipped}，待发送 {item.Queued}；停止位置 {item.StopOrNext}"));
            MessageBox.Show(
                $"检测到公网 IP 与任务触发前不一致，所有自动触达任务已经停止。\n\nIP：{e.PreviousIp} → {e.CurrentIp}\n已完成处理：{completed}\n已成功发送：{sent}\n发送失败：{failed}\n已跳过：{skipped}\n尚未发送：{remaining}\n\n{details}\n\n请确认网络环境后，在群发页面手动继续任务；继续时会重新建立 IP 基线。",
                "WhatsApp 群发安全阀门已触发",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        });
    }
}
