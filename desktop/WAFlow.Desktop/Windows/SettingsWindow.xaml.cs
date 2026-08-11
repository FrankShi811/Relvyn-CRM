using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;
using WAFlow.Desktop.Updates;

namespace WAFlow.Desktop.Windows;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly IApplicationUpdateService _updates;
    private readonly bool _focusCustomerEnrichment;
    private readonly DispatcherTimer _modelFetchTimer;
    private readonly Dictionary<string, AiProviderProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AiModuleRoutingRow> _moduleRows = [];
    private CancellationTokenSource? _modelFetchCancellation;
    private AppSettings _settings = new();
    private CustomerEnrichmentSettings _enrichmentSettings = new();
    private List<string> _availableModels = [];
    private string _modelsBaseUrl = "";
    private DateTimeOffset? _modelsFetchedAt;
    private string _currentProviderId = "deepseek";
    private bool _loaded;
    private bool _updatingRoutingUi;
    private bool _hadConfiguredProviderAtLoad;
    private OnboardingState _onboardingState = new();
    private string _lastPresetRoleSkill = BusinessRoleProfile.DefaultRoleSkillDescription;

    private static readonly IReadOnlyList<BusinessRolePreset> BusinessRolePresets =
    [
        new("通用销售", BusinessRoleProfile.DefaultRoleSkillDescription),
        new("销售负责人", "确定客户优先级和资源投入，复核商机判断、销售策略与团队下一步；重要决策和对外承诺由负责人确认。"),
        new("商务拓展", "识别潜在合作机会、决策人、合作路径和关键风险，准备有依据的沟通建议并推动下一步会谈。"),
        new("客户成功", "理解客户目标、使用情况和阻碍，识别续约、增购、流失与服务风险，提出需要人工确认的跟进建议。"),
        new("客户经理", "维护客户关系和跨渠道上下文，明确需求、预算、时间和决策条件，协调内部资源并记录承诺。"),
        new("市场与增长", "结合客户反馈和商机证据识别细分人群、内容方向与增长机会，不把推断写成客户事实。"),
        new("创始人或经营者", "从收入机会、客户价值、交付能力和经营风险综合判断优先级，保留关键决策的人工控制。")
    ];

    public SettingsWindow(
        AppServices services,
        IApplicationUpdateService updates,
        bool focusCustomerEnrichment = false)
    {
        InitializeComponent();
        _services = services;
        _updates = updates;
        _focusCustomerEnrichment = focusCustomerEnrichment;
        if (_focusCustomerEnrichment)
        {
            SettingsTitleText.Text = "启用客户外部调查";
            SettingsSubtitleText.Text = "填写一个联网搜索密钥即可收集公开来源；需要生成事实时，再启用 AI 并设置本程序的月度估算提醒额度。";
        }
        _modelFetchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _modelFetchTimer.Tick += async (_, _) =>
        {
            _modelFetchTimer.Stop();
            await FetchModelsAsync(false);
        };
        SettingsGuide.AllowGlobalLink = false;
        SettingsGuide.CloseRequested += SettingsGuide_CloseRequested;
        SettingsGuide.FinishedRequested += SettingsGuide_FinishedRequested;
        Loaded += SettingsWindow_Loaded;
        Closed += (_, _) =>
        {
            _modelFetchCancellation?.Cancel();
            SettingsGuide.CloseRequested -= SettingsGuide_CloseRequested;
            SettingsGuide.FinishedRequested -= SettingsGuide_FinishedRequested;
        };
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _hadConfiguredProviderAtLoad = _services.DeepSeek.HasApiKey();
        _settings = await _services.Repository.GetAppSettingsAsync();
        LoadBusinessRoleProfile();
        await LoadCustomerEnrichmentSettingsAsync();
        _settings.AiModulePreferences ??= new Dictionary<string, AiModuleModelPreference>(StringComparer.OrdinalIgnoreCase);
        ThemeModeBox.ItemsSource = new[]
        {
            new ThemeOption("跟随 Windows 系统", "System"),
            new ThemeOption("浅色", "Light"),
            new ThemeOption("深色", "Dark")
        };
        ThemeModeBox.DisplayMemberPath = nameof(ThemeOption.Label);
        ThemeModeBox.SelectedItem = ((IEnumerable<ThemeOption>)ThemeModeBox.ItemsSource)
            .First(item => item.Value == ThemeManager.Normalize(_settings.ThemeMode));
        var normalizedScale = UiScaleManager.Normalize(_settings.UiScalePercentage);
        UiScaleBox.ItemsSource = UiScaleManager.SupportedPercentages
            .Select(value => new UiScaleOption($"{value}%", value))
            .ToList();
        UiScaleBox.SelectedItem = ((IEnumerable<UiScaleOption>)UiScaleBox.ItemsSource)
            .First(item => item.Value == normalizedScale);
        SettingsScaleHost.Scale = UiScaleManager.ToScale(normalizedScale);

        foreach (var profile in _settings.ConfiguredAiProviders)
            _profiles[profile.ProviderId] = Clone(profile);

        MigrateLegacyProvider();
        AiProviderBox.ItemsSource = AiProviderCatalog.Supported;
        _currentProviderId = AiProviderCatalog.Resolve(_settings.ActiveProviderId).Id;
        AiProviderBox.SelectedItem = AiProviderCatalog.Resolve(_currentProviderId);
        LoadProvider(_currentProviderId);
        UseGlobalAiConfigurationBox.IsChecked = _settings.UseGlobalAiConfiguration;
        BuildModuleRoutingRows();
        UpdateRoutingModeUi();

        DatabasePathText.Text = _services.DataWorkspace.RootDirectory;
        await RefreshWorkspaceSummaryAsync();
        _onboardingState = await _services.Repository.GetOnboardingStateAsync();
        if (GuideCatalog.MigrateLegacyState(_onboardingState))
            await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
        _loaded = true;
        RefreshConfiguredProviders();

        if (HasProviderKey(_currentProviderId))
            await FetchModelsAsync(false);
        if (_focusCustomerEnrichment)
        {
            await Dispatcher.InvokeAsync(
                () =>
                {
                    EnrichmentSettingsSection.BringIntoView();
                    TavilySearchKeyBox.Focus();
                },
                DispatcherPriority.ContextIdle);
        }
        else if (!GuideCatalog.IsSeen(_onboardingState, "settings"))
            SettingsGuide.ShowGuide(GuideCatalog.ForModule("settings"));
    }

    private void LoadBusinessRoleProfile()
    {
        var profile = BusinessRoleProfile.Normalize(_settings.BusinessRoleProfile);
        _settings.BusinessRoleProfile = profile;
        BusinessRoleBox.ItemsSource = BusinessRolePresets;
        BusinessRoleBox.DisplayMemberPath = nameof(BusinessRolePreset.Name);
        BusinessOrganizationNameBox.Text = profile.OrganizationName;
        BusinessDescriptionBox.Text = profile.BusinessDescription;
        RoleSkillDescriptionBox.Text = profile.RoleSkillDescription;
        var preset = BusinessRolePresets.FirstOrDefault(item =>
            item.Name.Equals(profile.RoleName, StringComparison.Ordinal));
        if (preset is not null)
        {
            BusinessRoleBox.SelectedItem = preset;
            _lastPresetRoleSkill = preset.SkillDescription;
        }
        else
        {
            BusinessRoleBox.Text = profile.RoleName;
            _lastPresetRoleSkill = "";
        }
    }

    private void BusinessRoleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || BusinessRoleBox.SelectedItem is not BusinessRolePreset preset) return;
        var current = RoleSkillDescriptionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(current)
            || current.Equals(_lastPresetRoleSkill, StringComparison.Ordinal))
            RoleSkillDescriptionBox.Text = preset.SkillDescription;
        _lastPresetRoleSkill = preset.SkillDescription;
    }

    private async Task LoadCustomerEnrichmentSettingsAsync()
    {
        _enrichmentSettings = await _services.CustomerEnrichment.GetSettingsAsync();
        var providerOrderOptions = new[]
        {
            new SearchProviderOrderOption("Tavily → Brave → SearXNG（默认）", "tavily,brave,searxng"),
            new SearchProviderOrderOption("Brave → Tavily → SearXNG", "brave,tavily,searxng"),
            new SearchProviderOrderOption("SearXNG → Tavily → Brave（本地优先）", "searxng,tavily,brave")
        };
        EnrichmentProviderOrderBox.ItemsSource = providerOrderOptions;
        var savedOrder = string.Join(',', _enrichmentSettings.ProviderOrder).ToLowerInvariant();
        EnrichmentProviderOrderBox.SelectedItem = providerOrderOptions.FirstOrDefault(option => option.Value == savedOrder)
            ?? providerOrderOptions[0];
        SearXngEnabledBox.IsChecked = _enrichmentSettings.SearXngEnabled;
        SearXngBaseUrlBox.Text = _enrichmentSettings.SearXngBaseUrl;
        EnrichmentBudgetBox.Text = _enrichmentSettings.MonthlyBudgetUsd.ToString("0.####", CultureInfo.InvariantCulture);
        TavilyFreeRequestsBox.Text = _enrichmentSettings.TavilyMonthlyFreeRequests.ToString(CultureInfo.InvariantCulture);
        BraveFreeRequestsBox.Text = _enrichmentSettings.BraveMonthlyFreeRequests.ToString(CultureInfo.InvariantCulture);
        EnrichmentAllowAiAnalysisBox.IsChecked = _enrichmentSettings.AllowAiAnalysisRequests;
        EnrichmentAiReservationBox.Text = _enrichmentSettings.AiAnalysisReservationUsd.ToString("0.####", CultureInfo.InvariantCulture);
        EnrichmentAiStatusText.Text = _services.DeepSeek.HasApiKey(AiModuleKeys.CustomerEnrichment)
            ? "板块 AI 路由已配置。本程序仅按预留额进行本地估算并停止超额新调用；Provider 定价和实际账单可能不同。"
            : "板块 AI 路由未配置；调查仍会保存公开来源，但不会生成推断事实。";
        EnrichmentMaxQueriesBox.Text = _enrichmentSettings.MaxQueriesPerCustomer.ToString(CultureInfo.InvariantCulture);
        EnrichmentMaxResultsBox.Text = _enrichmentSettings.MaxResultsPerQuery.ToString(CultureInfo.InvariantCulture);
        EnrichmentMaxPagesBox.Text = _enrichmentSettings.MaxPagesPerCustomer.ToString(CultureInfo.InvariantCulture);
        EnrichmentCacheDaysBox.Text = _enrichmentSettings.CacheDays.ToString(CultureInfo.InvariantCulture);
        EnrichmentRefreshDaysBox.Text = _enrichmentSettings.StandardRefreshDays.ToString(CultureInfo.InvariantCulture);
        EnrichmentRetentionDaysBox.Text = _enrichmentSettings.DataRetentionDays.ToString(CultureInfo.InvariantCulture);
        EnrichmentManualEnabledBox.IsChecked = _enrichmentSettings.ManualEnrichmentEnabled;
        EnrichmentAutoGradeABox.IsChecked = _enrichmentSettings.AutoEnrichmentGrades.Contains("A", StringComparer.OrdinalIgnoreCase);
        EnrichmentAutoGradeBBox.IsChecked = _enrichmentSettings.AutoEnrichmentGrades.Contains("B", StringComparer.OrdinalIgnoreCase);
        EnrichmentAllowPaidBox.IsChecked = _enrichmentSettings.AllowPaidRequests;
        var tavilyConfigured = _services.CustomerEnrichment.HasProviderKey("tavily");
        var braveConfigured = _services.CustomerEnrichment.HasProviderKey("brave");
        TavilySearchStatusText.Text = tavilyConfigured ? "已安全保存；留空会继续使用" : "未填写；与下方选项任选一个即可";
        BraveSearchStatusText.Text = braveConfigured ? "已安全保存；留空会继续使用" : "未填写；与上方选项任选一个即可";
        var configuredCount = (tavilyConfigured ? 1 : 0) + (braveConfigured ? 1 : 0) + (_enrichmentSettings.SearXngEnabled ? 1 : 0);
        EnrichmentProviderStatusText.Text = configuredCount == 0 ? "需要填写一项" : "联网调查已就绪";
        await RefreshCustomerEnrichmentUsageAsync();
    }

    private async Task RefreshCustomerEnrichmentUsageAsync()
    {
        var usage = await _services.Repository.GetCustomerEnrichmentUsageSummaryAsync();
        var tavilyRemaining = Math.Max(0, _enrichmentSettings.TavilyMonthlyFreeRequests - usage.ProviderRequests.GetValueOrDefault("tavily"));
        var braveRemaining = Math.Max(0, _enrichmentSettings.BraveMonthlyFreeRequests - usage.ProviderRequests.GetValueOrDefault("brave"));
        EnrichmentUsageText.Text = $"本程序本地估算：今日 {usage.TodayRequests} 次，本月 {usage.MonthRequests} 次，累计预留或估算 ${usage.MonthEstimatedCostUsd:0.####}；选项一账号额度估算剩余 {tavilyRemaining}，选项二剩余 {braveRemaining}。不含账号在其他工具中的用量，实际账单以 Provider 为准。";
    }

    private bool CaptureCustomerEnrichmentSettings()
    {
        if (!decimal.TryParse(EnrichmentBudgetBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var budget)
            && !decimal.TryParse(EnrichmentBudgetBox.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out budget))
        {
            MessageBox.Show("本地月度估算提醒额度必须是有效数字。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if ((!decimal.TryParse(EnrichmentAiReservationBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var aiReservation)
                && !decimal.TryParse(EnrichmentAiReservationBox.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out aiReservation))
            || aiReservation < 0)
        {
            MessageBox.Show("每次 AI 本地估算预留必须是大于或等于 0 的有效数字。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!TryReadEnrichmentInteger(TavilyFreeRequestsBox, "Tavily 每月账号额度估算", 0, 1_000_000, out var tavilyFreeRequests)
            || !TryReadEnrichmentInteger(BraveFreeRequestsBox, "Brave 每月账号额度估算", 0, 1_000_000, out var braveFreeRequests)
            || !TryReadEnrichmentInteger(EnrichmentMaxQueriesBox, "每位客户最大查询数", 1, 6, out var maxQueries)
            || !TryReadEnrichmentInteger(EnrichmentMaxResultsBox, "每条查询最大结果数", 1, 8, out var maxResults)
            || !TryReadEnrichmentInteger(EnrichmentMaxPagesBox, "每位客户最大网页数", 1, 12, out var maxPages)
            || !TryReadEnrichmentInteger(EnrichmentCacheDaysBox, "缓存天数", 1, 365, out var cacheDays)
            || !TryReadEnrichmentInteger(EnrichmentRefreshDaysBox, "标准刷新天数", 7, 365, out var refreshDays)
            || !TryReadEnrichmentInteger(EnrichmentRetentionDaysBox, "数据保留天数", 30, 3650, out var retentionDays))
            return false;
        if (budget < 0)
        {
            MessageBox.Show("本地月度估算提醒额度不能小于 0。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (EnrichmentAllowPaidBox.IsChecked == true && budget <= 0)
        {
            MessageBox.Show("允许继续付费搜索前，必须设置大于 0 的本地月度估算提醒额度。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (EnrichmentAllowAiAnalysisBox.IsChecked == true && budget <= 0)
        {
            MessageBox.Show("启用 AI 事实整理前，请设置大于 0 的本地月度估算提醒额度。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (EnrichmentAllowAiAnalysisBox.IsChecked == true)
        {
            if (aiReservation <= 0)
                aiReservation = CustomerEnrichmentSettings.DefaultAiAnalysisReservationUsd;
            aiReservation = Math.Min(aiReservation, budget);
        }
        _enrichmentSettings.SearXngEnabled = SearXngEnabledBox.IsChecked == true;
        _enrichmentSettings.ProviderOrder = ((EnrichmentProviderOrderBox.SelectedValue as string)
                ?? "tavily,brave,searxng")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _enrichmentSettings.SearXngBaseUrl = SearXngBaseUrlBox.Text.Trim();
        _enrichmentSettings.MonthlyBudgetUsd = budget;
        _enrichmentSettings.AllowPaidRequests = EnrichmentAllowPaidBox.IsChecked == true;
        _enrichmentSettings.AllowAiAnalysisRequests = EnrichmentAllowAiAnalysisBox.IsChecked == true;
        _enrichmentSettings.AiAnalysisReservationUsd = aiReservation;
        _enrichmentSettings.TavilyMonthlyFreeRequests = tavilyFreeRequests;
        _enrichmentSettings.BraveMonthlyFreeRequests = braveFreeRequests;
        _enrichmentSettings.MaxQueriesPerCustomer = maxQueries;
        _enrichmentSettings.MaxResultsPerQuery = maxResults;
        _enrichmentSettings.MaxPagesPerCustomer = maxPages;
        _enrichmentSettings.CacheDays = cacheDays;
        _enrichmentSettings.StandardRefreshDays = refreshDays;
        _enrichmentSettings.DataRetentionDays = retentionDays;
        _enrichmentSettings.HighValueRefreshDays = Math.Min(_enrichmentSettings.HighValueRefreshDays, refreshDays);
        _enrichmentSettings.MajorOpportunityRefreshDays = Math.Min(_enrichmentSettings.MajorOpportunityRefreshDays, _enrichmentSettings.HighValueRefreshDays);
        _enrichmentSettings.ManualEnrichmentEnabled = EnrichmentManualEnabledBox.IsChecked != false;
        _enrichmentSettings.AutoEnrichmentGrades = new[]
        {
            EnrichmentAutoGradeABox.IsChecked == true ? "A" : "",
            EnrichmentAutoGradeBBox.IsChecked == true ? "B" : ""
        }.Where(value => value.Length > 0).ToList();
        return true;
    }

    private static bool TryReadEnrichmentInteger(TextBox box, string label, int minimum, int maximum, out int value)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= minimum && value <= maximum) return true;
        MessageBox.Show($"{label}必须是 {minimum}–{maximum} 之间的整数。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async void SearchProviderTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string providerId } button) return;
        var keyOverride = providerId switch
        {
            "tavily" => TavilySearchKeyBox.Password,
            "brave" => BraveSearchKeyBox.Password,
            _ => null
        };
        button.IsEnabled = false;
        EnrichmentConnectionStatusText.Text = "正在测试联网连接；本次会计入对应账号调用量…";
        try
        {
            var health = await _services.CustomerEnrichment.TestProviderConfigurationAsync(
                providerId,
                keyOverride,
                SearXngBaseUrlBox.Text.Trim());
            EnrichmentConnectionStatusText.Text = health.Available
                ? "联网连接正常。保存并应用后即可回到客户外部调查。"
                : "联网连接不可用。请检查密钥或网络后重试。";
            EnrichmentConnectionStatusText.SetResourceReference(
                TextBlock.ForegroundProperty,
                health.Available ? "Success" : "Danger");
            await RefreshCustomerEnrichmentUsageAsync();
        }
        catch (Exception error)
        {
            EnrichmentConnectionStatusText.Text = error.Message;
            EnrichmentConnectionStatusText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        }
        finally { button.IsEnabled = true; }
    }

    private void MigrateLegacyProvider()
    {
        var active = AiProviderCatalog.Resolve(_settings.ActiveProviderId);
        if (!_profiles.ContainsKey(active.Id))
        {
            _profiles[active.Id] = new AiProviderProfile
            {
                ProviderId = active.Id,
                DisplayName = active.DisplayName,
                BaseUrl = string.IsNullOrWhiteSpace(_settings.DeepSeekBaseUrl) ? active.DefaultBaseUrl : _settings.DeepSeekBaseUrl,
                Model = _settings.DeepSeekModel,
                AvailableModels = _settings.AvailableModels.ToList(),
                ModelsFetchedAt = _settings.ModelsFetchedAt,
                IsConfigured = _services.DeepSeek.HasApiKey()
            };
        }

        // Existing installations stored the active key under the historical
        // DeepSeek target. Copy it once to the provider-specific credential.
        var legacyKey = _services.Secrets.Read();
        var providerStore = ProviderCredentialStore(active.Id);
        if (!string.IsNullOrWhiteSpace(legacyKey) && string.IsNullOrWhiteSpace(providerStore.Read()))
            providerStore.Save(legacyKey);
    }

    private void AiProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || AiProviderBox.SelectedItem is not AiProviderDefinition selected
            || selected.Id.Equals(_currentProviderId, StringComparison.OrdinalIgnoreCase)) return;
        CaptureCurrentProvider();
        _currentProviderId = selected.Id;
        LoadProvider(_currentProviderId);
        RefreshConfiguredProviders();
    }

    private void LoadProvider(string providerId)
    {
        _modelFetchTimer.Stop();
        _modelFetchCancellation?.Cancel();
        var definition = AiProviderCatalog.Resolve(providerId);
        if (!_profiles.TryGetValue(providerId, out var profile))
        {
            profile = new AiProviderProfile
            {
                ProviderId = definition.Id,
                DisplayName = definition.DisplayName,
                BaseUrl = definition.DefaultBaseUrl,
                Model = definition.ExampleModels.FirstOrDefault() ?? ""
            };
            _profiles[providerId] = profile;
        }

        ProviderDescriptionText.Text = definition.Description
            + (definition.ExampleModels.Count == 0 ? "" : $"；常用模型示例：{string.Join("、", definition.ExampleModels)}。实际可用模型以 API 实时拉取结果为准。");
        BaseUrlBox.Text = string.IsNullOrWhiteSpace(profile.BaseUrl) ? definition.DefaultBaseUrl : profile.BaseUrl;
        ApiKeyBox.Clear();
        _availableModels = profile.AvailableModels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _modelsBaseUrl = profile.BaseUrl;
        _modelsFetchedAt = profile.ModelsFetchedAt;
        SetModelItems(profile.Model);
        RefreshGlobalReasoningOptions(
            providerId.Equals(_settings.ActiveProviderId, StringComparison.OrdinalIgnoreCase)
                ? _settings.DefaultReasoningEffort
                : AiReasoningEfforts.Auto);
        ApiStatusText.Text = HasProviderKey(providerId) ? "已安全配置" : "未配置";
        ModelStatusText.Text = _availableModels.Count > 0
            ? $"已缓存 {_availableModels.Count} 个模型；点击“拉取”可验证 Key 并刷新。"
            : "填写 API Key 后点击“拉取”，验证连接并获取该账号可用模型。";
    }

    private void CaptureCurrentProvider()
    {
        var definition = AiProviderCatalog.Resolve(_currentProviderId);
        if (!_profiles.TryGetValue(_currentProviderId, out var profile))
            _profiles[_currentProviderId] = profile = new AiProviderProfile { ProviderId = definition.Id, DisplayName = definition.DisplayName };
        profile.DisplayName = definition.DisplayName;
        profile.BaseUrl = BaseUrlBox.Text.Trim();
        profile.Model = ModelBox.Text.Trim();
        profile.AvailableModels = _availableModels.ToList();
        profile.ModelsFetchedAt = _modelsFetchedAt;
        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            _pendingKeys[_currentProviderId] = ApiKeyBox.Password.Trim();
            profile.IsConfigured = true;
        }
        else
        {
            profile.IsConfigured = HasProviderKey(_currentProviderId);
        }
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingRoutingUi) return;
        RefreshGlobalReasoningOptions();
    }

    private void UseGlobalAiConfiguration_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        UpdateRoutingModeUi();
    }

    private void UpdateRoutingModeUi()
    {
        var global = UseGlobalAiConfigurationBox.IsChecked != false;
        ModuleRoutingPanel.IsEnabled = !global;
        ModuleRoutingPanel.Opacity = global ? 0.55 : 1;
        AiRoutingSummaryText.Text = global
            ? $"统一模式：所有板块继承 {ModelBox.Text} / {ReasoningLabel(ReasoningEffortBox.SelectedValue as string)}。"
            : "分板块模式：每个板块按下方配置调用；未声明推理档位的模型始终使用 API 默认值。";
    }

    private void RefreshGlobalReasoningOptions(string? selected = null)
    {
        if (ReasoningEffortBox is null) return;
        var requested = AiReasoningEfforts.Normalize(
            selected
            ?? ReasoningEffortBox.SelectedValue as string
            ?? _settings.DefaultReasoningEffort);
        var profile = _profiles.GetValueOrDefault(_currentProviderId);
        var options = BuildReasoningOptions(profile, ModelBox?.Text);
        _updatingRoutingUi = true;
        try
        {
            ReasoningEffortBox.ItemsSource = options;
            ReasoningEffortBox.SelectedValue = options.Any(item => item.Value == requested)
                ? requested
                : AiReasoningEfforts.Auto;
        }
        finally { _updatingRoutingUi = false; }

        var capability = profile?.ModelCapabilities?.FirstOrDefault(item =>
            item.ModelId.Equals(ModelBox?.Text, StringComparison.OrdinalIgnoreCase));
        var adjustable = options.Count > 1;
        var capabilitySource = capability?.Source.Equals("provider_spec", StringComparison.OrdinalIgnoreCase) == true
            ? "官方模型规格"
            : "API 模型目录";
        ReasoningStatusText.Text = adjustable
            ? $"根据{capabilitySource}支持：{string.Join("、", options.Skip(1).Select(item => item.Label))}。请求时只发送所选档位。"
            : "API 模型目录未声明可调推理档位，系统使用模型默认值且不发送未知参数。";
        if (_loaded) UpdateRoutingModeUi();
    }

    private void BuildModuleRoutingRows()
    {
        var preserved = _moduleRows.ToDictionary(
            row => row.ModuleKey,
            row => new AiModuleModelPreference
            {
                ProviderId = row.ProviderId,
                Model = row.Model,
                ReasoningEffort = row.ReasoningEffort
            },
            StringComparer.OrdinalIgnoreCase);
        var definitions = new[]
        {
            new ModuleDefinition(AiModuleKeys.LeadIntelligence, "Command Center · 商机智能", "客户价值、成交可能性、证据、风险与下一步的结构化分析；查看列表和筛选不耗 Token。"),
            new ModuleDefinition(AiModuleKeys.Customers, "Command Center · 客户列表", "Customer Brain 人工分析；查看、编辑和同步客户资料不耗 Token。"),
            new ModuleDefinition(AiModuleKeys.WhatsAppInbox, "Customer Operations · WhatsApp", "AI 会话助理与按主要角色联动的协作助手；普通消息同步不耗 Token。"),
            new ModuleDefinition(AiModuleKeys.EmailInbox, "Customer Operations · 邮件箱", "Email Sales Copilot 根据 CRM、Customer Brain、邮件上下文和你的写信意图生成新邮件或回复草稿；同步和手写收发不耗 Token。"),
            new ModuleDefinition(AiModuleKeys.Campaigns, "Customer Operations · 自动化群发", "AI 触达话术生成；普通群发、排期和投递本身不耗 Token。"),
            new ModuleDefinition(AiModuleKeys.CustomerEnrichment, "Customer Operations · 客户外部调查", "公开来源的主体匹配与证据事实提取；查看缓存、来源和人工审核不耗 Token。"),
            new ModuleDefinition(AiModuleKeys.KnowledgeBase, "Insights · 知识库", "图片资料 OCR 调用视觉模型；文本入库、审批和检索不耗 Token。"),
            new ModuleDefinition(AiModuleKeys.CustomerAnalytics, "Insights · 客户智能分析", "分阶段事实提取、商业判断、销售策略和报告生成。")
        };
        var providers = GetConfiguredProviderOptions();

        _updatingRoutingUi = true;
        try
        {
            _moduleRows.Clear();
            foreach (var definition in definitions)
            {
                var preference = preserved.GetValueOrDefault(definition.Key)
                    ?? _settings.AiModulePreferences.GetValueOrDefault(definition.Key)
                    ?? new AiModuleModelPreference
                    {
                        ProviderId = _settings.ActiveProviderId,
                        Model = _settings.DeepSeekModel,
                        ReasoningEffort = _settings.DefaultReasoningEffort
                    };
                var row = new AiModuleRoutingRow(definition.Key, definition.Name, definition.Description);
                foreach (var provider in providers) row.ProviderOptions.Add(provider);
                row.ProviderId = providers.Any(item => item.Id.Equals(preference.ProviderId, StringComparison.OrdinalIgnoreCase))
                    ? preference.ProviderId
                    : providers.FirstOrDefault()?.Id ?? _currentProviderId;
                PopulateModuleModels(row, preference.Model, preference.ReasoningEffort);
                _moduleRows.Add(row);
            }
            ModuleRoutingItems.ItemsSource = null;
            ModuleRoutingItems.ItemsSource = _moduleRows;
        }
        finally { _updatingRoutingUi = false; }
        UpdateRoutingModeUi();
    }

    private List<ConfiguredProviderOption> GetConfiguredProviderOptions()
    {
        var options = _profiles.Values
            .Where(profile => profile.IsConfigured || HasProviderKey(profile.ProviderId)
                || profile.ProviderId.Equals(_currentProviderId, StringComparison.OrdinalIgnoreCase))
            .Select(profile => new ConfiguredProviderOption(profile.ProviderId, profile.DisplayName))
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.DisplayName)
            .ToList();
        if (options.Count == 0)
        {
            var current = AiProviderCatalog.Resolve(_currentProviderId);
            options.Add(new ConfiguredProviderOption(current.Id, current.DisplayName));
        }
        return options;
    }

    private void PopulateModuleModels(
        AiModuleRoutingRow row,
        string? selectedModel = null,
        string? selectedReasoningEffort = null)
    {
        var profile = _profiles.GetValueOrDefault(row.ProviderId)
            ?? _profiles.GetValueOrDefault(_currentProviderId);
        var models = profile?.AvailableModels?.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(profile?.Model)
            && !models.Contains(profile.Model, StringComparer.OrdinalIgnoreCase))
            models.Insert(0, profile.Model);
        var model = !string.IsNullOrWhiteSpace(selectedModel)
            && models.Contains(selectedModel, StringComparer.OrdinalIgnoreCase)
                ? models.First(item => item.Equals(selectedModel, StringComparison.OrdinalIgnoreCase))
                : profile?.Model ?? models.FirstOrDefault() ?? "";
        row.ModelOptions.Clear();
        foreach (var item in models) row.ModelOptions.Add(item);
        row.Model = model;

        var requestedEffort = AiReasoningEfforts.Normalize(selectedReasoningEffort);
        var reasoningOptions = BuildReasoningOptions(profile, model);
        row.ReasoningOptions.Clear();
        foreach (var option in reasoningOptions) row.ReasoningOptions.Add(option);
        row.ReasoningEffort = reasoningOptions.Any(item => item.Value == requestedEffort)
            ? requestedEffort
            : AiReasoningEfforts.Auto;
    }

    private void ModuleProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingRoutingUi || sender is not ComboBox { DataContext: AiModuleRoutingRow row } box
            || box.SelectedValue is not string providerId)
            return;
        row.ProviderId = providerId;
        _updatingRoutingUi = true;
        try { PopulateModuleModels(row); }
        finally { _updatingRoutingUi = false; }
    }

    private void ModuleModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingRoutingUi || sender is not ComboBox { DataContext: AiModuleRoutingRow row } box
            || box.SelectedItem is not string model)
            return;
        row.Model = model;
        _updatingRoutingUi = true;
        try
        {
            var options = BuildReasoningOptions(_profiles.GetValueOrDefault(row.ProviderId), model);
            row.ReasoningOptions.Clear();
            foreach (var option in options) row.ReasoningOptions.Add(option);
            if (!options.Any(item => item.Value == row.ReasoningEffort))
                row.ReasoningEffort = AiReasoningEfforts.Auto;
        }
        finally { _updatingRoutingUi = false; }
    }

    private static List<ReasoningOption> BuildReasoningOptions(AiProviderProfile? profile, string? model)
    {
        var capability = profile?.ModelCapabilities?.FirstOrDefault(item =>
            item.ModelId.Equals(model, StringComparison.OrdinalIgnoreCase));
        var adjustable = capability is not null
            && !string.IsNullOrWhiteSpace(capability.ReasoningParameter)
            && capability.ReasoningEfforts.Count > 0;
        var options = new List<ReasoningOption>
        {
            new(ReasoningAutoLabel(profile?.ProviderId, model), AiReasoningEfforts.Auto)
        };
        if (adjustable)
            options.AddRange(capability!.ReasoningEfforts
                .Select(value => new ReasoningOption(ReasoningLabel(value), value)));
        return options;
    }

    private static string ReasoningAutoLabel(string? providerId, string? model)
    {
        var provider = providerId?.Trim().ToLowerInvariant() ?? "";
        var normalizedModel = model?.Trim().ToLowerInvariant() ?? "";
        if (provider == "deepseek" && normalizedModel is "deepseek-v4-flash" or "deepseek-v4-pro")
            return "自动（官方默认 high）";
        if (provider == "zhipu" && (normalizedModel.Contains("glm-5.2") || normalizedModel.Contains("glm-5-2")))
            return "自动（官方默认 max）";
        if (provider == "qwen" && (normalizedModel.Contains("qwen3.8-max") || normalizedModel.Contains("qwen3-8-max")))
            return "自动（官方默认 xhigh）";
        if (provider == "anthropic"
            && (normalizedModel.Contains("claude-opus-5")
                || normalizedModel.Contains("claude-sonnet-5")
                || normalizedModel.Contains("claude-fable-5")
                || normalizedModel.Contains("claude-mythos-5")
                || normalizedModel.Contains("claude-opus-4-8")
                || normalizedModel.Contains("claude-opus-4-7")
                || normalizedModel.Contains("claude-opus-4-6")
                || normalizedModel.Contains("claude-sonnet-4-6")
                || normalizedModel.Contains("claude-opus-4-5")))
            return "自动（官方默认 high）";
        if (provider == "xai" && normalizedModel.StartsWith("grok-4.5", StringComparison.Ordinal))
            return "自动（官方默认 high）";
        return "自动（模型默认）";
    }

    private static string ReasoningLabel(string? value) => AiReasoningEfforts.Normalize(value) switch
    {
        "none" => "关闭推理",
        "minimal" => "极低（minimal）",
        "low" => "低（low）",
        "medium" => "中（medium）",
        "high" => "高（high）",
        "xhigh" => "极高（xhigh）",
        "ultra" => "最高（ultra）",
        "max" => "最高（max）",
        _ => "自动（模型默认）"
    };

    private void ShowGuide_Click(object sender, RoutedEventArgs e) =>
        SettingsGuide.ShowGuide(GuideCatalog.ForModule("settings"));

    private async Task MarkSettingsGuideSeenAsync()
    {
        GuideCatalog.MarkSeen(_onboardingState, "settings");
        await _services.Repository.SaveOnboardingStateAsync(_onboardingState);
    }

    private async void SettingsGuide_CloseRequested(object? sender, EventArgs e)
    {
        await MarkSettingsGuideSeenAsync();
        SettingsGuide.HideGuide();
    }

    private async void SettingsGuide_FinishedRequested(object? sender, EventArgs e)
    {
        await MarkSettingsGuideSeenAsync();
        SettingsGuide.HideGuide();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingModuleSelections(ModuleRoutingItems);
        CaptureCurrentProvider();
        if (!CaptureCustomerEnrichmentSettings()) return;
        if (!_profiles.TryGetValue(_currentProviderId, out var active)) return;
        var hasActiveKey = HasProviderKey(_currentProviderId);
        if (hasActiveKey
            && (!Uri.TryCreate(active.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show("AI Base URL 必须是有效的 HTTPS 地址。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (hasActiveKey
            && (string.IsNullOrWhiteSpace(active.Model)
            || !active.AvailableModels.Contains(active.Model, StringComparer.OrdinalIgnoreCase)
            || !_modelsBaseUrl.Equals(active.BaseUrl, StringComparison.OrdinalIgnoreCase)))
        {
            if (!await FetchModelsAsync(true)) return;
            CaptureCurrentProvider();
            active = _profiles[_currentProviderId];
        }
        if (hasActiveKey && UseGlobalAiConfigurationBox.IsChecked == false)
        {
            foreach (var row in _moduleRows)
            {
                if (!_profiles.TryGetValue(row.ProviderId, out var provider)
                    || !HasProviderKey(row.ProviderId))
                {
                    MessageBox.Show($"“{row.DisplayName}”选择的 Provider 尚未完成 API Key 验证。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(row.Model)
                    || !provider.AvailableModels.Contains(row.Model, StringComparer.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"“{row.DisplayName}”尚未选择该 Provider 实际返回的可用模型。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }

        SaveButton.IsEnabled = false;
        try
        {
            _settings.BusinessRoleProfile = BusinessRoleProfile.Normalize(new BusinessRoleProfile
            {
                OrganizationName = BusinessOrganizationNameBox.Text,
                BusinessDescription = BusinessDescriptionBox.Text,
                RoleName = BusinessRoleBox.Text,
                RoleSkillDescription = RoleSkillDescriptionBox.Text
            });
            if (!string.IsNullOrWhiteSpace(TavilySearchKeyBox.Password))
                _services.CustomerEnrichment.SaveProviderKey("tavily", TavilySearchKeyBox.Password);
            if (!string.IsNullOrWhiteSpace(BraveSearchKeyBox.Password))
                _services.CustomerEnrichment.SaveProviderKey("brave", BraveSearchKeyBox.Password);
            await _services.CustomerEnrichment.SaveSettingsAsync(_enrichmentSettings);
            foreach (var pending in _pendingKeys)
                ProviderCredentialStore(pending.Key).Save(pending.Value);

            var activeKey = ReadProviderKey(_currentProviderId);
            // The existing provider service remains OpenAI-compatible and reads
            // this stable credential target. Keep it synchronized to the active
            // profile without exposing the key to settings or logs.
            if (!string.IsNullOrWhiteSpace(activeKey))
                _services.Secrets.Save(activeKey);
            foreach (var profile in _profiles.Values)
                profile.IsConfigured = HasProviderKey(profile.ProviderId);

            _settings.ActiveProviderId = _currentProviderId;
            _settings.ConfiguredAiProviders = _profiles.Values
                .Where(profile => profile.IsConfigured)
                .Select(Clone)
                .OrderBy(profile => profile.DisplayName)
                .ToList();
            _settings.DeepSeekBaseUrl = active.BaseUrl.TrimEnd('/');
            _settings.DeepSeekModel = active.Model;
            _settings.DefaultReasoningEffort = AiReasoningEfforts.Normalize(
                ReasoningEffortBox.SelectedValue as string);
            _settings.UseGlobalAiConfiguration = UseGlobalAiConfigurationBox.IsChecked != false;
            var expectedModulePreferences = AiModulePreferencePersistence.CreateSnapshot(
                _moduleRows.Select(row => new AiModulePreferenceSelection(
                    row.ModuleKey,
                    row.ProviderId,
                    row.Model,
                    row.ReasoningEffort)));
            _settings.AiModulePreferences = expectedModulePreferences;
            _settings.AvailableModels = active.AvailableModels.ToList();
            _settings.ModelsBaseUrl = active.BaseUrl.TrimEnd('/');
            _settings.ModelsFetchedAt = active.ModelsFetchedAt;
            _settings.ThemeMode = (ThemeModeBox.SelectedItem as ThemeOption)?.Value ?? "System";
            _settings.UiScalePercentage = UiScaleManager.Normalize(
                (UiScaleBox.SelectedItem as UiScaleOption)?.Value ?? 100);
            await _services.Repository.SaveAppSettingsAsync(_settings);
            var persistedSettings = await _services.Repository.GetAppSettingsAsync();
            var routeMismatches = AiModulePreferencePersistence.FindMismatches(
                expectedModulePreferences,
                persistedSettings.AiModulePreferences);
            if (routeMismatches.Count > 0)
            {
                var affectedModules = routeMismatches
                    .Select(key => _moduleRows.FirstOrDefault(row =>
                        row.ModuleKey.Equals(key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? key);
                throw new InvalidOperationException(
                    $"板块模型保存校验失败：{string.Join("、", affectedModules)}。设置窗口已保持打开，请重试。");
            }
            _settings = persistedSettings;
            ThemeManager.Apply(_settings.ThemeMode);
            if (!_hadConfiguredProviderAtLoad && !string.IsNullOrWhiteSpace(activeKey))
                _ = ResumeQueuedLeadAnalysisAsync();
            DialogResult = true;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            SaveButton.IsEnabled = true;
        }
    }

    private static void CommitPendingModuleSelections(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ComboBox comboBox)
            {
                comboBox.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource();
                comboBox.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateSource();
            }
            CommitPendingModuleSelections(child);
        }
    }

    private async Task ResumeQueuedLeadAnalysisAsync()
    {
        try
        {
            await _services.LeadAutomation.NotifyProviderConfiguredAsync();
        }
        catch
        {
            // Queued work is durable and will be retried by the normal analysis
            // workflow. A background resume failure must never block settings.
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void VersionHistory_Click(object sender, RoutedEventArgs e) =>
        new VersionHistoryWindow(_updates) { Owner = this }.ShowDialog();

    private async Task RefreshWorkspaceSummaryAsync()
    {
        try
        {
            var usage = await _services.DataWorkspaceManager.GetUsageAsync(
                _services.DataWorkspace);
            DatabasePathText.Text = usage.RootDirectory;
            WorkspaceUsageText.Text =
                $"工作区占用 {DataWorkspaceManager.FormatBytes(usage.UsedBytes)} · " +
                $"{usage.DriveName} 可用 {DataWorkspaceManager.FormatBytes(usage.AvailableBytes)}";
            MoveWorkspaceButton.IsEnabled = !usage.IsEnvironmentOverride;
            if (usage.IsEnvironmentOverride)
            {
                WorkspaceStatusText.Text =
                    "当前由测试环境变量指定数据库路径，正式工作区迁移已停用。";
            }
        }
        catch (Exception error)
        {
            WorkspaceUsageText.Text = $"无法读取工作区占用：{error.Message}";
            MoveWorkspaceButton.IsEnabled = false;
        }
    }

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _services.DataWorkspace.RootDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"无法打开工作区：\n{error.Message}",
                "AI Sales OS",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void MoveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "选择本地数据工作区要迁移到的磁盘或文件夹",
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;

        MoveWorkspaceButton.IsEnabled = false;
        try
        {
            var targetRoot = _services.DataWorkspaceManager.BuildSuggestedTargetRoot(
                picker.FolderName);
            var preview = await _services.DataWorkspaceManager.PreviewMigrationAsync(
                targetRoot);
            var confirmed = MessageBox.Show(
                $"准备把完整本地数据工作区迁移到：\n{preview.TargetRoot}\n\n" +
                $"需要复制：{DataWorkspaceManager.FormatBytes(preview.SourceBytes)}\n" +
                $"目标磁盘可用：{DataWorkspaceManager.FormatBytes(preview.TargetAvailableBytes)}\n\n" +
                "程序将重启，依次完成复制、文件哈希和 SQLite 完整性校验。只有新工作区成功启动后才会清理原位置；任何失败都会继续使用原位置。",
                "确认迁移本地数据工作区",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (confirmed != MessageBoxResult.Yes) return;

            WorkspaceStatusText.Text = "迁移计划已保存，正在安全重启…";
            await _services.DataWorkspaceManager.ScheduleMigrationAsync(preview);
            try
            {
                var restart = BuildWorkspaceMigrationRestart();
                if (Process.Start(restart) is null)
                    throw new InvalidOperationException("未能启动迁移重启进程。");
                if (Application.Current is App app)
                    app.RequestWorkspaceMigrationShutdown();
                Application.Current.Shutdown();
            }
            catch
            {
                await _services.DataWorkspaceManager.CancelScheduledMigrationAsync();
                throw;
            }
        }
        catch (Exception error)
        {
            WorkspaceStatusText.Text =
                "迁移未开始，程序仍在使用原工作区。请检查目标磁盘后重试。";
            MessageBox.Show(
                $"无法迁移本地数据工作区：\n{error.Message}",
                "迁移未开始",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            if (!Application.Current.Dispatcher.HasShutdownStarted)
                MoveWorkspaceButton.IsEnabled = true;
        }
    }

    private static ProcessStartInfo BuildWorkspaceMigrationRestart()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位当前程序入口。");
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false
        };
        if (Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请在正式安装版中迁移本地数据工作区。");
        start.ArgumentList.Add("--apply-workspace-migration");
        start.ArgumentList.Add("--wait-for-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        return start;
    }

    private async void ReloadModels_Click(object sender, RoutedEventArgs e) => await FetchModelsAsync(true);

    private void UiScaleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiScaleBox.SelectedItem is UiScaleOption option)
            SettingsScaleHost.Scale = UiScaleManager.ToScale(option.Value);
    }

    private void ProviderInput_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _modelFetchTimer.Stop();
        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            _pendingKeys[_currentProviderId] = ApiKeyBox.Password.Trim();
        if (HasProviderKey(_currentProviderId)
            && Uri.TryCreate(BaseUrlBox.Text.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
            _modelFetchTimer.Start();
    }

    private async Task<bool> FetchModelsAsync(bool showError)
    {
        if (!Uri.TryCreate(BaseUrlBox.Text.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            if (showError) MessageBox.Show("AI Base URL 必须是有效的 HTTPS 地址。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var key = !string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? ApiKeyBox.Password.Trim() : ReadProviderKey(_currentProviderId);
        if (string.IsNullOrWhiteSpace(key))
        {
            ModelStatusText.Text = "请先填写 API Key。";
            if (showError) MessageBox.Show("请先填写 API Key。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        _modelFetchCancellation?.Cancel();
        _modelFetchCancellation = new CancellationTokenSource();
        ReloadModelsButton.IsEnabled = false;
        ModelStatusText.Text = "正在验证 API Key 并拉取全部可用模型…";
        try
        {
            var selected = ModelBox.Text.Trim();
            var normalizedBaseUrl = baseUri.ToString().TrimEnd('/');
            var catalog = await _services.DeepSeek.DiscoverModelsAsync(_currentProviderId, normalizedBaseUrl, key, _modelFetchCancellation.Token);
            _pendingKeys[_currentProviderId] = key;
            _availableModels = catalog.Models.ToList();
            _profiles[_currentProviderId].ModelCapabilities = catalog.ModelCapabilities.Select(Clone).ToList();
            _modelsBaseUrl = normalizedBaseUrl;
            _modelsFetchedAt = catalog.FetchedAt;
            SetModelItems(_availableModels.Contains(selected, StringComparer.OrdinalIgnoreCase) ? selected : _availableModels.First());
            CaptureCurrentProvider();
            _profiles[_currentProviderId].IsConfigured = true;
            ApiStatusText.Text = "验证通过";
            ModelStatusText.Text = $"API Key 验证通过 · 已拉取 {_availableModels.Count} 个模型 · 推理档位已校准 · {catalog.FetchedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
            RefreshConfiguredProviders();
            BuildModuleRoutingRows();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error)
        {
            ApiStatusText.Text = "验证失败";
            ModelStatusText.Text = $"连接验证失败：{error.Message}";
            if (showError) MessageBox.Show(error.Message, "API 验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            ReloadModelsButton.IsEnabled = true;
        }
    }

    private void SetModelItems(string selected)
    {
        if (!string.IsNullOrWhiteSpace(selected)
            && !_availableModels.Contains(selected, StringComparer.OrdinalIgnoreCase))
            _availableModels.Insert(0, selected);
        ModelBox.ItemsSource = null;
        ModelBox.ItemsSource = _availableModels;
        ModelBox.SelectedItem = _availableModels.FirstOrDefault(model => model.Equals(selected, StringComparison.OrdinalIgnoreCase))
            ?? _availableModels.FirstOrDefault();
        RefreshGlobalReasoningOptions();
    }

    private void RefreshConfiguredProviders()
    {
        CaptureCurrentProvider();
        var rows = _profiles.Values
            .Where(profile => profile.IsConfigured || HasProviderKey(profile.ProviderId))
            .OrderBy(profile => profile.DisplayName)
            .Select(profile => new ConfiguredProviderRow(
                profile.DisplayName,
                string.IsNullOrWhiteSpace(profile.Model) ? "尚未选择模型" : profile.Model,
                profile.ProviderId.Equals(_currentProviderId, StringComparison.OrdinalIgnoreCase) ? "当前使用" : "已配置"))
            .ToList();
        ConfiguredProvidersItems.ItemsSource = rows;
        NoConfiguredProvidersText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool HasProviderKey(string providerId) =>
        _pendingKeys.TryGetValue(providerId, out var pending) && !string.IsNullOrWhiteSpace(pending)
        || !string.IsNullOrWhiteSpace(ProviderCredentialStore(providerId).Read());

    private string? ReadProviderKey(string providerId) =>
        _pendingKeys.TryGetValue(providerId, out var pending) && !string.IsNullOrWhiteSpace(pending)
            ? pending
            : ProviderCredentialStore(providerId).Read();

    private static WindowsCredentialStore ProviderCredentialStore(string providerId) =>
        new($"WAFlow/AiProvider/{providerId}");

    private static AiProviderProfile Clone(AiProviderProfile source) => new()
    {
        ProviderId = source.ProviderId,
        DisplayName = source.DisplayName,
        BaseUrl = source.BaseUrl,
        Model = source.Model,
        AvailableModels = (source.AvailableModels ?? []).ToList(),
        ModelCapabilities = (source.ModelCapabilities ?? []).Select(Clone).ToList(),
        ModelsFetchedAt = source.ModelsFetchedAt,
        IsConfigured = source.IsConfigured
    };

    private static AiModelCapability Clone(AiModelCapability source) => new()
    {
        ModelId = source.ModelId,
        ReasoningEfforts = (source.ReasoningEfforts ?? []).ToList(),
        ReasoningParameter = source.ReasoningParameter,
        Source = source.Source
    };

    private sealed record ThemeOption(string Label, string Value);
    private sealed record BusinessRolePreset(string Name, string SkillDescription);
    private sealed record UiScaleOption(string Label, int Value);
    private sealed record SearchProviderOrderOption(string Label, string Value);
    private sealed record ConfiguredProviderRow(string DisplayName, string ModelLabel, string StatusLabel);
    private sealed record ConfiguredProviderOption(string Id, string DisplayName);
    private sealed record ReasoningOption(string Label, string Value);
    private sealed record ModuleDefinition(string Key, string Name, string Description);

    private sealed class AiModuleRoutingRow : INotifyPropertyChanged
    {
        private string _providerId = "";
        private string _model = "";
        private string _reasoningEffort = AiReasoningEfforts.Auto;

        public AiModuleRoutingRow(string moduleKey, string displayName, string description)
        {
            ModuleKey = moduleKey;
            DisplayName = displayName;
            Description = description;
        }

        public string ModuleKey { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public ObservableCollection<ConfiguredProviderOption> ProviderOptions { get; } = [];
        public ObservableCollection<string> ModelOptions { get; } = [];
        public ObservableCollection<ReasoningOption> ReasoningOptions { get; } = [];
        public string ProviderId
        {
            get => _providerId;
            set => SetField(ref _providerId, value);
        }
        public string Model
        {
            get => _model;
            set => SetField(ref _model, value);
        }
        public string ReasoningEffort
        {
            get => _reasoningEffort;
            set => SetField(ref _reasoningEffort, AiReasoningEfforts.Normalize(value));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
        {
            if (string.Equals(field, value, StringComparison.Ordinal)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
