using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Windows;

/// <summary>Edits WhatsApp labels for one conversation. Mutations are submitted through
/// the bridge and persisted locally after the protocol call succeeds. WhatsApp controls
/// linked-device propagation, so protocol acceptance is not presented as phone confirmation.</summary>
public partial class LabelManagerWindow : Window
{
    private readonly AppServices _services;
    private readonly string _accountId;
    private readonly string _phone;
    private readonly string _displayName;
    private readonly ObservableCollection<LabelItem> _assigned = [];
    private readonly ObservableCollection<LabelItem> _all = [];
    private int _selectedColor;
    private bool _busy;
    private bool _loading;
    private bool _reloadPending;
    private bool _closed;

    public LabelManagerWindow(AppServices services, string accountId, string phone, string displayName)
    {
        _services = services;
        _accountId = accountId;
        _phone = phone;
        _displayName = displayName;
        InitializeComponent();
        TitleText.Text = $"标签 · {displayName}";
        AssignedList.ItemsSource = _assigned;
        AllLabelsList.ItemsSource = _all;
        NewLabelColorCombo.ItemsSource = LabelPalette.Names;
        NewLabelColorCombo.SelectedIndex = 0;
        _selectedColor = 0;
        UpdateCreateButtonState();
        _services.WhatsApp.EventReceived += WhatsApp_EventReceived;
        _services.WhatsAppSync.SynchronizationChanged += WhatsAppSync_SynchronizationChanged;
        Loaded += async (_, _) => await LoadAsync();
        Closed += LabelManagerWindow_Closed;
    }

    private static class LabelPalette
    {
        public static readonly string[] Names =
        [
            "红", "橙", "黄", "绿", "青", "蓝", "紫", "粉", "棕", "灰",
            "红2", "橙2", "黄2", "绿2", "青2", "蓝2", "紫2", "粉2", "棕2", "灰2"
        ];
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            _reloadPending = true;
            return;
        }
        _loading = true;
        try
        {
            var labels = await _services.Repository.GetWhatsAppLabelsAsync(_accountId);
            var assignedIds = await _services.Repository.GetWhatsAppChatLabelIdsAsync(_accountId, _phone);
            var assigned = assignedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _assigned.Clear();
            _all.Clear();
            foreach (var label in labels)
            {
                var item = new LabelItem(label, assigned.Contains(label.Id));
                _all.Add(item);
                if (item.Assigned) _assigned.Add(item);
            }
            EmptyHint.Visibility = _assigned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (labels.Count == 0)
                StatusText.Text = "尚未从 WhatsApp 收到标签。可在连接状态下创建，并在手机端确认同步结果。";
            else if (!_services.WhatsApp.IsConnectedFor(_accountId))
                StatusText.Text = "当前可查看缓存标签；连接 WhatsApp 后才能修改。";
            UpdateCreateButtonState();
        }
        catch (Exception error)
        {
            StatusText.Text = $"加载标签失败：{error.Message}";
        }
        finally
        {
            _loading = false;
            if (!_busy && _reloadPending)
            {
                _reloadPending = false;
                QueueLabelRefresh();
            }
        }
    }

    private void WhatsApp_EventReceived(object? sender, WhatsAppBridgeEvent e)
    {
        if (_closed || !e.AccountId.Equals(_accountId, StringComparison.OrdinalIgnoreCase) || e.Name != "connection") return;
        _ = Dispatcher.InvokeAsync(UpdateCreateButtonState);
    }

    private void WhatsAppSync_SynchronizationChanged(object? sender, WhatsAppSyncProgress e)
    {
        if (_closed
            || !e.AccountId.Equals(_accountId, StringComparison.OrdinalIgnoreCase)
            || e.State != "data"
            || e.Phase != "labels") return;
        QueueLabelRefresh();
    }

    private void QueueLabelRefresh()
    {
        if (_closed) return;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_closed) return;
            if (_busy || _loading)
            {
                _reloadPending = true;
                return;
            }
            await LoadAsync();
        });
    }

    private void LabelManagerWindow_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _services.WhatsApp.EventReceived -= WhatsApp_EventReceived;
        _services.WhatsAppSync.SynchronizationChanged -= WhatsAppSync_SynchronizationChanged;
    }

    private async Task ToggleAsync(LabelItem item, bool add)
    {
        if (_busy) return;
        try
        {
            if (!_services.WhatsApp.IsConnectedFor(_accountId))
            {
                StatusText.Text = "请先连接 WhatsApp，再同步标签。";
                return;
            }
            SetBusy(true);
            StatusText.Text = add ? $"正在添加「{item.Name}」并提交到 WhatsApp…" : $"正在移除「{item.Name}」并提交到 WhatsApp…";
            await _services.WhatsApp.SetChatLabelAsync(_accountId, _phone, item.Id, add);
            await _services.Repository.SetWhatsAppChatLabelAsync(_accountId, _phone, item.Id, add);
            item.Assigned = add;
            if (add)
            {
                if (!_assigned.Contains(item)) _assigned.Add(item);
            }
            else
            {
                var existing = _assigned.FirstOrDefault(candidate => candidate.Id == item.Id);
                if (existing is not null) _assigned.Remove(existing);
            }
            EmptyHint.Visibility = _assigned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"已在 OS {(add ? "添加" : "移除")}「{item.Name}」并提交到 WhatsApp；请在手机端确认显示。";
        }
        catch (Exception error)
        {
            StatusText.Text = $"同步失败：{error.Message}";
        }
        finally { SetBusy(false); }
    }

    private async void AddLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: LabelItem item }) await ToggleAsync(item, add: true);
    }

    private async void RemoveLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: LabelItem item }) await ToggleAsync(item, add: false);
    }

    private async void DeleteLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: LabelItem item }) return;
        if (_busy) return;
        if (MessageBox.Show($"确定从 WhatsApp 删除标签「{item.Name}」吗？所有使用该标签的客户都会失去此标签。",
                "删除标签", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            if (!_services.WhatsApp.IsConnectedFor(_accountId))
            {
                StatusText.Text = "请先连接 WhatsApp，再同步标签。";
                return;
            }
            SetBusy(true);
            StatusText.Text = $"正在删除「{item.Name}」并提交到 WhatsApp…";
            await _services.WhatsApp.UpsertLabelAsync(_accountId, new WhatsAppLabel { Id = item.Id, Name = item.Name, Color = item.Color, Deleted = true });
            await _services.Repository.UpsertWhatsAppLabelAsync(new WhatsAppLabel { Id = item.Id, AccountId = _accountId, Name = item.Name, Color = item.Color, Deleted = true });
            _all.Remove(item);
            var assigned = _assigned.FirstOrDefault(candidate => candidate.Id == item.Id);
            if (assigned is not null) _assigned.Remove(assigned);
            EmptyHint.Visibility = _assigned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"已在 OS 删除「{item.Name}」并提交到 WhatsApp；请在手机端确认结果。";
        }
        catch (Exception error)
        {
            StatusText.Text = $"删除失败：{error.Message}";
        }
        finally { SetBusy(false); }
    }

    private void NewLabelColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NewLabelColorCombo.SelectedIndex >= 0) _selectedColor = NewLabelColorCombo.SelectedIndex;
    }

    private void NewLabelName_TextChanged(object sender, TextChangedEventArgs e) => UpdateCreateButtonState();

    private async void NewLabelName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !CreateLabelButton.IsEnabled) return;
        e.Handled = true;
        await CreateLabelAsync();
    }

    private async void CreateLabel_Click(object sender, RoutedEventArgs e) => await CreateLabelAsync();

    private async Task CreateLabelAsync()
    {
        if (_busy) return;
        var name = NewLabelNameBox.Text.Trim();
        if (name.Length == 0) return;
        if (name.Length > 100)
        {
            StatusText.Text = "标签名称不能超过 100 个字符。";
            return;
        }
        if (_all.Any(item => item.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            StatusText.Text = $"WhatsApp 已有同名标签「{name}」，请直接添加该标签。";
            NewLabelNameBox.Focus();
            return;
        }
        try
        {
            if (!_services.WhatsApp.IsConnectedFor(_accountId))
            {
                StatusText.Text = "请先连接 WhatsApp，再同步标签。";
                return;
            }
            SetBusy(true);
            StatusText.Text = $"正在创建「{name}」并提交到 WhatsApp…";
            var label = new WhatsAppLabel
            {
                Id = Guid.NewGuid().ToString("N"),
                AccountId = _accountId,
                Name = name,
                Color = _selectedColor,
                Deleted = false
            };
            await _services.WhatsApp.UpsertLabelAsync(_accountId, label);
            await _services.Repository.UpsertWhatsAppLabelAsync(label);
            var item = new LabelItem(label, assigned: false);
            _all.Add(item);
            try
            {
                await _services.WhatsApp.SetChatLabelAsync(_accountId, _phone, label.Id, add: true);
                await _services.Repository.SetWhatsAppChatLabelAsync(_accountId, _phone, label.Id, add: true);
                item.Assigned = true;
                _assigned.Add(item);
                EmptyHint.Visibility = Visibility.Collapsed;
                StatusText.Text = $"已在 OS 创建并关联「{name}」，请求已提交到 WhatsApp；请在手机端确认显示。";
            }
            catch (Exception assignmentError)
            {
                StatusText.Text = $"「{name}」的创建请求已提交并保存在 OS，但关联当前客户失败：{assignmentError.Message}。可在上方点击“添加”重试。";
            }
            NewLabelNameBox.Text = "";
        }
        catch (Exception error)
        {
            StatusText.Text = $"创建未完成：{error.Message}。若 WhatsApp 已接收请求，请重新同步标签进行核对。";
            NewLabelNameBox.Focus();
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AssignedList.IsEnabled = !busy;
        AllLabelsList.IsEnabled = !busy;
        NewLabelNameBox.IsEnabled = !busy;
        NewLabelColorCombo.IsEnabled = !busy;
        UpdateCreateButtonState();
        if (!busy && _reloadPending)
        {
            _reloadPending = false;
            QueueLabelRefresh();
        }
    }

    private void UpdateCreateButtonState()
    {
        if (CreateLabelButton is null || NewLabelNameBox is null) return;
        var length = NewLabelNameBox.Text.Trim().Length;
        CreateLabelButton.IsEnabled = !_busy
            && length is >= 1 and <= 100
            && _services.WhatsApp.IsConnectedFor(_accountId);
    }

    public sealed class LabelItem : INotifyPropertyChanged
    {
        private bool _assigned;
        public LabelItem(WhatsAppLabel label, bool assigned)
        {
            Id = label.Id;
            Name = label.Name;
            Color = label.Color;
            AccentBrush = WhatsAppLabelChip.From(label).AccentBrush;
            _assigned = assigned;
        }
        public string Id { get; }
        public string Name { get; }
        public int Color { get; }
        public Brush AccentBrush { get; }
        public string AddAutomationName => $"添加标签：{Name}";
        public string RemoveAutomationName => $"移除标签：{Name}";
        public string DeleteAutomationName => $"删除 WhatsApp 标签：{Name}";
        public bool Assigned { get => _assigned; set { if (_assigned != value) { _assigned = value; OnPropertyChanged(nameof(Assigned)); OnPropertyChanged(nameof(AddVisibility)); } } }
        public Visibility AddVisibility => Assigned ? Visibility.Collapsed : Visibility.Visible;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
