using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Imports;
using WAFlow.Core.Services;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop.Pages;

public partial class CampaignsView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private readonly ObservableCollection<WhatsAppCampaign> _campaigns = [];
    private readonly ObservableCollection<AudienceRow> _rows = [];
    private readonly ObservableCollection<CampaignAccountChoice> _accountChoices = [];
    private readonly List<WhatsAppAccount> _whatsAppAccounts = [];
    private readonly List<EmailAccount> _emailAccounts = [];
    private readonly ObservableCollection<CampaignMessageTemplate> _templates = [];
    private readonly ObservableCollection<CampaignTemplateField> _fields = [];
    private readonly ObservableCollection<CampaignExecutionSummary> _history = [];
    private WhatsAppCampaign? _current;
    private CampaignMessageTemplate? _currentTemplate;
    private bool _loading;

    public event EventHandler? DataChanged;

    public CampaignsView(AppServices services)
    {
        InitializeComponent();
        _services = services;
        CampaignList.ItemsSource = _campaigns;
        AudienceGrid.ItemsSource = _rows;
        AccountBox.ItemsSource = _accountChoices;
        SavedTemplateBox.ItemsSource = _templates;
        FieldBox.ItemsSource = _fields;
        HistoryGrid.ItemsSource = _history;
        ScheduleModeBox.ItemsSource = new[]
        {
            new Choice<CampaignScheduleMode>("即时任务 · 批准后立即开始", CampaignScheduleMode.Immediate),
            new Choice<CampaignScheduleMode>("定时任务 · 北京时间", CampaignScheduleMode.Scheduled)
        };
        IntervalUnitBox.ItemsSource = new[]
        {
            new Choice<CampaignIntervalUnit>("秒 / 条", CampaignIntervalUnit.Seconds),
            new Choice<CampaignIntervalUnit>("分钟 / 条", CampaignIntervalUnit.Minutes)
        };
        ChannelBox.ItemsSource = new[]
        {
            new Choice<CampaignChannel>("WhatsApp 自动触达", CampaignChannel.WhatsApp),
            new Choice<CampaignChannel>("邮件自动触达", CampaignChannel.Email)
        };
        CustomerGradeFilterBox.ItemsSource = new[] { "全部等级", "A", "B", "C", "D" };
        CustomerStageFilterBox.ItemsSource = new[] { new StageChoice("全部阶段", null) }.Concat(Enum.GetValues<LeadStage>().Select(value => new StageChoice(Labels.Stage(value), value))).ToList();
        CustomerCategoryPreferenceFilterBox.ItemsSource = new[] { new CategoryPreferenceChoice("全部一级品类", null) };
        CustomerCategoryPreferenceFilterBox.SelectedIndex = 0;
        _services.Campaigns.CampaignChanged += (_, _) => Dispatcher.InvokeAsync(async () => await RefreshAsync());
        _services.WhatsApp.EventReceived += (_, e) => { if (e.Name == "connection") Dispatcher.InvokeAsync(UpdateConnectionState); };
        ResetFormCore();
    }

    public async Task RefreshAsync()
    {
        var selectedCampaignId = _current?.Id;
        var selectedTemplateId = _currentTemplate?.Id;
        _loading = true;
        try
        {
            var accounts = await _services.Repository.GetWhatsAppAccountsAsync();
            _whatsAppAccounts.Clear(); _whatsAppAccounts.AddRange(accounts);
            var emailAccounts = await _services.Repository.GetEmailAccountsAsync();
            _emailAccounts.Clear(); _emailAccounts.AddRange(emailAccounts);

            var templates = await _services.Repository.GetCampaignMessageTemplatesAsync();
            _templates.Clear(); foreach (var template in templates.OrderByDescending(item => item.UpdatedAt)) _templates.Add(template);
            _currentTemplate = _templates.FirstOrDefault(item => item.Id == selectedTemplateId);
            SavedTemplateBox.SelectedItem = _currentTemplate;

            var fields = await _services.Campaigns.GetTemplateFieldsAsync();
            _fields.Clear(); foreach (var field in fields) _fields.Add(field);
            if (FieldBox.SelectedItem is null) FieldBox.SelectedItem = _fields.FirstOrDefault();

            var loaded = await _services.Repository.GetCampaignsAsync(null);
            _campaigns.Clear(); foreach (var campaign in loaded) _campaigns.Add(campaign);
            CampaignCountText.Text = loaded.Count.ToString(CultureInfo.InvariantCulture);
            ActiveCountText.Text = loaded.Count(item => item.Status is CampaignStatus.Scheduled or CampaignStatus.Running or CampaignStatus.SafetyStopped).ToString(CultureInfo.InvariantCulture);
            var sent = 0;
            foreach (var campaign in loaded) sent += (await _services.Repository.GetCampaignRecipientsAsync(campaign.Id)).Count(item => item.Status == CampaignRecipientStatus.Sent);
            SentCountText.Text = sent.ToString(CultureInfo.InvariantCulture);
            var history = await _services.Campaigns.GetExecutionHistoryAsync();
            _history.Clear(); foreach (var item in history) _history.Add(item);
            UpdateConnectionState();

            if (selectedCampaignId is not null && loaded.FirstOrDefault(item => item.Id == selectedCampaignId) is { } selected)
            {
                _current = selected;
                CampaignList.SelectedItem = selected;
                await LoadCampaignAsync(selected);
            }
            else if (_current is null)
            {
                ResetFormCore();
                await LoadAudienceRowsAsync(_current!);
            }
            else
            {
                ApplyChannelSelection(_current.Channel, _current.AccountId);
                await LoadAudienceRowsAsync(_current);
            }
        }
        finally { _loading = false; }
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        ResetFormCore();
        await LoadAudienceRowsAsync(_current!);
    }

    private async void CampaignList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CampaignList.SelectedItem is not WhatsAppCampaign campaign) return;
        await LoadCampaignAsync(campaign);
    }

    private async Task LoadCampaignAsync(WhatsAppCampaign campaign)
    {
        _loading = true;
        try
        {
            _current = campaign;
            ChannelBox.SelectedItem = ChannelBox.Items.Cast<Choice<CampaignChannel>>().First(item => item.Value == campaign.Channel);
            ApplyChannelSelection(campaign.Channel, campaign.AccountId);
            NameBox.Text = campaign.Name;
            TemplateBox.Text = campaign.MessageTemplate;
            EmailSubjectBox.Text = campaign.EmailSubjectTemplate;
            _currentTemplate = _templates.FirstOrDefault(item => item.Id == campaign.TemplateId);
            SavedTemplateBox.SelectedItem = _currentTemplate;
            TemplateNameBox.Text = _currentTemplate?.Name ?? "";
            ScheduleModeBox.SelectedItem = ScheduleModeBox.Items.Cast<Choice<CampaignScheduleMode>>().First(item => item.Value == campaign.ScheduleMode);
            StartAtBox.Text = FormatBeijing(campaign.StartsAt);
            IntervalBox.Text = campaign.EffectiveIntervalValue.ToString(CultureInfo.InvariantCulture);
            IntervalUnitBox.SelectedItem = IntervalUnitBox.Items.Cast<Choice<CampaignIntervalUnit>>().First(item => item.Value == campaign.IntervalUnit);
            DailyLimitBox.Text = campaign.DailyLimit.ToString(CultureInfo.InvariantCulture);
            await LoadAudienceRowsAsync(campaign);
            ApplyState(campaign);
        }
        finally { _loading = false; }
    }

    private async Task LoadAudienceRowsAsync(WhatsAppCampaign campaign)
    {
        var items = await _services.Campaigns.ListAudienceAsync(campaign);
        var recipients = await _services.Repository.GetCampaignRecipientsAsync(campaign.Id);
        var recipientByLead = recipients.ToDictionary(item => item.LeadId, StringComparer.OrdinalIgnoreCase);
        var selected = campaign.SelectedLeadIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 && recipients.Count > 0) selected = recipients.Select(item => item.LeadId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows.Clear();
        foreach (var item in items)
        {
            recipientByLead.TryGetValue(item.Lead.Id, out var recipient);
            _rows.Add(new AudienceRow(item.Lead, campaign.Channel)
            {
                Eligible = item.Eligible,
                IsSelected = item.Eligible && selected.Contains(item.Lead.Id),
                StatusText = recipient?.StatusLabel ?? item.EligibilityLabel,
                Detail = recipient is null ? item.Reason : string.IsNullOrWhiteSpace(recipient.LastError) ? recipient.ScheduledLabel : recipient.LastError
            });
        }
        UpdateCategoryPreferenceFilter();
        ApplyAudienceFilter();
        UpdateSelectionSummary();
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var campaign = ReadForm();
            var audience = await _services.Campaigns.PreviewAudienceAsync(campaign);
            if (audience.Count == 0) throw new InvalidOperationException("请至少勾选 1 位客户。");
            var resultByLead = audience.ToDictionary(item => item.Lead.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows.Where(row => row.IsSelected))
            {
                if (!resultByLead.TryGetValue(row.Lead.Id, out var result)) continue;
                row.StatusText = result.EligibilityLabel;
                row.Detail = result.Reason;
            }
            var eligible = audience.Count(item => item.Eligible);
            PreviewSummaryText.Text = $"已选 {audience.Count} 人 · 可发送 {eligible} · 排除 {audience.Count - eligible}";
            new CampaignPreviewWindow(campaign, audience)
            {
                Owner = Window.GetWindow(this)
            }.ShowDialog();
        }
        catch (Exception error) { ShowError(error, "预览失败"); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var campaign = ReadForm();
            await _services.Campaigns.SaveDraftAsync(campaign);
            _current = campaign;
            await RefreshAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("发送任务草稿已保存。", "多渠道自动化触达", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { ShowError(error, "保存失败"); }
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var campaign = ReadForm();
            var audience = await _services.Campaigns.PreviewAudienceAsync(campaign);
            var eligible = audience.Count(item => item.Eligible);
            if (eligible == 0) throw new InvalidOperationException("所选客户中没有符合发送条件的客户。");
            var mode = campaign.ScheduleMode == CampaignScheduleMode.Immediate ? "批准后立即进入队列" : $"{FormatBeijing(campaign.StartsAt)}（北京时间）开始";
            var unit = campaign.IntervalUnit == CampaignIntervalUnit.Seconds ? "秒" : "分钟";
            var channelWarning = campaign.Channel == CampaignChannel.WhatsApp
                ? "发送间隔不能规避 WhatsApp 风控；个人号非官方连接仍可能被限制或封号。"
                : "邮件成功表示 SMTP 服务器已接受；退信或后续投递结果可能由收件服务器另行返回。";
            var confirmation = $"将通过 {campaign.ChannelLabel} 为 {eligible} 位客户生成独立消息并自动逐条发送。\n\n触发：{mode}\n间隔：每 {campaign.EffectiveIntervalValue} {unit} 1 条\n每日上限：{campaign.DailyLimit}\n\n{channelWarning}\n确认建立任务吗？";
            if (MessageBox.Show(confirmation, "批准自动发送", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var count = await _services.Campaigns.ApproveAndScheduleAsync(campaign);
            _current = campaign;
            await RefreshAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show($"已建立 {count} 条逐人发送任务。请保持 AI Sales OS 运行，并确保对应{campaign.ChannelLabel}账号已连接。", "发送任务已建立", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { ShowError(error, "建立任务失败"); }
    }

    private async void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        try
        {
            if (_current.Status is CampaignStatus.Paused or CampaignStatus.SafetyStopped) await _services.Campaigns.ResumeAsync(_current);
            else await _services.Campaigns.PauseAsync(_current);
            await RefreshAsync();
        }
        catch (Exception error) { ShowError(error, "操作失败"); }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || MessageBox.Show("取消后，尚未发送的客户将不会再发送。是否继续？", "取消发送任务", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _services.Campaigns.CancelAsync(_current); await RefreshAsync(); }
        catch (Exception error) { ShowError(error, "取消失败"); }
    }

    private async void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var template = _currentTemplate ?? new CampaignMessageTemplate();
            template.Name = TemplateNameBox.Text;
            template.Body = TemplateBox.Text;
            _currentTemplate = await _services.Campaigns.SaveMessageTemplateAsync(template);
            if (_current is not null) _current.TemplateId = _currentTemplate.Id;
            await RefreshTemplatesAsync(_currentTemplate.Id);
            MessageBox.Show("话术模板已保存。", "多渠道自动化触达", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { ShowError(error, "模板保存失败"); }
    }

    private void NewTemplate_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        _currentTemplate = null;
        SavedTemplateBox.SelectedItem = null;
        TemplateNameBox.Clear();
        TemplateBox.Clear();
        _loading = false;
        TemplateNameBox.Focus();
    }

    private async void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTemplate is null) return;
        if (MessageBox.Show($"删除话术模板“{_currentTemplate.Name}”吗？已建立任务中的发送快照不会改变。", "删除模板", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _services.Campaigns.DeleteMessageTemplateAsync(_currentTemplate);
        _currentTemplate = null;
        TemplateNameBox.Clear(); TemplateBox.Clear();
        await RefreshTemplatesAsync();
    }

    private void SavedTemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SavedTemplateBox.SelectedItem is not CampaignMessageTemplate template) return;
        _currentTemplate = template;
        TemplateNameBox.Text = template.Name;
        TemplateBox.Text = template.Body;
        if (_current is not null) _current.TemplateId = template.Id;
    }

    private void InsertField_Click(object sender, RoutedEventArgs e)
    {
        if (FieldBox.SelectedItem is not CampaignTemplateField field) return;
        var start = TemplateBox.SelectionStart;
        TemplateBox.Text = TemplateBox.Text.Insert(start, field.Token);
        TemplateBox.SelectionStart = start + field.Token.Length;
        TemplateBox.Focus();
    }

    private void InsertSubjectField_Click(object sender, RoutedEventArgs e)
    {
        if (FieldBox.SelectedItem is not CampaignTemplateField field) return;
        var start = EmailSubjectBox.SelectionStart;
        EmailSubjectBox.Text = EmailSubjectBox.Text.Insert(start, field.Token);
        EmailSubjectBox.SelectionStart = start + field.Token.Length;
        EmailSubjectBox.Focus();
    }

    private async Task RefreshTemplatesAsync(string? selectId = null)
    {
        _loading = true;
        try
        {
            var templates = await _services.Repository.GetCampaignMessageTemplatesAsync();
            _templates.Clear(); foreach (var template in templates.OrderByDescending(item => item.UpdatedAt)) _templates.Add(template);
            _currentTemplate = _templates.FirstOrDefault(item => item.Id == selectId);
            SavedTemplateBox.SelectedItem = _currentTemplate;
        }
        finally { _loading = false; }
    }

    private WhatsAppCampaign ReadForm()
    {
        var campaign = _current is { Status: CampaignStatus.Draft } ? _current : new WhatsAppCampaign();
        campaign.Channel = (ChannelBox.SelectedItem as Choice<CampaignChannel>)?.Value ?? CampaignChannel.WhatsApp;
        campaign.AccountId = (AccountBox.SelectedItem as CampaignAccountChoice)?.Id ?? throw new InvalidOperationException(campaign.Channel == CampaignChannel.Email ? "请先连接并选择邮件账号。" : "请选择 WhatsApp 账号。");
        campaign.Name = NameBox.Text.Trim();
        campaign.TemplateId = _currentTemplate?.Id ?? "";
        campaign.MessageTemplate = TemplateBox.Text.Trim();
        campaign.EmailSubjectTemplate = EmailSubjectBox.Text.Trim();
        campaign.SelectedLeadIds = _rows.Where(row => row.IsSelected).Select(row => row.Lead.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        campaign.ScheduleMode = (ScheduleModeBox.SelectedItem as Choice<CampaignScheduleMode>)?.Value ?? CampaignScheduleMode.Scheduled;
        campaign.StartsAt = campaign.ScheduleMode == CampaignScheduleMode.Scheduled ? ParseBeijing(StartAtBox.Text) : DateTimeOffset.Now;
        if (!int.TryParse(IntervalBox.Text, out var interval)) throw new InvalidOperationException("发送间隔必须是整数。");
        campaign.IntervalValue = interval;
        campaign.IntervalUnit = (IntervalUnitBox.SelectedItem as Choice<CampaignIntervalUnit>)?.Value ?? CampaignIntervalUnit.Minutes;
        campaign.IntervalMinutes = campaign.IntervalUnit == CampaignIntervalUnit.Minutes ? interval : Math.Max(1, (int)Math.Ceiling(interval / 60d));
        if (!int.TryParse(DailyLimitBox.Text, out var limit)) throw new InvalidOperationException("每日上限必须是整数。");
        campaign.DailyLimit = limit;
        return campaign;
    }

    private void ResetFormCore()
    {
        _loading = true;
        CampaignList.SelectedItem = null;
        _current = new WhatsAppCampaign { Name = $"群发任务 {DateTime.Now:MM-dd}", IntervalValue = 5 };
        _currentTemplate = null;
        SavedTemplateBox.SelectedItem = null;
        TemplateNameBox.Clear();
        TemplateBox.Text = _current.MessageTemplate;
        EmailSubjectBox.Text = _current.EmailSubjectTemplate;
        ChannelBox.SelectedItem = ChannelBox.Items.Cast<Choice<CampaignChannel>>().First(item => item.Value == CampaignChannel.WhatsApp);
        ApplyChannelSelection(CampaignChannel.WhatsApp, _services.WhatsApp.ActiveAccountId);
        NameBox.Text = _current.Name;
        ScheduleModeBox.SelectedItem = ScheduleModeBox.Items.Cast<Choice<CampaignScheduleMode>>().First(item => item.Value == CampaignScheduleMode.Scheduled);
        StartAtBox.Text = FormatBeijing(_current.StartsAt);
        IntervalBox.Text = "5";
        IntervalUnitBox.SelectedItem = IntervalUnitBox.Items.Cast<Choice<CampaignIntervalUnit>>().First(item => item.Value == CampaignIntervalUnit.Minutes);
        DailyLimitBox.Text = "50";
        CustomerGradeFilterBox.SelectedIndex = 0;
        CustomerStageFilterBox.SelectedIndex = 0;
        CustomerCategoryPreferenceFilterBox.SelectedIndex = 0;
        AudienceSearchBox.Clear();
        _rows.Clear();
        PreviewSummaryText.Text = "请勾选一位或多位客户";
        EligibleCountText.Text = "已选 0";
        PreviewButton.Content = "预览个性化话术";
        System.Windows.Automation.AutomationProperties.SetName(PreviewButton, "预览所有已选客户的个性化话术");
        CampaignStateText.Text = "新草稿";
        PauseReasonText.Text = "";
        _loading = false;
        ApplyState(_current);
        UpdateScheduleMode();
    }

    private void ApplyState(WhatsAppCampaign campaign)
    {
        var editable = campaign.Status == CampaignStatus.Draft;
        foreach (var control in new Control[] { ChannelBox, AccountBox, NameBox, SavedTemplateBox, TemplateNameBox, TemplateBox, EmailSubjectBox, FieldBox, SaveTemplateButton, DeleteTemplateButton, ScheduleModeBox, StartAtBox, IntervalBox, IntervalUnitBox, DailyLimitBox }) control.IsEnabled = editable;
        AudienceGrid.IsEnabled = editable;
        PreviewButton.IsEnabled = editable;
        SaveButton.IsEnabled = editable;
        ApproveButton.IsEnabled = editable;
        PauseResumeButton.IsEnabled = campaign.Status is CampaignStatus.Scheduled or CampaignStatus.Running or CampaignStatus.Paused or CampaignStatus.SafetyStopped;
        PauseResumeButton.Content = campaign.Status is CampaignStatus.Paused or CampaignStatus.SafetyStopped ? "确认网络并继续" : "暂停";
        CancelButton.IsEnabled = campaign.Status is CampaignStatus.Scheduled or CampaignStatus.Running or CampaignStatus.Paused or CampaignStatus.SafetyStopped;
        CampaignStateText.Text = campaign.StatusLabel + (campaign.ApprovedAt is null ? "" : $" · {campaign.ApprovedBy} 已批准");
        PauseReasonText.Text = campaign.PauseReason;
        EditHintText.Text = editable ? "保存模板，勾选客户并预览，再批准建立自动发送任务。" : "发送话术和客户快照已锁定；暂停不会改变已生成的队列。";
        UpdateScheduleMode();
    }

    private void ScheduleModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateScheduleMode();

    private async void ChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ChannelBox.SelectedItem is not Choice<CampaignChannel> choice) return;
        if (_current is not null) _current.Channel = choice.Value;
        ApplyChannelSelection(choice.Value, null);
        if (_current is not null) await LoadAudienceRowsAsync(_current);
        UpdateConnectionState();
    }

    private void ApplyChannelSelection(CampaignChannel channel, string? preferredAccountId)
    {
        var previousLoading = _loading;
        _loading = true;
        _accountChoices.Clear();
        if (channel == CampaignChannel.Email)
        {
            foreach (var account in _emailAccounts)
                _accountChoices.Add(new CampaignAccountChoice(account.Id, account.DisplayLabel, channel, account.Status == EmailConnectionStatus.Connected));
        }
        else
        {
            foreach (var account in _whatsAppAccounts)
                _accountChoices.Add(new CampaignAccountChoice(account.Id, account.DisplayLabel, channel, _services.WhatsApp.IsConnectedFor(account.Id)));
        }
        AccountBox.SelectedItem = _accountChoices.FirstOrDefault(item => item.Id == preferredAccountId) ?? _accountChoices.FirstOrDefault();
        EmailSubjectPanel.Visibility = channel == CampaignChannel.Email ? Visibility.Visible : Visibility.Collapsed;
        SafetyHintText.Text = channel == CampaignChannel.Email
            ? "邮件任务以 SMTP 服务器接受结果计为成功；退信和后续投递结果可能由收件服务器另行返回。失败原因会与 WhatsApp 历史分开统计。"
            : "WhatsApp 任务会记录公网 IP 并持续核对；IP 变化立即停止所有自动触达。号码查询只作诊断，不再因本地联系人同步不完整而拒绝发送。";
        _loading = previousLoading;
        UpdateConnectionState();
    }

    private void UpdateScheduleMode()
    {
        if (StartAtPanel is null) return;
        var scheduled = (ScheduleModeBox.SelectedItem as Choice<CampaignScheduleMode>)?.Value != CampaignScheduleMode.Immediate;
        StartAtPanel.Opacity = scheduled ? 1 : .45;
        StartAtBox.IsEnabled = scheduled && (_current?.Status ?? CampaignStatus.Draft) == CampaignStatus.Draft;
    }

    private void AudienceFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) ApplyAudienceFilter();
    }

    private void ApplyAudienceFilter()
    {
        var query = AudienceSearchBox?.Text.Trim() ?? "";
        var grade = CustomerGradeFilterBox?.SelectedItem as string ?? "全部等级";
        var stage = (CustomerStageFilterBox?.SelectedItem as StageChoice)?.Value;
        var category = (CustomerCategoryPreferenceFilterBox?.SelectedItem as CategoryPreferenceChoice)?.Value;
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.Filter = item => item is AudienceRow row
            && (grade == "全部等级" || row.Grade.Equals(grade, StringComparison.OrdinalIgnoreCase))
            && (stage is null || row.Lead.Stage == stage)
            && (string.IsNullOrWhiteSpace(category) || row.PrimaryCategoryPreference.Equals(category, StringComparison.CurrentCultureIgnoreCase))
            && (query.Length == 0 || string.Join(" ", row.DisplayName, row.Company, row.Phone, row.Email, row.PrimaryCategoryPreference, row.Lead.TagsLabel).Contains(query, StringComparison.CurrentCultureIgnoreCase));
        PreviewSummaryText.Text = $"显示 {view.Cast<object>().Count()} / {_rows.Count} 位客户";
    }

    private void UpdateCategoryPreferenceFilter()
    {
        var selected = (CustomerCategoryPreferenceFilterBox.SelectedItem as CategoryPreferenceChoice)?.Value;
        var values = _rows
            .Select(row => row.PrimaryCategoryPreference)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var options = new[] { new CategoryPreferenceChoice("全部一级品类", null) }
            .Concat(values.Select(value => new CategoryPreferenceChoice(value, value)))
            .ToList();
        CustomerCategoryPreferenceFilterBox.ItemsSource = options;
        CustomerCategoryPreferenceFilterBox.SelectedItem = string.IsNullOrWhiteSpace(selected)
            ? options[0]
            : options.FirstOrDefault(option => option.Value?.Equals(selected, StringComparison.CurrentCultureIgnoreCase) == true) ?? options[0];
    }

    private void SelectAllEligible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in CollectionViewSource.GetDefaultView(_rows).Cast<AudienceRow>().Where(row => row.Eligible)) row.IsSelected = true;
        UpdateSelectionSummary();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = false;
        UpdateSelectionSummary();
    }

    private void AudienceCheck_Click(object sender, RoutedEventArgs e) => UpdateSelectionSummary();

    private async void HistoryGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not CampaignExecutionSummary summary) return;
        if (_campaigns.FirstOrDefault(item => item.Id == summary.Campaign.Id) is not { } campaign) return;
        CampaignTabs.SelectedIndex = 0;
        CampaignList.SelectedItem = campaign;
        await LoadCampaignAsync(campaign);
    }

    private void UpdateSelectionSummary()
    {
        var selected = _rows.Count(row => row.IsSelected);
        var eligible = _rows.Count(row => row.IsSelected && row.Eligible);
        EligibleCountText.Text = selected == eligible ? $"已选 {selected}" : $"已选 {selected} · 可发 {eligible}";
        PreviewButton.Content = selected == 0 ? "预览个性化话术" : $"预览个性化话术（{selected}）";
        System.Windows.Automation.AutomationProperties.SetName(
            PreviewButton,
            selected == 0 ? "预览所有已选客户的个性化话术" : $"预览 {selected} 位已选客户的个性化话术");
    }

    private void UpdateConnectionState()
    {
        var choice = AccountBox.SelectedItem as CampaignAccountChoice;
        var channel = (ChannelBox.SelectedItem as Choice<CampaignChannel>)?.Value ?? CampaignChannel.WhatsApp;
        var accountId = choice?.Id ?? "primary";
        var connected = channel == CampaignChannel.Email ? choice?.Connected == true : _services.WhatsApp.IsConnectedFor(accountId);
        var state = channel == CampaignChannel.Email ? "email" : _services.WhatsApp.ConnectionStateFor(accountId);
        ConnectionText.Text = connected ? $"{(channel == CampaignChannel.Email ? "邮件" : "WhatsApp")}已连接" : state switch { "connecting" => "连接中 / 等待扫码", "logged_out" => "登录已失效", _ => channel == CampaignChannel.Email ? "未连接邮件账号" : "未连接" };
        ConnectionText.Foreground = (System.Windows.Media.Brush)FindResource(connected ? "Success" : "Warning");
    }

    private static DateTimeOffset ParseBeijing(string value)
    {
        if (!DateTime.TryParse(value.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var local) && !DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out local))
            throw new InvalidOperationException("开始时间格式无效，请填写例如 2026-07-20 18:30。");
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var zone = BeijingZone();
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static string FormatBeijing(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, BeijingZone()).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static TimeZoneInfo BeijingZone()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" }) try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Local;
    }

    private static void ShowError(Exception error, string title) => MessageBox.Show(error.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private sealed record StageChoice(string Label, LeadStage? Value);
    private sealed record CategoryPreferenceChoice(string Label, string? Value);
    private sealed record Choice<T>(string Label, T Value);
    private sealed record CampaignAccountChoice(string Id, string DisplayLabel, CampaignChannel Channel, bool Connected);

    private sealed class AudienceRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _statusText = "";
        private string _detail = "";

        public AudienceRow(Lead lead, CampaignChannel channel) { Lead = lead; Channel = channel; }
        public Lead Lead { get; }
        public CampaignChannel Channel { get; }
        public string DisplayName => Lead.DisplayName;
        public string Company => Lead.Company;
        public string Phone => Lead.PhoneE164;
        public string Email => Lead.Email;
        public string Contact => Channel == CampaignChannel.Email ? Lead.Email : Lead.PhoneE164;
        public string Grade => Lead.Grade;
        public string PrimaryCategoryPreference => CustomerDimensionCatalog.ResolvePrimaryCategoryPreference(Lead);
        public bool Eligible { get; init; }
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(nameof(StatusText)); } }
        public string Detail { get => _detail; set { _detail = value; OnPropertyChanged(nameof(Detail)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
