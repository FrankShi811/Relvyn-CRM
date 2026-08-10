using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record WhatsAppBridgeEvent(string Name, string AccountId, JsonElement Data);
public sealed record WhatsAppHistoryCursor(
    string Jid,
    string Phone,
    bool IsGroup,
    DateTimeOffset LastMessageAt,
    int UnreadCount);

public sealed class WhatsAppBridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>
    /// Structured context the bridge attaches to send-budget refusals
    /// (<c>retryAfterMs</c>, the cap that was hit, the origin that hit it).
    /// Absent for every other error.
    /// </summary>
    public JsonElement Detail { get; init; }

    /// <summary>
    /// How long the bridge says to wait before retrying.
    ///
    /// Soft refusals carry a relative <c>retryAfterMs</c>; suspensions carry an
    /// absolute <c>until</c> epoch instead. Reading only the first would make a
    /// suspended account look retryable immediately, and the caller would hammer
    /// a service that is already rate limiting it.
    /// </summary>
    public TimeSpan? RetryAfter
    {
        get
        {
            if (Detail.ValueKind != JsonValueKind.Object) return null;
            var cap = TimeSpan.FromHours(24);
            if (Detail.TryGetProperty("retryAfterMs", out var relative)
                && relative.TryGetInt64(out var milliseconds)
                && milliseconds > 0)
                return TimeSpan.FromMilliseconds(Math.Min(milliseconds, (long)cap.TotalMilliseconds));
            if (Detail.TryGetProperty("until", out var until)
                && until.ValueKind == JsonValueKind.Number
                && until.TryGetInt64(out var deadline))
            {
                var remaining = deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (remaining > 0) return TimeSpan.FromMilliseconds(Math.Min(remaining, (long)cap.TotalMilliseconds));
            }
            return null;
        }
    }

    /// <summary>True when the send was refused by the account-level outbound governor.</summary>
    public bool IsOutboundBlocked => OutboundBlockCodes.IsBlocked(Code);
}

public sealed class WhatsAppBridgeClient : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private TaskCompletionSource _ready = NewSignal();
    private TaskCompletionSource<string>? _pairingMilestone;
    private readonly object _pairingLock = new();
    private CancellationTokenSource? _lifetime;
    private readonly string _dataRoot;

    public event EventHandler<WhatsAppBridgeEvent>? EventReceived;
    public bool IsRunning => _process is { HasExited: false };
    public bool IsConnected => ConnectionState == "connected";
    public string ConnectionState { get; private set; } = "disconnected";
    public string CurrentAccountId { get; private set; } = "primary";
    public string LastBridgeError { get; private set; } = "";
    public string LatestQrDataUrl { get; private set; } = "";

    public WhatsAppBridgeClient(string? dataRoot = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot
            ?? new DataWorkspaceManager().Resolve().RootDirectory);
    }

    public async Task StartAsync(string accountId = "primary", CancellationToken cancellationToken = default)
    {
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) return;
            CurrentAccountId = string.IsNullOrWhiteSpace(accountId) ? "primary" : accountId;
            var sessionSecrets = new WindowsCredentialStore($"WAFlow/WhatsAppSessionKey/{CurrentAccountId}");
            var encryptionKey = sessionSecrets.Read();
            var requiresLocalAuthorization = string.IsNullOrWhiteSpace(encryptionKey)
                && HasEncryptedSession(CurrentAccountId);
            string? sessionBackupName = null;
            if (requiresLocalAuthorization)
                sessionBackupName = PrepareFreshLocalSession(CurrentAccountId);
            if (string.IsNullOrWhiteSpace(encryptionKey))
            {
                encryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                sessionSecrets.Save(encryptionKey);
            }

            var launch = BridgeLaunch.Resolve(_dataRoot);
            _ready = NewSignal();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var start = new ProcessStartInfo
            {
                FileName = launch.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
                WorkingDirectory = launch.WorkingDirectory
            };
            start.Environment["WAFLOW_DATA_ROOT"] = _dataRoot;
            foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
            _process = Process.Start(start) ?? throw new WhatsAppBridgeException("bridge_start_failed", "无法启动 WhatsApp 桥接进程。");
            _input = _process.StandardInput;
            _ = ReadOutputAsync(_process.StandardOutput, _lifetime.Token);
            _ = ReadErrorsAsync(_process.StandardError, _lifetime.Token);
            _ = ObserveExitAsync(_process, _lifetime.Token);

            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            // The governor is created during initialize, so its configuration has
            // to travel with that command; a later configure_outbound would leave
            // a window where the first sends run on bridge defaults.
            await SendCommandAsync(
                "initialize",
                new { accountId = CurrentAccountId, encryptionKey, outbound = OutboundSettings.Normalized().ToBridgePayload() },
                cancellationToken);
            if (requiresLocalAuthorization)
            {
                EventReceived?.Invoke(this, new WhatsAppBridgeEvent(
                    "local_authorization_required",
                    CurrentAccountId,
                    JsonSerializer.SerializeToElement(new
                    {
                        reason = "machine_local_session_key_missing",
                        backupName = sessionBackupName ?? "",
                        requiresQr = true
                    })));
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private bool HasEncryptedSession(string accountId) =>
        File.Exists(Path.Combine(_dataRoot, "whatsapp-sessions", accountId, "creds.json.enc"));

    private string PrepareFreshLocalSession(string accountId)
    {
        var sessionDirectory = Path.Combine(_dataRoot, "whatsapp-sessions", accountId);
        if (!Directory.Exists(sessionDirectory)) return "";
        var suffix = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backupDirectory = $"{sessionDirectory}.other-device-{suffix}";
        var counter = 1;
        while (Directory.Exists(backupDirectory))
            backupDirectory = $"{sessionDirectory}.other-device-{suffix}-{counter++}";
        Directory.Move(sessionDirectory, backupDirectory);
        Directory.CreateDirectory(sessionDirectory);
        return Path.GetFileName(backupDirectory);
    }

    public Task<JsonElement> PingAsync(CancellationToken cancellationToken = default) => SendCommandAsync("ping", null, cancellationToken);
    public async Task<JsonElement> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectionState == "connected") return default;
        TaskCompletionSource<string> milestone;
        var sendConnectCommand = true;
        lock (_pairingLock)
        {
            if (ConnectionState == "connecting" && _pairingMilestone is not null)
            {
                milestone = _pairingMilestone;
                sendConnectCommand = false;
            }
            else
            {
                milestone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pairingMilestone = milestone;
                LatestQrDataUrl = "";
            }
        }
        ConnectionState = "connecting";
        JsonElement result = default;
        try
        {
            if (sendConnectCommand)
            {
                var networkRoute = NetworkProxyResolver.Resolve(new Uri("https://web.whatsapp.com/"));
                result = await SendCommandAsync(
                    "connect",
                    new
                    {
                        proxyUrl = networkRoute.ProxyUrl,
                        proxySource = networkRoute.Source,
                        allowDirectFallback = networkRoute.AllowDirectFallback
                    },
                    cancellationToken);
            }
            await milestone.Task.WaitAsync(TimeSpan.FromSeconds(95), cancellationToken);
            return result;
        }
        catch (TimeoutException)
        {
            try
            {
                await SendCommandAsync("disconnect", null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
            ConnectionState = "disconnected";
            throw new WhatsAppBridgeException(
                "qr_generation_timeout",
                "WhatsApp 二维码未能在限定时间内生成。程序已自动尝试 Windows 系统代理与直连；请检查防火墙、公司网络或代理是否允许访问 WhatsApp 后点击重试。");
        }
        finally
        {
            lock (_pairingLock)
                if (ReferenceEquals(_pairingMilestone, milestone)) _pairingMilestone = null;
        }
    }

    public void CancelPendingPairing(string reason = "WhatsApp 连接已取消。")
    {
        lock (_pairingLock)
            _pairingMilestone?.TrySetException(new WhatsAppBridgeException("connection_cancelled", reason));
    }
    public async Task<JsonElement> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelPendingPairing();
        var result = await SendCommandAsync("disconnect", null, cancellationToken);
        ConnectionState = "disconnected";
        LatestQrDataUrl = "";
        FailPairing(new WhatsAppBridgeException("connection_cancelled", "WhatsApp 连接已取消。"));
        return result;
    }
    public async Task<JsonElement> LogoutAsync(CancellationToken cancellationToken = default)
    {
        CancelPendingPairing("本机登录会话正在清除，可稍后重新生成二维码。");
        var remoteLogoutCompleted = false;
        var recoveryReason = "";
        try
        {
            var result = await SendCommandAsync("logout", null, cancellationToken);
            remoteLogoutCompleted = result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("remoteLogoutCompleted", out var completed)
                || completed.ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            recoveryReason = error.Message;
        }
        finally
        {
            // A successful bridge reply is not enough: Baileys can still deliver a
            // late creds.update callback from the old socket. Always kill this
            // account's child process before clearing its exact session directory,
            // otherwise the stale callback can recreate creds.json.enc and the next
            // Connect click oscillates between old-session states instead of showing QR.
            await ResetForPairingAsync();
        }

        return JsonSerializer.SerializeToElement(new
        {
            state = "logged_out",
            remoteLogoutCompleted,
            localSessionReset = true,
            recoveryReason
        });
    }

    public async Task ResetForPairingAsync()
    {
        CancelPendingPairing("正在建立全新的 WhatsApp 扫码会话。");
        await StopBridgeProcessAsync();
        ResetLocalSessionDirectory(CurrentAccountId);
        ConnectionState = "logged_out";
        LatestQrDataUrl = "";
        FailPairing(new WhatsAppBridgeException("logged_out", "WhatsApp 登录已清除，请重新扫码登录。"));
    }

    private void ResetLocalSessionDirectory(string accountId)
    {
        var sessionDirectory = Path.Combine(_dataRoot, "whatsapp-sessions", accountId);
        if (Directory.Exists(sessionDirectory)) Directory.Delete(sessionDirectory, recursive: true);
        Directory.CreateDirectory(sessionDirectory);
    }

    private async Task StopBridgeProcessAsync()
    {
        _lifetime?.Cancel();
        var process = _process;
        if (process is { HasExited: false })
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { }
        }
        try { _input?.Dispose(); } catch { }
        _input = null;
        process?.Dispose();
        _process = null;
        _lifetime?.Dispose();
        _lifetime = null;
        _ready = NewSignal();
        FailPending(new WhatsAppBridgeException("bridge_restarted", "WhatsApp 桥接已重置，可重新连接。"));
    }
    /// <summary>
    /// Configuration handed to the bridge governor on initialize. Assigned before
    /// <see cref="StartAsync"/> by <c>WhatsAppConnectionManager</c>; a fresh
    /// instance means "bridge defaults".
    /// </summary>
    public OutboundGovernorSettings OutboundSettings { get; set; } = new();

    public Task<JsonElement> SendTextAsync(string phone, string text, CancellationToken cancellationToken = default) =>
        SendTextAsync(phone, text, OutboundSendOptions.Human, cancellationToken);
    public Task<JsonElement> SendTextAsync(string phone, string text, OutboundSendOptions options, CancellationToken cancellationToken = default) =>
        SendCommandAsync("send_text", new { phone, text, origin = OutboundOrigin.Normalize(options.Origin), idempotencyKey = options.IdempotencyKey }, cancellationToken);
    public Task<JsonElement> ValidateNumberAsync(string phone, CancellationToken cancellationToken = default) =>
        SendCommandAsync("validate_number", new { phone }, cancellationToken);
    public Task<JsonElement> SendReplyTextAsync(string phone, string text, string quotedMessageId, string quotedText, bool quotedFromMe, CancellationToken cancellationToken = default) =>
        SendReplyTextAsync(phone, text, quotedMessageId, quotedText, quotedFromMe, OutboundSendOptions.Human, cancellationToken);
    public Task<JsonElement> SendReplyTextAsync(string phone, string text, string quotedMessageId, string quotedText, bool quotedFromMe, OutboundSendOptions options, CancellationToken cancellationToken = default) =>
        SendCommandAsync("send_text", new { phone, text, quotedMessageId, quotedText, quotedFromMe, origin = OutboundOrigin.Normalize(options.Origin), idempotencyKey = options.IdempotencyKey }, cancellationToken);
    public Task<JsonElement> SendMediaAsync(string phone, string path, string caption, CancellationToken cancellationToken = default) =>
        SendMediaAsync(phone, path, caption, OutboundSendOptions.Human, cancellationToken);
    public Task<JsonElement> SendMediaAsync(string phone, string path, string caption, OutboundSendOptions options, CancellationToken cancellationToken = default) =>
        SendCommandAsync("send_media", new { phone, path, caption, origin = OutboundOrigin.Normalize(options.Origin), idempotencyKey = options.IdempotencyKey }, cancellationToken);
    public Task<JsonElement> SendReplyMediaAsync(string phone, string path, string caption, string quotedMessageId, string quotedText, bool quotedFromMe, CancellationToken cancellationToken = default) =>
        SendReplyMediaAsync(phone, path, caption, quotedMessageId, quotedText, quotedFromMe, OutboundSendOptions.Human, cancellationToken);
    public Task<JsonElement> SendReplyMediaAsync(string phone, string path, string caption, string quotedMessageId, string quotedText, bool quotedFromMe, OutboundSendOptions options, CancellationToken cancellationToken = default) =>
        SendCommandAsync("send_media", new { phone, path, caption, quotedMessageId, quotedText, quotedFromMe, origin = OutboundOrigin.Normalize(options.Origin), idempotencyKey = options.IdempotencyKey }, cancellationToken);
    public async Task<OutboundGovernorStatus> ConfigureOutboundAsync(OutboundGovernorSettings settings, CancellationToken cancellationToken = default)
    {
        OutboundSettings = settings;
        var result = await SendCommandAsync("configure_outbound", new { outbound = settings.Normalized().ToBridgePayload() }, cancellationToken);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("status", out var status)
            ? OutboundGovernorStatus.FromJson(status)
            : OutboundGovernorStatus.Unknown;
    }
    public async Task<OutboundGovernorStatus> OutboundStatusAsync(bool resume = false, CancellationToken cancellationToken = default) =>
        OutboundGovernorStatus.FromJson(await SendCommandAsync("outbound_status", new { resume }, cancellationToken));
    public Task<JsonElement> RevokeMessageAsync(string phone, string messageId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("revoke_message", new { phone, messageId }, cancellationToken);
    public Task<JsonElement> SetChatPinnedAsync(string phone, bool pinned, CancellationToken cancellationToken = default) =>
        SendCommandAsync("set_chat_pin", new { phone, pinned }, cancellationToken);
    public Task<JsonElement> UpsertLabelAsync(WhatsAppLabel label, CancellationToken cancellationToken = default) =>
        SendCommandAsync("label_upsert", new { id = label.Id, name = label.Name, color = label.Color, deleted = label.Deleted }, cancellationToken);
    public Task<JsonElement> CreateLabelAsync(string name, int color, CancellationToken cancellationToken = default) =>
        SendCommandAsync("label_create", new { name, color }, cancellationToken);
    public Task<JsonElement> SetChatLabelAsync(string phone, string labelId, bool add, CancellationToken cancellationToken = default) =>
        SendCommandAsync("chat_label_set", new { phone, labelId, add }, cancellationToken);
    public Task<JsonElement> CreateGroupAsync(WhatsAppGroupCreateRequest request, CancellationToken cancellationToken = default) =>
        SendCommandAsync("create_group", new { subject = request.Subject, participants = request.ParticipantPhones }, cancellationToken);
    public Task<JsonElement> SyncNowAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("sync_now", null, cancellationToken);
    public Task<JsonElement> CatchUpHistoryAsync(CancellationToken cancellationToken = default) =>
        CatchUpHistoryAsync([], cancellationToken);
    public Task<JsonElement> CatchUpHistoryAsync(IReadOnlyCollection<WhatsAppHistoryCursor> cursors, CancellationToken cancellationToken = default) =>
        SendCommandAsync("catch_up_history", new
        {
            cursors = cursors.Select(cursor => new
            {
                jid = cursor.Jid,
                phone = cursor.Phone,
                isGroup = cursor.IsGroup,
                lastMessageAt = cursor.LastMessageAt == default ? "" : cursor.LastMessageAt.ToUniversalTime().ToString("O"),
                unreadCount = Math.Max(0, cursor.UnreadCount)
            }).ToArray()
        }, cancellationToken);

    private async Task<JsonElement> SendCommandAsync(string command, object? payload, CancellationToken cancellationToken)
    {
        if (!IsRunning || _input is null) throw new WhatsAppBridgeException("bridge_not_running", "WhatsApp 桥接进程尚未启动。");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("无法创建桥接请求。");
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { command, requestId, payload }));
            var message = new Dictionary<string, object?>
            {
                ["command"] = command,
                ["requestId"] = requestId
            };
            if (payload is not null)
                foreach (var property in document.RootElement.GetProperty("payload").EnumerateObject()) message[property.Name] = property.Value.Clone();
            var line = JsonSerializer.Serialize(message);
            await _writeLock.WaitAsync(cancellationToken);
            try { await _input.WriteLineAsync(line.AsMemory(), cancellationToken); await _input.FlushAsync(); }
            finally { _writeLock.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private async Task ReadOutputAsync(StreamReader output, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && await output.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException)
                {
                    LastBridgeError = "桥接进程产生了一行非协议输出，已安全忽略。";
                    continue;
                }
                using (document)
                {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement)) continue;
                var type = typeElement.GetString();
                if (type == "response")
                {
                    var requestId = root.GetProperty("requestId").GetString() ?? "";
                    if (!_pending.TryGetValue(requestId, out var completion)) continue;
                    if (root.GetProperty("ok").GetBoolean()) completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                    else
                    {
                        var error = root.TryGetProperty("error", out var errorElement) ? errorElement : default;
                        var errorCode = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) ? code.GetString() ?? "bridge_error" : "bridge_error";
                        // Governor refusals carry a machine-readable code but a
                        // useless message (the code again); translate those so the
                        // inbox shows a sentence rather than an identifier.
                        var errorMessage = OutboundBlockCodes.IsBlocked(errorCode)
                            ? OutboundBlockCodes.Describe(errorCode)
                            : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) ? message.GetString() ?? "WhatsApp 桥接调用失败。" : "WhatsApp 桥接调用失败。";
                        completion.TrySetException(new WhatsAppBridgeException(errorCode, errorMessage)
                        {
                            Detail = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("detail", out var detail)
                                ? detail.Clone()
                                : default
                        });
                    }
                    continue;
                }
                if (type != "event") continue;
                var eventName = root.TryGetProperty("event", out var eventElement) ? eventElement.GetString() ?? "unknown" : "unknown";
                if (eventName == "ready") _ready.TrySetResult();
                var accountId = root.TryGetProperty("accountId", out var account) ? account.GetString() ?? "" : "";
                var data = root.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : default;
                if (eventName == "connection" && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("state", out var state))
                {
                    ConnectionState = state.GetString() ?? "disconnected";
                    if (ConnectionState == "connected")
                    {
                        LatestQrDataUrl = "";
                        CompletePairing("connected");
                    }
                    else if (ConnectionState == "logged_out")
                    {
                        LatestQrDataUrl = "";
                        FailPairing(new WhatsAppBridgeException("logged_out", "WhatsApp 登录已失效，请重新生成二维码。"));
                    }
                    else if (ConnectionState == "disconnected"
                        && data.TryGetProperty("manual", out var manual)
                        && manual.ValueKind == JsonValueKind.True)
                    {
                        FailPairing(new WhatsAppBridgeException("connection_cancelled", "WhatsApp 连接已取消。"));
                    }
                }
                else if (eventName == "qr"
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("dataUrl", out var qrDataUrl))
                {
                    LatestQrDataUrl = qrDataUrl.GetString() ?? "";
                    CompletePairing("qr");
                }
                else if (eventName == "bridge_error")
                {
                    var message = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error)
                        ? error.GetString() ?? "WhatsApp 桥接连接失败。"
                        : "WhatsApp 桥接连接失败。";
                    LastBridgeError = message;
                    FailPairing(new WhatsAppBridgeException("bridge_error", message));
                }
                EventReceived?.Invoke(this, new WhatsAppBridgeEvent(eventName, accountId, data));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            LastBridgeError = error.Message;
            FailPending(error);
            FailPairing(error);
        }
    }

    private async Task ReadErrorsAsync(StreamReader errors, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && await errors.ReadLineAsync(cancellationToken) is { } line)
                if (!string.IsNullOrWhiteSpace(line)) LastBridgeError = line.Length > 1000 ? line[..1000] : line;
        }
        catch (OperationCanceledException) { }
    }

    private async Task ObserveExitAsync(Process process, CancellationToken cancellationToken)
    {
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { return; }
        ConnectionState = "disconnected";
        LatestQrDataUrl = "";
        var error = new WhatsAppBridgeException("bridge_exited", $"WhatsApp 桥接进程已退出，代码 {process.ExitCode}。");
        FailPending(error);
        FailPairing(error);
        EventReceived?.Invoke(this, new WhatsAppBridgeEvent(
            "bridge_error",
            CurrentAccountId,
            JsonSerializer.SerializeToElement(new { code = "bridge_exited", error = error.Message })));
    }

    private void FailPending(Exception error)
    {
        foreach (var pair in _pending) pair.Value.TrySetException(error);
    }

    private void CompletePairing(string milestone)
    {
        lock (_pairingLock) _pairingMilestone?.TrySetResult(milestone);
    }

    private void FailPairing(Exception error)
    {
        lock (_pairingLock) _pairingMilestone?.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (IsRunning) await DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _lifetime?.Cancel();
        if (_process is { HasExited: false })
        {
            try { _input?.Close(); await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { try { _process.Kill(entireProcessTree: true); } catch { } }
        }
        _process?.Dispose();
        _writeLock.Dispose();
        _startLock.Dispose();
        _lifetime?.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record BridgeLaunch(string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments)
    {
        public static BridgeLaunch Resolve(string dataRoot)
        {
            var explicitExe = Environment.GetEnvironmentVariable("AI_SALES_OS_WHATSAPP_BRIDGE_PATH");
            if (string.IsNullOrWhiteSpace(explicitExe))
                explicitExe = Environment.GetEnvironmentVariable("WAFLOW_BRIDGE_EXE");
            if (!string.IsNullOrWhiteSpace(explicitExe) && File.Exists(explicitExe))
                return new(explicitExe, Path.GetDirectoryName(explicitExe)!, []);

            var processDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var packaged = Path.Combine(processDirectory, "WAFlow.WhatsApp.Bridge.exe");
            if (File.Exists(packaged)) return new(packaged, processDirectory, []);

            var script = Environment.GetEnvironmentVariable("WAFLOW_BRIDGE_SCRIPT");
            if (string.IsNullOrWhiteSpace(script) || !File.Exists(script)) script = FindDevelopmentScript(AppContext.BaseDirectory);
            var node = Environment.GetEnvironmentVariable("WAFLOW_NODE_PATH");
            if (string.IsNullOrWhiteSpace(node) || !File.Exists(node)) node = FindNode();
            if (!string.IsNullOrWhiteSpace(script) && File.Exists(script) && !string.IsNullOrWhiteSpace(node) && File.Exists(node))
                return new(node, Path.GetDirectoryName(Path.GetDirectoryName(script))!, [script]);

            throw new WhatsAppBridgeException(
                "bridge_runtime_missing",
                "未找到 WhatsApp Bridge。请修复安装，或通过 AI_SALES_OS_WHATSAPP_BRIDGE_PATH 选择自行编译的 Bridge。");
        }

        private static string? FindDevelopmentScript(string start)
        {
            var directory = new DirectoryInfo(start);
            for (var level = 0; level < 8 && directory is not null; level++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "bridge", "src", "index.mjs");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string? FindNode()
        {
            foreach (var segment in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(segment.Trim('"'), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            var codexRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "node", "bin", "node.exe");
            return File.Exists(codexRuntime) ? codexRuntime : null;
        }
    }
}
