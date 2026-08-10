using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Windows;

public partial class AiConversationAssistantWindow : Window
{
    private readonly ObservableCollection<FieldProposalItem> _proposals;

    public ConversationAssistantAction Action { get; private set; } = ConversationAssistantAction.Cancel;
    public string ReplyText => ReplyBox.Text.Trim();
    public IReadOnlyList<ConversationFieldUpdate> SelectedUpdates => _proposals.Where(item => item.IsSelected).Select(item => item.Update).ToList();

    public AiConversationAssistantWindow(ConversationAssistantResult result, bool canSend)
    {
        InitializeComponent();
        ReplyBox.Text = result.ReplyText;
        ModelText.Text = string.IsNullOrWhiteSpace(result.Model) ? "AI 模型" : result.Model;
        ConfidenceText.Text = $"提取置信度 {result.Confidence:P0}";
        LanguageText.Text = string.IsNullOrWhiteSpace(result.ReplyLanguage) ? "自动识别语言" : result.ReplyLanguage;
        NeedsSummaryText.Text = result.NeedsSummary;
        IntentText.Text = result.CustomerIntent;
        SignalsText.Text = result.PurchaseSignals.Count == 0 ? "尚未识别到明确需求或合作信号" : string.Join("\n", result.PurchaseSignals.Select(value => $"• {value}"));
        RisksText.Text = result.Risks.Count == 0 ? "尚未识别到明确风险" : string.Join("\n", result.Risks.Select(value => $"• {value}"));
        NextActionText.Text = result.RecommendedNextAction;
        _proposals = new ObservableCollection<FieldProposalItem>(result.FieldUpdates.Select(update => new FieldProposalItem(update)));
        ProposalGrid.ItemsSource = _proposals;
        ProposalCountText.Text = $"{_proposals.Count:N0} 项建议";
        EmptyProposalPanel.Visibility = _proposals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProposalGrid.Visibility = _proposals.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SendAndSyncButton.IsEnabled = canSend;
        SendHintText.Text = canSend
            ? "发送成功以 WhatsApp 回执为准；AI 字段与需求摘要会写入同一客户档案并留下审计记录。"
            : "WhatsApp 尚未连接或客户已退订：可以先把 AI 回复填入输入框，连接并核对后再发送。";
    }

    private void FillComposer_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateReply()) return;
        Action = ConversationAssistantAction.FillComposer;
        DialogResult = true;
    }

    private void SendAndSync_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateReply()) return;
        Action = ConversationAssistantAction.SendAndSync;
        DialogResult = true;
    }

    private bool ValidateReply()
    {
        if (!string.IsNullOrWhiteSpace(ReplyText) && ReplyText.Length <= 4096) return true;
        MessageBox.Show("请保留一条 1–4096 个字符的回复内容。", "AI 会话助理", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private sealed class FieldProposalItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        public ConversationFieldUpdate Update { get; }
        public string FieldLabel => Update.FieldLabel;
        public string CurrentValue => string.IsNullOrWhiteSpace(Update.CurrentValue) ? "（空）" : Update.CurrentValue;
        public string ProposedValue => Update.Value;
        public string EvidenceQuote => Update.EvidenceQuote;
        public string Reason => Update.Reason;
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }

        public FieldProposalItem(ConversationFieldUpdate update) => Update = update;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
