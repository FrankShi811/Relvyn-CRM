using System.Collections.Concurrent;
using System.Text.Json;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class WhatsAppConnectionManager :
    IWhatsAppNumberRegistrationLookup,
    ICustomerSuccessMessageSender,
    ICustomerSuccessHostingReadiness,
    IAsyncDisposable
{
    private readonly string _dataRoot;
    private readonly ConcurrentDictionary<string, WhatsAppBridgeClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _autoReconnectSuppressed = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<WhatsAppBridgeEvent>? EventReceived;
    public string ActiveAccountId { get; private set; } = "primary";
    public bool IsConnected => IsConnectedFor(ActiveAccountId);
    public string ConnectionState => ConnectionStateFor(ActiveAccountId);

    public WhatsAppConnectionManager(string? dataRoot = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot
            ?? new DataWorkspaceManager().Resolve().RootDirectory);
    }

    /// <summary>
    /// Outbound governor configuration applied to every bridge client, existing
    /// and future. Set once from persisted settings during startup; see
    /// <c>AppServices.InitializeAsync</c>.
    /// </summary>
    public OutboundGovernorSettings OutboundSettings
    {
        get => _outboundSettings;
        set
        {
            _outboundSettings = value ?? new OutboundGovernorSettings();
            foreach (var client in _clients.Values) client.OutboundSettings = _outboundSettings;
        }
    }
    private OutboundGovernorSettings _outboundSettings = new();

    /// <summary>
    /// Pushes new limits to a running bridge. Unlike assigning
    /// <see cref="OutboundSettings"/> this takes effect without a reconnect.
    /// </summary>
    public Task<OutboundGovernorStatus> ConfigureOutboundAsync(string accountId, OutboundGovernorSettings settings, CancellationToken cancellationToken = default)
    {
        OutboundSettings = settings;
        return GetClient(accountId).ConfigureOutboundAsync(settings, cancellationToken);
    }

    /// <summary>Reads today's send budget for the account health panel.</summary>
    public Task<OutboundGovernorStatus> OutboundStatusAsync(string accountId, CancellationToken cancellationToken = default) =>
        GetClient(accountId).OutboundStatusAsync(false, cancellationToken);

    /// <summary>
    /// Clears a governor suspension. A 403 suspension is indefinite by design —
    /// it means WhatsApp may have restricted the account — so it is only ever
    /// lifted by an explicit human action, never automatically.
    /// </summary>
    public Task<OutboundGovernorStatus> ResumeOutboundAsync(string accountId, CancellationToken cancellationToken = default) =>
        GetClient(accountId).OutboundStatusAsync(true, cancellationToken);

    public void SetActiveAccount(string accountId) => ActiveAccountId = Normalize(accountId);
    public bool IsConnectedFor(string accountId) => _clients.TryGetValue(Normalize(accountId), out var client) && client.IsConnected;
    public string ConnectionStateFor(string accountId) => _clients.TryGetValue(Normalize(accountId), out var client) ? client.ConnectionState : "disconnected";
    public string LatestQrDataUrlFor(string accountId) => _clients.TryGetValue(Normalize(accountId), out var client) ? client.LatestQrDataUrl : "";
    public bool IsAutoReconnectEnabled(string accountId) => !_autoReconnectSuppressed.ContainsKey(Normalize(accountId));
    public bool HasStoredSession(string accountId)
    {
        accountId = Normalize(accountId);
        var directory = Path.Combine(_dataRoot, "whatsapp-sessions", accountId);
        return File.Exists(Path.Combine(directory, "creds.json.enc"))
            && new WindowsCredentialStore($"WAFlow/WhatsAppSessionKey/{accountId}").Exists();
    }

    public bool RequiresLocalAuthorization(string accountId)
    {
        accountId = Normalize(accountId);
        var directory = Path.Combine(_dataRoot, "whatsapp-sessions", accountId);
        return File.Exists(Path.Combine(directory, "creds.json.enc"))
            && !new WindowsCredentialStore($"WAFlow/WhatsAppSessionKey/{accountId}").Exists();
    }

    public async Task StartAsync(string accountId = "primary", CancellationToken cancellationToken = default)
    {
        accountId = Normalize(accountId); ActiveAccountId = accountId;
        await GetClient(accountId).StartAsync(accountId, cancellationToken);
    }

    public Task<JsonElement> ConnectAsync(CancellationToken cancellationToken = default) => ConnectAsync(ActiveAccountId, cancellationToken);
    public Task<JsonElement> PingAsync(CancellationToken cancellationToken = default) => GetClient(ActiveAccountId).PingAsync(cancellationToken);
    public async Task<JsonElement> ConnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        accountId = Normalize(accountId); ActiveAccountId = accountId;
        _autoReconnectSuppressed.TryRemove(accountId, out _);
        return await ConnectCoreAsync(accountId, cancellationToken);
    }

    public async Task EnsureConnectedAsync(string accountId, CancellationToken cancellationToken = default)
    {
        accountId = Normalize(accountId);
        if (!IsAutoReconnectEnabled(accountId)) return;
        var state = ConnectionStateFor(accountId);
        if (state is "connected" or "connecting" or "logged_out") return;
        await ConnectCoreAsync(accountId, cancellationToken);
    }

    public Task<JsonElement> DisconnectAsync(CancellationToken cancellationToken = default) => DisconnectAsync(ActiveAccountId, cancellationToken);
    public async Task<JsonElement> DisconnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        accountId = Normalize(accountId);
        _autoReconnectSuppressed[accountId] = 0;
        var client = GetClient(accountId);
        client.CancelPendingPairing();
        var gate = _connectionGates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await client.DisconnectAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public Task<JsonElement> LogoutAsync(CancellationToken cancellationToken = default) => LogoutAsync(ActiveAccountId, cancellationToken);
    public async Task<JsonElement> LogoutAsync(string accountId, CancellationToken cancellationToken = default)
    {
        accountId = Normalize(accountId);
        _autoReconnectSuppressed[accountId] = 0;
        var client = GetClient(accountId);
        client.CancelPendingPairing("本机登录会话正在清除，可稍后重新生成二维码。");
        var gate = _connectionGates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await client.LogoutAsync(cancellationToken); }
        finally { gate.Release(); }
    }
    public Task<JsonElement> SendTextAsync(string phone, string text, CancellationToken cancellationToken = default) => SendTextAsync(ActiveAccountId, phone, text, cancellationToken);
    public Task<JsonElement> SendTextAsync(string accountId, string phone, string text, CancellationToken cancellationToken = default) => SendTextAsync(accountId, phone, text, OutboundSendOptions.Human, cancellationToken);
    public Task<JsonElement> SendTextAsync(string accountId, string phone, string text, OutboundSendOptions options, CancellationToken cancellationToken = default) => GetClient(accountId).SendTextAsync(phone, text, options, cancellationToken);
    public Task<JsonElement> ValidateNumberAsync(string accountId, string phone, CancellationToken cancellationToken = default) => GetClient(accountId).ValidateNumberAsync(phone, cancellationToken);
    public async Task<WhatsAppNumberRegistrationLookupResult> LookupRegistrationAsync(string accountId, string phone, CancellationToken cancellationToken = default)
    {
        var result = await ValidateNumberAsync(accountId, phone, cancellationToken);
        if (!result.TryGetProperty("exists", out var existsElement)
            || existsElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("WhatsApp 号码检测未返回明确的注册结果。");
        var jid = result.TryGetProperty("jid", out var jidElement) ? jidElement.GetString() ?? "" : "";
        return new WhatsAppNumberRegistrationLookupResult(existsElement.GetBoolean(), jid);
    }
    public Task<JsonElement> SendReplyTextAsync(string accountId, string phone, string text, string quotedMessageId, string quotedText, bool quotedFromMe, CancellationToken cancellationToken = default) => GetClient(accountId).SendReplyTextAsync(phone, text, quotedMessageId, quotedText, quotedFromMe, cancellationToken);
    public Task<JsonElement> SendMediaAsync(string phone, string path, string caption = "", CancellationToken cancellationToken = default) => SendMediaAsync(ActiveAccountId, phone, path, caption, cancellationToken);
    public Task<JsonElement> SendMediaAsync(string accountId, string phone, string path, string caption, CancellationToken cancellationToken = default) => GetClient(accountId).SendMediaAsync(phone, path, caption, cancellationToken);
    public Task<JsonElement> SendReplyMediaAsync(string accountId, string phone, string path, string caption, string quotedMessageId, string quotedText, bool quotedFromMe, CancellationToken cancellationToken = default) => GetClient(accountId).SendReplyMediaAsync(phone, path, caption, quotedMessageId, quotedText, quotedFromMe, cancellationToken);
    public Task<JsonElement> RevokeMessageAsync(string accountId, string phone, string messageId, CancellationToken cancellationToken = default) => GetClient(accountId).RevokeMessageAsync(phone, messageId, cancellationToken);
    public Task<JsonElement> SetChatPinnedAsync(string phone, bool pinned, CancellationToken cancellationToken = default) => SetChatPinnedAsync(ActiveAccountId, phone, pinned, cancellationToken);
    public Task<JsonElement> SetChatPinnedAsync(string accountId, string phone, bool pinned, CancellationToken cancellationToken = default) => GetClient(accountId).SetChatPinnedAsync(phone, pinned, cancellationToken);
    public Task<JsonElement> UpsertLabelAsync(string accountId, WhatsAppLabel label, CancellationToken cancellationToken = default) => GetClient(accountId).UpsertLabelAsync(label, cancellationToken);
    public Task<JsonElement> CreateLabelAsync(string accountId, string name, int color, CancellationToken cancellationToken = default) => GetClient(accountId).CreateLabelAsync(name, color, cancellationToken);
    public Task<JsonElement> SetChatLabelAsync(string accountId, string phone, string labelId, bool add, CancellationToken cancellationToken = default) => GetClient(accountId).SetChatLabelAsync(phone, labelId, add, cancellationToken);
    public async Task<WhatsAppGroupCreateResult> CreateGroupAsync(string accountId, WhatsAppGroupCreateRequest request, CancellationToken cancellationToken = default)
    {
        var result = await GetClient(accountId).CreateGroupAsync(request, cancellationToken);
        var jid = result.TryGetProperty("jid", out var jidElement) ? jidElement.GetString() ?? "" : "";
        var subject = result.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString() ?? request.Subject : request.Subject;
        var participants = result.TryGetProperty("participants", out var participantsElement) && participantsElement.ValueKind == JsonValueKind.Array
            ? participantsElement.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToList()
            : request.ParticipantPhones.ToList();
        var count = result.TryGetProperty("participantCount", out var countElement) && countElement.TryGetInt32(out var parsedCount) ? parsedCount : participants.Count;
        if (string.IsNullOrWhiteSpace(jid)) throw new WhatsAppBridgeException("group_create_missing_id", "WhatsApp 未返回新群组 ID。");
        return new WhatsAppGroupCreateResult(jid, subject, count, participants);
    }
    public Task<JsonElement> SyncNowAsync(CancellationToken cancellationToken = default) => SyncNowAsync(ActiveAccountId, cancellationToken);
    public Task<JsonElement> SyncNowAsync(string accountId, CancellationToken cancellationToken = default) => GetClient(accountId).SyncNowAsync(cancellationToken);
    public Task<JsonElement> CatchUpHistoryAsync(CancellationToken cancellationToken = default) => CatchUpHistoryAsync(ActiveAccountId, cancellationToken);
    public Task<JsonElement> CatchUpHistoryAsync(string accountId, CancellationToken cancellationToken = default) => GetClient(accountId).CatchUpHistoryAsync(cancellationToken);
    public Task<JsonElement> CatchUpHistoryAsync(IReadOnlyCollection<WhatsAppHistoryCursor> cursors, CancellationToken cancellationToken = default) =>
        CatchUpHistoryAsync(ActiveAccountId, cursors, cancellationToken);
    public Task<JsonElement> CatchUpHistoryAsync(string accountId, IReadOnlyCollection<WhatsAppHistoryCursor> cursors, CancellationToken cancellationToken = default) =>
        GetClient(accountId).CatchUpHistoryAsync(cursors, cancellationToken);

    private async Task<JsonElement> ConnectCoreAsync(string accountId, CancellationToken cancellationToken)
    {
        var gate = _connectionGates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var client = GetClient(accountId);
            if (client.ConnectionState == "connected") return default;
            var startedFromLoggedOutState = client.ConnectionState == "logged_out";
            if (startedFromLoggedOutState)
                await client.ResetForPairingAsync();
            await client.StartAsync(accountId, cancellationToken);
            try
            {
                return await client.ConnectAsync(cancellationToken);
            }
            catch (WhatsAppBridgeException error) when (
                error.Code == "logged_out"
                && !startedFromLoggedOutState
                && !cancellationToken.IsCancellationRequested)
            {
                // The phone may have removed this linked device while the desktop
                // was closed. Recover in the same click: discard the rejected
                // session, start a new bridge and wait for its real QR milestone.
                await client.ResetForPairingAsync();
                await client.StartAsync(accountId, cancellationToken);
                return await client.ConnectAsync(cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private WhatsAppBridgeClient GetClient(string accountId)
    {
        accountId = Normalize(accountId);
        return _clients.GetOrAdd(accountId, id =>
        {
            var client = new WhatsAppBridgeClient(_dataRoot) { OutboundSettings = _outboundSettings };
            client.EventReceived += (_, e) => EventReceived?.Invoke(this, string.IsNullOrWhiteSpace(e.AccountId) ? e with { AccountId = id } : e);
            return client;
        });
    }

    private static string Normalize(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "primary" : value.Trim();
        if (normalized.Length > 64 || normalized.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_' and not '-')) throw new InvalidOperationException("WhatsApp 账号 ID 无效。");
        return normalized;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values) await client.DisposeAsync();
        _clients.Clear();
        foreach (var gate in _connectionGates.Values) gate.Dispose();
        _connectionGates.Clear();
        _autoReconnectSuppressed.Clear();
    }
}
