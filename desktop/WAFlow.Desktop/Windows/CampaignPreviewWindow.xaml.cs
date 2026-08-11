using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Windows;

public partial class CampaignPreviewWindow : Window
{
    private readonly ObservableCollection<CampaignPreviewItem> _items;
    private readonly ICollectionView _view;

    public CampaignPreviewWindow(WhatsAppCampaign campaign, IReadOnlyList<CampaignAudienceItem> audience)
    {
        InitializeComponent();

        _items = new ObservableCollection<CampaignPreviewItem>(audience.Select(item => new CampaignPreviewItem(
            item.Lead,
            campaign.Channel,
            item.Eligible,
            item.Reason,
            campaign.Channel == CampaignChannel.Email
                ? CampaignAutomationService.RenderTemplate(campaign.EmailSubjectTemplate, item.Lead)
                : "",
            item.PreviewMessage)));
        _view = CollectionViewSource.GetDefaultView(_items);
        PreviewList.ItemsSource = _view;

        var eligible = _items.Count(item => item.Eligible);
        SummaryText.Text = $"共 {_items.Count} 位 · 可发送 {eligible} · 已排除 {_items.Count - eligible}";
        UpdateSearchState();
        PreviewList.SelectedIndex = 0;
        Loaded += (_, _) => PreviewList.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        _view.Filter = item => item is CampaignPreviewItem preview
            && (query.Length == 0 || preview.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        _view.Refresh();
        UpdateSearchState();
        var selected = PreviewList.SelectedItem as CampaignPreviewItem;
        if (selected is null || !_view.Cast<CampaignPreviewItem>().Contains(selected))
            PreviewList.SelectedItem = _view.Cast<CampaignPreviewItem>().FirstOrDefault();
    }

    private void PreviewList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewList.SelectedItem is null && _view.Cast<CampaignPreviewItem>().FirstOrDefault() is { } first)
            PreviewList.SelectedItem = first;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void UpdateSearchState()
    {
        var visible = _view.Cast<object>().Count();
        VisibleCountText.Text = $"显示 {visible} / {_items.Count} 位客户";
        var hasResults = visible > 0;
        FilterEmptyPanel.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
        PreviewDetailPanel.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        NoPreviewPanel.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed record CampaignPreviewItem(
        Lead Lead,
        CampaignChannel Channel,
        bool Eligible,
        string Reason,
        string Subject,
        string Message)
    {
        public string DisplayName => Lead.DisplayName;
        public string Contact => Channel == CampaignChannel.Email ? Lead.Email : Lead.PhoneE164;
        public string CustomerMeta => $"{Contact} · 等级 {Lead.Grade} · {Labels.Stage(Lead.Stage)}";
        public string StatusLabel => Eligible ? "可发送" : "已排除";
        public string EligibilityDetail => $"{(Eligible ? "发送检查" : "排除原因")}：{(string.IsNullOrWhiteSpace(Reason) ? "无额外说明" : Reason)}";
        public Visibility EmailSubjectVisibility => Channel == CampaignChannel.Email ? Visibility.Visible : Visibility.Collapsed;
        public string SearchText => string.Join(" ", DisplayName, Contact, Lead.Company, Lead.Grade, Labels.Stage(Lead.Stage), StatusLabel, Reason, Subject, Message);
    }
}
