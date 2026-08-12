using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Pages;

public partial class DraftsView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private List<Lead> _leads = [];
    private List<OutreachDraft> _drafts = [];
    private OutreachDraft? _current;
    public event EventHandler? DataChanged;

    public DraftsView(AppServices services)
    {
        InitializeComponent(); _services = services;
        PurposeCombo.ItemsSource = new[] { new PurposeOption("首次触达", "first_contact"), new PurposeOption("确认需求", "qualify_need"), new PurposeOption("分享报价", "share_quote"), new PurposeOption("跟进", "follow_up"), new PurposeOption("重新激活", "reactivate") }; PurposeCombo.SelectedIndex = 0;
        LanguageCombo.ItemsSource = new[] { "en", "zh-CN", "es", "ar", "fr", "de", "it" }; LanguageCombo.SelectedIndex = 0;
        UpdateButtons();
    }

    public async Task RefreshAsync()
    {
        var leadId = (LeadCombo.SelectedItem as Lead)?.Id;
        var draftId = _current?.Id;
        _leads = await _services.Repository.GetLeadsAsync(); _drafts = await _services.Repository.GetDraftsAsync();
        GenerateButton.Content = $"{await _services.AiProvider.GetSelectedModelAsync(AiModuleKeys.Campaigns)} 生成";
        LeadCombo.ItemsSource = _leads; LeadCombo.SelectedItem = _leads.FirstOrDefault(l => l.Id == leadId) ?? _leads.FirstOrDefault();
        DraftList.ItemsSource = _drafts; DraftCountText.Text = $"{_drafts.Count} 条";
        if (draftId is not null) DraftList.SelectedItem = _drafts.FirstOrDefault(d => d.Id == draftId);
        UpdateContext(); UpdateButtons();
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (LeadCombo.SelectedItem is not Lead lead) { MessageBox.Show("请先选择客户。", "AI Sales OS"); return; }
        GenerateButton.IsEnabled = false; GenerateButton.Content = "生成中…";
        try
        {
            var purpose = (PurposeCombo.SelectedItem as PurposeOption)?.Value ?? "first_contact";
            var language = LanguageCombo.Text.Trim();
            _current = await _services.AiProvider.GenerateDraftAsync(lead, purpose, language, ExtraInstructionsBox.Text);
            Editor.Text = _current.Body; await RefreshAsync(); DraftList.SelectedItem = _drafts.FirstOrDefault(d => d.Id == _current.Id);
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "话术生成失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { GenerateButton.IsEnabled = true; GenerateButton.Content = $"{await _services.AiProvider.GetSelectedModelAsync(AiModuleKeys.Campaigns)} 生成"; UpdateButtons(); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) { MessageBox.Show("请先生成或选择一条草稿。", "AI Sales OS"); return; }
        if (string.IsNullOrWhiteSpace(Editor.Text)) { MessageBox.Show("话术正文不能为空。", "AI Sales OS"); return; }
        if (Editor.Text.Length > 4096) { MessageBox.Show("话术正文不能超过 4096 个字符。", "AI Sales OS"); return; }
        _current.Body = Editor.Text.Trim();
        if (_current.Status == DraftStatus.Approved) { _current.Status = DraftStatus.Draft; _current.ApprovedAt = null; _current.ApprovedBy = ""; }
        await _services.Repository.SaveDraftAsync(_current, "edited"); await _services.Repository.LogEventAsync("draft_edited", _current.LeadId, _current.Id);
        await RefreshAsync(); MessageBox.Show("编辑版本已保存；如曾确认，现已恢复为待确认状态。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) { MessageBox.Show("请先生成或选择一条草稿。", "AI Sales OS"); return; }
        if (string.IsNullOrWhiteSpace(Editor.Text) || Editor.Text.Length > 4096) { MessageBox.Show("请先检查话术正文。", "AI Sales OS"); return; }
        _current.Body = Editor.Text.Trim(); _current.Status = DraftStatus.Approved; _current.ApprovedAt = DateTimeOffset.Now; _current.ApprovedBy = Environment.UserName;
        await _services.Repository.SaveDraftAsync(_current, "approved", Environment.UserName); await _services.Repository.LogEventAsync("draft_approved", _current.LeadId, _current.Id, "人工确认，不代表已发送");
        await RefreshAsync(); DataChanged?.Invoke(this, EventArgs.Empty);
        MessageBox.Show("已记录人工确认。系统没有发送消息。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseApprovedDraft(out _)) return;
        Clipboard.SetText(_current!.Body); await _services.Repository.LogEventAsync("draft_copied", _current.LeadId, _current.Id);
        MessageBox.Show("话术已复制到剪贴板。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseApprovedDraft(out var lead)) return;
        if (lead!.OptedOut) { MessageBox.Show("客户已退订，禁止打开 WhatsApp。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!lead.PhoneValid) { MessageBox.Show("客户号码无效，禁止打开 WhatsApp。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var url = PhoneNormalizer.BuildWaMeUrl(lead.PhoneE164, _current!.Body);
        await _services.Repository.LogEventAsync("whatsapp_opened", lead.Id, _current.Id, url.Split('?')[0]);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private bool CanUseApprovedDraft(out Lead? lead)
    {
        lead = null;
        if (_current is null || _current.Status != DraftStatus.Approved) { MessageBox.Show("话术必须先完成人工确认。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (Editor.Text.Trim() != _current.Body) { MessageBox.Show("正文有未保存修改，请保存并重新确认。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        lead = _leads.FirstOrDefault(x => x.Id == _current.LeadId);
        if (lead is null) { MessageBox.Show("客户不存在。", "AI Sales OS"); return false; }
        return true;
    }

    private void DraftList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DraftList.SelectedItem is not OutreachDraft draft) return;
        _current = draft; Editor.Text = draft.Body;
        LeadCombo.SelectedItem = _leads.FirstOrDefault(x => x.Id == draft.LeadId);
        PurposeCombo.SelectedItem = PurposeCombo.Items.Cast<PurposeOption>().FirstOrDefault(x => x.Value == draft.Purpose);
        LanguageCombo.Text = draft.Language; UpdateContext(); UpdateButtons();
    }

    private void LeadCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateContext();
    private void Editor_TextChanged(object sender, TextChangedEventArgs e) { CharCountText.Text = $"{Editor.Text.Length} / 4096"; UpdateButtons(); }

    private void UpdateContext()
    {
        var lead = LeadCombo.SelectedItem as Lead;
        ContextTitle.Text = lead is null ? "选择客户并生成话术" : lead.DisplayName;
        ContextSubtitle.Text = lead is null ? "人工确认前不能复制或跳转" : $"{lead.Company} · {lead.Country} · {lead.PhoneE164} ({lead.PhoneState})";
        StatusText.Text = _current?.StatusLabel ?? "未生成";
        StatusBadge.Background = (System.Windows.Media.Brush)FindResource(_current?.Status == DraftStatus.Approved ? "SuccessSoft" : "WarningSoft");
    }

    private void UpdateButtons()
    {
        var hasDraft = _current is not null; SaveButton.IsEnabled = hasDraft; ApproveButton.IsEnabled = hasDraft;
        var approved = _current?.Status == DraftStatus.Approved && Editor.Text.Trim() == _current.Body;
        CopyButton.IsEnabled = approved; OpenButton.IsEnabled = approved;
    }

    private sealed record PurposeOption(string Label, string Value);
}
