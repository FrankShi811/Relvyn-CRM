using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record WhatsAppSyncProgress(
    string AccountId,
    string State,
    string Phase,
    int? Progress,
    int Contacts,
    int Chats,
    int Messages,
    bool ExistingSession,
    string Error = "",
    int RecoveredMessages = 0,
    int RequestedChats = 0);

/// <summary>
/// A message that reached local storage, together with how it got here.
///
/// The arrival channel is the difference between "a customer just wrote to you"
/// and "WhatsApp flushed three days of queued messages because you opened the
/// laptop", and only the first of those may drive an automatic reply.
/// </summary>
public sealed record WhatsAppMessageSyncedEvent(WhatsAppMessage Message, MessageArrival Arrival)
{
    public bool IsOfflineBacklog => Arrival == MessageArrival.OfflineBacklog;
}

/// <summary>Raised when a reconnect finished flushing the offline queue.</summary>
public sealed record WhatsAppOfflineCatchupEvent(string AccountId, bool Started);

public sealed class WhatsAppSyncService
{
    /// <summary>Automation settings are re-read at most this often, not per message.</summary>
    private static readonly TimeSpan AutomationSettingsTtl = TimeSpan.FromSeconds(30);

    private readonly LocalRepository _repository;
    private readonly Channel<WhatsAppBridgeEvent> _events = Channel.CreateUnbounded<WhatsAppBridgeEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, byte> _offlineCatchupAccounts =
        new(StringComparer.OrdinalIgnoreCase);
    private AgentAutomationSettings _automation = new();
    private DateTimeOffset _automationLoadedAt = DateTimeOffset.MinValue;

    public event EventHandler<WhatsAppMessageSyncedEvent>? MessageSynchronized;
    public event EventHandler<WhatsAppSyncProgress>? SynchronizationChanged;
    public event EventHandler<WhatsAppOfflineCatchupEvent>? OfflineCatchupChanged;
    public event EventHandler<string>? SyncError;

    /// <summary>Overridable clock so the age threshold can be tested deterministically.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    public WhatsAppSyncService(LocalRepository repository, WhatsAppConnectionManager bridge)
    {
        _repository = repository;
        bridge.EventReceived += (_, e) => _events.Writer.TryWrite(e);
        _ = Task.Run(ProcessEventsAsync);
    }

    public bool IsOfflineCatchupActive(string accountId) =>
        !string.IsNullOrWhiteSpace(accountId) && _offlineCatchupAccounts.ContainsKey(accountId);

    private async Task<AgentAutomationSettings> GetAutomationSettingsAsync()
    {
        var now = Clock();
        if (now - _automationLoadedAt < AutomationSettingsTtl) return _automation;
        try
        {
            _automation = (await _repository.GetAppSettingsAsync()).AgentAutomation ?? new AgentAutomationSettings();
        }
        catch
        {
            // Fail closed in both directions: keep the gate on, and use the
            // narrowest grace window rather than the default, so an unreadable
            // settings row can only classify *more* traffic as backlog, never less.
            _automation = new AgentAutomationSettings
            {
                OfflineGraceMinutes = WhatsAppMessageArrivalClassifier.MinimumOfflineGraceMinutes
            };
        }
        _automationLoadedAt = now;
        return _automation;
    }

    private async Task ProcessEventsAsync()
    {
        await foreach (var item in _events.Reader.ReadAllAsync())
        {
            try { await HandleAsync(item); }
            catch (Exception error) { SyncError?.Invoke(this, error.Message); }
        }
    }

    private async Task HandleAsync(WhatsAppBridgeEvent e)
    {
        var accountId = string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId;
        switch (e.Name)
        {
            case "message":
                await IngestMessageAsync(accountId, e.Data);
                return;
            case "message_status":
                await IngestStatusAsync(e);
                return;
            case "message_revoked":
                await IngestRevocationAsync(accountId, e.Data);
                return;
            case "contacts_upsert":
                foreach (var item in Items(e.Data)) await IngestContactAsync(accountId, item);
                RaiseDataChanged(accountId, "contacts");
                return;
            case "chats_upsert":
                foreach (var item in Items(e.Data)) await IngestChatAsync(accountId, item);
                RaiseDataChanged(accountId, "chats");
                return;
            case "label_upsert":
                await IngestLabelAsync(accountId, e.Data);
                RaiseDataChanged(accountId, "labels");
                return;
            case "chat_label_upsert":
                await IngestChatLabelAsync(accountId, e.Data);
                RaiseDataChanged(accountId, "labels");
                return;
            case "messages_history":
                foreach (var item in Items(e.Data))
                {
                    if (Bool(item, "isRevocation")) await IngestRevocationAsync(accountId, item);
                    else await IngestMessageAsync(accountId, item);
                }
                RaiseDataChanged(accountId, "messages");
                return;
            case "sync_status":
                var progress = ParseProgress(accountId, e.Data);
                RaiseOfflineCatchupChange(progress);
                SynchronizationChanged?.Invoke(this, progress);
                return;
        }
    }

    private async Task IngestContactAsync(string accountId, JsonElement data)
    {
        var jid = Text(data, "jid");
        var sourceJid = Text(data, "sourceJid");
        if (string.IsNullOrWhiteSpace(jid)) jid = sourceJid;
        if (string.IsNullOrWhiteSpace(jid)) return;
        var phone = Digits(Text(data, "phone"));
        var displayName = WhatsAppTextEncodingRepair.Repair(FirstText(data, "displayName", "savedName", "notifyName", "verifiedName", "username"));
        if (string.IsNullOrWhiteSpace(displayName)) displayName = string.IsNullOrWhiteSpace(phone) ? jid : $"+{phone}";
        var contact = new WhatsAppContact
        {
            Id = $"{accountId}:{(string.IsNullOrWhiteSpace(sourceJid) ? jid : sourceJid)}",
            AccountId = accountId,
            Jid = jid,
            SourceJid = sourceJid,
            Phone = phone,
            DisplayName = displayName,
            SavedName = WhatsAppTextEncodingRepair.Repair(Text(data, "savedName")),
            NotifyName = WhatsAppTextEncodingRepair.Repair(Text(data, "notifyName")),
            VerifiedName = WhatsAppTextEncodingRepair.Repair(Text(data, "verifiedName")),
            Username = WhatsAppTextEncodingRepair.Repair(Text(data, "username")),
            Source = Text(data, "source")
        };
        await _repository.UpsertWhatsAppContactAsync(contact);
        if (string.IsNullOrWhiteSpace(phone)) return;
        var ownedPeer = await _repository.GetOwnedWhatsAppPeerAccountAsync(accountId, phone);
        var conversation = await _repository.GetWhatsAppConversationAsync(accountId, phone) ?? new WhatsAppConversation
        {
            Id = $"{accountId}:{phone}", AccountId = accountId, Phone = phone
        };
        var resolution = ownedPeer is null
            ? await ResolveConversationLeadAsync(accountId, conversation.Id, phone)
            : (HasIdentityDecision: false, Lead: (Lead?)null);
        var lead = resolution.Lead;
        if (lead is not null)
        {
            conversation.LeadId = lead.Id;
        }
        else if (ownedPeer is not null)
        {
            conversation.LeadId = "";
        }
        else if (resolution.HasIdentityDecision)
        {
            conversation.LeadId = "";
        }
        conversation.DisplayName = WhatsAppConversationNaming.Resolve(
            lead,
            phone,
            ownedPeer?.Name,
            contact.SavedName,
            contact.DisplayName,
            contact.NotifyName,
            contact.VerifiedName,
            contact.Username,
            conversation.DisplayName);
        await _repository.UpsertWhatsAppConversationAsync(conversation);
    }

    private async Task IngestChatAsync(string accountId, JsonElement data)
    {
        var jid = FirstText(data, "groupJid", "jid", "sourceJid").Trim();
        var isGroup = Bool(data, "isGroup") || jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
        if (isGroup)
        {
            if (!jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return;
            var conversationId = $"{accountId}:{jid}";
            var groupConversation = await _repository.GetWhatsAppConversationByIdAsync(conversationId) ?? new WhatsAppConversation
            {
                Id = conversationId,
                AccountId = accountId,
                Jid = jid,
                IsGroup = true
            };
            groupConversation.Jid = jid;
            groupConversation.IsGroup = true;
            groupConversation.Phone = "";
            groupConversation.LeadId = "";
            var groupName = WhatsAppTextEncodingRepair.Repair(FirstText(data, "displayName", "groupName"));
            if (!string.IsNullOrWhiteSpace(groupName)) groupConversation.DisplayName = groupName;
            ApplyChatSnapshot(groupConversation, data);
            if (groupConversation.LastReadAt is not null)
                groupConversation.UnreadCount = await _repository.CountUnreadWhatsAppMessagesAsync(groupConversation.Id, groupConversation.LastReadAt);
            else if (data.TryGetProperty("unreadCount", out var groupUnread) && groupUnread.ValueKind == JsonValueKind.Number && groupUnread.TryGetInt32(out var groupUnreadCount))
                groupConversation.UnreadCount = Math.Max(0, groupUnreadCount);
            if (string.IsNullOrWhiteSpace(groupConversation.DisplayName)) groupConversation.DisplayName = "WhatsApp 群聊";
            await _repository.UpsertWhatsAppConversationAsync(groupConversation);
            return;
        }

        var phone = Digits(Text(data, "phone"));
        if (string.IsNullOrWhiteSpace(phone)) return;
        var ownedPeer = await _repository.GetOwnedWhatsAppPeerAccountAsync(accountId, phone);
        var conversation = await _repository.GetWhatsAppConversationAsync(accountId, phone) ?? new WhatsAppConversation
        {
            Id = $"{accountId}:{phone}", AccountId = accountId, Phone = phone
        };
        var resolution = ownedPeer is null
            ? await ResolveConversationLeadAsync(accountId, conversation.Id, phone)
            : (HasIdentityDecision: false, Lead: (Lead?)null);
        var lead = resolution.Lead;
        var displayName = WhatsAppTextEncodingRepair.Repair(Text(data, "displayName"));
        if (lead is not null)
        {
            conversation.LeadId = lead.Id;
        }
        else if (ownedPeer is not null)
        {
            conversation.LeadId = "";
        }
        else if (resolution.HasIdentityDecision)
        {
            conversation.LeadId = "";
        }
        conversation.DisplayName = WhatsAppConversationNaming.Resolve(
            lead,
            phone,
            ownedPeer?.Name,
            displayName,
            conversation.DisplayName);
        ApplyChatSnapshot(conversation, data);
        if (conversation.LastReadAt is not null)
        {
            // WhatsApp history sync can repeatedly return the phone's old unread
            // counter. Once the desktop user has opened a conversation, derive the
            // badge from locally persisted messages newer than that read cursor.
            conversation.UnreadCount = await _repository.CountUnreadWhatsAppMessagesAsync(conversation.Id, conversation.LastReadAt);
        }
        else if (data.TryGetProperty("unreadCount", out var unread) && unread.ValueKind == JsonValueKind.Number && unread.TryGetInt32(out var unreadCount))
        {
            conversation.UnreadCount = Math.Max(0, unreadCount);
        }
        if (string.IsNullOrWhiteSpace(conversation.DisplayName)) conversation.DisplayName = $"+{phone}";
        await _repository.UpsertWhatsAppConversationAsync(conversation);
    }

    private async Task IngestLabelAsync(string accountId, JsonElement data)
    {
        var id = Text(data, "id");
        if (string.IsNullOrWhiteSpace(id)) return;
        var name = WhatsAppTextEncodingRepair.Repair(Text(data, "name"));
        if (string.IsNullOrWhiteSpace(name)) name = id;
        var label = new WhatsAppLabel
        {
            Id = id,
            AccountId = accountId,
            Name = name,
            Color = Int(data, "color"),
            Deleted = Bool(data, "deleted"),
            PredefinedId = NullableInt(data, "predefinedId"),
            UpdatedAt = DateTimeOffset.Now
        };
        await _repository.UpsertWhatsAppLabelAsync(label);
    }

    private async Task IngestChatLabelAsync(string accountId, JsonElement data)
    {
        // chat_id is stored as the bare phone (matches WhatsAppConversation.Id
        // which is accountId:phone); the bridge sends both jid and phone.
        var chatId = Text(data, "phone");
        if (string.IsNullOrWhiteSpace(chatId)) chatId = Text(data, "chatId");
        var labelId = Text(data, "labelId");
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(labelId)) return;
        await _repository.SetWhatsAppChatLabelAsync(accountId, chatId, labelId, Text(data, "type") != "remove");
    }

    private async Task IngestMessageAsync(string accountId, JsonElement data)
    {
        var jid = FirstText(data, "groupJid", "jid", "sourceJid").Trim();
        var isGroup = Bool(data, "isGroup") || jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
        var phone = Digits(Text(data, "phone"));
        var providerId = Text(data, "id");
        if (string.IsNullOrWhiteSpace(providerId) ||
            (isGroup ? !jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) : string.IsNullOrWhiteSpace(phone)))
            return;
        var existingMessage = await _repository.GetWhatsAppMessageByProviderIdAsync(accountId, providerId);
        var fromMe = Bool(data, "fromMe");
        var timestamp = DateTimeOffset.TryParse(Text(data, "timestamp"), out var parsed) ? parsed : DateTimeOffset.Now;
        var deliveredAt = ParseTimestamp(data, "deliveredAt");
        var readAt = ParseTimestamp(data, "readAt");
        var source = Text(data, "source");
        var automation = await GetAutomationSettingsAsync();
        var arrival = WhatsAppMessageArrivalClassifier.Classify(
            source, timestamp, Clock(), automation.NormalizedGraceMinutes());
        // `historical` still means "bulk history sync" only. Offline backlog is a
        // real, unread message that belongs in the conversation and in the unread
        // count; what it must not do is trigger an automatic reply.
        var historical = arrival == MessageArrival.HistorySync;
        var ownedPeer = isGroup ? null : await _repository.GetOwnedWhatsAppPeerAccountAsync(accountId, phone);
        var conversationId = isGroup ? $"{accountId}:{jid}" : $"{accountId}:{phone}";
        var conversation = isGroup
            ? await _repository.GetWhatsAppConversationByIdAsync(conversationId)
            : await _repository.GetWhatsAppConversationAsync(accountId, phone);
        conversation ??= new WhatsAppConversation
        {
            Id = conversationId,
            AccountId = accountId,
            Jid = jid,
            Phone = isGroup ? "" : phone,
            IsGroup = isGroup,
            DisplayName = isGroup
                ? WhatsAppTextEncodingRepair.Repair(FirstText(data, "groupName", "displayName"))
                : !fromMe && !string.IsNullOrWhiteSpace(Text(data, "pushName"))
                    ? WhatsAppTextEncodingRepair.Repair(Text(data, "pushName"))
                    : ownedPeer?.Name ?? $"+{phone}"
        };
        var resolution = isGroup || ownedPeer is not null
            ? (HasIdentityDecision: false, Lead: (Lead?)null)
            : await ResolveConversationLeadAsync(accountId, conversationId, phone);
        var lead = resolution.Lead;
        if (string.IsNullOrWhiteSpace(conversation.Jid)) conversation.Jid = jid;
        conversation.IsGroup = isGroup;
        if (isGroup)
        {
            conversation.Phone = "";
            conversation.LeadId = "";
            var groupName = WhatsAppTextEncodingRepair.Repair(FirstText(data, "groupName", "displayName"));
            if (!string.IsNullOrWhiteSpace(groupName)) conversation.DisplayName = groupName;
            if (string.IsNullOrWhiteSpace(conversation.DisplayName)) conversation.DisplayName = "WhatsApp 群聊";
        }
        if (lead is not null)
        {
            conversation.LeadId = lead.Id;
        }
        else if (ownedPeer is not null)
        {
            conversation.LeadId = "";
        }
        else if (resolution.HasIdentityDecision)
        {
            conversation.LeadId = "";
        }
        if (!isGroup)
        {
            var pushName = WhatsAppTextEncodingRepair.Repair(Text(data, "pushName"));
            conversation.DisplayName = WhatsAppConversationNaming.Resolve(
                lead,
                phone,
                ownedPeer?.Name,
                pushName,
                conversation.DisplayName);
        }
        var message = new WhatsAppMessage
        {
            Id = $"{accountId}:{providerId}",
            ProviderMessageId = providerId,
            AccountId = accountId,
            ConversationId = conversationId,
            LeadId = lead?.Id ?? "",
            Jid = jid,
            Phone = isGroup ? "" : phone,
            IsGroup = isGroup,
            ParticipantJid = Text(data, "participantJid"),
            ParticipantPhone = Digits(Text(data, "participantPhone")),
            ParticipantName = WhatsAppTextEncodingRepair.Repair(FirstText(data, "participantName", "pushName")),
            Direction = fromMe ? WhatsAppMessageDirection.Outgoing : WhatsAppMessageDirection.Incoming,
            Status = fromMe ? ParseOutgoingStatus(data, deliveredAt, readAt) : WhatsAppMessageStatus.Received,
            Kind = Text(data, "kind"),
            Body = WhatsAppTextEncodingRepair.Repair(Text(data, "text")),
            FileName = WhatsAppTextEncodingRepair.Repair(Text(data, "fileName")),
            MimeType = Text(data, "mimeType"),
            MediaPath = Text(data, "mediaPath"),
            MediaDownloadError = Text(data, "mediaDownloadError"),
            PushName = WhatsAppTextEncodingRepair.Repair(Text(data, "pushName")),
            QuotedMessageId = Text(data, "quotedMessageId"),
            QuotedText = WhatsAppTextEncodingRepair.Repair(Text(data, "quotedText")),
            QuotedFromMe = Bool(data, "quotedFromMe"),
            IsRevoked = Bool(data, "isRevoked"),
            RevokedAt = ParseTimestamp(data, "revokedAt"),
            IsStatusUpdate = Bool(data, "isStatusUpdate"),
            StatusExpiresAt = ParseTimestamp(data, "statusExpiresAt"),
            Timestamp = timestamp,
            DeliveredAt = deliveredAt,
            ReadAt = readAt,
            StatusUpdatedAt = readAt ?? deliveredAt,
            Source = source
        };
        var contentRecovered = existingMessage is not null
                               && !HasUsableContent(existingMessage)
                               && HasUsableContent(message);
        // The message table references the owning conversation. A first-ever
        // live message can arrive before any contact/chat snapshot, so persist
        // the conversation shell first and then apply the final unread/preview
        // update below after the message insert succeeds.
        await _repository.UpsertWhatsAppConversationAsync(conversation, allowUnreadIncrease: false);
        var inserted = await _repository.UpsertWhatsAppMessageAsync(message);
        message = await _repository.GetWhatsAppMessageByProviderIdAsync(accountId, providerId) ?? message;
        var usableContent = HasUsableContent(message);
        if (timestamp >= conversation.LastMessageAt)
        {
            conversation.LastMessageAt = timestamp;
            var preview = MessagePreview(message);
            if (isGroup && !fromMe)
            {
                var sender = FirstText(data, "participantName", "pushName", "participantPhone");
                if (!string.IsNullOrWhiteSpace(sender)) preview = $"{WhatsAppTextEncodingRepair.Repair(sender)}：{preview}";
            }
            conversation.LastMessage = message.IsStatusUpdate ? $"[最新动态] {preview}" : preview;
        }
        var unreadIncreased = inserted && !fromMe && !historical && !message.IsStatusUpdate &&
                              (conversation.LastReadAt is null || timestamp > conversation.LastReadAt.Value);
        if (unreadIncreased)
        {
            // Late/out-of-order bridge events that predate the local read cursor
            // are history, even when the bridge did not label them as history.
            conversation.UnreadCount++;
        }
        await _repository.UpsertWhatsAppConversationAsync(conversation, allowUnreadIncrease: unreadIncreased);
        var confirmedOutgoing = !fromMe ||
                                message.Status is WhatsAppMessageStatus.Sent
                                    or WhatsAppMessageStatus.Delivered
                                    or WhatsAppMessageStatus.Read;
        var newlyAvailable = (inserted && usableContent) || contentRecovered;
        if (newlyAvailable && !message.IsStatusUpdate && confirmedOutgoing)
        {
            message = await _repository.ApplySynchronizedWhatsAppMessageOutcomeAsync(
                          message.Id,
                          fromMe ? "whatsapp_message_sent" : "whatsapp_message_received",
                          $"message_id={providerId}; account={accountId}")
                      ?? message;
        }
        if (newlyAvailable && !historical)
            MessageSynchronized?.Invoke(this, new WhatsAppMessageSyncedEvent(message, arrival));
    }

    private static bool HasUsableContent(WhatsAppMessage message) =>
        !string.IsNullOrWhiteSpace(message.Body)
        || message.Kind is "image" or "video" or "audio" or "document" or "sticker"
            or "contact" or "location" or "poll" or "reaction" or "event";

    private async Task<(bool HasIdentityDecision, Lead? Lead)> ResolveConversationLeadAsync(
        string accountId,
        string conversationId,
        string phone)
    {
        var identityLink = await _repository.GetWhatsAppIdentityLinkAsync(accountId, conversationId);
        if (identityLink is not null)
            return (
                true,
                identityLink.IsActive && !string.IsNullOrWhiteSpace(identityLink.CustomerId)
                    ? await _repository.GetLeadAsync(identityLink.CustomerId)
                    : null);
        return (false, await _repository.GetLeadByPhoneAsync(phone));
    }

    private static string MessagePreview(WhatsAppMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Body)) return message.Body;
        return message.Kind switch
        {
            "image" => "[图片]",
            "video" => "[视频]",
            "audio" => "[音频]",
            "document" => string.IsNullOrWhiteSpace(message.FileName) ? "[文件]" : $"[文件] {message.FileName}",
            "sticker" => "[贴图]",
            "contact" => "[联系人]",
            "location" => "[位置]",
            "poll" => "[投票]",
            "reaction" => "[表情回应]",
            "event" => "[活动]",
            "unavailable" => "[正在从手机恢复消息内容]",
            "unknown" => "[消息内容未同步成功]",
            _ => "[暂不支持的 WhatsApp 消息]"
        };
    }

    private async Task IngestRevocationAsync(string accountId, JsonElement data)
    {
        var providerId = Text(data, "revokedMessageId");
        if (string.IsNullOrWhiteSpace(providerId)) return;
        var revokedAt = ParseTimestamp(data, "timestamp") ?? DateTimeOffset.Now;
        var message = await _repository.MarkWhatsAppMessageRevokedAsync(accountId, providerId, revokedAt);
        if (message is null) return;
        var conversation = await _repository.GetWhatsAppConversationByIdAsync(message.ConversationId);
        if (conversation is not null && conversation.LastMessageAt <= message.Timestamp)
        {
            conversation.LastMessage = message.Direction == WhatsAppMessageDirection.Outgoing ? "你撤回了一条消息" : "对方撤回了一条消息";
            conversation.LastMessageAt = message.Timestamp;
            await _repository.UpsertWhatsAppConversationAsync(conversation);
        }
        message = await _repository.ApplySynchronizedWhatsAppMessageOutcomeAsync(
            message.Id,
            "whatsapp_message_revoked",
            $"message_id={providerId}; account={accountId}") ?? message;
        // A revocation is always a live event, whatever the age of the message
        // being revoked.
        MessageSynchronized?.Invoke(this, new WhatsAppMessageSyncedEvent(message, MessageArrival.Live));
    }

    private async Task IngestStatusAsync(WhatsAppBridgeEvent e)
    {
        var providerId = Text(e.Data, "id");
        if (string.IsNullOrWhiteSpace(providerId)) return;
        var numeric = e.Data.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var value) ? value : -1;
        var status = numeric switch
        {
            <= 0 => WhatsAppMessageStatus.Failed,
            1 => WhatsAppMessageStatus.Pending,
            2 => WhatsAppMessageStatus.Sent,
            3 => WhatsAppMessageStatus.Delivered,
            >= 4 => WhatsAppMessageStatus.Read
        };
        var statusAt = ParseTimestamp(e.Data, "statusAt") ?? DateTimeOffset.Now;
        var deliveredAt = ParseTimestamp(e.Data, "deliveredAt");
        var readAt = ParseTimestamp(e.Data, "readAt");
        var message = await _repository.UpdateWhatsAppMessageStatusAsync(
            string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId,
            providerId,
            status,
            statusAt,
            deliveredAt,
            readAt,
            Text(e.Data, "failureReason"));
        if (message is null) return;
        if (message.Direction == WhatsAppMessageDirection.Outgoing)
            message = await _repository.ApplySynchronizedWhatsAppMessageOutcomeAsync(message.Id, "", "") ?? message;
        // A delivery receipt is live news about an existing message; it never
        // re-opens automation for the message body itself.
        MessageSynchronized?.Invoke(this, new WhatsAppMessageSyncedEvent(message, MessageArrival.Live));
    }

    private static WhatsAppMessageStatus ParseOutgoingStatus(JsonElement data, DateTimeOffset? deliveredAt, DateTimeOffset? readAt)
    {
        if (readAt is not null) return WhatsAppMessageStatus.Read;
        if (deliveredAt is not null) return WhatsAppMessageStatus.Delivered;
        if (!data.TryGetProperty("status", out var statusElement) || !statusElement.TryGetInt32(out var numeric)) return WhatsAppMessageStatus.Pending;
        return numeric switch
        {
            <= 0 => WhatsAppMessageStatus.Failed,
            1 => WhatsAppMessageStatus.Pending,
            2 => WhatsAppMessageStatus.Sent,
            3 => WhatsAppMessageStatus.Delivered,
            >= 4 => WhatsAppMessageStatus.Read
        };
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement data, string name) =>
        DateTimeOffset.TryParse(Text(data, name), out var timestamp) ? timestamp : null;

    private static void ApplyChatSnapshot(WhatsAppConversation conversation, JsonElement data)
    {
        var lastMessage = WhatsAppTextEncodingRepair.Repair(Text(data, "lastMessage"));
        if (DateTimeOffset.TryParse(Text(data, "lastMessageAt"), out var lastAt) && lastAt >= conversation.LastMessageAt)
        {
            conversation.LastMessageAt = lastAt;
            if (!string.IsNullOrWhiteSpace(lastMessage)) conversation.LastMessage = lastMessage;
        }
        if (data.TryGetProperty("pinned", out var pinned) && pinned.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            conversation.IsPinned = pinned.GetBoolean();
            conversation.PinnedAt = conversation.IsPinned && DateTimeOffset.TryParse(Text(data, "pinnedAt"), out var pinnedAt) ? pinnedAt : null;
        }
    }

    private static string Text(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static int? NullableInt(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric) ? numeric : null;
    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());
    private static IEnumerable<JsonElement> Items(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
    private static string FirstText(JsonElement data, params string[] names)
    {
        foreach (var name in names) if (Text(data, name) is { Length: > 0 } value) return value;
        return "";
    }

    /// <summary>
    /// Turns the bridge's offline-catch-up phases into a start/finish signal.
    /// The coordinator uses it to scope its per-catch-up draft budget: coming
    /// back from three days offline must cost at most one bounded batch of LLM
    /// calls, not one per waiting conversation.
    /// </summary>
    private void RaiseOfflineCatchupChange(WhatsAppSyncProgress progress)
    {
        if (!progress.Phase.StartsWith("offline_messages", StringComparison.OrdinalIgnoreCase)) return;
        if (progress.State.Equals("syncing", StringComparison.OrdinalIgnoreCase))
        {
            _offlineCatchupAccounts[progress.AccountId] = 0;
            OfflineCatchupChanged?.Invoke(this, new WhatsAppOfflineCatchupEvent(progress.AccountId, true));
        }
        else if (progress.State.Equals("complete", StringComparison.OrdinalIgnoreCase)
                 || progress.State.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            _offlineCatchupAccounts.TryRemove(progress.AccountId, out _);
            OfflineCatchupChanged?.Invoke(this, new WhatsAppOfflineCatchupEvent(progress.AccountId, false));
        }
    }

    private void RaiseDataChanged(string accountId, string phase) =>
        SynchronizationChanged?.Invoke(this, new WhatsAppSyncProgress(accountId, "data", phase, null, 0, 0, 0, false));

    private static WhatsAppSyncProgress ParseProgress(string accountId, JsonElement data) => new(
        accountId,
        Text(data, "state"),
        Text(data, "phase"),
        data.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Number && progress.TryGetInt32(out var numeric) ? numeric : null,
        Int(data, "contacts"),
        Int(data, "chats"),
        Int(data, "messages"),
        Bool(data, "existingSession"),
        Text(data, "error"),
        Int(data, "recoveredMessages"),
        Int(data, "requestedChats"));

    private static int Int(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric) ? numeric : 0;
}
