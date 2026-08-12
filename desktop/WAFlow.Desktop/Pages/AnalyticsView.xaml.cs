using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop.Pages;

public partial class AnalyticsView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private List<Lead> _allLeads = [];
    private List<CustomerAnalysisReport> _history = [];
    private Lead? _currentLead;
    private CustomerAnalysisReport? _currentReport;
    private CancellationTokenSource? _generationCancellation;

    public event EventHandler? DataChanged;

    public AnalyticsView(AppServices services)
    {
        InitializeComponent();
        _services = services;
    }

    public async Task RefreshAsync()
    {
        var selectedId = _currentLead?.Id;
        _allLeads = await _services.Repository.GetLeadsAsync();
        ApplyCustomerFilter(selectedId);
    }

    private void ApplyCustomerFilter(string? preferredId = null)
    {
        var query = SearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allLeads
            : _allLeads.Where(lead => string.Join(' ', lead.DisplayName, lead.Country, lead.PhoneE164, lead.ProductInterest, lead.Owner, lead.CustomFieldsLabel)
                .Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        CustomerList.ItemsSource = filtered;
        CustomerList.SelectedItem = filtered.FirstOrDefault(lead => lead.Id == preferredId)
            ?? filtered.FirstOrDefault(lead => lead.Id == _currentLead?.Id)
            ?? filtered.FirstOrDefault();
        if (filtered.Count == 0) ClearCustomer();
    }

    private async void CustomerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomerList.SelectedItem is not Lead lead) return;
        _currentLead = lead;
        CustomerNameText.Text = lead.DisplayName;
        CustomerMetaText.Text = $"{ValueOrDash(lead.Country)} · {ValueOrDash(lead.PhoneE164)} · {lead.StageLabel} · 负责人 {ValueOrDash(lead.Owner)}";
        GradeText.Text = $"{lead.Grade}级";
        ScoreText.Text = lead.Score.ToString();
        ProbabilityText.Text = "—";
        GenerateButton.IsEnabled = _generationCancellation is null;
        GenerateButton.Content = "✦  一键生成报告";
        await LoadHistoryAsync(lead.Id);
    }

    private async Task LoadHistoryAsync(string customerId, string? preferredReportId = null)
    {
        _history = await _services.CustomerAnalysis.GetHistoryAsync(customerId);
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = _history;
        VersionCountText.Text = $"{_history.Count} 份";
        HistoryList.SelectedItem = _history.FirstOrDefault(report => report.Id == preferredReportId) ?? _history.FirstOrDefault();
        if (_history.Count == 0) ShowReport(null);
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowReport(HistoryList.SelectedItem as CustomerAnalysisReport);

    private void ShowReport(CustomerAnalysisReport? report)
    {
        _currentReport = report;
        var ready = report?.Status == CustomerReportStatus.Succeeded;
        ExportWordButton.IsEnabled = ready;
        ExportPdfButton.IsEnabled = ready;
        CompareButton.IsEnabled = ready && _history.Count > 1;
        ReportPanel.Children.Clear();
        ActionItems.ItemsSource = null;
        PendingQuestionItems.ItemsSource = null;
        TalkTrackText.Text = "生成报告后显示";

        if (report is null)
        {
            EmptyReportPanel.Visibility = Visibility.Visible;
            ReportScroll.Visibility = Visibility.Collapsed;
            ProbabilityText.Text = "—";
            CoverageText.Text = _currentLead is null ? "等待选择客户" : "尚未生成数据快照。点击“一键生成报告”后读取全部可用数据。";
            return;
        }

        CustomerNameText.Text = report.CustomerName;
        CustomerMetaText.Text = $"{ValueOrDash(report.SourceSnapshot.Lead.Country)} · V{report.Version} · {report.AiModel} · {report.CreatedTime.LocalDateTime:yyyy-MM-dd HH:mm}";
        if (!ready)
        {
            EmptyReportPanel.Visibility = Visibility.Collapsed;
            ReportScroll.Visibility = Visibility.Visible;
            ReportPanel.Children.Add(CreateStatusCard(report));
            CoverageText.Text = report.Error.Length > 0 ? $"失败原因：{report.Error}" : "报告正在分阶段生成。";
            return;
        }

        EmptyReportPanel.Visibility = Visibility.Collapsed;
        ReportScroll.Visibility = Visibility.Visible;
        var content = report.Report;
        GradeText.Text = $"{content.OpportunityJudgment.Grade}级";
        ScoreText.Text = content.OpportunityJudgment.AiScore.ToString();
        ProbabilityText.Text = $"{content.OpportunityJudgment.DealProbability}%";
        ActionItems.ItemsSource = content.SalesStrategy.Actions;
        PendingQuestionItems.ItemsSource = content.SalesStrategy.PendingQuestions.Count > 0 ? content.SalesStrategy.PendingQuestions : ["当前没有记录待验证问题"];
        TalkTrackText.Text = ValueOrPlaceholder(content.SalesStrategy.RecommendedTalkTrack);
        CoverageText.Text = $"CRM 1 份 · WhatsApp {report.SourceSnapshot.WhatsAppMessages.Count} 条 · 邮件 {report.SourceSnapshot.EmailMessages.Count} 条 · 自动化触达 {report.SourceSnapshot.CampaignTouches.Count} 次 · 客户轨迹 {report.SourceSnapshot.Timeline.Count} 条 · 历史 AI 分析 {report.SourceSnapshot.LeadAnalysisHistory.Count} 次\n快照时间：{report.SourceSnapshot.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm}" +
            (string.IsNullOrWhiteSpace(report.Error) ? "" : $"\n生成说明：{report.Error}");
        if (!string.IsNullOrWhiteSpace(report.Error)) ReportPanel.Children.Add(CreateGenerationNotice(report.Error));
        BuildReport(content);
    }

    private void BuildReport(CustomerIntelligenceReportContent content)
    {
        ReportPanel.Children.Add(CreateSection("01", "客户概览（Executive Summary）",
            Field("客户一句话定位", content.ExecutiveSummary.OneLinePositioning, "事实与判断摘要"),
            Field("客户类型", content.ExecutiveSummary.CustomerType),
            Field("商业阶段", content.ExecutiveSummary.BusinessStage),
            Field("综合价值判断", content.ExecutiveSummary.OverallValueJudgment, "AI判断"),
            Field("当前销售建议", content.ExecutiveSummary.CurrentSalesRecommendation, "销售建议")));

        ReportPanel.Children.Add(CreateSection("02", "客户基础画像",
            Field("客户类型", content.BasicProfile.CustomerType),
            BulletBlock("商业模式", content.BasicProfile.BusinessModels),
            Field("产品方向", content.BasicProfile.ProductDirection),
            Field("经营规模", content.BasicProfile.OperatingScale),
            Field("发展阶段", content.BasicProfile.DevelopmentStage)));

        ReportPanel.Children.Add(CreateSection("03", "客户商业背景分析",
            Field("当前业务模式", content.BusinessBackground.CurrentBusinessModel),
            BulletBlock("核心优势", content.BusinessBackground.CoreAdvantages, "SuccessSoft"),
            BulletBlock("当前限制", content.BusinessBackground.CurrentLimitations, "WarningSoft"),
            BulletBlock("未来增长空间", content.BusinessBackground.GrowthOpportunities, "AiSurface")));

        ReportPanel.Children.Add(CreateSection("04", "当前痛点分析",
            TwoColumn("表层痛点", content.PainAnalysis.SurfacePains, "深层商业问题", content.PainAnalysis.DeepBusinessProblems)));

        ReportPanel.Children.Add(CreateSection("05", "购买动机分析",
            BulletBlock("为什么产生兴趣", content.PurchaseMotivation.InterestReasons),
            BulletBlock("当前触发事件", content.PurchaseMotivation.TriggerEvents),
            BulletBlock("决策关键因素", content.PurchaseMotivation.DecisionFactors)));

        var whatsapp = new StackPanel();
        whatsapp.Children.Add(Field("沟通积极度", content.WhatsAppAnalysis.EngagementLevel));
        whatsapp.Children.Add(BulletBlock("关注主题", content.WhatsAppAnalysis.FocusTopics));
        whatsapp.Children.Add(BulletBlock("需求与合作信号", content.WhatsAppAnalysis.PurchaseSignals, "SuccessSoft"));
        whatsapp.Children.Add(BulletBlock("顾虑", content.WhatsAppAnalysis.Concerns, "WarningSoft"));
        foreach (var quote in content.WhatsAppAnalysis.Quotes.Take(12)) whatsapp.Children.Add(QuoteCard(quote));
        ReportPanel.Children.Add(CreateSection("06", "WhatsApp 沟通分析", whatsapp));

        ReportPanel.Children.Add(CreateSection("07", "AI 商机判断",
            ScorePanel(content.OpportunityJudgment),
            TwoColumn("正向因素", content.OpportunityJudgment.PositiveFactors, "风险因素", content.OpportunityJudgment.NegativeFactors)));

        ReportPanel.Children.Add(CreateSection("08", "产品匹配分析",
            BulletBlock("高匹配点", content.ProductFit.HighMatchPoints, "SuccessSoft"),
            BulletBlock("低匹配点", content.ProductFit.LowMatchPoints, "WarningSoft"),
            BulletBlock("需要验证的问题", content.ProductFit.QuestionsToValidate, "AiSurface")));

        var strategy = new StackPanel();
        foreach (var action in content.SalesStrategy.Actions) strategy.Children.Add(ActionCard(action));
        strategy.Children.Add(Field("推荐话术", content.SalesStrategy.RecommendedTalkTrack, "销售建议"));
        strategy.Children.Add(BulletBlock("待解决问题", content.SalesStrategy.PendingQuestions));
        ReportPanel.Children.Add(CreateSection("09", "销售推进建议", strategy));

        ReportPanel.Children.Add(CreateSection("10", "风险分析",
            BulletBlock("成交风险", content.RiskAnalysis.DealRisks, "DangerSoft"),
            BulletBlock("使用风险", content.RiskAnalysis.AdoptionRisks, "WarningSoft"),
            BulletBlock("流失风险", content.RiskAnalysis.ChurnRisks, "DangerSoft")));

        var summary = new StackPanel();
        summary.Children.Add(Field("管理层摘要", content.ManagementSummary, "AI判断"));
        summary.Children.Add(EvidenceLedger(content.EvidenceLedger));
        ReportPanel.Children.Add(CreateSection("11", "AI 总结与证据账本", summary));
    }

    private Border CreateSection(string number, string title, params UIElement[] children)
    {
        var stack = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 13) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = new Border { Background = Brush("AiSurface"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 10, 0) };
        badge.Child = new TextBlock { Text = number, Foreground = Brush("AiAccent"), FontWeight = FontWeights.Bold, FontSize = 10 };
        var titleText = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(titleText, 1); header.Children.Add(badge); header.Children.Add(titleText); stack.Children.Add(header);
        foreach (var child in children) stack.Children.Add(child);
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 13), Child = stack };
    }

    private Border CreateStatusCard(CustomerAnalysisReport report)
    {
        var title = new TextBlock { Text = report.StatusLabel, FontSize = 18, FontWeight = FontWeights.Bold };
        var body = new TextBlock { Text = report.Error.Length > 0 ? report.Error : "报告正在执行多阶段 AI 分析，请稍候。", TextWrapping = TextWrapping.Wrap, Foreground = Brush("Muted"), Margin = new Thickness(0, 9, 0, 0) };
        return new Border { Style = (Style)FindResource("AiCard"), Padding = new Thickness(18), Child = new StackPanel { Children = { title, body } } };
    }

    private Border CreateGenerationNotice(string message) => new()
    {
        Background = Brush("WarningSoft"),
        BorderBrush = Brush("Warning"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(15),
        Margin = new Thickness(0, 0, 0, 13),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "当前资料版本说明", FontWeight = FontWeights.Bold, Foreground = Brush("Warning") },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("InkSecondary"), Margin = new Thickness(0, 6, 0, 0) }
            }
        }
    };

    private FrameworkElement Field(string label, string value, string nature = "事实")
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var labelText = new TextBlock { Text = label, Foreground = Brush("Muted"), FontSize = 10.5, Margin = new Thickness(0, 2, 10, 0) };
        var body = new StackPanel(); body.Children.Add(NatureChip(nature));
        body.Children.Add(new TextBlock { Text = ValueOrPlaceholder(value), TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 5, 0, 0), Foreground = Brush("InkSecondary") });
        Grid.SetColumn(body, 1); grid.Children.Add(labelText); grid.Children.Add(body); return grid;
    }

    private FrameworkElement BulletBlock(string label, IEnumerable<string> values, string? backgroundResource = null)
    {
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (list.Count == 0) list.Add("暂无充分信息，建议后续补充验证。");
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        foreach (var value in list) stack.Children.Add(new TextBlock { Text = $"• {value}", TextWrapping = TextWrapping.Wrap, Foreground = Brush("InkSecondary"), LineHeight = 18, Margin = new Thickness(0, 2, 0, 0) });
        return new Border { Background = backgroundResource is null ? Brushes.Transparent : Brush(backgroundResource), CornerRadius = new CornerRadius(10), Padding = backgroundResource is null ? new Thickness(0, 0, 0, 10) : new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Child = stack };
    }

    private FrameworkElement TwoColumn(string leftTitle, IEnumerable<string> left, string rightTitle, IEnumerable<string> right)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var leftBlock = BulletBlock(leftTitle, left, "SurfaceMuted"); var rightBlock = BulletBlock(rightTitle, right, "AiSurface");
        leftBlock.Margin = new Thickness(0, 0, 6, 0); rightBlock.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(rightBlock, 1); grid.Children.Add(leftBlock); grid.Children.Add(rightBlock); return grid;
    }

    private FrameworkElement QuoteCard(CustomerQuote quote)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "客户原话", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Brush("AiAccent") });
        stack.Children.Add(new TextBlock { Text = $"“{ValueOrPlaceholder(quote.Original)}”", TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic, FontSize = 12.5, Margin = new Thickness(0, 5, 0, 0) });
        stack.Children.Add(new TextBlock { Text = $"中文含义：{ValueOrPlaceholder(quote.ChineseMeaning)}", TextWrapping = TextWrapping.Wrap, Foreground = Brush("InkSecondary"), Margin = new Thickness(0, 7, 0, 0) });
        stack.Children.Add(new TextBlock { Text = $"AI 分析：{ValueOrPlaceholder(quote.AiAnalysis)}", TextWrapping = TextWrapping.Wrap, Foreground = Brush("Muted"), Margin = new Thickness(0, 4, 0, 0) });
        return new Border { Background = Brush("SurfaceMuted"), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(13), Margin = new Thickness(0, 4, 0, 8), Child = stack };
    }

    private FrameworkElement ScorePanel(CustomerOpportunityJudgment opportunity)
    {
        var stack = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) }; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var item in new[] { ("AI 评分", $"{opportunity.AiScore}/100", "AiAccent"), ("当前等级", $"{opportunity.Grade}级", "Ink"), ("成交概率", $"{opportunity.DealProbability}%", "Success") }.Select((value, index) => (value, index)))
        {
            var card = new Border { Background = Brush("SurfaceMuted"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Margin = new Thickness(item.index == 0 ? 0 : 5, 0, item.index == 2 ? 0 : 5, 0) };
            card.Child = new StackPanel { Children = { new TextBlock { Text = item.value.Item1, Foreground = Brush("Muted"), FontSize = 9.5 }, new TextBlock { Text = item.value.Item2, Foreground = Brush(item.value.Item3), FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0) } } };
            Grid.SetColumn(card, item.index); header.Children.Add(card);
        }
        stack.Children.Add(header);
        foreach (var factor in opportunity.DimensionScores)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 5) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(115) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            row.Children.Add(new TextBlock { Text = DimensionLabel(factor.Key), FontSize = 9.5, Foreground = Brush("InkSecondary") });
            var bar = new ProgressBar { Maximum = Math.Max(1, factor.MaxScore), Value = factor.Score, Height = 6, Foreground = Brush("AiAccent"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(bar, 1); row.Children.Add(bar);
            var score = new TextBlock { Text = $"{factor.Score}/{factor.MaxScore}", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 9.5 }; Grid.SetColumn(score, 2); row.Children.Add(score); stack.Children.Add(row);
        }
        return stack;
    }

    private FrameworkElement ActionCard(CustomerSalesAction action)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var time = new Border { Background = Brush("AiSurface"), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 5, 8, 5), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = ValueOrPlaceholder(action.Timeframe), Foreground = Brush("AiAccent"), FontWeight = FontWeights.SemiBold, FontSize = 9.5, TextAlignment = TextAlignment.Center } };
        var body = new StackPanel { Margin = new Thickness(11, 0, 0, 0) }; body.Children.Add(new TextBlock { Text = ValueOrPlaceholder(action.Action), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold }); body.Children.Add(new TextBlock { Text = $"依据：{ValueOrPlaceholder(action.Rationale)}", TextWrapping = TextWrapping.Wrap, Foreground = Brush("Muted"), FontSize = 9.5, Margin = new Thickness(0, 4, 0, 0) }); body.Children.Add(new TextBlock { Text = $"成功标准：{ValueOrPlaceholder(action.SuccessCriterion)}", TextWrapping = TextWrapping.Wrap, Foreground = Brush("Success"), FontSize = 9.5, Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetColumn(body, 1); grid.Children.Add(time); grid.Children.Add(body); return grid;
    }

    private FrameworkElement EvidenceLedger(IEnumerable<ReportStatement> statements)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 13, 0, 0) };
        stack.Children.Add(new TextBlock { Text = "证据账本", FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(0, 0, 0, 7) });
        var list = statements.Where(item => !string.IsNullOrWhiteSpace(item.Statement)).Take(40).ToList();
        if (list.Count == 0) stack.Children.Add(new TextBlock { Text = "暂无可核验事实。", Foreground = Brush("Muted") });
        foreach (var item in list)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var chip = NatureChip(item.Nature); var content = new StackPanel { Margin = new Thickness(9, 0, 0, 0) }; content.Children.Add(new TextBlock { Text = item.Statement, TextWrapping = TextWrapping.Wrap, Foreground = Brush("InkSecondary") }); content.Children.Add(new TextBlock { Text = $"来源：{ValueOrPlaceholder(item.Source)} · 证据：{ValueOrPlaceholder(item.Evidence)}", TextWrapping = TextWrapping.Wrap, Foreground = Brush("Muted"), FontSize = 9, Margin = new Thickness(0, 3, 0, 0) }); Grid.SetColumn(content, 1); grid.Children.Add(chip); grid.Children.Add(content); stack.Children.Add(grid);
        }
        return stack;
    }

    private Border NatureChip(string nature)
    {
        var key = nature.Contains("建议") ? "PrimarySoft" : nature.Contains("判断") ? "AiSurface" : "SuccessSoft";
        var ink = nature.Contains("建议") ? "Primary" : nature.Contains("判断") ? "AiAccent" : "Success";
        return new Border { Background = Brush(key), CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = string.IsNullOrWhiteSpace(nature) ? "事实" : nature, Foreground = Brush(ink), FontSize = 8.5, FontWeight = FontWeights.SemiBold } };
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLead is null || _generationCancellation is not null) return;
        if (!_services.AiProvider.HasApiKey()) { MessageBox.Show("请先在左侧“设置”中配置 API Key 并选择模型。", "无法生成报告", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _generationCancellation = new CancellationTokenSource();
        SetGenerationState(true);
        var progress = new Progress<CustomerAnalysisProgress>(state => { GenerationProgress.Maximum = state.Total; GenerationProgress.Value = Math.Max(0, state.Sequence - 1); ProgressText.Text = $"{state.Sequence}/{state.Total} · {state.Message}"; });
        try
        {
            var report = await _services.CustomerAnalysis.GenerateAsync(_currentLead.Id, progress, _generationCancellation.Token);
            GenerationProgress.Value = 5; ProgressText.Text = "报告已完成，正在刷新版本与决策摘要";
            await LoadHistoryAsync(_currentLead.Id, report.Id); DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { MessageBox.Show("报告生成已停止，当前版本已标记为可重试。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information); await LoadHistoryAsync(_currentLead.Id); }
        catch (Exception error) { MessageBox.Show($"本次 AI 分析未完成；客户资料已安全保留，可稍后重新生成新版本。\n\n失败原因：{error.Message}", "可重新分析", MessageBoxButton.OK, MessageBoxImage.Warning); await LoadHistoryAsync(_currentLead.Id); }
        finally { _generationCancellation.Dispose(); _generationCancellation = null; SetGenerationState(false); }
    }

    private void SetGenerationState(bool running)
    {
        ProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.IsEnabled = !running && _currentLead is not null; ExportWordButton.IsEnabled = !running && _currentReport?.Status == CustomerReportStatus.Succeeded; ExportPdfButton.IsEnabled = ExportWordButton.IsEnabled; CustomerList.IsEnabled = !running; HistoryList.IsEnabled = !running;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelButton.IsEnabled = false; ProgressText.Text = "正在安全停止当前 AI 请求…"; _generationCancellation?.Cancel(); }

    private async void ExportWord_Click(object sender, RoutedEventArgs e) => await ExportAsync("Word 文档 (*.docx)|*.docx", ".docx", true);
    private async void ExportPdf_Click(object sender, RoutedEventArgs e) => await ExportAsync("PDF 文档 (*.pdf)|*.pdf", ".pdf", false);

    private async Task ExportAsync(string filter, string extension, bool word)
    {
        if (_currentReport?.Status != CustomerReportStatus.Succeeded) return;
        var dialog = new SaveFileDialog { Filter = filter, DefaultExt = extension, AddExtension = true, FileName = $"{SafeFileName(_currentReport.CustomerName)}_客户背景调查报告_V{_currentReport.Version}" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (word) await _services.CustomerReportExports.ExportWordAsync(_currentReport, dialog.FileName); else await _services.CustomerReportExports.ExportPdfAsync(_currentReport, dialog.FileName);
            MessageBox.Show($"报告已导出：\n{dialog.FileName}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null) return;
        var previous = _history.Where(report => report.Version < _currentReport.Version && report.Status == CustomerReportStatus.Succeeded).OrderByDescending(report => report.Version).FirstOrDefault();
        if (previous is null) { MessageBox.Show("当前报告没有可比较的上一成功版本。", "版本对比", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        new CustomerReportComparisonWindow(previous, _currentReport)
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) ApplyCustomerFilter(); }
    private void ClearCustomer() { _currentLead = null; _history = []; HistoryList.ItemsSource = null; VersionCountText.Text = "0 份"; CustomerNameText.Text = "未找到客户"; CustomerMetaText.Text = "请调整搜索条件"; GenerateButton.IsEnabled = false; ShowReport(null); }
    private Brush Brush(string key) => (Brush)FindResource(key);
    private static string ValueOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string ValueOrPlaceholder(string value) => string.IsNullOrWhiteSpace(value) ? "暂无充分信息，建议后续补充验证。" : value;
    private static string SafeFileName(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    private static string DimensionLabel(string key) => key switch { "paid_marketing_willingness" => "增长投入意愿", "supply_stability" => "运营与交付稳定性", "ecommerce_foundation" => "相关业务基础", "private_traffic" => "客户触达能力", "existing_sales" => "商业验证程度", "materials_readiness" => "合作准备度", _ => key };
}
