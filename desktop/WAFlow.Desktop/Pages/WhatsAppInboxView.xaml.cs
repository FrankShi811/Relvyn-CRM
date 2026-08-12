using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;
using WAFlow.Desktop.Collections;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop.Pages;

public partial class WhatsAppInboxView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private readonly BatchObservableCollection<ConversationItem> _conversations = [];
    private readonly BatchObservableCollection<WhatsAppAccount> _accounts = [];
    private readonly List<Lead> _leads = [];
    private Lead? _currentLead;
    private CustomerIdentityResolution? _currentIdentityResolution;
    private CustomerSuccessContext? _currentCustomerSuccessContext;
    private AgentTask? _latestSourcingTask;
    private BusinessRoleProfile _workspaceProfile = new();
    private CustomerSuccessAgentDecision? _pendingKnowledgeDecision;
    private bool _connected;
    private bool _switchingAccount;
    private bool _existingSession;
    private bool _refreshScheduled;
    private bool _refreshAgain;
    private bool _initialLeadLinkCompleted;
    private bool _sending;
    private bool _aiAssisting;
    private int _conversationSelectionGeneration;
    private int _customerBrainRefreshGeneration;
    private CustomerSuccessRunContextToken? _pendingAgentDraftContextToken;
    private string _pendingKnowledgeCustomerId = "";
    private string _pendingKnowledgeAccountId = "";
    private string _pendingKnowledgeConversationId = "";
    private string _attachmentPath = "";
    private MessageItem? _replyingTo;
    private string _composerConversationId = "";
    private string _currentStatusUpdateUrl = "";
    private int _persistedConversationCount;
    private int _contactCount;
    private readonly HashSet<string> _warnedIpChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _ipTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool _checkingIp;
    private bool _leadDrawerExpanded = true;
    private readonly object _refreshLock = new();
    private Task _activeRefresh = Task.CompletedTask;
    private bool _refreshRequestedAgain;
    private CancellationTokenSource? _contextRefreshDebounce;
    private CancellationTokenSource? _translationContextCts;
    private CancellationTokenSource? _translationRunCts;
    private WhatsAppConversationLanguageProfile? _translationProfile;
    private bool _translationBusy;
    private string _draftTranslationOriginal = "";
    private string _draftTranslationText = "";
    private bool _translatedDraftApplied;
    private bool _applyingTranslatedDraft;
    private bool _connectionActionInProgress;

    private string CurrentAccountId => (AccountCombo.SelectedItem as WhatsAppAccount)?.Id ?? "primary";

    public event EventHandler? DataChanged;

    public WhatsAppInboxView(AppServices services)
    {
        InitializeComponent();
        _services = services;
        // TextChanged may fire while InitializeComponent is still connecting named
        // controls. Wire it only after the complete visual tree is available.
        ComposerBox.TextChanged += ComposerBox_TextChanged;
        ConversationList.ItemsSource = _conversations;
        AccountCombo.ItemsSource = _accounts;
        StageCombo.ItemsSource = Enum.GetValues<LeadStage>().Select(x => new StageOption(Labels.Stage(x), x)).ToList();
        AgentModeCombo.ItemsSource = new[]
        {
            ConversationAgentMode.AutoOff, ConversationAgentMode.SuggestOnly,
            ConversationAgentMode.CopilotActive, ConversationAgentMode.AutoActive
        }.Select(value => new AgentModeOption(CustomerSuccessAgentLabels.Mode(value), value)).ToList();
        _services.WhatsApp.EventReceived += WhatsApp_EventReceived;
        _services.WhatsAppSync.SynchronizationChanged += WhatsAppSync_SynchronizationChanged;
        _services.CustomerSuccessCoordinator.RunCompleted += CustomerSuccessCoordinator_RunCompleted;
        _ipTimer.Tick += async (_, _) => await RefreshPublicIpAsync();
        Loaded += async (_, _) =>
        {
            _ipTimer.Start();
            _existingSession = _services.WhatsApp.HasStoredSession(CurrentAccountId);
            RestoreLatestQr();
            await RefreshPublicIpAsync();
        };
        Unloaded += (_, _) => _ipTimer.Stop();
    }

    public Task RefreshAsync()
    {
        lock (_refreshLock)
        {
            if (!_activeRefresh.IsCompleted)
            {
                _refreshRequestedAgain = true;
                return _activeRefresh;
            }

            _activeRefresh = RefreshLoopAsync();
            return _activeRefresh;
        }
    }

    private async Task RefreshLoopAsync()
    {
        do
        {
            lock (_refreshLock) _refreshRequestedAgain = false;
            await RefreshCoreAsync();
        }
        while (IsVisible && ReadRefreshRequestedAgain());
    }

    private bool ReadRefreshRequestedAgain()
    {
        lock (_refreshLock) return _refreshRequestedAgain;
    }

    private async Task RefreshCoreAsync()
    {
        var selectedAccountId = (AccountCombo.SelectedItem as WhatsAppAccount)?.Id
            ?? _services.WhatsApp.ActiveAccountId;
        var selectedConversationId = (ConversationList.SelectedItem as ConversationItem)?.Id;
        var runLeadLink = !_initialLeadLinkCompleted;
        var snapshot = await Task.Run(async () =>
        {
            var workspaceProfile = BusinessRoleProfile.Normalize(
                (await _services.Repository.GetAppSettingsAsync()).BusinessRoleProfile);
            var accounts = await _services.Repository.GetWhatsAppAccountsAsync();
            var selectedAccount = accounts.FirstOrDefault(item =>
                item.Id.Equals(selectedAccountId, StringComparison.OrdinalIgnoreCase))
                ?? accounts.FirstOrDefault();
            var accountId = selectedAccount?.Id ?? "primary";
            var leads = await _services.Repository.GetLeadsAsync();
            if (runLeadLink)
            {
                await _services.Repository.SynchronizeLeadConnectionsFromInboxAsync(leads);
                leads = await _services.Repository.GetLeadsAsync();
            }

            var persisted = await _services.Repository.GetWhatsAppConversationsAsync(accountId);
            var contacts = await _services.Repository.GetWhatsAppContactsAsync(accountId);
            var refreshed = new Dictionary<string, ConversationItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var saved in persisted)
            {
                var ownedPeer = saved.IsGroup
                    ? null
                    : FindOwnedPeerAccount(accounts, saved.AccountId, saved.Phone);
                var linkedLead = saved.IsGroup ? null : FindLead(leads, accounts, saved.AccountId, saved.Phone);
                var preferredName = saved.IsGroup
                    ? saved.DisplayName
                    : WhatsAppConversationNaming.Resolve(
                        linkedLead,
                        saved.Phone,
                        ownedPeer?.Name,
                        saved.DisplayName);
                var conversation = new ConversationItem(saved.AccountId, saved.Phone, preferredName, saved.Jid)
                {
                    IsGroup = saved.IsGroup,
                    LeadId = ownedPeer is not null
                        ? ""
                        : linkedLead?.Id ?? saved.LeadId,
                    LastMessage = saved.LastMessage,
                    LastAt = saved.LastMessageAt,
                    Unread = saved.UnreadCount,
                    LastReadAt = saved.LastReadAt,
                    IsPinned = saved.IsPinned,
                    PinnedAt = saved.PinnedAt
                };
                refreshed[conversation.Id] = conversation;
            }

            foreach (var contact in contacts)
            {
                var itemId = string.IsNullOrWhiteSpace(contact.Phone)
                    ? contact.Id
                    : $"{contact.AccountId}:{contact.Phone}";
                var linkedLead = string.IsNullOrWhiteSpace(contact.Phone)
                    ? null
                    : FindLead(leads, accounts, contact.AccountId, contact.Phone);
                var ownedPeer = string.IsNullOrWhiteSpace(contact.Phone)
                    ? null
                    : FindOwnedPeerAccount(accounts, contact.AccountId, contact.Phone);
                var contactName = WhatsAppConversationNaming.Resolve(
                    linkedLead,
                    contact.Phone,
                    ownedPeer?.Name,
                    BestContactName(contact));
                if (!refreshed.TryGetValue(itemId, out var conversation))
                {
                    conversation = new ConversationItem(contact.AccountId, contact.Phone, contactName, contact.Jid)
                    {
                        LastMessage = "WhatsApp 联系人",
                        LeadId = linkedLead?.Id ?? ""
                    };
                    refreshed[itemId] = conversation;
                }
                else
                {
                    conversation.Jid = contact.Jid;
                    conversation.LeadId = ownedPeer is not null ? "" : linkedLead?.Id ?? conversation.LeadId;
                    conversation.DisplayName = WhatsAppConversationNaming.Resolve(
                        linkedLead,
                        contact.Phone,
                        ownedPeer?.Name,
                        contactName,
                        conversation.DisplayName);
                }
            }

            return new WhatsAppInboxSnapshot(
                workspaceProfile,
                accounts,
                leads,
                accountId,
                OrderConversations(refreshed.Values).ToList(),
                persisted.Count,
                contacts.Count,
                runLeadLink);
        });

        _workspaceProfile = snapshot.WorkspaceProfile;
        _switchingAccount = true;
        _accounts.ReplaceAll(snapshot.Accounts);
        AccountCombo.SelectedItem = _accounts.FirstOrDefault(item =>
            item.Id.Equals(snapshot.SelectedAccountId, StringComparison.OrdinalIgnoreCase));
        _switchingAccount = false;
        _services.WhatsApp.SetActiveAccount(CurrentAccountId);
        _connected = _services.WhatsApp.IsConnectedFor(CurrentAccountId);
        _leads.Clear();
        _leads.AddRange(snapshot.Leads);
        if (snapshot.CompletedLeadLink) _initialLeadLinkCompleted = true;
        _conversations.ReplaceAll(snapshot.Conversations);
        _persistedConversationCount = snapshot.PersistedConversationCount;
        _contactCount = snapshot.ContactCount;
        ConversationCountText.Text = $"{_persistedConversationCount} 会话 · {_contactCount} 联系人";
        await RefreshConversationLabelsAsync();
        ApplyConversationFilter();
        ConversationList.SelectedItem = _conversations.FirstOrDefault(item => item.Id == selectedConversationId);
        UpdateConnectionControls();
        RestoreLatestQr();
        if (_currentCustomerSuccessContext is null)
            UpdateCustomerSuccessPanel(_currentIdentityResolution, null);
            _latestSourcingTask = null;
            RenderSourcingResultPanel();
    }

    private async void AccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingAccount || AccountCombo.SelectedItem is not WhatsAppAccount) return;
        _services.WhatsApp.SetActiveAccount(CurrentAccountId); _conversations.Clear(); ConversationList.SelectedItem = null; ClearLead();
        await RefreshAsync();
        await RefreshPublicIpAsync();
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var accounts = await _services.Repository.GetWhatsAppAccountsAsync();
            var account = new WhatsAppAccount { Id = $"personal_{Guid.NewGuid():N}"[..29], Name = $"个人号 {accounts.Count + 1}" };
            accounts.Add(account); await _services.Repository.SaveWhatsAppAccountsAsync(accounts);
            await RefreshAsync(); AccountCombo.SelectedItem = _accounts.First(x => x.Id == account.Id);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "添加账号失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        if (!_services.WhatsApp.IsConnectedFor(CurrentAccountId))
        {
            MessageBox.Show("请先连接当前 WhatsApp 账号。", "建立 WhatsApp 群组", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var contacts = await _services.Repository.GetWhatsAppContactsAsync(CurrentAccountId);
            var candidates = contacts
                .Where(contact => PhoneNormalizer.Normalize(contact.Phone, null).Valid)
                .Select(contact => new { Contact = contact, Lead = FindLead(contact.Phone) })
                .Select(item => new CreateWhatsAppGroupWindow.GroupMemberCandidate(
                    WhatsAppConversationNaming.Resolve(item.Lead, item.Contact.Phone, BestContactName(item.Contact)),
                    item.Contact.Phone,
                    item.Lead is null ? "WhatsApp 联系人" : "CRM 客户"))
                .ToList();
            var dialog = new CreateWhatsAppGroupWindow(candidates) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Request is null) return;

            CreateGroupButton.IsEnabled = false;
            var result = await _services.WhatsApp.CreateGroupAsync(CurrentAccountId, dialog.Request);
            await _services.Repository.LogEventAsync("whatsapp_group_created", null, null,
                $"account={CurrentAccountId};group={result.GroupJid};subject={result.Subject};participants={result.ParticipantCount}");
            try { await _services.WhatsApp.SyncNowAsync(); } catch { }
            MessageBox.Show($"群组“{result.Subject}”已建立，并已同步到手机 WhatsApp。\n\n成员：{result.ParticipantCount:N0} 位\n群组 ID：{result.GroupJid}", "WhatsApp 建群成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (WhatsAppBridgeException error)
        {
            var message = error.Code switch
            {
                "invalid_group_subject" => "群名称无效，请控制在 1-100 个字符。",
                "invalid_group_participants" or "invalid_group_participant_count" => "群成员无效，请重新选择具有国际区号的 WhatsApp 联系人。",
                "whatsapp_not_connected" => "WhatsApp 连接已经断开，请重新连接后再建群。",
                _ => error.Message
            };
            MessageBox.Show(message, "WhatsApp 建群失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "WhatsApp 建群失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { UpdateConnectionControls(); }
    }

    private void ToggleLeadDrawer_Click(object sender, RoutedEventArgs e)
    {
        _leadDrawerExpanded = !_leadDrawerExpanded;
        LeadSidebarColumn.Width = new GridLength(_leadDrawerExpanded ? 360 : 40);
        LeadSidebarBorder.Visibility = _leadDrawerExpanded ? Visibility.Visible : Visibility.Collapsed;
        LeadDrawerCollapsedRail.Visibility = _leadDrawerExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionActionInProgress) return;
        var accountId = CurrentAccountId;
        _connectionActionInProgress = true;
        SetConnectionText("正在启动本地桥…", false);
        ShowQrProgress(
            _services.WhatsApp.RequiresLocalAuthorization(accountId)
                ? "检测到这是另一台电脑：旧会话密钥不会跨设备迁移，正在安全建立新的扫码会话…"
                : "正在准备安全登录会话，请稍候…",
            clearQr: true);
        UpdateConnectionControls();
        _ = RefreshPublicIpAsync();
        try
        {
            await _services.WhatsApp.ConnectAsync(accountId);
            RestoreLatestQr();
        }
        catch (WhatsAppBridgeException error)
        {
            SetConnectionText("请重试连接", false);
            ShowQrIssue(error.Code switch
            {
                "qr_generation_timeout" => "二维码生成超时。程序已尝试 Windows 系统代理与直连；请确认防火墙、代理或公司网络允许访问 WhatsApp 后再试。",
                "bridge_runtime_missing" => "本地 WhatsApp 连接组件缺失。请通过设置中的“版本与更新”修复或重新安装当前版本。",
                "bridge_exited" => "本地 WhatsApp 连接组件意外退出。程序已停止本次连接，请点击按钮重试；若持续发生，请检查安全软件是否拦截。",
                _ => $"WhatsApp 连接暂未完成：{error.Message} 请检查网络后重试。"
            });
        }
        catch (Exception error)
        {
            SetConnectionText("连接失败", false);
            ShowQrIssue($"WhatsApp 连接暂未完成：{error.Message} 请检查网络后重试。");
        }
        finally
        {
            _connectionActionInProgress = false;
            UpdateConnectionControls();
        }
    }

    private void AccountActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionActionInProgress) return;
        var accountId = CurrentAccountId;
        _connectionActionInProgress = true;
        SetConnectionText("正在断开…", false);
        ShowQrProgress("正在停止当前连接与自动重试，请稍候…", clearQr: true);
        UpdateConnectionControls();
        try
        {
            await _services.Campaigns.PauseAccountAsync(accountId, "用户手动断开 WhatsApp，活动 Campaign 已暂停。");
            await _services.WhatsApp.DisconnectAsync(accountId);
            ShowQrIssue("连接已停止。点击“连接 / 显示二维码”可重新连接；如需更换登录，请先退出账号。");
        }
        catch (Exception error) { MessageBox.Show(error.Message, "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _connectionActionInProgress = false;
            UpdateConnectionControls();
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("退出后将删除本机登录会话，需要重新扫码；已经同步到 AI Sales OS 的联系人和消息仍会保留。是否继续？", "退出 WhatsApp 账号", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_connectionActionInProgress) return;
        var accountId = CurrentAccountId;
        _connectionActionInProgress = true;
        SetConnectionText("正在清除本机登录…", false);
        ShowQrProgress("正在停止所有自动重试并清除本机登录会话…", clearQr: true);
        UpdateConnectionControls();
        try
        {
            await _services.Campaigns.PauseAccountAsync(accountId, "用户退出 WhatsApp，活动 Campaign 已暂停。");
            await _services.WhatsApp.LogoutAsync(accountId); _conversations.Clear(); ClearLead();
            ShowQrIssue("本机登录会话已清除。现在点击“连接 / 显示二维码”即可生成新的 WhatsApp 二维码。");
        }
        catch (Exception error) { MessageBox.Show(error.Message, "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _connectionActionInProgress = false;
            UpdateConnectionControls();
        }
    }

    private void WhatsApp_EventReceived(object? sender, WhatsAppBridgeEvent e)
    {
        if (!IsVisible) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (IsVisible) HandleBridgeEvent(e);
        });
    }

    private void WhatsAppSync_SynchronizationChanged(object? sender, WhatsAppSyncProgress progress) => Dispatcher.InvokeAsync(() =>
    {
        if (!IsVisible) return;
        if (!progress.AccountId.Equals(CurrentAccountId, StringComparison.OrdinalIgnoreCase)) return;
        if (progress.State == "data")
        {
            ScheduleRefresh();
            return;
        }
        SyncStatusText.Text = progress.State switch
        {
            "action_required" when progress.Phase == "offline_history_profile" =>
                string.IsNullOrWhiteSpace(progress.Error)
                    ? "当前账号需退出并用新版重新扫码一次，才能补回完整离线历史"
                    : progress.Error,
            "syncing" => $"正在同步 {PhaseLabel(progress.Phase)}{(progress.Progress is null ? "" : $" {progress.Progress}%")}",
            "complete" when progress.Phase == "offline_messages" =>
                $"已恢复 {progress.RecoveredMessages} 条离线消息，实时同步已恢复",
            "complete" when progress.Phase == "offline_messages_no_new_messages" => progress.RequestedChats > 0
                ? $"已核对 {progress.RequestedChats} 个缺口会话；本次未收到新增历史，程序将保持在线继续接收"
                : "本次未收到新增历史，程序将保持在线继续接收",
            "complete" when progress.Phase == "offline_messages_timeout" =>
                "离线历史恢复仍在等待手机响应，程序将保持在线；可稍后再次同步",
            "complete" => progress.Messages > 0 || progress.Contacts > 0 || progress.Chats > 0
                ? $"已同步 {progress.Chats} 会话 / {progress.Contacts} 联系人 / {progress.Messages} 消息"
                : _existingSession ? "已同步联系人与会话状态" : "同步完成",
            "paused" => "已保存手机提供的历史，传输现已暂停",
            "failed" => $"同步失败：{progress.Error}",
            _ => SyncStatusText.Text
        };
        if (progress.State is "complete" or "paused") ScheduleRefresh();
    });

    private void CustomerSuccessCoordinator_RunCompleted(
        object? sender,
        CustomerSuccessAgentRunCompletedEvent e)
    {
        if (!IsVisible) return;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (!IsVisible) return;
            if (ConversationList.SelectedItem is not ConversationItem conversation ||
                !conversation.AccountId.Equals(e.AccountId, StringComparison.OrdinalIgnoreCase) ||
                !conversation.Id.Equals(e.ConversationId, StringComparison.OrdinalIgnoreCase))
                return;
            await LoadLeadAsync(conversation);
            DataChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private async void ScheduleRefresh()
    {
        if (_refreshScheduled) { _refreshAgain = true; return; }
        _refreshScheduled = true;
        try
        {
            do
            {
                _refreshAgain = false;
                await Task.Delay(250);
                await RefreshAsync();
            }
            while (_refreshAgain);
        }
        finally { _refreshScheduled = false; }
    }

    private async Task RefreshPublicIpAsync()
    {
        if (_checkingIp) return;
        var accountId = CurrentAccountId;
        _checkingIp = true;
        try
        {
            var result = await _services.PublicIp.CheckAsync(accountId);
            if (!accountId.Equals(CurrentAccountId, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                IpStatusText.Text = $"公网 IP：{result.Error} · 60 秒后重试";
                IpStatusDot.Foreground = (Brush)FindResource("Muted");
                IpStatusText.Foreground = (Brush)FindResource("Muted");
                IpStatusBorder.Background = (Brush)FindResource("SurfaceMuted");
                return;
            }
            var state = result.State;
            var location = string.IsNullOrWhiteSpace(state.LocationLabel) ? "位置未知" : state.LocationLabel;
            var recentlyChanged = !string.IsNullOrWhiteSpace(state.PreviousIp)
                && !state.PreviousIp.Equals(state.CurrentIp, StringComparison.OrdinalIgnoreCase)
                && state.ChangedAt >= DateTimeOffset.Now.AddHours(-24);
            IpStatusText.Text = recentlyChanged
                ? $"公网 IP 已变化：{state.PreviousIp} → {state.CurrentIp} · {location} · 每 60 秒监测"
                : $"公网 IP：{state.CurrentIp} · {location} · 每 60 秒监测";
            IpStatusDot.Foreground = (Brush)FindResource(recentlyChanged ? "Danger" : "Success");
            IpStatusText.Foreground = (Brush)FindResource(recentlyChanged ? "Danger" : "Success");
            IpStatusBorder.Background = (Brush)FindResource(recentlyChanged ? "DangerSoft" : "SuccessSoft");
            if (result.Changed)
            {
                var warningKey = $"{accountId}|{state.PreviousIp}|{state.CurrentIp}|{state.ChangedAt:O}";
                if (_warnedIpChanges.Add(warningKey))
                    MessageBox.Show($"检测到本机公网出口 IP 发生变化：\n{state.PreviousIp} → {state.CurrentIp}\n当前位置：{location}\n\nIP 变化不等于封号，但频繁跨地区切换、VPN/代理跳变可能增加异常登录风险。建议先暂停自动化并确认网络环境。", "WhatsApp 网络风险提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { _checkingIp = false; }
    }

    private void UpdateVisibleMessageStatus(JsonElement data)
    {
        var id = Text(data, "id");
        if (string.IsNullOrWhiteSpace(id)) return;
        var numeric = data.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var parsed) ? parsed : -1;
        if (numeric < 0) return;
        var status = StatusFromNumeric(numeric);
        foreach (var conversation in _conversations)
        {
            var message = conversation.Messages.FirstOrDefault(item => item.Id == id);
            if (message is null) continue;
            message.UpdateStatus(status, ParseTime(data, "statusAt") ?? DateTimeOffset.Now, ParseTime(data, "deliveredAt"), ParseTime(data, "readAt"), Text(data, "failureReason"));
            break;
        }
    }

    private void UpdateVisibleMessageRevocation(JsonElement data)
    {
        var id = Text(data, "revokedMessageId");
        if (string.IsNullOrWhiteSpace(id)) return;
        var revokedAt = ParseTime(data, "timestamp") ?? DateTimeOffset.Now;
        foreach (var conversation in _conversations)
        {
            var message = conversation.Messages.FirstOrDefault(item => item.Id == id);
            if (message is null) continue;
            message.MarkRevoked(revokedAt);
            if (ReferenceEquals(_replyingTo, message)) ClearReply();
            if (ReferenceEquals(conversation.Messages.LastOrDefault(), message))
                conversation.LastMessage = message.FromMe ? "你撤回了一条消息" : "对方撤回了一条消息";
            break;
        }
    }

    private void HandleBridgeEvent(WhatsAppBridgeEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.AccountId) && !e.AccountId.Equals(CurrentAccountId, StringComparison.OrdinalIgnoreCase)) return;
        if (e.Name == "connection_stage")
        {
            var stage = Text(e.Data, "state");
            var message = Text(e.Data, "message");
            ShowQrProgress(string.IsNullOrWhiteSpace(message)
                ? stage switch
                {
                    "checking_protocol" => "正在检查 WhatsApp 兼容协议…",
                    "opening" => "正在建立 WhatsApp 安全连接…",
                    "retrying" => "连接暂时中断，程序正在自动重试…",
                    _ => "正在准备安全登录会话…"
                }
                : $"{message}…");
            return;
        }
        if (e.Name == "connection_issue")
        {
            var message = Text(e.Data, "message");
            ShowQrProgress(string.IsNullOrWhiteSpace(message)
                ? "二维码暂未生成，程序正在自动重试…"
                : $"{message}…");
            SetConnectionText("自动重试中", false);
            return;
        }
        if (e.Name == "bridge_error")
        {
            ShowQrIssue($"本地 WhatsApp 连接组件暂时无法完成连接：{Text(e.Data, "error")} 请检查网络后重试。");
            SetConnectionText("请重试连接", false);
            return;
        }
        if (e.Name == "auth_recovery")
        {
            SetConnectionText("请重新扫码", false);
            QrHintText.Text = "旧登录凭据已损坏或密钥不匹配，软件已安全备份旧会话。请扫描新二维码重新登录。";
            return;
        }
        if (e.Name == "local_authorization_required")
        {
            SetConnectionText("此电脑需扫码", false);
            ShowQrProgress("账号资料来自另一台电脑；为保护登录安全，本机不会复制旧密钥，正在生成新的真实 WhatsApp 二维码…");
            return;
        }
        if (e.Name == "qr" && e.Data.TryGetProperty("dataUrl", out var dataUrl))
        {
            ShowQr(dataUrl.GetString() ?? "");
            return;
        }
        if (e.Name == "connection")
        {
            var connection = e.Data.TryGetProperty("state", out var state) ? state.GetString() ?? "disconnected" : "disconnected";
            _connected = connection == "connected";
            _existingSession = Bool(e.Data, "existingSession");
            var requiresHistoryRepair = Bool(e.Data, "requiresHistoryRepair");
            SetConnectionText(connection switch { "connected" => "已连接", "connecting" => "连接中", "retrying" => "自动重试中", "logged_out" => "登录已失效", _ => "已断开" }, _connected);
            var canStop = _connected || connection is "connecting" or "retrying";
            var canLogout = canStop || _services.WhatsApp.HasStoredSession(CurrentAccountId);
            AccountActionsButton.IsEnabled = !_connectionActionInProgress && (canStop || canLogout);
            DisconnectMenuItem.IsEnabled = !_connectionActionInProgress && canStop;
            LogoutMenuItem.IsEnabled = !_connectionActionInProgress && canLogout;
            ConnectButton.IsEnabled = !_connectionActionInProgress && !canStop;
            UpdateComposerState();
            SyncButton.IsEnabled = _connected;
            if (_connected)
            {
                QrProgressBar.Visibility = Visibility.Collapsed;
                QrPanel.Visibility = Visibility.Collapsed;
                MessageList.Visibility = Visibility.Visible;
                _ = SaveLinkedAccountAsync(e);
                SyncStatusText.Text = requiresHistoryRepair
                    ? "当前账号需退出并用新版重新扫码一次，才能补回完整离线历史"
                    : _existingSession ? "正在自动补齐离线期间的新消息…" : "正在接收首次历史与联系人…";
            }
            else if (connection == "logged_out")
            {
                QrProgressBar.Visibility = Visibility.Collapsed;
                QrHintText.Text = "登录凭据已失效。点击“连接 / 显示二维码”将创建新二维码，请重新扫码登录。";
                QrPanel.Visibility = Visibility.Visible;
                MessageList.Visibility = Visibility.Collapsed;
            }
            return;
        }
        if (e.Name == "message_status")
        {
            UpdateVisibleMessageStatus(e.Data);
            return;
        }
        if (e.Name == "message_revoked")
        {
            UpdateVisibleMessageRevocation(e.Data);
            return;
        }
        if (e.Name != "message") return;
        var jid = Text(e.Data, "jid");
        var isGroup = Bool(e.Data, "isGroup") || jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
        var phone = Text(e.Data, "phone");
        if (isGroup)
        {
            jid = Text(e.Data, "groupJid") is { Length: > 0 } groupJid ? groupJid : jid;
            if (!jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return;
            phone = "";
        }
        else if (string.IsNullOrWhiteSpace(phone)) return;
        var messageId = Text(e.Data, "id");
        var text = WhatsAppTextEncodingRepair.Repair(Text(e.Data, "text"));
        var fromMe = Bool(e.Data, "fromMe");
        var displayName = WhatsAppTextEncodingRepair.Repair(isGroup ? Text(e.Data, "groupName") : Text(e.Data, "pushName"));
        var kind = Text(e.Data, "kind");
        var fileName = WhatsAppTextEncodingRepair.Repair(Text(e.Data, "fileName"));
        var mimeType = Text(e.Data, "mimeType");
        var mediaPath = Text(e.Data, "mediaPath");
        var mediaDownloadError = Text(e.Data, "mediaDownloadError");
        var timestamp = DateTimeOffset.TryParse(Text(e.Data, "timestamp"), out var parsed) ? parsed : DateTimeOffset.Now;
        var accountId = string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId;
        if (!accountId.Equals(CurrentAccountId, StringComparison.OrdinalIgnoreCase)) return;
        var conversationId = isGroup ? $"{accountId}:{jid}" : $"{accountId}:{phone}";
        var conversation = _conversations.FirstOrDefault(x => x.Id.Equals(conversationId, StringComparison.OrdinalIgnoreCase));
        if (conversation is null)
        {
            var ownedPeer = isGroup ? null : FindOwnedPeerAccount(accountId, phone);
            var linkedLead = isGroup ? null : FindLead(phone);
            var preferredName = string.IsNullOrWhiteSpace(displayName)
                ? isGroup ? "WhatsApp 群聊" : ownedPeer?.Name ?? $"+{phone}"
                : displayName;
            if (!isGroup)
                preferredName = WhatsAppConversationNaming.Resolve(
                    linkedLead,
                    phone,
                    ownedPeer?.Name,
                    preferredName);
            conversation = new ConversationItem(accountId, phone, preferredName, jid) { LeadId = linkedLead?.Id ?? "", IsGroup = isGroup };
            _conversations.Insert(0, conversation);
        }
        else if (isGroup && !string.IsNullOrWhiteSpace(displayName))
        {
            conversation.DisplayName = displayName;
        }
        else if (!isGroup)
        {
            var ownedPeer = FindOwnedPeerAccount(accountId, phone);
            var linkedLead = FindLead(phone);
            conversation.LeadId = ownedPeer is not null ? "" : linkedLead?.Id ?? conversation.LeadId;
            conversation.DisplayName = WhatsAppConversationNaming.Resolve(
                linkedLead,
                phone,
                ownedPeer?.Name,
                displayName,
                conversation.DisplayName);
        }
        var isStatusUpdate = Bool(e.Data, "isStatusUpdate");
        var senderName = WhatsAppTextEncodingRepair.Repair(Text(e.Data, "participantName"));
        if (string.IsNullOrWhiteSpace(senderName)) senderName = WhatsAppTextEncodingRepair.Repair(Text(e.Data, "pushName"));
        if (string.IsNullOrWhiteSpace(senderName) && isGroup)
        {
            var participantPhone = Text(e.Data, "participantPhone");
            senderName = string.IsNullOrWhiteSpace(participantPhone) ? "群成员" : $"+{participantPhone}";
        }
        var incomingMessage = new MessageItem(messageId, text, timestamp, fromMe, kind, fileName, mimeType, mediaPath, mediaDownloadError, ParseMessageStatus(e.Data, fromMe), ParseTime(e.Data, "statusAt"), ParseTime(e.Data, "deliveredAt"), ParseTime(e.Data, "readAt"), Text(e.Data, "failureReason"), Text(e.Data, "quotedMessageId"), WhatsAppTextEncodingRepair.Repair(Text(e.Data, "quotedText")), Bool(e.Data, "quotedFromMe"), Bool(e.Data, "isRevoked"), ParseTime(e.Data, "revokedAt"), isStatusUpdate, ParseTime(e.Data, "statusExpiresAt"), senderName, isGroup);
        var existingIndex = conversation.Messages
            .Select((item, index) => (item, index))
            .FirstOrDefault(entry => entry.item.Id == messageId);
        var contentAccepted = false;
        if (existingIndex.item is null)
        {
            conversation.Messages.Add(incomingMessage);
            contentAccepted = true;
        }
        else if (existingIndex.item.ShouldReplaceContentWith(incomingMessage))
        {
            incomingMessage.UpdateStatus(existingIndex.item.Status, existingIndex.item.StatusUpdatedAt, existingIndex.item.DeliveredAt, existingIndex.item.ReadAt, existingIndex.item.FailureReason);
            if (existingIndex.item.IsRevoked) incomingMessage.MarkRevoked(existingIndex.item.RevokedAt);
            if (ReferenceEquals(_replyingTo, existingIndex.item)) _replyingTo = incomingMessage;
            conversation.Messages[existingIndex.index] = incomingMessage;
            contentAccepted = true;
        }
        if (contentAccepted)
        {
            var preview = MessagePreview(text, kind, fileName);
            if (isGroup && !fromMe) preview = $"{senderName}：{preview}";
            conversation.LastMessage = isStatusUpdate ? $"[最新动态] {preview}" : preview;
        }
        conversation.LastAt = timestamp;
        var visibleConversation = IsVisible && ConversationList.SelectedItem == conversation;
        if (existingIndex.item is null && !fromMe && !isStatusUpdate && !visibleConversation) conversation.Unread++;
        else if (!fromMe && visibleConversation) _ = PersistConversationReadAfterSyncAsync(conversation);
        ReorderConversations(conversation);
        if (ConversationList.SelectedItem == conversation)
        {
            UpdateStatusUpdateBanner(conversation);
            ScrollMessages(conversation);
            if (contentAccepted && !isStatusUpdate && _currentLead is not null)
                ScheduleConversationContextRefresh(_currentLead.Id);
        }
    }

    private async Task SaveLinkedAccountAsync(WhatsAppBridgeEvent e)
    {
        try
        {
            var accounts = await _services.Repository.GetWhatsAppAccountsAsync();
            var account = accounts.FirstOrDefault(x => x.Id == CurrentAccountId); if (account is null) return;
            var user = Text(e.Data, "user"); var name = Text(e.Data, "name");
            var phone = new string(user.Split(':')[0].Where(char.IsDigit).ToArray());
            if (phone.Length > 0) account.LinkedPhone = "+" + phone;
            if (!string.IsNullOrWhiteSpace(name) && account.Name.StartsWith("个人号 ", StringComparison.Ordinal)) account.Name = name;
            await _services.Repository.SaveWhatsAppAccountsAsync(accounts);
        }
        catch { }
    }

    private async Task PersistConversationReadAfterSyncAsync(ConversationItem conversation)
    {
        try
        {
            // The bridge persistence subscriber and this visible Inbox receive
            // the same live event. Persist after that write so a message already
            // visible on screen cannot resurrect as unread on the next refresh.
            await Task.Delay(150);
            conversation.Unread = 0;
            conversation.LastReadAt = DateTimeOffset.Now;
            await _services.Repository.MarkWhatsAppConversationReadAsync(conversation.Id);
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    private async void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectionGeneration = ++_conversationSelectionGeneration;
        if (ConversationList.SelectedItem is not ConversationItem conversation)
        {
            _composerConversationId = "";
            ClearAttachment();
            ClearReply();
            ResetTranslationUi();
            ChatTitleText.Text = "选择会话"; ChatNumberText.Text = "连接后会同步个人与群聊会话"; ChatModeBadgeText.Text = "CRM LIVE SYNC"; ChatLabelButton.Visibility = Visibility.Collapsed; MessageList.ItemsSource = null; HideStatusUpdateBanner(); ClearLead(); return;
        }
        if (!_composerConversationId.Equals(conversation.Id, StringComparison.OrdinalIgnoreCase))
        {
            _composerConversationId = conversation.Id;
            ClearAttachment();
            ClearReply();
            ClearKnowledgeReferences(clearBoundComposer: true);
            ResetTranslationUi(conversation);
        }
        ++_customerBrainRefreshGeneration;
        ClearLead();
        if (IsVisible)
        {
            var hadUnread = conversation.Unread > 0;
            conversation.Unread = 0;
            conversation.LastReadAt = DateTimeOffset.Now;
            await _services.Repository.MarkWhatsAppConversationReadAsync(conversation.Id);
            if (!IsCurrentConversationSelection(selectionGeneration, conversation)) return;
            if (hadUnread) DataChanged?.Invoke(this, EventArgs.Empty);
        }
        ChatTitleText.Text = conversation.DisplayName;
        ChatLabelButton.Visibility = conversation.IsGroup ? Visibility.Collapsed : Visibility.Visible;
        ChatNumberText.Text = conversation.IsGroup
            ? "WhatsApp 群聊 · 实时同步与未读提醒 · CRM、Customer Brain 和自动回复已隔离"
            : string.IsNullOrWhiteSpace(conversation.Phone) ? "WhatsApp 尚未提供该联系人的电话号码" : $"+{conversation.Phone}";
        ChatModeBadgeText.Text = conversation.IsGroup ? "GROUP VIEW" : "CRM LIVE SYNC";
        var persistedMessages = conversation.IsGroup || !string.IsNullOrWhiteSpace(conversation.Phone)
            ? await _services.Repository.GetWhatsAppMessagesAsync(conversation.Id, 2000)
            : [];
        if (!IsCurrentConversationSelection(selectionGeneration, conversation)) return;
        foreach (var message in persistedMessages)
            if (!conversation.Messages.Any(x => x.Id == message.ProviderMessageId))
                conversation.Messages.Add(new MessageItem(message.ProviderMessageId, message.Body, message.Timestamp, message.Direction == WhatsAppMessageDirection.Outgoing, message.Kind, message.FileName, message.MimeType, message.MediaPath, message.MediaDownloadError, message.Status, message.StatusUpdatedAt, message.DeliveredAt, message.ReadAt, message.FailureReason, message.QuotedMessageId, message.QuotedText, message.QuotedFromMe, message.IsRevoked, message.RevokedAt, message.IsStatusUpdate, message.StatusExpiresAt, message.ParticipantName, message.IsGroup));
        MessageList.ItemsSource = VisibleMessages(conversation);
        if (_connected || _existingSession) { QrPanel.Visibility = Visibility.Collapsed; MessageList.Visibility = Visibility.Visible; }
        SaveLeadButton.IsEnabled = !conversation.IsGroup
                                   && !string.IsNullOrWhiteSpace(conversation.Phone)
                                   && FindOwnedPeerAccount(conversation.AccountId, conversation.Phone) is null;
        await LoadLeadAsync(conversation, selectionGeneration);
        if (!IsCurrentConversationSelection(selectionGeneration, conversation)) return;
        UpdateComposerState();
        UpdateStatusUpdateBanner(conversation);
        ScrollMessages(conversation);
        await LoadTranslationContextAsync(conversation);
    }

    private bool IsCurrentConversationSelection(int selectionGeneration, ConversationItem conversation) =>
        selectionGeneration == _conversationSelectionGeneration && IsCurrentConversation(conversation);

    private bool IsCurrentConversation(ConversationItem conversation) =>
        ConversationList.SelectedItem is ConversationItem current &&
        current.AccountId.Equals(conversation.AccountId, StringComparison.OrdinalIgnoreCase) &&
        current.Id.Equals(conversation.Id, StringComparison.OrdinalIgnoreCase) &&
        current.Phone.Equals(conversation.Phone, StringComparison.Ordinal) &&
        current.Jid.Equals(conversation.Jid, StringComparison.OrdinalIgnoreCase);

    private async Task<(bool IsCurrent, ConversationLeadBinding Binding)> ResolveCurrentConversationLeadAsync(
        ConversationItem conversation,
        int selectionGeneration)
    {
        var binding = await ResolveConversationLeadBindingAsync(conversation);
        return IsCurrentConversationSelection(selectionGeneration, conversation)
            ? (true, binding)
            : (false, ConversationLeadBinding.Unbound);
    }

    private async Task<ConversationLeadBinding> ResolveConversationLeadBindingAsync(ConversationItem conversation)
    {
        var identity = string.IsNullOrWhiteSpace(conversation.Phone)
            ? new CustomerIdentityResolution { Result = CustomerIdentityMatchResult.NoMatch }
            : await _services.CustomerIdentity.ResolveAsync(
                conversation.AccountId,
                conversation.Id,
                conversation.Phone,
                conversation.Jid,
                "",
                conversation.DisplayName);
        if (!identity.AllowsAutomation || string.IsNullOrWhiteSpace(identity.CustomerId))
            return ConversationLeadBinding.Unbound;
        var lead = await _services.Repository.GetLeadAsync(identity.CustomerId);
        if (lead is null) return ConversationLeadBinding.Unbound;
        var link = await _services.Repository.GetWhatsAppIdentityLinkAsync(
            conversation.AccountId,
            conversation.Id);
        var linkToken = link is { IsActive: true } &&
                        link.CustomerId.Equals(lead.Id, StringComparison.OrdinalIgnoreCase)
            ? string.Join("|",
                link.Id,
                link.CustomerId,
                link.ContactJid,
                link.ContactLid,
                link.PhoneIdentityId,
                link.MatchResult,
                link.MatchMethod,
                link.ManuallyConfirmed,
                link.UpdatedAt.ToUniversalTime().ToString("O"))
            : $"resolved|{identity.Result}|{identity.Method}|{lead.Id}";
        return new ConversationLeadBinding(lead.Id, linkToken, lead);
    }

    private async Task LoadLeadAsync(ConversationItem conversation, int? selectionGeneration = null)
    {
        bool IsCurrent() =>
            (!selectionGeneration.HasValue || selectionGeneration.Value == _conversationSelectionGeneration) &&
            IsCurrentConversation(conversation);

        if (!IsCurrent()) return;
        if (conversation.IsGroup)
        {
            _currentLead = null;
            _currentIdentityResolution = new CustomerIdentityResolution
            {
                Result = CustomerIdentityMatchResult.NoMatch,
                Reason = "群聊包含多个参与者，不自动绑定单一客户。"
            };
            _currentCustomerSuccessContext = null;
            LeadLinkStateText.Text = "群聊安全隔离：不关联单一客户，不触发 CRM/AI 自动化";
            NameBox.Clear(); OwnerBox.Clear(); OptInCheck.IsChecked = false;
            OptedOutCheck.IsChecked = false; NotesBox.Clear(); CustomFieldsBox.Clear();
            SaveLeadButton.IsEnabled = false;
            UpdateCustomerSuccessPanel(_currentIdentityResolution, null);
            UpdateLeadIntelligenceSummary(null);
            await UpdateCustomerBrainSummaryAsync(null);
            UpdateComposerState();
            return;
        }
        var identityResolution = string.IsNullOrWhiteSpace(conversation.Phone)
            ? new CustomerIdentityResolution { Result = CustomerIdentityMatchResult.NoMatch, Reason = "WhatsApp 尚未提供号码。" }
            : await _services.CustomerIdentity.ResolveAsync(
                conversation.AccountId, conversation.Id, conversation.Phone, conversation.Jid, "", conversation.DisplayName);
        if (!IsCurrent()) return;
        var lead = identityResolution.AllowsAutomation && !string.IsNullOrWhiteSpace(identityResolution.CustomerId)
            ? await _services.Repository.GetLeadAsync(identityResolution.CustomerId)
            : null;
        if (!IsCurrent()) return;
        var customerSuccessContext = lead is null
            ? null
            : await _services.CustomerSuccessAgent.GetContextAsync(conversation.AccountId, conversation.Id);
        if (!IsCurrent()) return;
        _currentIdentityResolution = identityResolution;
        _currentLead = lead;
        _currentCustomerSuccessContext = customerSuccessContext;
        if (_pendingAgentDraftContextToken is { } draftToken &&
            (lead is null ||
             !draftToken.CustomerId.Equals(lead.Id, StringComparison.OrdinalIgnoreCase) ||
             !draftToken.AccountId.Equals(conversation.AccountId, StringComparison.OrdinalIgnoreCase) ||
             !draftToken.ConversationId.Equals(conversation.Id, StringComparison.OrdinalIgnoreCase) ||
             customerSuccessContext?.AgentState?.PendingRunContextToken
                 .Equals(draftToken.RunToken, StringComparison.Ordinal) != true))
            ClearKnowledgeReferences(clearBoundComposer: true);
        var ownedPeer = FindOwnedPeerAccount(conversation.AccountId, conversation.Phone);
        LeadLinkStateText.Text = ownedPeer is not null
            ? $"WhatsApp：{conversation.DisplayName} · 本机账号互发，不关联 CRM"
            : _currentLead is null
                ? $"WhatsApp：{conversation.DisplayName} · {CustomerSuccessAgentLabels.Match(_currentIdentityResolution.Result)}"
                : $"WhatsApp：{conversation.DisplayName} · CRM：{_currentLead.DisplayName} · {_currentLead.Grade} 级";
        NameBox.Text = _currentLead?.Name ?? "";
        OwnerBox.Text = _currentLead?.Owner ?? "";
        OptInCheck.IsChecked = _currentLead?.WhatsAppOptIn == true;
        OptedOutCheck.IsChecked = _currentLead?.OptedOut == true;
        NotesBox.Text = _currentLead?.ManualNotes ?? "";
        CustomFieldsBox.Text = _currentLead is null ? "" : string.Join(Environment.NewLine, _currentLead.CustomFields.Select(x => $"{x.Key}={x.Value}"));
        StageCombo.SelectedItem = (StageCombo.ItemsSource as IEnumerable<StageOption>)?.FirstOrDefault(x => x.Value == (_currentLead?.Stage ?? LeadStage.New));
        UpdateLeadIntelligenceSummary(_currentLead);
        UpdateCustomerSuccessPanel(_currentIdentityResolution, _currentCustomerSuccessContext);
        await RefreshSourcingTaskPanelAsync();
        await UpdateCustomerBrainSummaryAsync(_currentLead);
    }

    private async void SaveLead_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationItem conversation) return;
        if (string.IsNullOrWhiteSpace(conversation.Phone)) { MessageBox.Show("WhatsApp 尚未向关联设备提供该联系人的电话号码，暂时不能创建客户。", "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            var lead = _currentLead ?? new Lead { PhoneE164 = "+" + conversation.Phone, PhoneValid = true, Source = "WhatsApp QR session" };
            lead.Name = NameBox.Text.Trim(); lead.Owner = OwnerBox.Text.Trim(); lead.ManualNotes = NotesBox.Text.Trim();
            var selectedStage = (StageCombo.SelectedItem as StageOption)?.Value ?? LeadStage.New;
            if (selectedStage != lead.Stage)
            {
                lead.Stage = selectedStage;
                lead.StageManuallyLocked = true;
                lead.StageSource = "user";
                lead.StageManuallyUpdatedAt = DateTimeOffset.Now;
            }
            var wasOptedIn = lead.WhatsAppOptIn;
            lead.WhatsAppOptIn = OptInCheck.IsChecked == true;
            if (!wasOptedIn && lead.WhatsAppOptIn)
            {
                lead.WhatsAppOptInAt = DateTimeOffset.Now;
                if (string.IsNullOrWhiteSpace(lead.WhatsAppOptInSource))
                    lead.WhatsAppOptInSource = "Customer Intelligence 人工确认";
            }
            if (!lead.WhatsAppOptIn)
            {
                lead.WhatsAppOptInAt = null;
                lead.WhatsAppOptInSource = "";
            }
            lead.OptedOut = OptedOutCheck.IsChecked == true;
            lead.CustomFields = ParseCustomFields(CustomFieldsBox.Text);
            await _services.Repository.UpsertLeadAsync(lead);
            await _services.CustomerIdentity.ConfirmBindingAsync(
                lead.Id, conversation.AccountId, conversation.Id, conversation.Phone,
                conversation.Jid, "", "inbox_sidebar");
            await _services.Repository.LogEventAsync("whatsapp_customer_sidebar_saved", lead.Id, null, "客户侧栏人工保存");
            _currentLead = lead;
            if (!_leads.Any(x => x.Id == lead.Id)) _leads.Add(lead);
            else
            {
                var index = _leads.FindIndex(x => x.Id == lead.Id);
                if (index >= 0) _leads[index] = lead;
            }
            await _services.Repository.SynchronizeLeadConnectionsFromInboxAsync([lead]);
            var latestReply = (await _services.Repository.GetWhatsAppMessagesForCustomerAsync(lead.Id, 40))
                .LastOrDefault(message => !message.IsStatusUpdate && message.Direction == WhatsAppMessageDirection.Incoming && !string.IsNullOrWhiteSpace(message.Body));
            if (latestReply is not null && (!lead.AiScoreApplied || lead.LastAnalyzedAt is null || latestReply.Timestamp > lead.LastAnalyzedAt))
                await _services.LeadAutomation.QueueLeadForReplyAsync(latestReply);
            conversation.LeadId = lead.Id;
            LeadLinkStateText.Text = $"已关联：{lead.Grade} 级 · {Labels.Stage(lead.Stage)}";
            UpdateLeadIntelligenceSummary(lead);
            _currentIdentityResolution = new CustomerIdentityResolution
            {
                Result = CustomerIdentityMatchResult.ExactMatch,
                Method = CustomerIdentityMatchMethod.ManualBinding,
                CustomerId = lead.Id,
                CandidateCustomerIds = [lead.Id],
                Confidence = 1,
                Reason = "用户已在 WhatsApp 明确确认绑定。"
            };
            _currentCustomerSuccessContext = await _services.CustomerSuccessAgent.GetContextAsync(conversation.AccountId, conversation.Id);
            UpdateCustomerSuccessPanel(_currentIdentityResolution, _currentCustomerSuccessContext);
            await RefreshSourcingTaskPanelAsync();
            await UpdateCustomerBrainSummaryAsync(lead);
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("客户资料已同步到 AI Sales OS。", "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async void AiAssistant_Click(object sender, RoutedEventArgs e) => await GenerateAgentSuggestionAsync();

    private async void GenerateAgentSuggestion_Click(object sender, RoutedEventArgs e) => await GenerateAgentSuggestionAsync();

    private async Task GenerateAgentSuggestionAsync()
    {
        if (_aiAssisting || ConversationList.SelectedItem is not ConversationItem conversation) return;
        var selectionGeneration = _conversationSelectionGeneration;
        var expectedPhone = conversation.Phone;
        var expectedCustomerId = _currentLead?.Id ?? "";
        bool IsCurrentTarget() =>
            IsCurrentConversationSelection(selectionGeneration, conversation) &&
            conversation.Phone.Equals(expectedPhone, StringComparison.Ordinal) &&
            string.Equals(_currentLead?.Id ?? "", expectedCustomerId, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(conversation.Phone))
        {
            MessageBox.Show("WhatsApp 尚未提供该联系人的电话号码，AI 暂时不能安全关联客户资料。", "AI 会话助理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_services.AiProvider.HasApiKey())
        {
            MessageBox.Show("请先打开左侧“设置”，填写 API Key 并选择工作模型。", "AI 会话助理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _aiAssisting = true;
        AiAssistantButton.Content = "分析中";
        GenerateAgentSuggestionButton.Content = "正在生成…";
        UpdateComposerState();
        try
        {
            var result = await _services.CustomerSuccessAgent.AnalyzeAsync(
                conversation.AccountId, conversation.Id, conversation.Phone,
                conversation.DisplayName, conversation.Jid);
            if (!IsCurrentTarget()) return;
            var resultCustomerId = result.ContextToken?.CustomerId ?? expectedCustomerId;
            if (!string.Equals(resultCustomerId, expectedCustomerId, StringComparison.OrdinalIgnoreCase)) return;
            if (result.Decision is null)
            {
                await LoadLeadAsync(conversation, selectionGeneration);
                if (!IsCurrentTarget()) return;
                MessageBox.Show(
                    string.IsNullOrWhiteSpace(result.BlockReason) ? "当前会话暂不允许 AI 自动处理。" : result.BlockReason,
                    "AI 协作助手", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (result.ContextToken is null)
                throw new InvalidOperationException("AI 草稿缺少可验证的客户上下文，请重新生成。");
            await _services.CustomerSuccessAgent.EnsureRunContextCurrentAsync(
                result.ContextToken,
                requireAutoLock: false,
                requireProcessedState: true);
            if (!IsCurrentTarget()) return;
            BindPendingAgentDraft(
                result.Decision,
                result.ContextToken,
                resultCustomerId,
                conversation.AccountId,
                conversation.Id);
            ComposerBox.Text = result.Decision.ReplyText;
            ComposerBox.CaretIndex = ComposerBox.Text.Length;
            ShowKnowledgeReferences(
                result.Decision,
                result.KnowledgeRetrieval);
            await LoadLeadAsync(conversation, selectionGeneration);
            if (!IsCurrentTarget()) return;
            DataChanged?.Invoke(this, EventArgs.Empty);
            ComposerBox.Focus();
        }
        catch (AiProviderException error)
        {
            try
            {
                await _services.CustomerSuccessAgent.UpdateRunOutcomeAsync(
                    conversation.AccountId,
                    conversation.Id,
                    CustomerSuccessRunStatus.Failed,
                    "建议生成未完成，运行状态已恢复，可以重新尝试。",
                    error: $"{error.Code}: {error.Message}");
                if (IsCurrentTarget()) await LoadLeadAsync(conversation, selectionGeneration);
            }
            catch { /* 保留原始 AI 错误，不让状态刷新错误覆盖它。 */ }
            if (!IsCurrentTarget()) return;
            MessageBox.Show(AgentErrorMessage(error), "AI 会话助理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            if (!IsCurrentTarget()) return;
            MessageBox.Show(error.Message, "AI 会话助理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _aiAssisting = false;
            GenerateAgentSuggestionButton.Content = _currentCustomerSuccessContext?.AgentState?.LastRunStatus == CustomerSuccessRunStatus.None
                ? "立即生成建议"
                : "重新生成建议";
            UpdateComposerState();
        }
    }

    private static string AgentErrorMessage(AiProviderException error) => error.Code switch
    {
        "provider_not_configured" or "model_not_selected" or "provider_unauthorized" => error.Message,
        "provider_timeout" or "provider_unavailable" or "provider_rate_limited" =>
            $"{error.Message}\n\n系统已恢复，可以稍后点击“重新生成建议”。",
        _ => "本次建议没有生成完成，系统已恢复且未修改客户资料。\n\n请点击“重新生成建议”再次尝试。"
    };

    private void ReplyMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not MessageItem { FromMe: false, IsRevoked: false } message) return;
        _replyingTo = message;
        ReplyText.Text = message.DisplayText;
        ReplyPanel.Visibility = Visibility.Visible;
        ComposerBox.Focus();
        ComposerBox.CaretIndex = ComposerBox.Text.Length;
    }

    private void ClearReply_Click(object sender, RoutedEventArgs e) => ClearReply();

    private void ClearReply()
    {
        _replyingTo = null;
        ReplyText.Text = "";
        ReplyPanel.Visibility = Visibility.Collapsed;
    }

    private async void RevokeMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not MessageItem message || !message.CanRevoke) return;
        if (ConversationList.SelectedItem is not ConversationItem conversation || !conversation.Messages.Contains(message)) return;
        if (!_connected)
        {
            MessageBox.Show("WhatsApp 当前未连接，无法撤回消息。", "撤回消息", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirmed = MessageBox.Show(
            "确定要从自己和对方的设备上撤回这条消息吗？\n\nWhatsApp 可能因消息时限或设备状态拒绝撤回，软件只会在收到成功回执后更新本地状态。",
            "从双方设备撤回",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes) return;

        message.SetRevoking(true);
        try
        {
            await _services.WhatsApp.RevokeMessageAsync(conversation.AccountId, conversation.Phone, message.Id);
            var revokedAt = DateTimeOffset.Now;
            message.MarkRevoked(revokedAt);
            await _services.Repository.MarkWhatsAppMessageRevokedAsync(conversation.AccountId, message.Id, revokedAt);
            if (ReferenceEquals(_replyingTo, message)) ClearReply();
            if (ReferenceEquals(conversation.Messages.LastOrDefault(), message))
            {
                conversation.LastMessage = "你撤回了一条消息";
                var storedConversation = await _services.Repository.GetWhatsAppConversationAsync(conversation.AccountId, conversation.Phone);
                if (storedConversation is not null)
                {
                    storedConversation.LastMessage = conversation.LastMessage;
                    await _services.Repository.UpsertWhatsAppConversationAsync(storedConversation);
                }
            }
        }
        catch (TimeoutException)
        {
            MessageBox.Show("撤回请求已发出，但本机没有及时收到 WhatsApp 回执。消息暂不标记为已撤回，请先同步会话确认实际状态。", "撤回状态待确认", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            MessageBox.Show($"WhatsApp 未确认撤回：{error.Message}\n\n消息仍保留在本地；请检查是否超过 WhatsApp 允许的撤回时限。", "撤回失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { message.SetRevoking(false); }
    }

    private async void ComposerBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var start = ComposerBox.SelectionStart;
            var length = ComposerBox.SelectionLength;
            ComposerBox.Text = ComposerBox.Text.Remove(start, length).Insert(start, Environment.NewLine);
            ComposerBox.CaretIndex = start + Environment.NewLine.Length;
            return;
        }
        await SendCurrentAsync();
    }

    private void ComposerBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_pendingAgentDraftContextToken is not null && string.IsNullOrWhiteSpace(ComposerBox.Text))
            ClearKnowledgeReferences(clearBoundComposer: true);
        if (!_applyingTranslatedDraft &&
            _translatedDraftApplied &&
            !string.Equals(ComposerBox.Text.Trim(), _draftTranslationText, StringComparison.Ordinal))
            _translatedDraftApplied = false;
        UpdateComposerState();
    }

    private async void TranslateConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_translationBusy || ConversationList.SelectedItem is not ConversationItem conversation) return;
        var cts = StartTranslationRun();
        _translationBusy = true;
        UpdateTranslationControls();
        TranslationStatusText.Text = "正在翻译最近消息…原文始终保留，完成后显示双语。";
        try
        {
            var translations = await _services.WhatsAppTranslation.TranslateRecentMessagesAsync(
                conversation.Id,
                cancellationToken: cts.Token);
            if (!IsCurrentTranslationRun(cts, conversation.Id)) return;
            ApplyTranslations(conversation, translations);
            ScrollMessages(conversation);
            TranslationStatusText.Text = translations.Count == 0
                ? "当前没有可翻译的文字消息。"
                : $"已刷新最近 {translations.Count} 条双语消息 · {DateTime.Now:HH:mm:ss} · 译文已缓存在本机";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrentTranslationRun(cts, conversation.Id))
                TranslationStatusText.Text = $"翻译未完成：{FriendlyTranslationError(error)}";
        }
        finally
        {
            CompleteTranslationRun(cts);
        }
    }

    private async void TranslateDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_translationBusy || ConversationList.SelectedItem is not ConversationItem conversation) return;
        var source = ComposerBox.Text.Trim();
        if (source.Length == 0) return;
        var cts = StartTranslationRun();
        _translationBusy = true;
        UpdateTranslationControls();
        OutgoingTranslationPanel.Visibility = Visibility.Visible;
        OutgoingTranslationLabel.Text = "正在翻译 · 只生成预览，不会自动发送";
        OutgoingTranslationText.Text = "请稍候…";
        try
        {
            var translation = await _services.WhatsAppTranslation.TranslateOutgoingAsync(
                conversation.Id,
                source,
                cts.Token);
            if (!IsCurrentTranslationRun(cts, conversation.Id)) return;
            _draftTranslationOriginal = source;
            _draftTranslationText = translation.TranslatedText;
            _translatedDraftApplied = false;
            OutgoingTranslationLabel.Text = $"发送预览 · {translation.TargetLanguageName}";
            OutgoingTranslationText.Text = translation.TranslatedText;
            TranslationStatusText.Text = $"译文已生成 · {translation.Model} · 采用后仍需手动点击发送";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrentTranslationRun(cts, conversation.Id))
            {
                _draftTranslationOriginal = "";
                _draftTranslationText = "";
                OutgoingTranslationLabel.Text = "翻译未完成";
                OutgoingTranslationText.Text = FriendlyTranslationError(error);
            }
        }
        finally
        {
            CompleteTranslationRun(cts);
        }
    }

    private void UseOutgoingTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_draftTranslationText)) return;
        _applyingTranslatedDraft = true;
        ComposerBox.Text = _draftTranslationText;
        ComposerBox.CaretIndex = ComposerBox.Text.Length;
        _applyingTranslatedDraft = false;
        _translatedDraftApplied = true;
        OutgoingTranslationPanel.Visibility = Visibility.Collapsed;
        ComposerBox.Focus();
        UpdateComposerState();
    }

    private void CancelOutgoingTranslation_Click(object sender, RoutedEventArgs e) =>
        ClearOutgoingTranslation();

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要通过 WhatsApp 发送的文件",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "WhatsApp 支持的文件|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.mp4;*.3gp;*.mov;*.mp3;*.m4a;*.ogg;*.opus;*.wav;*.aac;*.pdf;*.txt;*.csv;*.json;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.zip;*.rar;*.7z|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        var file = new FileInfo(dialog.FileName);
        if (file.Length <= 0 || file.Length > 100L * 1024 * 1024)
        {
            MessageBox.Show("附件大小必须大于 0 且不超过 100MB。", "WhatsApp 附件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _attachmentPath = file.FullName;
        AttachmentText.Text = $"{file.Name} · {file.Length / 1024d / 1024d:N1} MB";
        AttachmentPanel.Visibility = Visibility.Visible;
        UpdateComposerState();
    }

    private void ClearAttachment_Click(object sender, RoutedEventArgs e) => ClearAttachment();

    private void ClearAttachment()
    {
        _attachmentPath = "";
        AttachmentText.Text = "";
        AttachmentPanel.Visibility = Visibility.Collapsed;
        UpdateComposerState();
    }

    private async Task<bool> SendCurrentAsync(string origin = "human")
    {
        if (_sending || ConversationList.SelectedItem is not ConversationItem conversation) return false;
        var selectionGeneration = _conversationSelectionGeneration;
        var text = ComposerBox.Text.Trim();
        var attachmentPath = _attachmentPath;
        var reply = _replyingTo;
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(attachmentPath)) return false;
        if (string.IsNullOrWhiteSpace(conversation.Phone)) { MessageBox.Show("该联系人的电话号码尚未同步，暂时不能发送。", "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

        _sending = true;
        var accepted = false;
        UpdateComposerState();
        MessageItem? pendingMessage = null;
        try
        {
            var resolved = await ResolveCurrentConversationLeadAsync(conversation, selectionGeneration);
            if (!resolved.IsCurrent) return false;
            var sendBinding = resolved.Binding;
            var sendLead = sendBinding.Lead;
            if (sendLead?.OptedOut == true)
            {
                MessageBox.Show("客户已退订，禁止发送。", "WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var draftContextToken = _pendingAgentDraftContextToken;
            var knowledgeDecision = _pendingKnowledgeDecision;
            if (draftContextToken is not null &&
                !await EnsurePendingAgentDraftCurrentAsync(conversation, sendBinding, draftContextToken))
            {
                return false;
            }
            if (draftContextToken is null) knowledgeDecision = null;
            var effectiveOrigin = draftContextToken is not null
                ? knowledgeDecision?.KnowledgeCitations.Count > 0
                    ? "ai_knowledge_assisted"
                    : "ai_conversation_assistant"
                : _translatedDraftApplied &&
                  string.Equals(text, _draftTranslationText, StringComparison.Ordinal)
                    ? "human_translated"
                    : origin;
            var persistenceContextToken = draftContextToken;
            if (!string.IsNullOrWhiteSpace(sendBinding.CustomerId))
            {
                // The local user owns the conversation before the bridge sees
                // the message. This closes the race where an in-flight Agent
                // draft could otherwise send after a desktop manual reply.
                await _services.CustomerSuccessAgent.HumanTakeoverAsync(
                    sendBinding.CustomerId,
                    conversation.AccountId,
                    conversation.Id,
                    "desktop_user",
                    "",
                    "桌面端用户准备发送人工消息；已在外发前取消旧草稿并停止当前托管。" );
                // The takeover intentionally invalidates the Agent run token.
                // Customer attribution remains protected by the fresh identity
                // binding token captured above, so do not treat that expected
                // invalidation as a cross-customer context failure.
                persistenceContextToken = null;
            }
            var pendingId = $"local-{Guid.NewGuid():N}";
            var pendingTimestamp = DateTimeOffset.Now;
            var pendingKind = string.IsNullOrWhiteSpace(attachmentPath) ? "text" : KindFromFileName(attachmentPath);
            var pendingFileName = string.IsNullOrWhiteSpace(attachmentPath) ? "" : Path.GetFileName(attachmentPath);
            pendingMessage = new MessageItem(pendingId, text, pendingTimestamp, true, pendingKind, pendingFileName, "", attachmentPath, "", WhatsAppMessageStatus.Pending, pendingTimestamp, null, null, "", reply?.Id ?? "", reply?.DisplayText ?? "", reply?.FromMe ?? false);
            conversation.Messages.Add(pendingMessage);
            conversation.LastMessage = MessagePreview(text, pendingKind, pendingFileName);
            conversation.LastAt = pendingTimestamp;
            ReorderConversations(conversation);
            if (IsCurrentConversationSelection(selectionGeneration, conversation)) ScrollMessages(conversation);

            JsonElement result;
            if (string.IsNullOrWhiteSpace(attachmentPath))
                result = reply is null
                    ? await _services.WhatsApp.SendTextAsync(conversation.AccountId, conversation.Phone, text)
                    : await _services.WhatsApp.SendReplyTextAsync(conversation.AccountId, conversation.Phone, text, reply.Id, reply.DisplayText, reply.FromMe);
            else
                result = reply is null
                    ? await _services.WhatsApp.SendMediaAsync(conversation.AccountId, conversation.Phone, attachmentPath, text)
                    : await _services.WhatsApp.SendReplyMediaAsync(conversation.AccountId, conversation.Phone, attachmentPath, text, reply.Id, reply.DisplayText, reply.FromMe);
            var id = result.TryGetProperty("id", out var idElement) ? idElement.GetString()?.Trim() ?? "" : "";
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("WhatsApp 未返回服务端消息 ID，消息未确认发出。");
            if (!Bool(result, "targetVerified"))
                throw new InvalidOperationException("WhatsApp 未确认目标联系人，消息未发出。");
            var timestamp = result.TryGetProperty("timestamp", out var timestampElement) && DateTimeOffset.TryParse(timestampElement.GetString(), out var parsedTimestamp) ? parsedTimestamp : DateTimeOffset.Now;
            var kind = result.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() ?? "text" : "text";
            var fileName = result.TryGetProperty("fileName", out var fileNameElement) ? fileNameElement.GetString() ?? "" : "";
            var mimeType = result.TryGetProperty("mimeType", out var mimeElement) ? mimeElement.GetString() ?? "" : "";
            var numericStatus = result.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var parsedStatus) ? parsedStatus : 1;
            var status = StatusFromNumeric(numericStatus);
            var existing = conversation.Messages.FirstOrDefault(item => item.Id == id && !ReferenceEquals(item, pendingMessage));
            if (existing is not null)
            {
                conversation.Messages.Remove(pendingMessage);
                pendingMessage = existing;
            }
            else pendingMessage.UpdateTransport(id, timestamp, kind, fileName);
            pendingMessage.UpdateStatus(status, DateTimeOffset.Now, status is WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read ? DateTimeOffset.Now : null, status == WhatsAppMessageStatus.Read ? DateTimeOffset.Now : null, "");
            conversation.LastMessage = MessagePreview(text, kind, fileName);
            conversation.LastAt = timestamp;
            if (IsCurrentConversationSelection(selectionGeneration, conversation))
            {
                ComposerBox.Clear();
                ClearOutgoingTranslation();
                ClearAttachment();
                ClearReply();
            }

            var confirmedByServer = status is WhatsAppMessageStatus.Sent or WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read;
            var sourceContextCurrent = persistenceContextToken is null ||
                                       await IsAgentDraftContextCurrentAsync(persistenceContextToken);
            var proposedConversation = new WhatsAppConversation
            {
                Id = conversation.Id,
                AccountId = conversation.AccountId,
                Jid = conversation.Jid,
                Phone = conversation.Phone,
                DisplayName = conversation.DisplayName,
                LastMessage = conversation.LastMessage,
                LastMessageAt = timestamp,
                UnreadCount = conversation.Unread,
                LastReadAt = conversation.LastReadAt,
                IsPinned = conversation.IsPinned,
                PinnedAt = conversation.PinnedAt
            };
            var storedMessage = new WhatsAppMessage
            {
                Id = $"{conversation.AccountId}:{id}", ProviderMessageId = id, AccountId = conversation.AccountId,
                ConversationId = conversation.Id, LeadId = "", Jid = conversation.Jid, Phone = conversation.Phone,
                Direction = WhatsAppMessageDirection.Outgoing, Status = status, Kind = kind,
                Body = text, FileName = fileName, MimeType = mimeType, MediaPath = attachmentPath, Timestamp = timestamp,
                QuotedMessageId = reply?.Id ?? "", QuotedText = reply?.DisplayText ?? "", QuotedFromMe = reply?.FromMe ?? false,
                StatusUpdatedAt = DateTimeOffset.Now,
                DeliveredAt = status is WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read ? DateTimeOffset.Now : null,
                ReadAt = status == WhatsAppMessageStatus.Read ? DateTimeOffset.Now : null,
                Source = effectiveOrigin switch
                {
                    "ai_conversation_assistant" or "ai_knowledge_assisted" => "desktop_ai",
                    "human_translated" => "desktop_translated",
                    _ => "desktop"
                }
            };
            var commit = await _services.Repository.PersistAcknowledgedOutgoingWhatsAppAsync(
                proposedConversation,
                storedMessage,
                sendBinding.CustomerId,
                sendBinding.BindingToken,
                sourceContextCurrent,
                confirmedByServer,
                expectedCustomerIdentityHash: persistenceContextToken?.CustomerIdentityHash ?? "",
                expectedActiveFactSetToken: persistenceContextToken?.ActiveFactSetToken ?? "",
                expectedRunContextToken: persistenceContextToken?.RunToken ?? "",
                expectedConversationTargetToken: persistenceContextToken?.ConversationTargetToken ?? "",
                expectedSourceMessageId: persistenceContextToken?.SourceMessageId ?? "",
                expectedSourceMessageToken: persistenceContextToken?.SourceMessageToken ?? "");
            storedMessage = commit.Message;
            // From this point the transport ACK and local message commit are durable.
            // Follow-up analytics are best effort and must never turn a confirmed send
            // into a retryable-looking failure that could make the user send twice.
            accepted = true;
            try
            {
                var attributedLead = string.IsNullOrWhiteSpace(commit.AttributedCustomerId)
                    ? null
                    : await _services.Repository.GetLeadAsync(commit.AttributedCustomerId);
                ReorderConversations(conversation);
                if (IsCurrentConversationSelection(selectionGeneration, conversation)) ScrollMessages(conversation);
                if (confirmedByServer && attributedLead is not null)
                {
                    await _services.Repository.LogEventAsync("whatsapp_message_sent", attributedLead.Id, null, $"message_id={id}; kind={kind}; origin={effectiveOrigin}");
                    if (effectiveOrigin is "ai_conversation_assistant" or "ai_knowledge_assisted")
                    {
                        await _services.CustomerActions.RecordMessageExecutionAsync(
                            attributedLead.Id,
                            "WhatsApp",
                            text,
                            $"whatsapp-{conversation.AccountId}-{id}",
                            timestamp);
                    }
                    if (knowledgeDecision is not null)
                    {
                        foreach (var citation in knowledgeDecision.KnowledgeCitations)
                        {
                            await _services.Repository.SaveKnowledgeUsageOutcomeAsync(new KnowledgeUsageOutcome
                            {
                                Id = $"{conversation.AccountId}:{id}:{citation.ChunkId}",
                                RetrievalLogId = knowledgeDecision.KnowledgeRetrievalId,
                                ChunkId = citation.ChunkId,
                                CustomerId = attributedLead.Id,
                                SourceMessageId = id,
                                ActuallySent = true,
                                ObservationNote = "用户人工确认并发送知识辅助回复；回复、阶段推进、成交和复购仍需后续真实观察。"
                            });
                        }
                    }
                    if (IsCurrentConversationSelection(selectionGeneration, conversation)) _currentLead = attributedLead;
                }
                else if (confirmedByServer)
                {
                    await _services.Repository.LogEventAsync(
                        "whatsapp_message_sent_unattributed",
                        null,
                        null,
                        $"message_id={id}; kind={kind}; origin={effectiveOrigin}; context_changed={commit.ContextChanged}; reason={commit.ContextChangeReason}");
                    if (commit.ContextChanged && IsCurrentConversationSelection(selectionGeneration, conversation))
                        MessageBox.Show(
                            "WhatsApp 已确认发送；发送期间客户身份或外部调查事实发生变化，本地消息已按未关联保存。请勿重复发送，请刷新会话后再继续。",
                            "消息已发送 · 请勿重试",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                }
            }
            catch (Exception postProcessError)
            {
                if (IsCurrentConversationSelection(selectionGeneration, conversation))
                    MessageBox.Show(
                        $"WhatsApp 已确认发送并保存，后续分析记录暂未完成：{postProcessError.Message}\n\n请勿重复发送；刷新后可继续处理。",
                        "消息已发送 · 后处理待恢复",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
            }
            if (draftContextToken is not null && ReferenceEquals(_pendingAgentDraftContextToken, draftContextToken))
                ClearPendingKnowledgeDecision();
        }
        catch (TimeoutException)
        {
            pendingMessage?.UpdateStatus(WhatsAppMessageStatus.Pending, DateTimeOffset.Now, null, null, "等待 WhatsApp 回执，发送状态待确认");
            if (IsCurrentConversationSelection(selectionGeneration, conversation))
                MessageBox.Show("尚未收到 WhatsApp 服务端确认，消息保持待确认状态，不会显示为已发送。请等待会话同步后再决定是否重试。", "发送状态待确认", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            pendingMessage?.UpdateStatus(WhatsAppMessageStatus.Failed, DateTimeOffset.Now, null, null, error.Message);
            if (IsCurrentConversationSelection(selectionGeneration, conversation))
                MessageBox.Show(error.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _sending = false; UpdateComposerState(); }
        return accepted;
    }

    private void ConversationSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyConversationFilter();

    private void ApplyConversationFilter()
    {
        var query = ConversationSearchBox.Text.Trim();
        var selectedLabel = LabelFilterCombo.SelectedItem as LabelFilterOption;
        IEnumerable<ConversationItem> visible = _conversations;
        if (query.Length > 0)
            visible = visible.Where(x => x.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) || x.Phone.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Jid.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(selectedLabel?.Id))
            visible = visible.Where(x => x.Labels.Any(label => label.Id.Equals(selectedLabel.Id, StringComparison.OrdinalIgnoreCase)));
        ConversationList.ItemsSource = visible.ToList();
        var filtered = query.Length > 0 || !string.IsNullOrWhiteSpace(selectedLabel?.Id);
        ConversationCountText.Text = filtered ? $"找到 {ConversationList.Items.Count} 个" : $"{_persistedConversationCount} 会话 · {_contactCount} 联系人";
    }

    private async Task RefreshConversationLabelsAsync()
    {
        var accountId = CurrentAccountId;
        var labels = await _services.Repository.GetWhatsAppLabelsAsync(accountId);
        var labelsById = labels.ToDictionary(label => label.Id, StringComparer.OrdinalIgnoreCase);
        var previousId = (LabelFilterCombo.SelectedItem as LabelFilterOption)?.Id ?? "";
        LabelFilterCombo.Items.Clear();
        LabelFilterCombo.Items.Add(new LabelFilterOption("", "全部标签"));
        foreach (var label in labels) LabelFilterCombo.Items.Add(new LabelFilterOption(label.Id, label.Name));
        LabelFilterCombo.SelectedItem = LabelFilterCombo.Items.Cast<LabelFilterOption>()
            .FirstOrDefault(item => item.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase));
        if (LabelFilterCombo.SelectedItem is null) LabelFilterCombo.SelectedIndex = 0;

        var assignments = await _services.Repository.GetWhatsAppLabelsByChatIdsAsync(accountId, _conversations.Select(item => item.Phone));
        foreach (var conversation in _conversations)
        {
            var chips = assignments.TryGetValue(conversation.Phone, out var assigned)
                ? assigned.Where(label => labelsById.ContainsKey(label.Id)).Select(WhatsAppLabelChip.From)
                : [];
            conversation.SetLabels(chips);
        }
    }

    private void LabelFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingAccount) return;
        ApplyConversationFilter();
    }

    private async void ChatLabelButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationItem conversation || conversation.IsGroup) return;
        try
        {
            var window = new LabelManagerWindow(_services, conversation.AccountId, conversation.Phone, conversation.DisplayName)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            await RefreshConversationLabelsAsync();
            ApplyConversationFilter();
        }
        catch (Exception error)
        {
            MessageBox.Show($"无法打开标签管理：{error.Message}", "WhatsApp 标签", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ConversationList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ConversationList, e.OriginalSource as DependencyObject) is ListBoxItem item)
        {
            item.IsSelected = true;
            if (item.DataContext is ConversationItem conversation)
            {
                var action = new MenuItem { Header = conversation.PinActionLabel, CommandParameter = conversation, IsEnabled = !conversation.IsGroup };
                action.Click += PinConversation_Click;
                item.ContextMenu = new ContextMenu();
                item.ContextMenu.Items.Add(action);
            }
        }
    }

    private async void PinConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: ConversationItem conversation }) return;
        if (!_connected)
        {
            MessageBox.Show("请先连接 WhatsApp，再同步置顶状态。", "WhatsApp 置顶", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var pinned = !conversation.IsPinned;
        try
        {
            await _services.WhatsApp.SetChatPinnedAsync(conversation.AccountId, conversation.Phone, pinned);
            conversation.IsPinned = pinned;
            conversation.PinnedAt = pinned ? DateTimeOffset.Now : null;
            var stored = await _services.Repository.GetWhatsAppConversationAsync(conversation.AccountId, conversation.Phone) ?? new WhatsAppConversation
            {
                Id = conversation.Id, AccountId = conversation.AccountId, Phone = conversation.Phone, DisplayName = conversation.DisplayName
            };
            stored.IsPinned = conversation.IsPinned;
            stored.PinnedAt = conversation.PinnedAt;
            await _services.Repository.UpsertWhatsAppConversationAsync(stored);
            ReorderConversations(conversation);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "置顶同步失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await StartSyncAsync(showError: true);

    private async Task StartSyncAsync(bool showError)
    {
        if (!_connected) return;
        SyncButton.IsEnabled = false;
        SyncStatusText.Text = "正在重新连接并补齐离线期间的新消息…";
        try
        {
            var persisted = await _services.Repository.GetWhatsAppConversationsAsync(CurrentAccountId);
            var cursors = persisted.Select(item => new WhatsAppHistoryCursor(
                item.Jid,
                item.Phone,
                item.IsGroup,
                item.LastMessageAt,
                item.UnreadCount)).ToArray();
            await _services.WhatsApp.CatchUpHistoryAsync(CurrentAccountId, cursors);
        }
        catch (Exception error)
        {
            SyncStatusText.Text = "同步启动失败";
            if (showError) MessageBox.Show(error.Message, "WhatsApp 同步失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SyncButton.IsEnabled = _connected; }
    }

    private Lead? FindLead(string phone) => FindLead(_leads, _accounts, CurrentAccountId, phone);

    private static Lead? FindLead(
        IReadOnlyList<Lead> leads,
        IReadOnlyList<WhatsAppAccount> accounts,
        string accountId,
        string phone) =>
        FindOwnedPeerAccount(accounts, accountId, phone) is null
            ? PhoneIdentity.FindUniqueLead(leads, phone)
            : null;

    private WhatsAppAccount? FindOwnedPeerAccount(string accountId, string phone)
        => FindOwnedPeerAccount(_accounts, accountId, phone);

    private static WhatsAppAccount? FindOwnedPeerAccount(
        IReadOnlyList<WhatsAppAccount> accounts,
        string accountId,
        string phone)
    {
        var digits = PhoneIdentity.Digits(phone);
        if (digits.Length == 0) return null;
        return accounts.FirstOrDefault(account =>
            PhoneIdentity.Digits(account.LinkedPhone).Equals(digits, StringComparison.Ordinal));
    }

    private static string BestContactName(WhatsAppContact contact) => new[]
    {
        contact.SavedName, contact.DisplayName, contact.NotifyName, contact.VerifiedName, contact.Username,
        string.IsNullOrWhiteSpace(contact.Phone) ? contact.Jid : $"+{contact.Phone}"
    }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "WhatsApp 联系人";

    private static IOrderedEnumerable<ConversationItem> OrderConversations(IEnumerable<ConversationItem> source) => source
        .OrderByDescending(item => item.IsPinned)
        .ThenByDescending(item => item.IsPinned ? item.PinnedAt ?? item.LastAt : item.LastAt)
        .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    private void ReorderConversations(ConversationItem selected)
    {
        var ordered = OrderConversations(_conversations).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var current = _conversations.IndexOf(ordered[index]);
            if (current != index) _conversations.Move(current, index);
        }
        ApplyConversationFilter();
        ConversationList.SelectedItem = selected;
    }

    private static string MessagePreview(string text, string kind, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(text)) return text;
        var type = kind switch
        {
            "image" => "图片",
            "video" => "视频",
            "audio" => "音频",
            "document" => "文件",
            "sticker" => "贴图",
            "contact" => "联系人",
            "location" => "位置",
            "poll" => "投票",
            "reaction" => "表情回应",
            "event" => "活动",
            "unavailable" => "正在从手机恢复消息内容",
            "unknown" => "消息内容未同步成功",
            _ => "暂不支持的 WhatsApp 消息"
        };
        return string.IsNullOrWhiteSpace(fileName) ? $"[{type}]" : $"[{type}] {fileName}";
    }

    private void UpdateComposerState()
    {
        var available = _connected && ConversationList.SelectedItem is ConversationItem { Phone.Length: > 0 } && !_sending;
        var groupSelected = ConversationList.SelectedItem is ConversationItem { IsGroup: true };
        var agentState = _currentCustomerSuccessContext?.AgentState;
        var canGenerate = agentState is not null &&
                          (agentState.Mode == ConversationAgentMode.SuggestOnly ||
                           agentState.Mode == ConversationAgentMode.CopilotActive &&
                           agentState.RunState == ConversationAgentRunState.CollabActive) &&
                          ConversationList.SelectedItem is ConversationItem { Phone.Length: > 0 } &&
                          !_sending && !_aiAssisting;
        ComposerBox.IsEnabled = available;
        ComposerBox.ToolTip = groupSelected
            ? "群聊当前为只读同步；不会进入 CRM、Customer Brain 或自动回复。"
            : "Enter 发送，Ctrl+Enter 换行";
        AttachButton.IsEnabled = available;
        SendButton.IsEnabled = available && (!string.IsNullOrWhiteSpace(ComposerBox.Text) || !string.IsNullOrWhiteSpace(_attachmentPath));
        TranslateDraftButton.IsEnabled = available &&
                                         !_translationBusy &&
                                         !string.IsNullOrWhiteSpace(ComposerBox.Text) &&
                                         !string.IsNullOrWhiteSpace(_translationProfile?.CustomerLanguageCode);
        TranslateConversationButton.IsEnabled = ConversationList.SelectedItem is ConversationItem &&
                                                 !_translationBusy &&
                                                 !string.IsNullOrWhiteSpace(_translationProfile?.CustomerLanguageCode);
        AiAssistantButton.IsEnabled = agentState is not null &&
                                      agentState.RunState != ConversationAgentRunState.AutoPreflight &&
                                      !_sending && !_aiAssisting;
        GenerateAgentSuggestionButton.IsEnabled = canGenerate;
        RefreshAgentPrimaryButton();
    }

    private void ResetTranslationUi(ConversationItem? conversation = null)
    {
        _translationContextCts?.Cancel();
        _translationContextCts?.Dispose();
        _translationContextCts = null;
        _translationRunCts?.Cancel();
        _translationRunCts?.Dispose();
        _translationRunCts = null;
        _translationProfile = null;
        _translationBusy = false;
        ClearOutgoingTranslation();
        if (conversation is null)
        {
            TranslationBar.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var message in conversation.Messages) message.ClearTranslation();
        TranslationBar.Visibility = Visibility.Visible;
        TranslationRouteText.Text = "本机语言 ⇄ 客户主流语言";
        TranslationStatusText.Text = "正在读取 Windows 语言并识别本会话主流语言…";
        UpdateTranslationControls();
    }

    private async Task LoadTranslationContextAsync(ConversationItem conversation)
    {
        var cts = new CancellationTokenSource();
        _translationContextCts?.Cancel();
        _translationContextCts?.Dispose();
        _translationContextCts = cts;
        try
        {
            var context = await _services.WhatsAppTranslation.GetContextAsync(
                conversation.Id,
                cancellationToken: cts.Token);
            if (cts.IsCancellationRequested ||
                ConversationList.SelectedItem is not ConversationItem current ||
                !current.Id.Equals(conversation.Id, StringComparison.OrdinalIgnoreCase))
                return;
            _translationProfile = context.Profile;
            TranslationRouteText.Text = $"{context.Profile.LocalLanguageName} ⇄ {context.Profile.CustomerLanguageName}";
            TranslationStatusText.Text = context.Profile.SampleCount == 0
                ? "等待客户发来文字消息后自动识别语言"
                : $"基于 {context.Profile.SampleCount} 条客户消息 · 置信度 {context.Profile.Confidence:P0} · 已缓存译文不重复消耗 Token";
            ApplyTranslations(conversation, context.CachedTranslations);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (cts.IsCancellationRequested || !IsCurrentConversation(conversation)) return;
            _translationProfile = null;
            TranslationRouteText.Text = "本机语言 ⇄ 客户主流语言";
            TranslationStatusText.Text = $"语言识别未完成：{FriendlyTranslationError(error)}";
        }
        finally
        {
            if (ReferenceEquals(_translationContextCts, cts))
            {
                _translationContextCts = null;
                cts.Dispose();
                UpdateTranslationControls();
            }
        }
    }

    private void ApplyTranslations(
        ConversationItem conversation,
        IEnumerable<WhatsAppMessageTranslation> translations)
    {
        var byId = translations
            .GroupBy(item => item.MessageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);
        foreach (var message in conversation.Messages)
        {
            if (byId.TryGetValue(message.Id, out var translation) &&
                !string.Equals(message.Text.Trim(), translation.TranslatedText.Trim(), StringComparison.OrdinalIgnoreCase))
                message.SetTranslation(translation.TranslatedText, translation.TargetLanguageName);
            else
                message.ClearTranslation();
        }
    }

    private void UpdateTranslationControls()
    {
        TranslateConversationButton.Content = _translationBusy ? "翻译中…" : "翻译最近消息";
        UpdateComposerState();
    }

    private CancellationTokenSource StartTranslationRun()
    {
        _translationRunCts?.Cancel();
        _translationRunCts?.Dispose();
        _translationRunCts = new CancellationTokenSource();
        return _translationRunCts;
    }

    private bool IsCurrentTranslationRun(CancellationTokenSource cts, string conversationId) =>
        !cts.IsCancellationRequested &&
        ReferenceEquals(_translationRunCts, cts) &&
        ConversationList.SelectedItem is ConversationItem current &&
        current.Id.Equals(conversationId, StringComparison.OrdinalIgnoreCase);

    private void CompleteTranslationRun(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_translationRunCts, cts)) return;
        _translationRunCts = null;
        cts.Dispose();
        _translationBusy = false;
        UpdateTranslationControls();
    }

    private void ClearOutgoingTranslation()
    {
        _draftTranslationOriginal = "";
        _draftTranslationText = "";
        _translatedDraftApplied = false;
        if (OutgoingTranslationPanel is not null) OutgoingTranslationPanel.Visibility = Visibility.Collapsed;
        if (OutgoingTranslationText is not null) OutgoingTranslationText.Text = "";
    }

    private static string FriendlyTranslationError(Exception error) =>
        error is AiProviderException providerError
            ? providerError.Message
            : error.Message.Length <= 120 ? error.Message : error.Message[..120] + "…";

    private static Dictionary<string, string> ParseCustomFields(string text)
    {
        var output = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var line in text.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim(); var value = line[(separator + 1)..].Trim();
            if (key.Length > 0) output[key] = value;
        }
        return output;
    }

    private void ClearLead()
    {
        if (_pendingAgentDraftContextToken is not null)
            ClearKnowledgeReferences(clearBoundComposer: true);
        _currentLead = null; LeadLinkStateText.Text = "选择会话后关联客户"; NameBox.Clear(); OwnerBox.Clear(); OptInCheck.IsChecked = false; OptedOutCheck.IsChecked = false; NotesBox.Clear(); CustomFieldsBox.Clear(); SaveLeadButton.IsEnabled = false;
        _currentIdentityResolution = null;
        _currentCustomerSuccessContext = null;
        UpdateCustomerSuccessPanel(null, null);
        UpdateLeadIntelligenceSummary(null);
        RenderConversationContext(null);
        UpdateComposerState();
    }

    private void AgentModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentModeCombo.SelectedItem is AgentModeOption option)
            UpdateAgentModeGuide(option.Value);
    }

    private void UpdateAgentModeGuide(ConversationAgentMode mode)
    {
        AgentModeGuideTitleText.Text = CustomerSuccessAgentLabels.ModeHeadline(mode);
        AgentModeTriggerText.Text = CustomerSuccessAgentLabels.ModeTrigger(mode);
        AgentModeOutputText.Text = CustomerSuccessAgentLabels.ModeOutput(mode);
        AgentModeSendText.Text = CustomerSuccessAgentLabels.ModeSend(mode);
    }

    private async void ApplyAgentMode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLead is null || ConversationList.SelectedItem is not ConversationItem conversation ||
            AgentModeCombo.SelectedItem is not AgentModeOption option) return;
        try
        {
            await _services.CustomerSuccessAgent.SetModeAsync(
                _currentLead.Id, conversation.AccountId, conversation.Id, option.Value, true);
            await LoadLeadAsync(conversation);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Agent 模式切换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AgentPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (_aiAssisting || _currentLead is null ||
            ConversationList.SelectedItem is not ConversationItem conversation)
            return;
        var state = _currentCustomerSuccessContext?.AgentState;
        if (state is null) return;

        try
        {
            if (state.Mode == ConversationAgentMode.AutoOff)
            {
                await _services.CustomerSuccessAgent.SetModeAsync(
                    _currentLead.Id,
                    conversation.AccountId,
                    conversation.Id,
                    ConversationAgentMode.SuggestOnly,
                    true);
            }
            else if (state.Mode == ConversationAgentMode.SuggestOnly)
            {
                await GenerateAgentSuggestionAsync();
                return;
            }
            else if (state.Mode == ConversationAgentMode.CopilotActive)
            {
                if (state.RunState == ConversationAgentRunState.CollabActive)
                {
                    await _services.CustomerSuccessAgent.StopCollaborationAsync(
                        _currentLead.Id,
                        conversation.AccountId,
                        conversation.Id,
                        "desktop_user",
                        "用户从会话主按钮停止协作。" );
                }
                else if (state.RunState is ConversationAgentRunState.WaitingHuman or
                             ConversationAgentRunState.PausedRisk or
                             ConversationAgentRunState.PausedError or
                             ConversationAgentRunState.RiskInfoCollectionSent)
                {
                    ShowAgentStateDetail(state);
                    return;
                }
                else
                {
                    await _services.CustomerSuccessAgent.StartCollaborationAsync(
                        _currentLead.Id,
                        conversation.AccountId,
                        conversation.Id,
                        "desktop_user");
                }
            }
            else if (state.Mode == ConversationAgentMode.AutoActive)
            {
                if (state.RunState is ConversationAgentRunState.AutoPreflight or
                    ConversationAgentRunState.AutoArmed or
                    ConversationAgentRunState.AutoProcessing or
                    ConversationAgentRunState.AutoSending or
                    ConversationAgentRunState.WaitingCustomer or
                    ConversationAgentRunState.TopicResolved or
                    ConversationAgentRunState.RiskInfoCollectionSent or
                    ConversationAgentRunState.WaitingHuman or
                    ConversationAgentRunState.PausedRisk or
                    ConversationAgentRunState.PausedError)
                {
                    ShowAgentStateDetail(state);
                    return;
                }
                await _services.CustomerSuccessAgent.StartHostingAsync(
                    _currentLead.Id,
                    conversation.AccountId,
                    conversation.Id,
                    "desktop_user");
            }

            await LoadLeadAsync(conversation);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "AI 协作助手", MessageBoxButton.OK, MessageBoxImage.Warning);
            await LoadLeadAsync(conversation);
        }
    }

    private async void StopAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLead is null || ConversationList.SelectedItem is not ConversationItem conversation ||
            _currentCustomerSuccessContext?.AgentState is not { } state)
            return;
        if (MessageBox.Show(
                state.Mode == ConversationAgentMode.CopilotActive
                    ? "停止当前会话协作？已生成但未发送的草稿会失效。"
                    : "停止当前会话托管？已生成但未发送的草稿会失效，之后不会自动恢复。",
                "确认停止 AI 协作助手",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            if (state.Mode == ConversationAgentMode.CopilotActive)
                await _services.CustomerSuccessAgent.StopCollaborationAsync(
                    _currentLead.Id, conversation.AccountId, conversation.Id,
                    "desktop_user", "用户显式停止当前会话协作。" );
            else
                await _services.CustomerSuccessAgent.StopHostingAsync(
                    _currentLead.Id, conversation.AccountId, conversation.Id,
                    "desktop_user", "用户显式停止当前会话托管。" );
            await LoadLeadAsync(conversation);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "停止失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void HumanTakeover_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLead is null || ConversationList.SelectedItem is not ConversationItem conversation) return;
        try
        {
            await _services.CustomerSuccessAgent.HumanTakeoverAsync(
                _currentLead.Id,
                conversation.AccountId,
                conversation.Id,
                "desktop_user",
                "",
                "用户从 Inbox 显式人工接管；旧草稿和自动发送权限已撤销。" );
            await LoadLeadAsync(conversation);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "人工接管失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ViewAgentLog_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationItem conversation) return;
        try
        {
            var events = await _services.Repository.GetConversationAgentAuditEventsAsync(
                conversation.AccountId,
                conversation.Id,
                500);
            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var table = new DataGrid
            {
                ItemsSource = events,
                IsReadOnly = true,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                MinHeight = 300
            };
            System.Windows.Automation.AutomationProperties.SetName(
                table,
                "AI 协作助手会话审计事件列表");
            table.Columns.Add(new DataGridTextColumn { Header = "时间", Binding = new System.Windows.Data.Binding(nameof(ConversationAgentAuditEvent.CreatedAt)) { StringFormat = "MM-dd HH:mm:ss" }, Width = 125 });
            table.Columns.Add(new DataGridTextColumn { Header = "动作", Binding = new System.Windows.Data.Binding(nameof(ConversationAgentAuditEvent.Action)), Width = 150 });
            table.Columns.Add(new DataGridTextColumn { Header = "运行状态", Binding = new System.Windows.Data.Binding(nameof(ConversationAgentAuditEvent.StateAfter)), Width = 120 });
            table.Columns.Add(new DataGridTextColumn { Header = "决策", Binding = new System.Windows.Data.Binding(nameof(ConversationAgentAuditEvent.Decision)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            var detail = new TextBox
            {
                Margin = new Thickness(0, 10, 0, 0),
                MinHeight = 96,
                MaxHeight = 180,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Text = events.Count == 0 ? "当前会话尚无审计事件。" : "选择一条事件查看上下文版本、幂等键、模型、来源和完整结果。"
            };
            System.Windows.Automation.AutomationProperties.SetName(
                detail,
                "所选 AI 协作助手审计事件详情");
            System.Windows.Automation.AutomationProperties.SetLiveSetting(
                detail,
                System.Windows.Automation.AutomationLiveSetting.Polite);
            table.SelectionChanged += (_, _) =>
            {
                if (table.SelectedItem is not ConversationAgentAuditEvent item) return;
                detail.Text = $"{item.Detail}\n\n模式/状态：{item.Mode} · {item.StateBefore} → {item.StateAfter}\n客户/会话：{item.CustomerId} · {item.AccountId} · {item.ConversationId}\n商机：{item.OpportunityId}\n来源消息：{item.SourceMessageId}\n上下文版本：{item.ContextVersion}\n幂等键：{item.IdempotencyKey}\n模型/提示版本：{item.Model} · {item.PromptVersion}\nBrain：{string.Join("；", item.CustomerBrainReferences)}\n知识：{string.Join("；", item.KnowledgeReferences)}\n最终结果：{item.FinalResult}";
            };
            Grid.SetRow(table, 0);
            Grid.SetRow(detail, 1);
            grid.Children.Add(table);
            grid.Children.Add(detail);
            new Window
            {
                Title = $"AI 协作助手审计日志 · {conversation.DisplayName}",
                Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 960,
                Height = 650,
                MinWidth = 760,
                MinHeight = 500,
                Content = grid
            }.ShowDialog();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "日志读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void ShowAgentStateDetail(ConversationAgentState state)
    {
        var detail = new[] { state.StateReason, state.PauseReason, state.LastRunDetail, state.LastRunError }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase);
        MessageBox.Show(
            $"模式：{CustomerSuccessAgentLabels.Mode(state.Mode)}\n运行：{CustomerSuccessAgentLabels.RunState(state.RunState)}\n话题：{AgentTopicStateLabel(state.TopicState)}\n风险：{AgentRiskStateLabel(state.RiskState)}\n\n{string.Join("\n", detail)}",
            "AI 协作助手当前状态",
            MessageBoxButton.OK,
            state.RunState is ConversationAgentRunState.PausedError or ConversationAgentRunState.PausedRisk
                ? MessageBoxImage.Warning
                : MessageBoxImage.Information);
    }

    private async void UseAgentDraft_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationItem conversation ||
            _currentCustomerSuccessContext?.AgentState is not { } state ||
            state.LastRunStatus is not CustomerSuccessRunStatus.SuggestionReady and
                not CustomerSuccessRunStatus.CopilotDraftReady ||
            string.IsNullOrWhiteSpace(state.LastGeneratedReply))
            return;

        var selectionGeneration = _conversationSelectionGeneration;
        var contextToken = _pendingAgentDraftContextToken;
        if (contextToken is null ||
            !state.PendingRunContextToken.Equals(contextToken.RunToken, StringComparison.Ordinal))
        {
            ComposerBox.Clear();
            ClearKnowledgeReferences();
            MessageBox.Show(
                "这份后台草稿缺少可复验的完整客户快照。请点击“重新生成建议”，确认当前客户与调查事实后再使用。",
                "AI 草稿需要重新生成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        var resolved = await ResolveCurrentConversationLeadAsync(conversation, selectionGeneration);
        if (!resolved.IsCurrent) return;
        if (!await EnsurePendingAgentDraftCurrentAsync(conversation, resolved.Binding, contextToken)) return;
        ComposerBox.Text = state.LastGeneratedReply;
        ComposerBox.CaretIndex = ComposerBox.Text.Length;
        ComposerBox.Focus();
        await _services.CustomerSuccessAgent.UpdateRunOutcomeAsync(
            conversation.AccountId,
            conversation.Id,
            state.LastRunStatus,
            "草稿已填入会话输入框；你可以修改后点击发送。");
        if (IsCurrentConversationSelection(selectionGeneration, conversation))
            await LoadLeadAsync(conversation, selectionGeneration);
    }

    private async void TakeOverHandoff_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLead is null || ConversationList.SelectedItem is not ConversationItem conversation) return;
        try
        {
            await _services.CustomerSuccessAgent.TakeOverAsync(_currentLead.Id, "user");
            await LoadLeadAsync(conversation);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "人工接管失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void ResolveHandoff_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLead is null || ConversationList.SelectedItem is not ConversationItem conversation) return;
        try
        {
            await _services.CustomerSuccessAgent.ResolveHandoffAsync(_currentLead.Id, "用户已在 Inbox 标记处理完成");
            await LoadLeadAsync(conversation);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "交接处理失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void ResumeAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLead is null || ConversationList.SelectedItem is not ConversationItem conversation) return;
        try
        {
            var mode = AgentModeCombo.SelectedItem is AgentModeOption option
                ? option.Value : ConversationAgentMode.SuggestOnly;
            await _services.CustomerSuccessAgent.ResumeAsync(
                _currentLead.Id, conversation.AccountId, conversation.Id, mode);
            await LoadLeadAsync(conversation);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void UpdateCustomerSuccessPanel(
        CustomerIdentityResolution? identity, CustomerSuccessContext? context)
    {
        CustomerIdentityText.Text = identity is null
            ? "等待会话身份"
            : $"{CustomerSuccessAgentLabels.Match(identity.Result)} · 置信度 {identity.Confidence:P0} · {identity.Reason}";
        var assistantIdentity = context?.AgentState?.AssistantIdentity
            ?? BusinessRoleContextPolicy.BuildAssistantIdentity(context?.WorkspaceProfile ?? _workspaceProfile);
        var accountRoleName = context?.Persona?.RoleName;
        AgentRoleNameText.Text = string.IsNullOrWhiteSpace(accountRoleName)
            ? assistantIdentity
            : accountRoleName;
        var state = context?.AgentState;
        AgentModeBadgeText.Text = CustomerSuccessAgentLabels.Mode(state?.Mode ?? ConversationAgentMode.SuggestOnly);
        AgentStateReasonText.Text = state is null
            ? "身份确认后可设置处理模式"
            : $"{(string.IsNullOrWhiteSpace(state.StateReason) ? CustomerSuccessAgentLabels.ModeStateReason(state.Mode) : state.StateReason)}；暂停消息 {state.PausedMessageCount} 条";
        var selectableMode = state?.Mode is ConversationAgentMode.AutoOff or ConversationAgentMode.SuggestOnly or
            ConversationAgentMode.CopilotActive or ConversationAgentMode.AutoActive
            ? state.Mode : ConversationAgentMode.SuggestOnly;
        AgentModeCombo.SelectedItem = (AgentModeCombo.ItemsSource as IEnumerable<AgentModeOption>)
            ?.FirstOrDefault(item => item.Value == selectableMode);
        AgentModeCombo.IsEnabled = context is not null;
        UpdateAgentModeGuide(selectableMode);

        ChatAgentTrack.Visibility = context is null ? Visibility.Collapsed : Visibility.Visible;
        ChatAgentModeText.Text = CustomerSuccessAgentLabels.Mode(selectableMode);
        ChatAgentRunStateText.Text = CustomerSuccessAgentLabels.RunState(
            state?.RunState ?? ConversationAgentRunState.SuggestReady);
        ChatAgentIdentityText.Text = assistantIdentity;
        ChatAgentCustomerText.Text = context?.Customer is null
            ? "等待客户身份"
            : $"{context.Customer.Name} · {context.Customer.Id}";
        AgentRuntimeIdentityText.Text = context?.Customer is null
            ? $"{assistantIdentity} · 等待客户身份"
            : $"{assistantIdentity} · {context.Customer.Name}";
        AgentRunStateBadgeText.Text = CustomerSuccessAgentLabels.RunState(
            state?.RunState ?? ConversationAgentRunState.SuggestReady);
        AgentRiskStateBadgeText.Text = AgentRiskStateLabel(
            state?.RiskState ?? ConversationRiskVerificationState.None);
        AgentTopicStateText.Text = $"话题：{AgentTopicStateLabel(state?.TopicState ?? ConversationTopicState.Unknown)}";
        var startedAt = state?.HostingStartedAt;
        var lastActionAt = state?.LastAgentActionAt ?? state?.LastRunAt;
        var runtimeTiming = startedAt is null
            ? "尚未开始运行"
            : $"开始 {startedAt.Value.LocalDateTime:MM-dd HH:mm} · 最近动作 {(lastActionAt ?? startedAt).Value.LocalDateTime:MM-dd HH:mm}";
        AgentRuntimeTimingText.Text = runtimeTiming;
        ChatAgentRuntimeMetaText.Text = runtimeTiming;
        AgentBrainSourceText.Text = $"Customer Brain {context?.Brain?.Statements.Count ?? 0}";
        AgentWhatsAppSourceText.Text = $"WhatsApp {context?.Messages.Count ?? 0}";
        AgentEmailSourceText.Text = $"邮件 {context?.EmailMessages.Count ?? 0}";
        AgentKnowledgeSourceText.Text = $"知识库 {state?.LastKnowledgeReferences.Count ?? 0}";
        var references = new List<string>();
        if (context?.Opportunity is { } opportunity)
            references.Add($"商机 {opportunity.IntentSummary} / {opportunity.RiskSummary}");
        if (state is not null)
        {
            references.AddRange(state.LastCustomerBrainReferences.Take(3).Select(value => $"Brain {value}"));
            references.AddRange(state.LastKnowledgeReferences.Take(3).Select(value => $"KB {value}"));
        }
        AgentLatestReferencesText.Text = references.Count == 0
            ? "最近引用：未记录"
            : $"最近引用：{string.Join("；", references)}";
        var paused = state?.RunState is ConversationAgentRunState.RiskInfoCollectionSent or
            ConversationAgentRunState.WaitingHuman or
            ConversationAgentRunState.PausedRisk or
            ConversationAgentRunState.PausedError or
            ConversationAgentRunState.HumanTakeover;
        AgentPauseReasonPanel.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        AgentPauseReasonText.Text = state is null
            ? "暂停原因：未记录"
            : $"暂停原因：{(string.IsNullOrWhiteSpace(state.PauseReason) ? state.StateReason : state.PauseReason)}";
        var canStopRuntime = state is not null &&
                             (ConversationAgentStateMachine.IsHosting(state) ||
                              ConversationAgentStateMachine.AllowsCollaboration(state));
        var canTakeOver = state is not null &&
                          (canStopRuntime || state.RunState is ConversationAgentRunState.RiskInfoCollectionSent or
                              ConversationAgentRunState.WaitingHuman or
                              ConversationAgentRunState.PausedRisk or
                              ConversationAgentRunState.PausedError);
        StopAgentButton.Visibility = canStopRuntime ? Visibility.Visible : Visibility.Collapsed;
        StopAgentButton.Content = state?.Mode == ConversationAgentMode.CopilotActive ? "停止协作" : "停止托管";
        var stopAutomationName = state?.Mode == ConversationAgentMode.CopilotActive
            ? "停止当前会话协作"
            : "停止当前会话托管";
        System.Windows.Automation.AutomationProperties.SetName(StopAgentButton, stopAutomationName);
        HeaderHumanTakeoverButton.Visibility = canTakeOver ? Visibility.Visible : Visibility.Collapsed;
        ViewAgentLogButton.IsEnabled = state is not null;
        ApplyAgentRunStateVisual(state?.RunState ?? ConversationAgentRunState.SuggestReady);
        RefreshAgentPrimaryButton();

        var runStatus = state?.LastRunStatus ?? CustomerSuccessRunStatus.None;
        AgentRunStatusText.Text = CustomerSuccessAgentLabels.RunStatus(runStatus);
        AgentRunTimeText.Text = state?.LastRunAt is { } runAt
            ? runAt.LocalDateTime.ToString("MM-dd HH:mm")
            : "无";
        AgentRunSourceText.Text = string.IsNullOrWhiteSpace(state?.LastSourcePreview)
            ? "客户原话：等待生成"
            : $"客户原话：{state.LastSourcePreview}";
        AgentRunReplyText.Text = string.IsNullOrWhiteSpace(state?.LastGeneratedReply)
            ? "生成的建议回复会显示在这里。"
            : runStatus is CustomerSuccessRunStatus.AutoReplySent or CustomerSuccessRunStatus.AutoReplyPending
                ? $"托管回复：{state.LastGeneratedReply}"
                : $"建议回复：{state.LastGeneratedReply}";
        AgentRunSummaryText.Text = string.IsNullOrWhiteSpace(state?.LastRunSummary)
            ? "分析摘要：无"
            : $"分析摘要：{state.LastRunSummary}";
        AgentRunNextActionText.Text = string.IsNullOrWhiteSpace(state?.LastRecommendedAction)
            ? "下一步：无"
            : $"下一步：{state.LastRecommendedAction}";
        var runDetails = new[] { state?.LastRunDetail, state?.LastRunError }
            .Where(item => !string.IsNullOrWhiteSpace(item));
        AgentRunDetailText.Text = runDetails.Any()
            ? string.Join("；", runDetails)
            : "当前没有 Agent 运行结果。";
        GenerateAgentSuggestionButton.Content = runStatus == CustomerSuccessRunStatus.None
            ? "立即生成建议"
            : "重新生成建议";
        UseAgentDraftButton.IsEnabled =
            runStatus is CustomerSuccessRunStatus.SuggestionReady or CustomerSuccessRunStatus.CopilotDraftReady &&
            !string.IsNullOrWhiteSpace(state?.LastGeneratedReply);

        var sourcing = context?.SourcingRequest;
        var readiness = sourcing?.Readiness ?? new SourcingReadiness();
        SourcingProgressText.Text = $"{readiness.CollectedCount} / 5";
        SetSourcingElement(SourcingProductText, sourcing, SourcingFieldKey.ProductImage, "商品");
        SetSourcingElement(SourcingQuantityText, sourcing, SourcingFieldKey.Quantity, "数量");
        SetSourcingElement(SourcingPriceText, sourcing, SourcingFieldKey.TargetPrice, "价格");
        SetSourcingElement(SourcingDestinationText, sourcing, SourcingFieldKey.Destination, "目的地");
        SetSourcingElement(SourcingLogisticsText, sourcing, SourcingFieldKey.ShippingPreference, "物流");
        SourcingStatusText.Text = sourcing is null
            ? "Need more information"
            : readiness.Readiness switch
            {
                SourcingReadinessLevel.HighConfidence => $"Complete · requirement v{sourcing.Version}",
                SourcingReadinessLevel.AgentAvailable => $"Ready for Agent · requirement v{sourcing.Version}",
                _ => $"Need more information · requirement v{sourcing.Version}"
            };
        SourcingStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            readiness.CanUseAgent ? "Success" : "Muted");
        SourcingFieldsText.Text = sourcing is null || readiness.MissingElements.Count > 0
            ? $"Missing：{string.Join("、", (sourcing is null ? Enum.GetValues<SourcingFieldKey>().Select(SourcingFieldLabel) : readiness.MissingElements.Select(SourcingElementLabel)))}"
            : "五项需求已完整；5/5 代表更高置信度，不是功能解锁节点。";
        var conflicts = sourcing?.Conflicts.Where(item => !item.IsResolved).Select(item => SourcingFieldLabel(item.Field)).ToList() ?? [];
        SourcingConflictText.Text = conflicts.Count == 0 ? "" : $"冲突待处理：{string.Join("、", conflicts)}";
        PendingQuestionText.Text = context?.PendingQuestions.FirstOrDefault(item => !item.IsResolved) is { } question
            ? $"待确认：{question.Question}（{question.Safety}）" : "待确认：无";
        FindProductsButton.IsEnabled = sourcing is not null && readiness.CanUseAgent && _currentLead is not null;
        SourcingActionHelpText.Text = sourcing is null
            ? "尚未形成采购需求；达到 3/5 且商品可识别后会开放人工 Agent 入口。"
            : !readiness.ProductIdentifiable && readiness.CollectedCount >= 3
                ? $"已收集 {readiness.CollectedCount} 项，但仍需可识别的商品名称、型号、SKU、链接、图片或明确描述。"
                : readiness.CanUseAgent
                    ? readiness.MissingElements.Count == 0
                        ? "信息完整。点击后仍需选择 Agent、核对内容并人工确认。"
                        : $"仍缺 {readiness.MissingElements.Count} 项；可以现在搜索，也可以继续收集。不会自动调用。"
                    : $"还需至少 {Math.Max(0, 3 - readiness.CollectedCount)} 项有效采购信息，并且商品必须可识别。";
        RenderSourcingResultPanel();

        var handoff = context?.OpenHandoff;
        HandoffPanel.Visibility = handoff is null ? Visibility.Collapsed : Visibility.Visible;
        HandoffReasonText.Text = handoff is null ? "" : $"原因：{handoff.Reason} · 账号 {handoff.AccountId}";
        HandoffOriginalText.Text = handoff is null ? "" : $"客户原话：{handoff.OriginalMessage}";
        HandoffTranslationText.Text = handoff is null || string.IsNullOrWhiteSpace(handoff.ChineseAssistTranslation)
            ? "" : $"中文辅助：{handoff.ChineseAssistTranslation}";
        HandoffPausedText.Text = handoff is null ? "" : $"状态：{handoff.Status} · 已暂停 {handoff.PausedMessageCount} 条消息";
        UpdateComposerState();
    }

    private void RefreshAgentPrimaryButton()
    {
        var state = _currentCustomerSuccessContext?.AgentState;
        var label = state is null ? "启用 AI" : CustomerSuccessAgentLabels.PrimaryAction(state);
        var visibleLabel = _aiAssisting ? "分析中…" : label;
        AiAssistantButton.Content = visibleLabel;
        ToolTipService.SetToolTip(AiAssistantButton, state is null
            ? "请先关联当前会话客户。"
            : $"{CustomerSuccessAgentLabels.Mode(state.Mode)} · {CustomerSuccessAgentLabels.RunState(state.RunState)}");
        System.Windows.Automation.AutomationProperties.SetName(
            AiAssistantButton,
            $"AI 协作助手：{visibleLabel}");
    }

    private void ApplyAgentRunStateVisual(ConversationAgentRunState runState)
    {
        var (background, foreground) = runState switch
        {
            ConversationAgentRunState.CollabActive or
            ConversationAgentRunState.AutoArmed or
            ConversationAgentRunState.AutoProcessing or
            ConversationAgentRunState.AutoSending or
            ConversationAgentRunState.WaitingCustomer => ("SuccessSoft", "Success"),
            ConversationAgentRunState.RiskInfoCollectionSent or
            ConversationAgentRunState.WaitingHuman or
            ConversationAgentRunState.PausedRisk => ("WarningSoft", "Warning"),
            ConversationAgentRunState.PausedError or
            ConversationAgentRunState.HumanTakeover => ("DangerSoft", "Danger"),
            _ => ("SurfaceElevated", "InkSecondary")
        };
        ChatAgentRunStateBorder.SetResourceReference(Border.BackgroundProperty, background);
        ChatAgentRunStateText.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        AgentRunStateBadgeBorder.SetResourceReference(Border.BackgroundProperty, background);
        AgentRunStateBadgeText.SetResourceReference(TextBlock.ForegroundProperty, foreground);
    }

    private static string AgentTopicStateLabel(ConversationTopicState value) => value switch
    {
        ConversationTopicState.Open => "开放",
        ConversationTopicState.WaitingCustomer => "等待客户",
        ConversationTopicState.WaitingHuman => "等待人工",
        ConversationTopicState.Resolved => "已解决",
        ConversationTopicState.Ended => "已结束",
        _ => "等待判断"
    };

    private static string AgentRiskStateLabel(ConversationRiskVerificationState value) => value switch
    {
        ConversationRiskVerificationState.OpenUnverified => "风险待核实",
        ConversationRiskVerificationState.AlreadyDiscussed => "风险已讨论",
        ConversationRiskVerificationState.InformationCollectionSent => "已收集一次信息",
        ConversationRiskVerificationState.WaitingHuman => "风险待人工",
        ConversationRiskVerificationState.Resolved => "风险已解决",
        ConversationRiskVerificationState.Conflict => "风险信息冲突",
        _ => "无风险事项"
    };

    private void SetSourcingElement(TextBlock text, SourcingRequest? request, SourcingFieldKey key, string label)
    {
        var collected = request?.Fields.TryGetValue(key, out var value) == true && value.IsStructurallyValid;
        text.Text = collected ? $"✓ {label}" : $"○ {label}";
        text.SetResourceReference(TextBlock.ForegroundProperty, collected ? "Success" : "Muted");
        text.FontWeight = collected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static string SourcingElementLabel(string value) => value switch
    {
        "product" => "商品",
        "quantity" => "数量",
        "targetPrice" => "目标价格",
        "destination" => "目的地",
        "logisticsPreference" => "物流要求",
        _ => value
    };

    private async Task RefreshSourcingTaskPanelAsync()
    {
        var customerId = _currentLead?.Id;
        if (string.IsNullOrWhiteSpace(customerId))
        {
            _latestSourcingTask = null;
            RenderSourcingResultPanel();
            return;
        }
        _latestSourcingTask = (await _services.McpAgents.GetTasksAsync(customerId, 50))
            .FirstOrDefault(task => task.Type.Equals("product_sourcing", StringComparison.OrdinalIgnoreCase)
                                    && task.Status is McpTaskStatus.Completed or McpTaskStatus.NeedsInformation);
        RenderSourcingResultPanel();
    }

    private void RenderSourcingResultPanel()
    {
        var task = _latestSourcingTask;
        if (task is null || _currentLead is null
            || !task.Source.CustomerId.Equals(_currentLead.Id, StringComparison.OrdinalIgnoreCase))
        {
            SourcingResultPanel.Visibility = Visibility.Collapsed;
            return;
        }
        SourcingResultPanel.Visibility = Visibility.Visible;
        SourcingResultText.Text = task.Status == McpTaskStatus.NeedsInformation
            ? $"Agent needs more information · {task.Result?.Summary}"
            : task.Result?.Summary ?? "Agent 已返回结果。";
        var metadata = task.Result?.Metadata;
        SourcingResultBasisText.Text = metadata is null
            ? $"Based on requirement v{task.RequirementVersionUsed}"
            : $"Based on v{metadata.RequirementVersionUsed} · {metadata.RequirementCollectedCount}/5 at search time · Missing: {string.Join("、", metadata.MissingAtExecution.Select(SourcingElementLabel).DefaultIfEmpty("无"))}";
        var missing = task.Result?.ProductSourcing?.MissingInformation ?? [];
        AskCustomerButton.IsEnabled = missing.Count > 0;
        RefineSearchButton.IsEnabled = _currentCustomerSuccessContext?.SourcingRequest is { } request
                                       && request.Version > task.RequirementVersionUsed
                                       && request.Readiness.CanUseAgent;
        RefineSearchButton.ToolTip = RefineSearchButton.IsEnabled
            ? $"检测到更新的 requirement v{_currentCustomerSuccessContext!.SourcingRequest!.Version}"
            : "客户提供新的有效采购信息后可再次人工 Refine。";
    }

    private async void FindProducts_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLead is null
            || _currentCustomerSuccessContext?.SourcingRequest is not { } requirement
            || ConversationList.SelectedItem is not ConversationItem conversation)
            return;
        var settings = await _services.Repository.GetAppSettingsAsync();
        if (!settings.McpAgentGatewayEnabled)
        {
            MessageBox.Show("请先在设置中启用“MCP 与外部智能体”。", "Find Products", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new ProductSourcingReviewWindow(
            _services,
            requirement,
            new AgentTaskSource
            {
                Module = "whatsapp_inbox",
                CustomerId = _currentLead.Id,
                ConversationId = conversation.Id,
                AccountId = conversation.AccountId
            },
            _currentLead.DisplayName) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.CompletedTask is null) return;
        _latestSourcingTask = window.CompletedTask;
        RenderSourcingResultPanel();
        new AgentTaskDetailsWindow(window.CompletedTask) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void ViewSourcingResult_Click(object sender, RoutedEventArgs e)
    {
        if (_latestSourcingTask is null) return;
        new AgentTaskDetailsWindow(_latestSourcingTask) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void AskSourcingQuestion_Click(object sender, RoutedEventArgs e)
    {
        var missing = _latestSourcingTask?.Result?.ProductSourcing?.MissingInformation ?? [];
        if (missing.Count == 0) return;
        ComposerBox.Text = "To improve the product search, could you please confirm:\n" +
                           string.Join("\n", missing.Select(item => $"• {SourcingElementLabel(item)}"));
        ComposerBox.CaretIndex = ComposerBox.Text.Length;
        ComposerBox.Focus();
        MessageBox.Show("建议问题已填入输入框，但尚未发送。请修改并人工确认后再发送给客户。", "Ask Customer", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RefineSourcing_Click(object sender, RoutedEventArgs e)
    {
        if (_latestSourcingTask is null
            || _currentLead is null
            || _currentCustomerSuccessContext?.SourcingRequest is not { } requirement
            || requirement.Version <= _latestSourcingTask.RequirementVersionUsed
            || ConversationList.SelectedItem is not ConversationItem conversation)
            return;
        var window = new ProductSourcingReviewWindow(
            _services,
            requirement,
            new AgentTaskSource
            {
                Module = "whatsapp_inbox_refine",
                CustomerId = _currentLead.Id,
                ConversationId = conversation.Id,
                AccountId = conversation.AccountId
            },
            _currentLead.DisplayName,
            _latestSourcingTask) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.CompletedTask is null) return;
        _latestSourcingTask = window.CompletedTask;
        RenderSourcingResultPanel();
        new AgentTaskDetailsWindow(window.CompletedTask) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private static string SourcingFieldLabel(SourcingFieldKey value) => value switch
    {
        SourcingFieldKey.ProductImage => "产品/服务资料",
        SourcingFieldKey.Quantity => "范围/数量",
        SourcingFieldKey.TargetPrice => "预算/目标价格",
        SourcingFieldKey.Destination => "交付地区",
        SourcingFieldKey.ShippingPreference => "交付偏好",
        _ => value.ToString()
    };

    private static string SourcingStatusLabel(SourcingRequestStatus value) => value switch
    {
        SourcingRequestStatus.Draft => "草稿",
        SourcingRequestStatus.Collecting => "收集中",
        SourcingRequestStatus.FieldConflict => "字段冲突",
        SourcingRequestStatus.Complete => "已完整",
        SourcingRequestStatus.HumanReview => "人工复核",
        SourcingRequestStatus.Acknowledged => "已确认",
        SourcingRequestStatus.Submitted => "已提交",
        SourcingRequestStatus.Cancelled => "已取消",
        _ => value.ToString()
    };

    private void UpdateLeadIntelligenceSummary(Lead? lead)
    {
        var current = lead is { HasCurrentAiScore: true };
        var score = current ? lead!.Score : 0;
        var grade = current ? lead!.Grade : "D";
        var confidence = current ? lead!.AnalysisConfidence : 0;
        AiSidebarScoreRing.SetScore(score, grade, confidence);
        AiSidebarConfidenceBar.Value = Math.Clamp(confidence * 100, 0, 100);
        AiSidebarBrainMetaText.Text = lead is null ? "CUSTOMER BRAIN · 等待客户上下文" : "CUSTOMER BRAIN · 正在整合跨渠道证据…";
        AiSidebarConfidenceText.Text = current ? $"AI 置信度 {confidence:P0}" : lead is null ? "等待关联客户" : $"{lead.AnalysisStateLabel} · D / 0";
        AiSidebarProfileText.Text = current && !string.IsNullOrWhiteSpace(lead!.ProfileSummary) ? lead.ProfileSummary : lead is null ? "选择会话后显示对应客户画像" : "尚无经过验证的 AI 客户画像";
        AiSidebarNextActionText.Text = current && !string.IsNullOrWhiteSpace(lead!.NextAction) ? $"下一步：{lead.NextAction}" : "下一步：等待 AI 分析或人工判断";
    }

    private async Task UpdateCustomerBrainSummaryAsync(Lead? lead)
    {
        var generation = ++_customerBrainRefreshGeneration;
        if (lead is null)
        {
            AiSidebarBrainMetaText.Text = "CUSTOMER BRAIN · 等待客户上下文";
            RenderConversationContext(null);
            RenderCommitmentReminders([]);
            return;
        }

        // Promise reminders are local, durable state. Render them before any
        // network-backed Customer Brain refresh so a slow or unavailable model
        // cannot hide an obligation from the active conversation.
        await UpdateCommitmentRemindersAsync(lead.Id);
        if (generation != _customerBrainRefreshGeneration || _currentLead?.Id != lead.Id) return;

        try
        {
            var brain = await _services.CustomerBrain.RefreshAsync(lead.Id);
            if (generation != _customerBrainRefreshGeneration || _currentLead?.Id != lead.Id) return;

            var facts = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.Fact);
            var inferences = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.Inference);
            var gaps = brain.Statements.Count(item => item.Nature == IntelligenceStatementNature.InformationGap);
            AiSidebarBrainMetaText.Text = brain.HasCurrentDecision
                ? $"BRAIN V{brain.Version} · 覆盖 {brain.Coverage.Percentage}% · 事实 {facts} / 判断 {inferences} / 缺口 {gaps} · 知识 {brain.KnowledgeReferences.Count}"
                : $"BRAIN V{brain.Version} · 结论已过期 · 资料已变化；打开客户详情，点击“AI 分析并生成行动”";
            if (brain.HasCurrentDecision)
            {
                if (!string.IsNullOrWhiteSpace(brain.Summary)) AiSidebarProfileText.Text = brain.Summary;
                if (!string.IsNullOrWhiteSpace(brain.NextBestAction)) AiSidebarNextActionText.Text = $"下一步：{brain.NextBestAction}";
            }
            RenderConversationContext(brain.ConversationContext, loading: true);
            brain = await _services.CustomerBrain.UpdateConversationContextAsync(lead.Id);
            if (generation != _customerBrainRefreshGeneration || _currentLead?.Id != lead.Id) return;
            RenderConversationContext(brain.ConversationContext);
        }
        catch (Exception error)
        {
            if (generation != _customerBrainRefreshGeneration || _currentLead?.Id != lead.Id) return;
            AiSidebarBrainMetaText.Text = $"CUSTOMER BRAIN · 暂不可用：{error.Message}";
            var profile = await _services.CustomerBrain.GetAsync(lead.Id);
            if (generation != _customerBrainRefreshGeneration || _currentLead?.Id != lead.Id) return;
            RenderConversationContext(profile?.ConversationContext);
        }
    }

    private async void RefreshAiContext_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLead is null) return;
        RefreshAiContextButton.IsEnabled = false;
        AiContextStatusText.Text = "正在重新读取全部 WhatsApp 与邮件历史…";
        try
        {
            var profile = await _services.CustomerBrain.UpdateConversationContextAsync(_currentLead.Id, force: true);
            if (_currentLead?.Id == profile.CustomerId)
            {
                RenderConversationContext(profile.ConversationContext);
                await UpdateCommitmentRemindersAsync(profile.CustomerId);
            }
        }
        catch (Exception error)
        {
            AiContextStatusText.Text = "更新失败，可重试";
            AiContextMetaText.Text = error.Message;
        }
        finally
        {
            RefreshAiContextButton.IsEnabled = _currentLead is not null;
        }
    }

    private async Task UpdateCommitmentRemindersAsync(string customerId)
    {
        var commitments = await _services.CustomerCommitments.GetActiveAsync(customerId);
        if (_currentLead?.Id != customerId) return;
        RenderCommitmentReminders(commitments);
    }

    private void RenderCommitmentReminders(IReadOnlyCollection<CustomerCommitment> commitments)
    {
        CommitmentReminderCard.Visibility = commitments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (commitments.Count == 0)
        {
            CommitmentReminderItems.ItemsSource = null;
            CommitmentReminderStatusText.Text = "没有待履约承诺";
            CompleteCommitmentReminderButton.IsEnabled = false;
            return;
        }

        var ordered = commitments
            .OrderByDescending(item => item.IsOverdue)
            .ThenBy(item => item.DueAt is null)
            .ThenBy(item => item.DueAt)
            .ThenBy(item => item.DetectedAt)
            .ToList();
        CommitmentReminderItems.ItemsSource = ordered
            .Select(item => new CommitmentReminderOption(item, $"{item.DueLabel} · {item.Title}\n“{item.Evidence}”"))
            .ToList();
        CommitmentReminderItems.SelectedIndex = 0;
        var overdue = ordered.Count(item => item.IsOverdue);
        CommitmentReminderStatusText.Text = overdue > 0
            ? $"{ordered.Count} 条待履约，其中 {overdue} 条逾期"
            : $"{ordered.Count} 条待履约";
        CompleteCommitmentReminderButton.IsEnabled = true;
    }

    private void CommitmentReminderItems_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        CompleteCommitmentReminderButton.IsEnabled =
            CommitmentReminderItems.SelectedItem is CommitmentReminderOption;

    private async void CompleteCommitmentReminder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLead is null
            || CommitmentReminderItems.SelectedItem is not CommitmentReminderOption selected)
            return;
        if (MessageBox.Show(
                $"确认这项承诺已经真实履约？\n\n{selected.Item.Title}\n来源原文：{selected.Item.Evidence}\n\n确认后会保留历史记录，并结束所有板块的待履约标记。",
                "完成待履约承诺",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        CompleteCommitmentReminderButton.IsEnabled = false;
        try
        {
            await _services.CustomerCommitments.CompleteAsync(
                _currentLead.Id,
                selected.Item.Id,
                "用户在 Customer Intelligence 中确认已经履约");
            await UpdateCommitmentRemindersAsync(_currentLead.Id);
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "无法完成承诺", MessageBoxButton.OK, MessageBoxImage.Warning);
            CompleteCommitmentReminderButton.IsEnabled = true;
        }
    }

    private void ScheduleConversationContextRefresh(string customerId)
    {
        var debounce = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _contextRefreshDebounce, debounce);
        previous?.Cancel();
        previous?.Dispose();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), debounce.Token);
                await Dispatcher
                    .InvokeAsync(async () =>
                    {
                        if (IsVisible && _currentLead?.Id == customerId)
                            await UpdateCustomerBrainSummaryAsync(_currentLead);
                    })
                    .Task
                    .Unwrap();
            }
            catch (OperationCanceledException) when (debounce.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _contextRefreshDebounce, null, debounce), debounce))
                    debounce.Dispose();
            }
        });
    }

    private void RenderConversationContext(CustomerConversationContext? context, bool loading = false)
    {
        RefreshAiContextButton.IsEnabled = _currentLead is not null && !loading;
        if (_currentLead is null)
        {
            AiContextStatusText.Text = "等待关联客户";
            AiContextSummaryText.Text = "关联客户后，将综合 WhatsApp 与邮件历史生成态度、性格、语气和当前关系摘要。";
            AiContextMetaText.Text = "客户原文是证据；人工备注与 AI 推断会分开标注。";
            return;
        }
        if (loading && context is not { Status: CustomerContextStatus.Current })
        {
            AiContextStatusText.Text = "正在检查新增上下文…";
            AiContextSummaryText.Text = context?.HasContent == true ? BuildContextText(context) : "正在读取该客户的跨渠道历史。";
            AiContextMetaText.Text = "仅在消息或人工备注发生变化时调用模型。";
            return;
        }
        context ??= new CustomerConversationContext();
        AiContextStatusText.Text = context.Status switch
        {
            CustomerContextStatus.Current => "已更新",
            CustomerContextStatus.Stale => "有新增内容，等待更新",
            CustomerContextStatus.Generating => "正在生成…",
            CustomerContextStatus.NotConfigured => "等待配置 Customer Brain 模型",
            CustomerContextStatus.RetryableFailed => "更新失败，可重试",
            _ => "尚无可总结的沟通"
        };
        AiContextSummaryText.Text = context.HasContent
            ? BuildContextText(context)
            : context.Status == CustomerContextStatus.NotConfigured
                ? "配置模型后，将自动总结该客户的 WhatsApp 与邮件历史。"
                : "该客户尚无可用于总结的 WhatsApp、邮件或人工备注。";
        AiContextMetaText.Text = context.UpdatedAt is null
            ? string.IsNullOrWhiteSpace(context.Error) ? "客户原文是证据；人工备注与 AI 推断会分开标注。" : context.Error
            : $"WhatsApp {context.WhatsAppMessageCount} 条 · 邮件 {context.EmailMessageCount} 条 · {context.AiModel} · {context.UpdatedAt:MM-dd HH:mm}";
    }

    private static string BuildContextText(CustomerConversationContext context)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.Overview)) parts.Add(context.Overview);
        if (context.AttitudesAndInterests.Count > 0) parts.Add($"态度与偏好：{string.Join("；", context.AttitudesAndInterests.Take(3))}");
        if (context.PersonalityTraits.Count > 0) parts.Add($"性格倾向：{string.Join("；", context.PersonalityTraits.Take(3))}");
        if (context.CommunicationStyle.Count > 0) parts.Add($"沟通语气：{string.Join("；", context.CommunicationStyle.Take(3))}");
        if (context.ConcernsAndObjections.Count > 0) parts.Add($"关注与异议：{string.Join("；", context.ConcernsAndObjections.Take(3))}");
        if (context.PurchaseSignals.Count > 0) parts.Add($"购买信号：{string.Join("；", context.PurchaseSignals.Take(3))}");
        if (!string.IsNullOrWhiteSpace(context.RelationshipState)) parts.Add($"当前关系：{context.RelationshipState}");
        if (!string.IsNullOrWhiteSpace(context.RecommendedApproach)) parts.Add($"建议沟通：{context.RecommendedApproach}");
        return string.Join(Environment.NewLine, parts);
    }

    private void BindPendingAgentDraft(
        CustomerSuccessAgentDecision decision,
        CustomerSuccessRunContextToken contextToken,
        string customerId,
        string accountId,
        string conversationId)
    {
        if (string.IsNullOrWhiteSpace(customerId) ||
            !contextToken.CustomerId.Equals(customerId, StringComparison.OrdinalIgnoreCase) ||
            !contextToken.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase) ||
            !contextToken.ConversationId.Equals(conversationId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AI 草稿的客户上下文不一致，请重新生成。");
        _pendingKnowledgeDecision = decision;
        _pendingAgentDraftContextToken = contextToken;
        _pendingKnowledgeCustomerId = customerId;
        _pendingKnowledgeAccountId = accountId;
        _pendingKnowledgeConversationId = conversationId;
    }

    private void ShowKnowledgeReferences(
        CustomerSuccessAgentDecision decision,
        KnowledgeRetrievalResult? retrieval)
    {
        if (retrieval is null)
        {
            KnowledgeReferencePanel.Visibility = Visibility.Collapsed;
            KnowledgeReferenceList.ItemsSource = null;
            return;
        }
        KnowledgeReferencePanel.Visibility = Visibility.Visible;
        var rows = decision.KnowledgeCitations.Select(hit => new KnowledgeReferenceRow(
            hit.CitationLabel,
            hit.Content.Length <= 150 ? hit.Content : hit.Content[..150] + "…",
            hit)).ToList();
        KnowledgeReferenceList.ItemsSource = rows;
        if (rows.Count > 0) KnowledgeReferenceList.SelectedIndex = 0;
        KnowledgeReferenceSummaryText.Text = rows.Count > 0
            ? $"实际引用 {rows.Count} 项 · 检索 ID {retrieval.Id}。引用仅作业务参考，发送前仍需人工核对。"
            : retrieval.SufficientToAnswer
                ? $"检索到相关知识，但本次建议未使用任何知识块 · 检索 ID {retrieval.Id}。"
                : $"知识不足：{retrieval.InsufficiencyReason} · 检索 ID {retrieval.Id}";
    }

    private void ClearKnowledgeReferences(bool clearBoundComposer = false)
    {
        var clearComposer = clearBoundComposer && _pendingAgentDraftContextToken is not null;
        ClearPendingKnowledgeDecision();
        KnowledgeReferenceList.ItemsSource = null;
        KnowledgeReferenceSummaryText.Text = "尚未执行知识检索";
        KnowledgeReferencePanel.Visibility = Visibility.Collapsed;
        if (clearComposer && !string.IsNullOrWhiteSpace(ComposerBox.Text)) ComposerBox.Clear();
    }

    private void ClearPendingKnowledgeDecision()
    {
        _pendingKnowledgeDecision = null;
        _pendingAgentDraftContextToken = null;
        _pendingKnowledgeCustomerId = "";
        _pendingKnowledgeAccountId = "";
        _pendingKnowledgeConversationId = "";
    }

    private bool IsPendingKnowledgeTarget(ConversationItem conversation, string customerId) =>
        _pendingKnowledgeDecision is not null &&
        _pendingAgentDraftContextToken is not null &&
        _pendingKnowledgeAccountId.Equals(conversation.AccountId, StringComparison.OrdinalIgnoreCase) &&
        _pendingKnowledgeConversationId.Equals(conversation.Id, StringComparison.OrdinalIgnoreCase) &&
        _pendingKnowledgeCustomerId.Equals(customerId, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> EnsurePendingAgentDraftCurrentAsync(
        ConversationItem conversation,
        ConversationLeadBinding binding,
        CustomerSuccessRunContextToken contextToken)
    {
        if (!ReferenceEquals(_pendingAgentDraftContextToken, contextToken) ||
            binding.Lead is null ||
            !IsPendingKnowledgeTarget(conversation, binding.CustomerId) ||
            !contextToken.CustomerId.Equals(binding.CustomerId, StringComparison.OrdinalIgnoreCase))
        {
            InvalidatePendingAgentDraft();
            return false;
        }
        if (await IsAgentDraftContextCurrentAsync(contextToken)) return true;
        InvalidatePendingAgentDraft();
        return false;
    }

    private async Task<bool> IsAgentDraftContextCurrentAsync(CustomerSuccessRunContextToken contextToken)
    {
        try
        {
            await _services.CustomerSuccessAgent.EnsureRunContextCurrentAsync(
                contextToken,
                requireAutoLock: false,
                requireProcessedState: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void InvalidatePendingAgentDraft()
    {
        ClearKnowledgeReferences(clearBoundComposer: true);
        if (ConversationList.SelectedItem is ConversationItem)
            MessageBox.Show(
                "客户身份、会话原文或外部调查事实已变化。旧 AI 草稿已清空，请重新生成后再发送。",
                "AI 草稿已失效",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
    }

    private void ViewKnowledgeSource_Click(object sender, RoutedEventArgs e)
    {
        if (KnowledgeReferenceList.SelectedItem is not KnowledgeReferenceRow row)
        {
            MessageBox.Show("请选择一项知识来源。", "本次建议参考", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MessageBox.Show(
            $"{row.Hit.CitationLabel}\n\n作用域：{row.Hit.Scope.Label}\n分类：{KnowledgeLabels.Category(row.Hit.Category)}\n" +
            $"来源层：{row.Hit.SourceKind} / {row.Hit.EvidenceLevel}\n相关度：{row.Hit.RelevanceScore:P0}\n\n{row.Hit.Content}",
            "知识来源",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void ExcludeKnowledge_Click(object sender, RoutedEventArgs e)
    {
        if (KnowledgeReferenceList.SelectedItem is not KnowledgeReferenceRow row ||
            ConversationList.SelectedItem is not ConversationItem conversation) return;
        var selectionGeneration = _conversationSelectionGeneration;
        var customerId = _currentLead?.Id ?? "";
        if (!IsPendingKnowledgeTarget(conversation, customerId))
        {
            ClearKnowledgeReferences(clearBoundComposer: true);
            return;
        }
        await _services.Repository.SaveKnowledgeFeedbackAsync(new KnowledgeFeedback
        {
            RetrievalLogId = _pendingKnowledgeDecision?.KnowledgeRetrievalId ?? "",
            DocumentId = row.Hit.DocumentId,
            ChunkId = row.Hit.ChunkId,
            CustomerId = customerId,
            AccountId = conversation.AccountId,
            ConversationId = conversation.Id,
            Helpful = false,
            ExcludedForCurrentConversation = true,
            Note = "用户在 Inbox 将该知识排除出当前会话。"
        });
        if (!IsCurrentConversationSelection(selectionGeneration, conversation)) return;
        if (!IsPendingKnowledgeTarget(conversation, customerId))
        {
            ClearKnowledgeReferences(clearBoundComposer: true);
            return;
        }
        // Editing an AI draft does not turn it into an unrelated human draft. Once a cited
        // source is excluded, every derivative of that bound draft must be discarded.
        if (_pendingAgentDraftContextToken is not null && !string.IsNullOrWhiteSpace(ComposerBox.Text))
            ComposerBox.Clear();
        ClearPendingKnowledgeDecision();
        var remaining = (KnowledgeReferenceList.ItemsSource as IEnumerable<KnowledgeReferenceRow> ?? [])
            .Where(item => item.Hit.ChunkId != row.Hit.ChunkId).ToList();
        KnowledgeReferenceList.ItemsSource = remaining;
        KnowledgeReferenceSummaryText.Text = "已排除当前来源；原建议已清空，请重新运行 AI 以应用新的检索范围。";
    }

    private async void HelpfulKnowledge_Click(object sender, RoutedEventArgs e)
    {
        if (KnowledgeReferenceList.SelectedItem is not KnowledgeReferenceRow row ||
            ConversationList.SelectedItem is not ConversationItem conversation) return;
        var selectionGeneration = _conversationSelectionGeneration;
        var customerId = _currentLead?.Id ?? "";
        if (!IsPendingKnowledgeTarget(conversation, customerId))
        {
            ClearKnowledgeReferences(clearBoundComposer: true);
            return;
        }
        await _services.Repository.SaveKnowledgeFeedbackAsync(new KnowledgeFeedback
        {
            RetrievalLogId = _pendingKnowledgeDecision?.KnowledgeRetrievalId ?? "",
            DocumentId = row.Hit.DocumentId,
            ChunkId = row.Hit.ChunkId,
            CustomerId = customerId,
            AccountId = conversation.AccountId,
            ConversationId = conversation.Id,
            Helpful = true,
            Note = "用户在 Inbox 标记该知识引用有帮助。"
        });
        if (!IsCurrentConversationSelection(selectionGeneration, conversation)) return;
        if (!IsPendingKnowledgeTarget(conversation, customerId))
        {
            ClearKnowledgeReferences(clearBoundComposer: true);
            return;
        }
        KnowledgeReferenceSummaryText.Text = "已记录“有帮助”。这只是人工反馈，不会被系统表述为成交因果。";
    }

    private async void DisableKnowledgeSource_Click(object sender, RoutedEventArgs e)
    {
        if (KnowledgeReferenceList.SelectedItem is not KnowledgeReferenceRow row ||
            ConversationList.SelectedItem is not ConversationItem conversation) return;
        var selectionGeneration = _conversationSelectionGeneration;
        var customerId = _currentLead?.Id ?? "";
        if (!IsPendingKnowledgeTarget(conversation, customerId))
        {
            ClearKnowledgeReferences(clearBoundComposer: true);
            return;
        }
        if (MessageBox.Show(
                $"停用“{row.Hit.DocumentTitle}”后，它会立即退出全部后续检索。原件、版本和审计仍保留。是否继续？",
                "停用知识来源",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _services.KnowledgeBase.DisableAsync(row.Hit.DocumentId);
            if (!IsCurrentConversationSelection(selectionGeneration, conversation)) return;
            if (!IsPendingKnowledgeTarget(conversation, customerId))
            {
                ClearKnowledgeReferences(clearBoundComposer: true);
                return;
            }
            // The user may have edited the generated text; it is still derived from this
            // source and remains bound to the original customer/run context.
            ClearKnowledgeReferences(clearBoundComposer: true);
            MessageBox.Show("知识来源已停用。请重新运行 AI 生成建议。", "知识库", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            if (!IsCurrentConversationSelection(selectionGeneration, conversation)) return;
            MessageBox.Show(error.Message, "停用失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ScrollMessages(ConversationItem conversation)
    {
        var visibleMessages = VisibleMessages(conversation);
        MessageList.ItemsSource = visibleMessages;
        if (visibleMessages.LastOrDefault() is { } last) MessageList.ScrollIntoView(last);
    }

    private static List<MessageItem> VisibleMessages(ConversationItem conversation) =>
        conversation.Messages.Where(message => !message.IsStatusUpdate).ToList();

    private void UpdateStatusUpdateBanner(ConversationItem conversation)
    {
        var now = DateTimeOffset.Now;
        var status = conversation.Messages
            .Where(message => message.IsStatusUpdate && !message.IsRevoked && (message.StatusExpiresAt ?? message.Timestamp.AddHours(24)) > now)
            .OrderByDescending(message => message.Timestamp)
            .FirstOrDefault();
        if (status is null)
        {
            HideStatusUpdateBanner();
            return;
        }

        _currentStatusUpdateUrl = ExtractHttpUrl(status.Text);
        StatusUpdateLinkText.Text = status.DisplayText;
        var expiresAt = status.StatusExpiresAt ?? status.Timestamp.AddHours(24);
        StatusUpdateMetaText.Text = $"发布 {status.Timestamp.LocalDateTime:MM-dd HH:mm} · 置顶至 {expiresAt.LocalDateTime:MM-dd HH:mm}";
        StatusUpdateBanner.Visibility = Visibility.Visible;
    }

    private void HideStatusUpdateBanner()
    {
        _currentStatusUpdateUrl = "";
        StatusUpdateBanner.Visibility = Visibility.Collapsed;
        StatusUpdateLinkText.Text = "";
        StatusUpdateMetaText.Text = "";
    }

    private void OpenStatusUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentStatusUpdateUrl))
        {
            MessageBox.Show("该动态没有可直接打开的网页链接。", "WhatsApp 最新动态", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(_currentStatusUpdateUrl) { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(error.Message, "无法打开动态链接", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string ExtractHttpUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var start = text.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = text.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        var end = text.IndexOfAny([' ', '\r', '\n', '\t'], start);
        return (end < 0 ? text[start..] : text[start..end]).TrimEnd('.', ',', ';', ')', ']', '}');
    }

    private void SetConnectionText(string text, bool connected)
    {
        ConnectionStateText.Text = text;
        ConnectionStateText.Foreground = (Brush)FindResource(connected ? "Success" : "Warning");
    }

    private void UpdateConnectionControls()
    {
        var state = _services.WhatsApp.ConnectionStateFor(CurrentAccountId);
        SetConnectionText(state switch { "connected" => "已连接", "connecting" => "连接中", "retrying" => "自动重试中", "logged_out" => "登录已失效", _ => "未连接" }, state == "connected");
        var canStop = state is "connected" or "connecting" or "retrying";
        var canLogout = canStop || _services.WhatsApp.HasStoredSession(CurrentAccountId);
        ConnectButton.IsEnabled = !_connectionActionInProgress && !canStop;
        AccountActionsButton.IsEnabled = !_connectionActionInProgress && (canStop || canLogout);
        DisconnectMenuItem.IsEnabled = !_connectionActionInProgress && canStop;
        LogoutMenuItem.IsEnabled = !_connectionActionInProgress && canLogout;
        SyncButton.IsEnabled = !_connectionActionInProgress && state == "connected";
        CreateGroupButton.IsEnabled = !_connectionActionInProgress && state == "connected";
        UpdateComposerState();
    }

    private void ShowQrProgress(string message, bool clearQr = false)
    {
        if (clearQr)
        {
            QrImage.Source = null;
            QrImage.Visibility = Visibility.Collapsed;
        }
        QrProgressBar.Visibility = Visibility.Visible;
        QrHintText.Text = message;
        // A previously paired account keeps its message list visible during
        // reconnection stages; only a QR-less fresh pairing should cover the
        // conversation area with the progress panel.
        if (_existingSession) return;
        QrPanel.Visibility = Visibility.Visible;
        MessageList.Visibility = Visibility.Collapsed;
    }

    private void ShowQrIssue(string message)
    {
        QrImage.Source = null;
        QrImage.Visibility = Visibility.Collapsed;
        QrProgressBar.Visibility = Visibility.Collapsed;
        QrHintText.Text = message;
        if (_existingSession) return;
        QrPanel.Visibility = Visibility.Visible;
        MessageList.Visibility = Visibility.Collapsed;
    }

    private void ShowQr(string dataUrl)
    {
        try
        {
            var image = DecodeDataUrl(dataUrl);
            if (image is null)
            {
                ShowQrIssue("二维码数据不完整，程序正在等待下一张二维码；若长时间没有恢复，请点击“连接 / 显示二维码”重试。");
                return;
            }
            QrImage.Source = image;
            QrImage.Visibility = Visibility.Visible;
            QrProgressBar.Visibility = Visibility.Collapsed;
            QrHintText.Text = "请使用手机 WhatsApp → 设置 → 已关联设备扫描二维码。二维码会定期刷新。";
            QrPanel.Visibility = Visibility.Visible;
            MessageList.Visibility = Visibility.Collapsed;
            SetConnectionText("等待扫码", false);
        }
        catch
        {
            ShowQrIssue("二维码绘制失败，程序正在等待自动刷新；若长时间没有恢复，请点击“连接 / 显示二维码”重试。");
        }
    }

    private bool RestoreLatestQr()
    {
        if (_services.WhatsApp.IsConnectedFor(CurrentAccountId)) return false;
        var dataUrl = _services.WhatsApp.LatestQrDataUrlFor(CurrentAccountId);
        if (string.IsNullOrWhiteSpace(dataUrl)) return false;
        ShowQr(dataUrl);
        return true;
    }

    private static BitmapImage? DecodeDataUrl(string dataUrl)
    {
        var marker = dataUrl.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var bytes = Convert.FromBase64String(dataUrl[(marker + 7)..]);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    private void OpenMedia_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MessageItem item } || string.IsNullOrWhiteSpace(item.MediaPath) || !File.Exists(item.MediaPath))
        {
            MessageBox.Show("媒体文件尚未下载到本机。连接 WhatsApp 后再次同步，可重新尝试下载。", "WhatsApp 媒体", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(item.MediaPath) { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(error.Message, "无法打开媒体", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string KindFromFileName(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".webp" => "image",
        ".gif" or ".mp4" or ".3gp" or ".mov" => "video",
        ".mp3" or ".m4a" or ".ogg" or ".opus" or ".wav" or ".aac" => "audio",
        _ => "document"
    };

    private static WhatsAppMessageStatus ParseMessageStatus(JsonElement data, bool fromMe)
    {
        if (!fromMe) return WhatsAppMessageStatus.Received;
        if (ParseTime(data, "readAt") is not null) return WhatsAppMessageStatus.Read;
        if (ParseTime(data, "deliveredAt") is not null) return WhatsAppMessageStatus.Delivered;
        return data.TryGetProperty("status", out var value) && value.TryGetInt32(out var numeric) ? StatusFromNumeric(numeric) : WhatsAppMessageStatus.Pending;
    }
    private static WhatsAppMessageStatus StatusFromNumeric(int numeric) => numeric switch
    {
        <= 0 => WhatsAppMessageStatus.Failed,
        1 => WhatsAppMessageStatus.Pending,
        2 => WhatsAppMessageStatus.Sent,
        3 => WhatsAppMessageStatus.Delivered,
        >= 4 => WhatsAppMessageStatus.Read
    };
    private static DateTimeOffset? ParseTime(JsonElement data, string name) => DateTimeOffset.TryParse(Text(data, name), out var value) ? value : null;
    private static string Text(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string PhaseLabel(string phase) => phase switch
    {
        "initial_bootstrap" => "基础会话",
        "full" => "完整历史",
        "recent" => "近期历史",
        "push_name" => "联系人名称",
        "non_blocking_data" => "联系人资料",
        "app_state" => "联系人与会话变更",
        "offline_messages" => "离线期间的新消息",
        "offline_messages_no_new_messages" => "离线会话缺口核对",
        "offline_messages_timeout" => "离线消息补齐确认",
        "offline_history_profile" => "完整离线历史连接修复",
        _ => "WhatsApp 数据"
    };
    private sealed record ConversationLeadBinding(string CustomerId, string BindingToken, Lead? Lead)
    {
        public static ConversationLeadBinding Unbound { get; } = new("", "", null);

        public bool HasSameTarget(ConversationLeadBinding other) =>
            Lead is not null && other.Lead is not null &&
            CustomerId.Equals(other.CustomerId, StringComparison.OrdinalIgnoreCase) &&
            BindingToken.Equals(other.BindingToken, StringComparison.Ordinal);
    }

    private sealed record StageOption(string Label, LeadStage Value);
    private sealed record AgentModeOption(string Label, ConversationAgentMode Value);
    private sealed record LabelFilterOption(string Id, string Name);
    private sealed record CommitmentReminderOption(CustomerCommitment Item, string DisplayText);
    private sealed record KnowledgeReferenceRow(string Citation, string Preview, KnowledgeRetrievalHit Hit);
    private sealed record WhatsAppInboxSnapshot(
        BusinessRoleProfile WorkspaceProfile,
        IReadOnlyList<WhatsAppAccount> Accounts,
        IReadOnlyList<Lead> Leads,
        string SelectedAccountId,
        IReadOnlyList<ConversationItem> Conversations,
        int PersistedConversationCount,
        int ContactCount,
        bool CompletedLeadLink);

    private sealed class ConversationItem(string accountId, string phone, string displayName, string jid) : INotifyPropertyChanged
    {
        private string _displayName = displayName; private string _lastMessage = ""; private DateTimeOffset _lastAt; private int _unread; private bool _isPinned; private DateTimeOffset? _pinnedAt; private bool _isGroup; private IReadOnlyList<WhatsAppLabelChip> _labels = [];
        public string AccountId { get; } = accountId; public string Phone { get; } = phone; public string Jid { get; set; } = jid; public string LeadId { get; set; } = ""; public string Id => string.IsNullOrWhiteSpace(Phone) ? $"{AccountId}:{Jid}" : $"{AccountId}:{Phone}"; public ObservableCollection<MessageItem> Messages { get; } = [];
        public bool IsGroup { get => _isGroup; set { if (Set(ref _isGroup, value)) { OnPropertyChanged(nameof(GroupVisibility)); OnPropertyChanged(nameof(PinActionLabel)); } } }
        public Visibility GroupVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
        public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
        public string LastMessage { get => _lastMessage; set => Set(ref _lastMessage, value); }
        public DateTimeOffset LastAt { get => _lastAt; set { if (Set(ref _lastAt, value)) OnPropertyChanged(nameof(LastTimeLabel)); } }
        public string LastTimeLabel => LastAt == default ? "" : LastAt.LocalDateTime.ToString("MM-dd HH:mm");
        public int Unread { get => _unread; set { if (Set(ref _unread, value)) OnPropertyChanged(nameof(UnreadVisibility)); } }
        public DateTimeOffset? LastReadAt { get; set; }
        public Visibility UnreadVisibility => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        public bool IsPinned { get => _isPinned; set { if (Set(ref _isPinned, value)) { OnPropertyChanged(nameof(PinnedVisibility)); OnPropertyChanged(nameof(PinActionLabel)); } } }
        public DateTimeOffset? PinnedAt { get => _pinnedAt; set => Set(ref _pinnedAt, value); }
        public Visibility PinnedVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;
        public string PinActionLabel => IsGroup ? "群聊置顶请在手机 WhatsApp 操作" : IsPinned ? "取消置顶并同步到手机" : "置顶并同步到手机";
        public IReadOnlyList<WhatsAppLabelChip> Labels => _labels;
        public IReadOnlyList<WhatsAppLabelChip> VisibleLabels => _labels.Take(2).ToList();
        public Visibility LabelsVisibility => Labels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AdditionalLabelsVisibility => Labels.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        public string AdditionalLabelsText => Labels.Count > 2 ? $"+{Labels.Count - 2}" : "";
        public string LabelsToolTip => string.Join("、", Labels.Select(label => label.Name));
        public void SetLabels(IEnumerable<WhatsAppLabelChip> labels)
        {
            _labels = labels
                .GroupBy(label => label.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            OnPropertyChanged(nameof(Labels));
            OnPropertyChanged(nameof(VisibleLabels));
            OnPropertyChanged(nameof(LabelsVisibility));
            OnPropertyChanged(nameof(AdditionalLabelsVisibility));
            OnPropertyChanged(nameof(AdditionalLabelsText));
            OnPropertyChanged(nameof(LabelsToolTip));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(property); return true; }
        private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class MessageItem : INotifyPropertyChanged
    {
        private string _translatedText = "";
        private string _translationLanguageName = "";
        public MessageItem(
            string id,
            string text,
            DateTimeOffset timestamp,
            bool fromMe,
            string kind = "text",
            string fileName = "",
            string mimeType = "",
            string mediaPath = "",
            string mediaDownloadError = "",
            WhatsAppMessageStatus status = WhatsAppMessageStatus.Received,
            DateTimeOffset? statusUpdatedAt = null,
            DateTimeOffset? deliveredAt = null,
            DateTimeOffset? readAt = null,
            string failureReason = "",
            string quotedMessageId = "",
            string quotedText = "",
            bool quotedFromMe = false,
            bool isRevoked = false,
            DateTimeOffset? revokedAt = null,
            bool isStatusUpdate = false,
            DateTimeOffset? statusExpiresAt = null,
            string senderName = "",
            bool isGroup = false)
        {
            Id = id; Text = text; Timestamp = timestamp; FromMe = fromMe; Kind = kind; FileName = fileName; MimeType = mimeType; MediaPath = mediaPath; MediaDownloadError = mediaDownloadError;
            Status = status; StatusUpdatedAt = statusUpdatedAt; DeliveredAt = deliveredAt; ReadAt = readAt; FailureReason = failureReason;
            QuotedMessageId = quotedMessageId; QuotedText = quotedText; QuotedFromMe = quotedFromMe; IsRevoked = isRevoked; RevokedAt = revokedAt;
            IsStatusUpdate = isStatusUpdate; StatusExpiresAt = statusExpiresAt;
            SenderName = senderName; IsGroup = isGroup;
            MediaPreview = LoadMediaPreview(kind, mediaPath);
        }

        public string Id { get; private set; }
        public string Text { get; }
        public DateTimeOffset Timestamp { get; private set; }
        public bool FromMe { get; }
        public string Kind { get; private set; }
        public string FileName { get; private set; }
        public string MimeType { get; }
        public string MediaPath { get; }
        public string MediaDownloadError { get; }
        public ImageSource? MediaPreview { get; }
        public WhatsAppMessageStatus Status { get; private set; }
        public DateTimeOffset? StatusUpdatedAt { get; private set; }
        public DateTimeOffset? DeliveredAt { get; private set; }
        public DateTimeOffset? ReadAt { get; private set; }
        public string FailureReason { get; private set; }
        public string QuotedMessageId { get; }
        public string QuotedText { get; }
        public bool QuotedFromMe { get; }
        public bool IsRevoked { get; private set; }
        public DateTimeOffset? RevokedAt { get; private set; }
        public bool IsStatusUpdate { get; }
        public DateTimeOffset? StatusExpiresAt { get; }
        public string SenderName { get; }
        public bool IsGroup { get; }
        public string SenderLabel => string.IsNullOrWhiteSpace(SenderName) ? "群成员" : SenderName;
        public Visibility SenderVisibility => IsGroup && !FromMe ? Visibility.Visible : Visibility.Collapsed;
        private bool IsRevoking { get; set; }
        public string DisplayText => IsRevoked ? (FromMe ? "你撤回了一条消息" : "对方撤回了一条消息") : MessagePreview(Text, Kind, FileName);
        public bool HasMedia => !IsRevoked && Kind is "image" or "video" or "audio" or "document" or "sticker";
        public bool HasDownloadedMedia => !string.IsNullOrWhiteSpace(MediaPath) && File.Exists(MediaPath);
        public string TextContent => IsRevoked ? DisplayText : !string.IsNullOrWhiteSpace(Text) ? Text : HasMedia && HasDownloadedMedia ? "" : DisplayText;
        public Visibility TextVisibility => string.IsNullOrWhiteSpace(TextContent) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ImageVisibility => HasDownloadedMedia && MediaPreview is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FileVisibility => HasDownloadedMedia && MediaPreview is null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MediaMissingVisibility => HasMedia && !HasDownloadedMedia ? Visibility.Visible : Visibility.Collapsed;
        public string MediaActionLabel => Kind switch { "video" => $"▶ 打开视频 {FileName}", "audio" => $"♪ 播放音频 {FileName}", "document" => $"▣ 打开文件 {FileName}", _ => $"打开媒体 {FileName}" };
        public string MediaMissingText => string.IsNullOrWhiteSpace(MediaDownloadError) ? "媒体尚未下载；重新同步后会再次尝试。" : $"媒体下载失败：{MediaDownloadError}";
        public string TimeLabel => Timestamp.LocalDateTime.ToString("MM-dd HH:mm");
        public HorizontalAlignment Alignment => FromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public Brush BubbleBrush => IsStatusUpdate
            ? ThemeBrush("WarningSoft", Color.FromRgb(255, 249, 229))
            : FromMe
                ? ThemeBrush("ChatOutbound", Color.FromRgb(220, 248, 233))
                : ThemeBrush("ChatInbound", Colors.White);
        public Brush BubbleBorderBrush => IsStatusUpdate
            ? ThemeBrush("Warning", Color.FromRgb(232, 198, 108))
            : FromMe
                ? ThemeBrush("PrimarySoft", Color.FromRgb(190, 232, 211))
                : ThemeBrush("Line", Color.FromRgb(223, 230, 226));
        public Visibility StatusUpdateVisibility => IsStatusUpdate ? Visibility.Visible : Visibility.Collapsed;
        public Visibility QuoteVisibility => !IsRevoked && !string.IsNullOrWhiteSpace(QuotedMessageId) ? Visibility.Visible : Visibility.Collapsed;
        public string QuoteHeader => QuotedFromMe ? "你" : "对方";
        public string QuoteText => string.IsNullOrWhiteSpace(QuotedText) ? "[原消息]" : QuotedText;
        public Visibility ReplyMenuVisibility => !IsGroup && !FromMe && !IsRevoked ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RevokeMenuVisibility => !IsGroup && FromMe ? Visibility.Visible : Visibility.Collapsed;
        public bool CanRevoke => !IsGroup && FromMe && !IsRevoked && !IsRevoking && !string.IsNullOrWhiteSpace(Id) && !Id.StartsWith("local-", StringComparison.OrdinalIgnoreCase) && Status is WhatsAppMessageStatus.Sent or WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read;
        public Visibility OutgoingStatusVisibility => FromMe && !IsRevoked ? Visibility.Visible : Visibility.Collapsed;
        public string TranslatedText => _translatedText;
        public string TranslationLabel => string.IsNullOrWhiteSpace(_translationLanguageName)
            ? "译文"
            : $"译文 · {_translationLanguageName}";
        public Visibility TranslationVisibility => !IsRevoked && !string.IsNullOrWhiteSpace(_translatedText)
            ? Visibility.Visible
            : Visibility.Collapsed;
        public string ReceiptGlyph => !FromMe || IsRevoked ? "" : Status switch
        {
            WhatsAppMessageStatus.Pending => "…",
            WhatsAppMessageStatus.Sent => "✓",
            WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read => "✓✓",
            WhatsAppMessageStatus.Failed => "!",
            _ => ""
        };
        public Brush ReceiptBrush => Status switch
        {
            WhatsAppMessageStatus.Read => ThemeBrush("Info", Color.FromRgb(31, 142, 213)),
            WhatsAppMessageStatus.Failed => ThemeBrush("Danger", Color.FromRgb(183, 57, 57)),
            WhatsAppMessageStatus.Delivered => ThemeBrush("InkSecondary", Color.FromRgb(89, 105, 97)),
            _ => ThemeBrush("Muted", Color.FromRgb(104, 118, 111))
        };
        public string StatusDetailLabel => !FromMe ? "" : IsRevoked ? $"已从双方设备撤回 · {At(RevokedAt ?? Timestamp)}" : Status switch
        {
            WhatsAppMessageStatus.Pending when !string.IsNullOrWhiteSpace(FailureReason) => $"状态待确认 · 发送 {At(Timestamp)}",
            WhatsAppMessageStatus.Pending => $"发送中 · {At(Timestamp)}",
            WhatsAppMessageStatus.Sent => $"发送 {At(Timestamp)} · 尚未送达",
            WhatsAppMessageStatus.Delivered => $"发送 {At(Timestamp)} · 送达 {At(DeliveredAt ?? StatusUpdatedAt)}",
            WhatsAppMessageStatus.Read => $"发送 {At(Timestamp)} · 送达 {At(DeliveredAt)} · 已读 {At(ReadAt ?? StatusUpdatedAt)}",
            WhatsAppMessageStatus.Failed => $"发送失败 {At(StatusUpdatedAt ?? Timestamp)}{(string.IsNullOrWhiteSpace(FailureReason) ? "" : $" · {ShortReason(FailureReason)}")}",
            _ => $"发送 {At(Timestamp)}"
        };

        public bool ShouldReplaceContentWith(MessageItem candidate)
        {
            var currentQuality = ContentQuality();
            var candidateQuality = candidate.ContentQuality();
            if (candidateQuality != currentQuality) return candidateQuality > currentQuality;
            if (!string.Equals(candidate.Text, Text, StringComparison.Ordinal)) return !string.IsNullOrWhiteSpace(candidate.Text);
            if (!HasDownloadedMedia && candidate.HasDownloadedMedia) return true;
            return candidate.IsRevoked && !IsRevoked;
        }

        private int ContentQuality()
        {
            if (IsRevoked) return 4;
            if (!string.IsNullOrWhiteSpace(Text)) return 3;
            if (HasMedia || Kind is "contact" or "location" or "poll" or "reaction" or "event") return 2;
            if (Kind is "unavailable" or "unknown" or "text") return 0;
            return 1;
        }

        public void UpdateTransport(string id, DateTimeOffset timestamp, string kind, string fileName)
        {
            Id = id; Timestamp = timestamp; Kind = kind; FileName = fileName;
            NotifyAll();
        }

        private static Brush ThemeBrush(string key, Color fallback)
        {
            return Application.Current?.TryFindResource(key) as Brush
                   ?? new SolidColorBrush(fallback);
        }

        public void UpdateStatus(WhatsAppMessageStatus status, DateTimeOffset? statusAt, DateTimeOffset? deliveredAt, DateTimeOffset? readAt, string failureReason)
        {
            if (CanAdvance(Status, status)) Status = status;
            if (statusAt is not null && (StatusUpdatedAt is null || statusAt > StatusUpdatedAt)) StatusUpdatedAt = statusAt;
            if (deliveredAt is not null && (DeliveredAt is null || deliveredAt < DeliveredAt)) DeliveredAt = deliveredAt;
            if (readAt is not null && (ReadAt is null || readAt < ReadAt)) ReadAt = readAt;
            if (Status == WhatsAppMessageStatus.Read && DeliveredAt is null) DeliveredAt = ReadAt ?? StatusUpdatedAt;
            if (!string.IsNullOrWhiteSpace(failureReason)) FailureReason = failureReason;
            NotifyAll();
        }

        public void SetRevoking(bool value)
        {
            IsRevoking = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRevoke)));
        }

        public void MarkRevoked(DateTimeOffset? revokedAt)
        {
            IsRevoked = true;
            RevokedAt ??= revokedAt ?? DateTimeOffset.Now;
            IsRevoking = false;
            NotifyAll();
        }

        public void SetTranslation(string text, string targetLanguageName)
        {
            _translatedText = text.Trim();
            _translationLanguageName = targetLanguageName.Trim();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslatedText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationVisibility)));
        }

        public void ClearTranslation()
        {
            if (_translatedText.Length == 0 && _translationLanguageName.Length == 0) return;
            _translatedText = "";
            _translationLanguageName = "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslatedText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationVisibility)));
        }

        private static bool CanAdvance(WhatsAppMessageStatus current, WhatsAppMessageStatus next)
        {
            if (current == next) return true;
            if (next == WhatsAppMessageStatus.Failed) return current == WhatsAppMessageStatus.Pending;
            if (current == WhatsAppMessageStatus.Failed) return next is WhatsAppMessageStatus.Sent or WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read;
            static int Rank(WhatsAppMessageStatus value) => value switch
            {
                WhatsAppMessageStatus.Pending => 0, WhatsAppMessageStatus.Sent => 1,
                WhatsAppMessageStatus.Delivered => 2, WhatsAppMessageStatus.Read => 3,
                WhatsAppMessageStatus.Received => 3, _ => -1
            };
            return Rank(next) >= Rank(current);
        }

        private static string At(DateTimeOffset? value) => value is null ? "--" : value.Value.LocalDateTime.ToString("MM-dd HH:mm");
        private static string ShortReason(string value) => value.Length <= 60 ? value : value[..60] + "…";
        private static ImageSource? LoadMediaPreview(string kind, string mediaPath)
        {
            if (kind is not ("image" or "sticker") || string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath)) return null;
            try
            {
                using var stream = new FileStream(mediaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
                return image;
            }
            catch { return null; }
        }
        private void NotifyAll()
        {
            foreach (var name in new[] { nameof(Id), nameof(DisplayText), nameof(TextContent), nameof(TextVisibility), nameof(HasMedia), nameof(ImageVisibility), nameof(FileVisibility), nameof(MediaMissingVisibility), nameof(TimeLabel), nameof(ReceiptGlyph), nameof(ReceiptBrush), nameof(StatusDetailLabel), nameof(OutgoingStatusVisibility), nameof(QuoteVisibility), nameof(ReplyMenuVisibility), nameof(RevokeMenuVisibility), nameof(CanRevoke), nameof(IsRevoked), nameof(RevokedAt), nameof(SenderLabel), nameof(SenderVisibility) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
