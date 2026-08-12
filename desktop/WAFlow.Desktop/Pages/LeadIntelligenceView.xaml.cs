using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Pages;

public partial class LeadIntelligenceView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private List<Lead> _leads = [];
    private List<Lead> _visibleLeads = [];
    private CancellationTokenSource? _bulkCancellation;
    private LeadBulkAnalysisProgress? _lastBulkProgress;
    private string _bulkAnalyzeModel = "AI";
    private bool _bulkProgressSubscribed;
    private bool _decisionDrawerExpanded = true;
    private int _customerBrainRefreshGeneration;
    private int _currentPage = 1;
    private int _pageSize = 30;
    private bool _updatingOpportunityFilters;
    public event EventHandler? ImportRequested;
    public event EventHandler? OpportunityImportRequested;
    public event EventHandler? DataChanged;

    public LeadIntelligenceView(AppServices services)
    {
        InitializeComponent(); _services = services;
        GradeFilter.ItemsSource = new[] { "全部", "A", "B", "C", "D" }; GradeFilter.SelectedIndex = 0;
        OpportunitySignalFilter.ItemsSource = new[] { "全部交易信号", "有待付款交易", "有支付失败", "有纠纷风险" }; OpportunitySignalFilter.SelectedIndex = 0;
        OpportunityActivityFilter.ItemsSource = new[] { "全部更新时间", "最近 7 天有交易", "最近 30 天有交易", "最近 90 天有交易" }; OpportunityActivityFilter.SelectedIndex = 0;
        OpportunityCategoryFilter.ItemsSource = new[] { "全部一级品类" }; OpportunityCategoryFilter.SelectedIndex = 0;
        OpportunityAmountFilter.ItemsSource = new[] { "全部成交金额", "尚无成交", "0–1,000", "1,000–10,000", "10,000 以上" }; OpportunityAmountFilter.SelectedIndex = 0;
        PageSizeBox.ItemsSource = new[] { new PageSizeOption("10 条/页", 10), new PageSizeOption("30 条/页", 30), new PageSizeOption("50 条/页", 50) };
        PageSizeBox.SelectedIndex = 1;
        Loaded += LeadIntelligenceView_Loaded;
        Unloaded += LeadIntelligenceView_Unloaded;
    }

    private void LeadIntelligenceView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_bulkProgressSubscribed)
        {
            _services.LeadAutomation.BulkAnalysisProgressChanged += LeadAutomation_BulkAnalysisProgressChanged;
            _bulkProgressSubscribed = true;
        }
        TryRestoreActiveBulkProgress();
    }

    private void LeadIntelligenceView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_bulkProgressSubscribed) return;
        _services.LeadAutomation.BulkAnalysisProgressChanged -= LeadAutomation_BulkAnalysisProgressChanged;
        _bulkProgressSubscribed = false;
    }

    private void LeadAutomation_BulkAnalysisProgressChanged(object? sender, LeadBulkAnalysisProgress progress)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_services.LeadAutomation.IsBulkAnalysisRunning && progress.State != "completed") return;
            _lastBulkProgress = progress;
            if (!string.IsNullOrWhiteSpace(_services.LeadAutomation.CurrentBulkModel))
                _bulkAnalyzeModel = _services.LeadAutomation.CurrentBulkModel;
            ApplyBulkProgress(progress);
        });
    }

    public async Task RefreshAsync()
    {
        var selectedId = (LeadGrid.SelectedItem as Lead)?.Id;
        _leads = await _services.Repository.GetLeadsAsync(SearchBox.Text, GradeFilter.SelectedItem as string);
        var commitmentSummaries = await _services.CustomerCommitments.GetActiveSummariesAsync(_leads.Select(lead => lead.Id));
        foreach (var lead in _leads)
        {
            if (!commitmentSummaries.TryGetValue(lead.Id, out var commitment)) continue;
            lead.ActiveCommitmentCount = commitment.ActiveCount;
            lead.OverdueCommitmentCount = commitment.OverdueCount;
            lead.NextCommitmentDueAt = commitment.NextDueAt;
            lead.CommitmentTitle = commitment.FirstTitle;
        }
        var snapshotList = await _services.Repository.GetOpportunitySnapshotsAsync();
        UpdateCategoryFilter(snapshotList);
        var snapshots = snapshotList
            .ToDictionary(item => item.LeadId, StringComparer.OrdinalIgnoreCase);
        _leads = ApplyOpportunityFilters(_leads, snapshots);
        await RefreshAiRouteAsync();
        ApplyPagination(selectedId);
        var selectedLead = LeadGrid.SelectedItem as Lead;
        await UpdateInspectorAsync(selectedLead);
        await UpdateCustomerBrainAsync(selectedLead);
    }

    private void ApplyPagination(string? preferredLeadId = null)
    {
        var total = _leads.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)_pageSize));
        _currentPage = Math.Clamp(_currentPage, 1, totalPages);
        var startIndex = (_currentPage - 1) * _pageSize;
        _visibleLeads = _leads.Skip(startIndex).Take(_pageSize).ToList();

        LeadGrid.ItemsSource = null;
        LeadGrid.ItemsSource = _visibleLeads;
        LeadGrid.SelectedItem = _visibleLeads.FirstOrDefault(lead =>
            lead.Id.Equals(preferredLeadId, StringComparison.OrdinalIgnoreCase)) ?? _visibleLeads.FirstOrDefault();

        var first = total == 0 ? 0 : startIndex + 1;
        var last = total == 0 ? 0 : startIndex + _visibleLeads.Count;
        PageRangeText.Text = total == 0 ? "暂无商机" : $"显示第 {first:N0}–{last:N0} 位，共 {total:N0} 位";
        PageStatusText.Text = $"第 {_currentPage:N0} / {totalPages:N0} 页";
        PreviousPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _currentPage < totalPages;
    }

    public async Task RefreshAiRouteAsync()
    {
        var execution = await _services.DeepSeek.ResolveExecutionProfileAsync(AiModuleKeys.LeadIntelligence);
        _bulkAnalyzeModel = execution.Model;
        if (TryRestoreActiveBulkProgress()) return;

        var allLeads = await _services.Repository.GetLeadsAsync();

        // A running bulk task can advance while the page refresh is reading the
        // database. Its shared in-memory snapshot is the only authoritative
        // progress source until the task finishes.
        if (TryRestoreActiveBulkProgress()) return;

        UpdateBulkAnalyzeButtonIdleContent(
            execution.ProviderId,
            execution.Model,
            execution.ReasoningEffort,
            allLeads);
    }

    private void UpdateBulkAnalyzeButtonIdleContent(
        string providerId,
        string model,
        string reasoningEffort,
        IReadOnlyList<Lead> allLeads)
    {
        var total = allLeads.Count;
        var completed = allLeads.Count(lead => lead.HasCurrentAiScore);

        BulkAnalyzeButton.Content = $"使用 {model} 模型分析";
        BulkAnalyzeButton.ToolTip =
            $"商机智能实际路由：{providerId} · {model} · 推理深度 {reasoningEffort}。";
        BulkAnalyzeButton.IsEnabled = true;
        ImportButton.IsEnabled = true;
        CancelBulkButton.Visibility = Visibility.Collapsed;
        BulkProgressPanel.Visibility = Visibility.Visible;
        BulkProgressBar.Maximum = Math.Max(1, total);
        BulkProgressBar.Value = Math.Min(completed, total);
        BulkProgressText.Text = $"已分析 {completed:N0} / {total:N0}";
    }

    private bool TryRestoreActiveBulkProgress()
    {
        if (!_services.LeadAutomation.IsBulkAnalysisRunning) return false;

        if (!string.IsNullOrWhiteSpace(_services.LeadAutomation.CurrentBulkModel))
            _bulkAnalyzeModel = _services.LeadAutomation.CurrentBulkModel;

        BulkAnalyzeButton.IsEnabled = false;
        BulkAnalyzeButton.Content = $"使用 {_bulkAnalyzeModel} 模型分析";
        BulkAnalyzeButton.ToolTip = "后台批量分析正在运行；切换页面不会中断任务。";
        ImportButton.IsEnabled = false;
        CancelBulkButton.Visibility = Visibility.Visible;
        CancelBulkButton.IsEnabled = _bulkCancellation is { IsCancellationRequested: false };
        BulkProgressPanel.Visibility = Visibility.Visible;

        var progress = _services.LeadAutomation.CurrentBulkProgress;
        if (progress is not null)
        {
            _lastBulkProgress = progress;
            ApplyBulkProgress(progress);
        }
        else
        {
            BulkProgressBar.Maximum = 1;
            BulkProgressBar.Value = 0;
            BulkProgressText.Text = "正在准备分析";
        }

        return true;
    }

    private void ApplyBulkProgress(LeadBulkAnalysisProgress progress)
    {
        BulkProgressBar.Maximum = Math.Max(1, progress.Total);
        BulkProgressBar.Value = Math.Min(progress.Completed, progress.Total);
        BulkProgressText.Text = progress.State == "completed"
            ? $"已分析 {Math.Min(progress.Completed, progress.Total):N0} / {progress.Total:N0}"
            : $"正在分析 {Math.Min(progress.Completed, progress.Total):N0} / {progress.Total:N0}";
        BulkAnalyzeButton.Content = $"使用 {_bulkAnalyzeModel} 模型分析";
        CancelBulkButton.IsEnabled = _bulkCancellation is { IsCancellationRequested: false }
            && progress.State is not "cancelled";
    }

    private async Task UpdateInspectorAsync(Lead? lead)
    {
        if (lead is null)
        {
            LeadNameText.Text = "选择一个商机"; CompanyText.Text = ""; GradeText.Text = "—"; ScoreText.Text = "0"; StageText.Text = "—"; AmountText.Text = "—";
            BaseScoreText.Text = "0 / 100"; BehaviorScoreText.Text = "0";
            ProfileText.Text = "尚未选择客户"; AnalysisMetaText.Text = ""; CustomerBrainMetaText.Text = "CUSTOMER BRAIN · 等待选择客户"; SignalItems.ItemsSource = null; NextActionText.Text = "—"; FactorItems.ItemsSource = null; RiskItems.ItemsSource = null; AnalysisErrorText.Text = "";
            ConfidenceText.Text = "0%"; ConfidenceBar.Value = 0; ScoreRing.SetScore(0, "D", 0); RadarChart.SetValues([]);
            OpportunityEvidenceCard.Visibility = Visibility.Collapsed;
            OpportunityEventItems.ItemsSource = null;
            OpportunityCommitmentCard.Visibility = Visibility.Collapsed;
            OpportunityCommitmentItems.ItemsSource = null;
            return;
        }
        LeadNameText.Text = lead.DisplayName; CompanyText.Text = $"{lead.Company} · {lead.Country}"; GradeText.Text = $"{lead.Grade}级"; ScoreText.Text = lead.Score.ToString();
        StageText.Text = lead.StageLabel; AmountText.Text = lead.AmountLabel; ProfileText.Text = lead.ProfileSummary; NextActionText.Text = lead.NextAction;
        BaseScoreText.Text = $"{lead.BaseProfileScore} / 100";
        BehaviorScoreText.Text = $"{lead.BehaviorSignalScore:+#;-#;0} / ±20";
        ConfidenceText.Text = $"{lead.AnalysisConfidence:P0}";
        ConfidenceBar.Value = Math.Clamp(lead.AnalysisConfidence * 100, 0, 100);
        ScoreRing.SetScore(lead.Score, lead.Grade, lead.AnalysisConfidence);
        var trigger = lead.AnalysisTrigger switch
        {
            "whatsapp_reply" => "WhatsApp 新回复自动触发",
            "opportunity_supplement_import" => "商机补充数据变化自动触发",
            "manual" => "人工触发",
            _ => "尚未触发"
        };
        var analyzedAt = lead.LastAnalyzedAt is null ? "尚未完成 AI 分析" : $"最近完成 {lead.LastAnalyzedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}";
        var contract = lead.HasCurrentAiScore ? $"V{lead.AnalysisContractVersion}" : "等待 V2";
        AnalysisMetaText.Text = $"{contract} · {trigger} · {analyzedAt} · {lead.AnalysisStateLabel}";
        SignalItems.ItemsSource = lead.BehaviorSignals.Count > 0
            ? lead.BehaviorSignals.Select(signal => $"{signal.Signal} {signal.Score:+#;-#;0} · {signal.Evidence}").ToList()
            : new[] { "尚无经 AI 验证的 WhatsApp 行为信号" };
        var labels = new Dictionary<string, string> { ["paid_marketing_willingness"]="增长投入意愿", ["supply_stability"]="运营与交付稳定性", ["ecommerce_foundation"]="相关业务基础", ["private_traffic"]="客户触达能力", ["existing_sales"]="商业验证程度", ["materials_readiness"]="合作准备度" };
        var factorByKey = lead.ScoreFactors.ToDictionary(factor => factor.Key, StringComparer.OrdinalIgnoreCase);
        FactorItems.ItemsSource = LeadScoringLabel.Order.Select(key =>
        {
            factorByKey.TryGetValue(key, out var factor);
            return new FactorMetric(labels[key], lead.ScoreBreakdown.GetValueOrDefault(key), WAFlow.Core.Services.LeadScoringService.Weights[key], factor?.Rationale ?? "等待 AI 分析", factor is null ? "尚无证据" : string.Join("；", factor.Evidence));
        }).ToList();
        RadarChart.SetValues(LeadScoringLabel.Order.Select(key => (double)lead.ScoreBreakdown.GetValueOrDefault(key) / LeadScoringService.Weights[key]));
        RiskItems.ItemsSource = lead.Risks.Count > 0 ? lead.Risks : !lead.PhoneValid ? new[] { "号码无效，禁止打开 WhatsApp。" } : lead.AiScoreApplied ? new[] { "AI 分析结论仍需人工核对。" } : new[] { "当前 D 级是未分析初始值，不代表低价值客户。" };
        AnalysisErrorText.Text = lead.AnalysisError;
        GradeBadge.Background = (System.Windows.Media.Brush)FindResource(lead.Grade is "A" or "B" ? "SuccessSoft" : lead.Grade == "C" ? "WarningSoft" : "DangerSoft");
        var opportunity = await _services.Repository.GetOpportunitySnapshotAsync(lead.Id);
        if (opportunity is null)
        {
            OpportunityEvidenceCard.Visibility = Visibility.Collapsed;
            OpportunityEventItems.ItemsSource = null;
        }
        else
        {
            OpportunityEvidenceCard.Visibility = Visibility.Visible;
            OpportunityUpdatedText.Text = opportunity.UpdatedAt.LocalDateTime.ToString("MM-dd HH:mm");
            OpportunityValueText.Text = opportunity.ValueSummary
                + $"\n近 30 / 90 / 365 天：{opportunity.PaidAmount30Days:N2} / {opportunity.PaidAmount90Days:N2} / {opportunity.PaidAmount365Days:N2}";
            OpportunityIntentText.Text = opportunity.IntentSummary
                + (string.IsNullOrWhiteSpace(opportunity.LatestFailureReason) ? "" : $"\n最近障碍：{opportunity.LatestFailureReason}");
            OpportunityCategoryText.Text =
                $"一级：{Fallback(opportunity.PrimaryCategory)} · 二级：{Fallback(opportunity.SecondaryCategory)}\n高频产品/服务：{Fallback(opportunity.FrequentProduct)} · 最近产品/服务：{Fallback(opportunity.LatestProduct)}";
            OpportunityRiskText.Text = opportunity.RiskSummary
                + (string.IsNullOrWhiteSpace(opportunity.PrimaryDisputeReason) ? "" : $"\n主要原因：{opportunity.PrimaryDisputeReason}")
                + (opportunity.HasChargeback ? "\n含拒付交易，需人工核对。" : "");
            var recentEvents = (await _services.Repository.GetOpportunityEventsAsync([lead.Id]))
                .OrderByDescending(item => item.OccurredAt ?? item.DataDate)
                .Take(6)
                .Select(FormatOpportunityEvidence)
                .ToList();
            OpportunityEventItems.ItemsSource = recentEvents.Count > 0
                ? recentEvents
                : ["尚无可核对的交易明细。"];
        }
        var commitments = await _services.CustomerCommitments.GetActiveAsync(lead.Id);
        OpportunityCommitmentCard.Visibility = commitments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        OpportunityCommitmentCountText.Text = commitments.Count == 0
            ? ""
            : commitments.Any(item => item.IsOverdue)
                ? $"{commitments.Count(item => item.IsOverdue)} 条逾期"
                : $"{commitments.Count} 条待履约";
        OpportunityCommitmentItems.ItemsSource = commitments
            .Take(4)
            .Select(item => $"{item.DueLabel} · {item.Title}")
            .ToList();
    }

    private async Task UpdateCustomerBrainAsync(Lead? lead)
    {
        var generation = ++_customerBrainRefreshGeneration;
        if (lead is null)
        {
            CustomerBrainMetaText.Text = "CUSTOMER BRAIN · 等待选择客户";
            return;
        }

        CustomerBrainMetaText.Text = "CUSTOMER BRAIN · 正在整合 CRM、会话、触达与分析证据…";
        try
        {
            var brain = await _services.CustomerBrain.RefreshAsync(lead.Id);
            if (generation != _customerBrainRefreshGeneration || (LeadGrid.SelectedItem as Lead)?.Id != lead.Id) return;

            var facts = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.Fact);
            var inferences = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.Inference);
            var recommendations = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.Recommendation);
            var gaps = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.InformationGap);
            CustomerBrainMetaText.Text = brain.HasCurrentDecision
                ? $"CUSTOMER BRAIN V{brain.Version} · 覆盖 {brain.Coverage.Percentage}% · 事实 {facts} · AI 判断 {inferences} · 建议 {recommendations} · 缺口 {gaps}"
                : $"CUSTOMER BRAIN V{brain.Version} · 结论已过期 · 资料已变化；打开客户详情，点击“AI 分析并生成行动”";
            if (brain.HasCurrentDecision)
            {
                if (!string.IsNullOrWhiteSpace(brain.Summary)) ProfileText.Text = brain.Summary;
                if (!string.IsNullOrWhiteSpace(brain.NextBestAction)) NextActionText.Text = brain.NextBestAction;
                if (brain.Risks.Count > 0) RiskItems.ItemsSource = brain.Risks;
            }
        }
        catch (Exception error)
        {
            if (generation != _customerBrainRefreshGeneration || (LeadGrid.SelectedItem as Lead)?.Id != lead.Id) return;
            CustomerBrainMetaText.Text = $"CUSTOMER BRAIN · 暂未物化：{error.Message}";
        }
    }

    private async void BulkAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_bulkCancellation is not null) return;
        var allLeads = await _services.Repository.GetLeadsAsync();
        if (allLeads.Count == 0)
        {
            MessageBox.Show("商机智能列表中没有可分析的客户。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_services.DeepSeek.HasApiKey())
        {
            MessageBox.Show("请先在左侧“设置”中配置 API Key 并选择模型。", "无法开始批量分析", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _bulkCancellation = new CancellationTokenSource();
        _lastBulkProgress = null;
        BulkAnalyzeButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        CancelBulkButton.IsEnabled = true;
        CancelBulkButton.Visibility = Visibility.Visible;
        BulkProgressPanel.Visibility = Visibility.Visible;
        BulkProgressBar.Maximum = Math.Max(1, allLeads.Count);
        BulkProgressBar.Value = 0;
        BulkProgressText.Text = $"正在分析 0 / {allLeads.Count}";
        BulkAnalyzeButton.Content = $"使用 {_bulkAnalyzeModel} 模型分析";
        (string Message, string Title, MessageBoxImage Icon)? outcome = null;
        try
        {
            var result = await _services.LeadAutomation.AnalyzeAllLeadsAsync(null, _bulkCancellation.Token);
            DataChanged?.Invoke(this, EventArgs.Empty);
            outcome = (
                $"批量分析完成。\n\n总数：{result.Total}\n成功：{result.Succeeded}\n失败：{result.Failed}",
                "AI Sales OS",
                result.Failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
            var state = _lastBulkProgress;
            outcome = (
                $"批量分析已停止。\n\n已完成：{state?.Completed ?? 0} / {state?.Total ?? allLeads.Count}\n成功：{state?.Succeeded ?? 0}\n失败：{state?.Failed ?? 0}\n停止位置：{state?.CurrentLeadName ?? "—"}",
                "AI Sales OS",
                MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            outcome = (error.Message, "批量分析无法继续", MessageBoxImage.Warning);
        }
        finally
        {
            _bulkCancellation.Dispose();
            _bulkCancellation = null;
            BulkAnalyzeButton.IsEnabled = true;
            ImportButton.IsEnabled = true;
            CancelBulkButton.Visibility = Visibility.Collapsed;
            await RefreshAsync();
        }
        if (outcome is { } resultDialog)
            MessageBox.Show(resultDialog.Message, resultDialog.Title, MessageBoxButton.OK, resultDialog.Icon);
    }

    private void CancelBulk_Click(object sender, RoutedEventArgs e)
    {
        CancelBulkButton.IsEnabled = false;
        BulkProgressText.Text = "正在安全停止当前 AI 请求…";
        _bulkCancellation?.Cancel();
    }

    private async void LeadGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var lead = LeadGrid.SelectedItem as Lead;
        await UpdateInspectorAsync(lead);
        await UpdateCustomerBrainAsync(lead);
    }
    private void ToggleDecisionDrawer_Click(object sender, RoutedEventArgs e)
    {
        _decisionDrawerExpanded = !_decisionDrawerExpanded;
        DecisionSidebarColumn.Width = new GridLength(_decisionDrawerExpanded ? 430 : 40);
        DecisionSidebarBorder.Visibility = _decisionDrawerExpanded ? Visibility.Visible : Visibility.Collapsed;
        DecisionDrawerCollapsedRail.Visibility = _decisionDrawerExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);
    private void OpportunityImport_Click(object sender, RoutedEventArgs e) => OpportunityImportRequested?.Invoke(this, EventArgs.Empty);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void GradeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _currentPage = 1;
        await RefreshAsync();
    }
    private async void OpportunityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _updatingOpportunityFilters) return;
        _currentPage = 1;
        await RefreshAsync();
    }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _currentPage = 1;
        await RefreshAsync();
    }
    private void PageSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSizeBox.SelectedItem is not PageSizeOption option || _pageSize == option.Value) return;
        _pageSize = option.Value;
        _currentPage = 1;
        if (IsLoaded) ApplyPagination();
    }
    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage <= 1) return;
        _currentPage--;
        ApplyPagination();
    }
    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_leads.Count / (double)_pageSize));
        if (_currentPage >= totalPages) return;
        _currentPage++;
        ApplyPagination();
    }

    private sealed record FactorMetric(string Label, int Score, int Max, string Reason, string Evidence) { public double Percent => Max == 0 ? 0 : 100d * Score / Max; public string Value => $"{Score}/{Max}"; }
    private sealed record PageSizeOption(string Label, int Value);
    private static class LeadScoringLabel { public static readonly string[] Order = ["paid_marketing_willingness","supply_stability","ecommerce_foundation","private_traffic","existing_sales","materials_readiness"]; }
    private List<Lead> ApplyOpportunityFilters(List<Lead> leads, IReadOnlyDictionary<string, OpportunitySnapshot> snapshots)
    {
        var signal = OpportunitySignalFilter.SelectedItem as string ?? "全部交易信号";
        var activity = OpportunityActivityFilter.SelectedItem as string ?? "全部更新时间";
        var category = OpportunityCategoryFilter.SelectedItem as string ?? "全部一级品类";
        var amount = OpportunityAmountFilter.SelectedItem as string ?? "全部成交金额";
        var threshold = activity switch
        {
            "最近 7 天有交易" => DateTimeOffset.Now.AddDays(-7),
            "最近 30 天有交易" => DateTimeOffset.Now.AddDays(-30),
            "最近 90 天有交易" => DateTimeOffset.Now.AddDays(-90),
            _ => (DateTimeOffset?)null
        };
        return leads.Where(lead =>
        {
            snapshots.TryGetValue(lead.Id, out var snapshot);
            var signalMatch = signal switch
            {
                "有待付款交易" => snapshot?.AwaitingPaymentCount > 0,
                "有支付失败" => snapshot?.FailedPaymentCount > 0,
                "有纠纷风险" => snapshot?.HasRisk == true,
                _ => true
            };
            var activityMatch = threshold is null || snapshot?.LatestActivityAt >= threshold;
            var categoryMatch = category == "全部一级品类"
                || snapshot?.PrimaryCategory.Equals(category, StringComparison.OrdinalIgnoreCase) == true;
            var amountMatch = amount switch
            {
                "尚无成交" => snapshot is null || snapshot.SuccessfulPaymentTotal <= 0,
                "0–1,000" => snapshot is { SuccessfulPaymentTotal: > 0 and < 1000 },
                "1,000–10,000" => snapshot is { SuccessfulPaymentTotal: >= 1000 and < 10000 },
                "10,000 以上" => snapshot?.SuccessfulPaymentTotal >= 10000,
                _ => true
            };
            return signalMatch && activityMatch && categoryMatch && amountMatch;
        }).ToList();
    }
    private void UpdateCategoryFilter(IReadOnlyCollection<OpportunitySnapshot> snapshots)
    {
        var selected = OpportunityCategoryFilter.SelectedItem as string ?? "全部一级品类";
        var options = new[] { "全部一级品类" }
            .Concat(snapshots.Select(item => item.PrimaryCategory)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            .ToList();
        _updatingOpportunityFilters = true;
        try
        {
            OpportunityCategoryFilter.ItemsSource = options;
            OpportunityCategoryFilter.SelectedItem = options.Contains(selected, StringComparer.OrdinalIgnoreCase)
                ? options.First(item => item.Equals(selected, StringComparison.OrdinalIgnoreCase))
                : options[0];
        }
        finally
        {
            _updatingOpportunityFilters = false;
        }
    }
    private static string FormatOpportunityEvidence(OpportunityTransactionEvent item)
    {
        var kind = item.Kind switch
        {
            OpportunityEventKind.PaymentSucceeded => "支付成功",
            OpportunityEventKind.PaymentFailed => "支付失败",
            OpportunityEventKind.AwaitingPayment => "待付款交易",
            OpportunityEventKind.Dispute => "纠纷交易",
            _ => "交易事件"
        };
        var time = (item.OccurredAt ?? item.DataDate)?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "时间未知";
        var order = string.IsNullOrWhiteSpace(item.OrderId) ? "交易编号缺失" : $"交易 {item.OrderId}";
        var amount = item.Amount == 0
            ? ""
            : $" · {Fallback(item.Currency)} {item.Amount:N2}";
        return $"{kind} · {time}\n{order}{amount}";
    }
    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
