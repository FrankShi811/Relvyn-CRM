using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public sealed class WhatsAppConnectorResilienceService
{
    private const int CurrentProtocolVersion = 1;
    private const string CurrentConnector = "baileys";
    private const string CurrentConnectorVersion = "7.0.0-rc13";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _dataRoot;
    private readonly string _databasePath;
    private readonly ConcurrentDictionary<string, AccountState> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<DiagnosticError> _recentErrors = new();
    private readonly ScopedFeaturePolicy _policy;
    private readonly CompatibilityManifest _compatibility;
    private readonly object _safetyFileGate = new();
    private readonly Dictionary<string, WhatsAppConnectorSafeMode> _persistedSafeModes;

    public WhatsAppConnectorResilienceService(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _databasePath = Path.Combine(_dataRoot, "waflow.db");
        _policy = ScopedFeaturePolicy.Load(_dataRoot);
        _compatibility = CompatibilityManifest.Load();
        _persistedSafeModes = LoadSafeModes(_dataRoot);
    }

    public WhatsAppConnectorProtocolInfo ObserveHandshake(string accountId, JsonElement data)
    {
        var state = GetState(accountId);
        var protocol = _compatibility.Apply(ParseProtocol(data));
        state.Protocol = protocol;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        foreach (var feature in Enum.GetValues<WhatsAppConnectorFeature>())
        {
            var capability = CapabilityFor(feature);
            if (capability is null) continue;
            SetFeature(state, feature,
                protocol.Capabilities.TryGetValue(capability, out var enabled) && enabled
                    ? protocol.IsLegacyFallback ? WhatsAppFeatureHealthState.Degraded : WhatsAppFeatureHealthState.Healthy
                    : WhatsAppFeatureHealthState.Unavailable,
                protocol.IsLegacyFallback ? "legacy_capability_assumed" : enabled ? "capability_advertised" : "capability_unavailable");
        }

        if (!protocol.IsCompatible)
            EnterSafeMode(state, protocol.CompatibilityCode, blocksAutomaticText: true, blocksAutomaticMedia: true);
        return protocol;
    }

    public void ObserveEvent(WhatsAppBridgeEvent bridgeEvent)
    {
        var state = GetState(bridgeEvent.AccountId);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        switch (bridgeEvent.Name)
        {
            case "ready":
                ObserveHandshake(bridgeEvent.AccountId, bridgeEvent.Data);
                break;
            case "connection":
                ObserveConnection(state, bridgeEvent.Data);
                break;
            case "qr":
                SetFeature(state, WhatsAppConnectorFeature.QrPairing, WhatsAppFeatureHealthState.Healthy, "qr_received");
                break;
            case "auth_recovery":
                SetFeature(state, WhatsAppConnectorFeature.Session, WhatsAppFeatureHealthState.Degraded, "session_recovery_required");
                AddError(bridgeEvent.AccountId, "session_recovery_required", WhatsAppConnectorFeature.Session);
                break;
            case "sync_status":
                ObserveSync(state, bridgeEvent.Data);
                break;
            case "message":
                ObserveMessage(state, bridgeEvent.Data);
                break;
            case "messages_history":
                SetFeature(state, WhatsAppConnectorFeature.HistorySync, WhatsAppFeatureHealthState.Healthy, "history_received");
                break;
            case "message_status":
                SetFeature(state, WhatsAppConnectorFeature.DeliveryReceipts, WhatsAppFeatureHealthState.Healthy, "receipt_received");
                SetFeature(state, WhatsAppConnectorFeature.ReadReceipts, WhatsAppFeatureHealthState.Healthy, "receipt_received");
                break;
            case "label_upsert":
            case "chat_label_upsert":
                SetFeature(state, WhatsAppConnectorFeature.Labels, WhatsAppFeatureHealthState.Healthy, "label_sync_received");
                break;
            case "group_created":
                SetFeature(state, WhatsAppConnectorFeature.Groups, WhatsAppFeatureHealthState.Healthy, "group_created");
                break;
            case "outbound_suspended":
                SetFeature(state, WhatsAppConnectorFeature.TextSend, WhatsAppFeatureHealthState.Suspended, "provider_outbound_suspended");
                SetFeature(state, WhatsAppConnectorFeature.MediaSend, WhatsAppFeatureHealthState.Suspended, "provider_outbound_suspended");
                EnterSafeMode(state, "provider_outbound_suspended", true, true);
                AddError(bridgeEvent.AccountId, "provider_outbound_suspended", WhatsAppConnectorFeature.OutboundGovernor);
                break;
            case "connection_issue":
            case "bridge_error":
                ObserveIssue(state, bridgeEvent.Data);
                break;
        }
    }

    public void ObserveOperationFailure(string accountId, WhatsAppConnectorFeature feature, Exception error)
    {
        var state = GetState(accountId);
        var code = error is WhatsAppBridgeException bridge ? bridge.Code : "connector_operation_failed";
        if (OutboundBlockCodes.IsBlocked(code)
            && code is not (OutboundBlockCodes.SuspendedRateLimited or OutboundBlockCodes.SuspendedAccountRisk))
        {
            AddError(accountId, code, feature);
            return;
        }
        var health = code is OutboundBlockCodes.SuspendedRateLimited or OutboundBlockCodes.SuspendedAccountRisk
            ? WhatsAppFeatureHealthState.Suspended
            : WhatsAppFeatureHealthState.Degraded;
        SetFeature(state, feature, health, code);
        AddError(accountId, code, feature);

        if (code is "whatsapp_target_not_verified"
            or "whatsapp_server_message_id_missing"
            or "whatsapp_sender_identity_missing"
            or "whatsapp_sender_device_sync_unavailable"
            or "whatsapp_recipient_devices_unavailable"
            || code is OutboundBlockCodes.SuspendedRateLimited or OutboundBlockCodes.SuspendedAccountRisk)
            EnterSafeMode(state, code, true, true);
    }

    public void EnsureAutomaticSendAllowed(string accountId, OutboundSendOptions options, bool media)
    {
        if (OutboundOrigin.Normalize(options.Origin) == OutboundOrigin.Human) return;
        var state = GetState(accountId);
        var featureEnabled = media ? _policy.AutomaticMediaEnabled : _policy.AutomaticTextEnabled;
        if (!featureEnabled)
            throw new WhatsAppBridgeException("connector_feature_gate_blocked", "WhatsApp 自动发送已由本机安全策略暂停；人工发送和消息读取不受影响。");
        if (state.SafeMode.Active && (media ? state.SafeMode.BlocksAutomaticMedia : state.SafeMode.BlocksAutomaticText))
            throw new WhatsAppBridgeException("connector_safe_mode_automatic_send_blocked", "WhatsApp 连接已进入安全模式，自动发送暂停；消息读取、历史、CRM 与人工处理仍可继续。");
    }

    public WhatsAppConnectorSnapshot Snapshot(string accountId, string connectionState)
    {
        var normalized = NormalizeAccount(accountId);
        var state = GetState(normalized);
        return new WhatsAppConnectorSnapshot(
            normalized,
            state.Protocol,
            Enum.GetValues<WhatsAppConnectorFeature>()
                .Select(feature => state.Features.TryGetValue(feature, out var health)
                    ? health
                    : new WhatsAppFeatureHealth(feature, WhatsAppFeatureHealthState.Unknown, "not_observed", state.UpdatedAt))
                .ToArray(),
            state.SafeMode,
            connectionState,
            state.UpdatedAt);
    }

    public void ClearSafeMode(string accountId)
    {
        var state = GetState(accountId);
        state.SafeMode = WhatsAppConnectorSafeMode.Inactive;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        lock (_safetyFileGate)
        {
            _persistedSafeModes.Remove(state.AccountId);
            PersistSafeModes();
        }
    }

    public async Task<WhatsAppDiagnosticExportResult> ExportDiagnosticsAsync(
        string destinationPath,
        IReadOnlyDictionary<string, string> accountConnections,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("需要选择诊断包保存位置。", nameof(destinationPath));
        destinationPath = Path.GetFullPath(destinationPath);
        if (!destinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) destinationPath += ".zip";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporary = destinationPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteJsonAsync(archive, "manifest.json", new
                {
                    schemaVersion = 1,
                    createdAt = DateTimeOffset.UtcNow,
                    privacy = "technical-metadata-only",
                    applicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                    compatibilityManifest = new { source = _compatibility.Source, _compatibility.Available, _compatibility.BridgeVersion, _compatibility.ProtocolVersion, _compatibility.Connector, _compatibility.ConnectorVersion, _compatibility.MinRelvynVersion, _compatibility.MaxRelvynVersion },
                    policy = new { source = _policy.Source, automaticTextEnabled = _policy.AutomaticTextEnabled, automaticMediaEnabled = _policy.AutomaticMediaEnabled }
                }, cancellationToken);
                await WriteJsonAsync(archive, "connector-health.json", accountConnections.Select(pair =>
                {
                    var snapshot = Snapshot(pair.Key, pair.Value);
                    return new
                    {
                        account = HashAccount(pair.Key),
                        snapshot.Protocol,
                        snapshot.Features,
                        snapshot.SafeMode,
                        snapshot.ConnectionState,
                        snapshot.UpdatedAt
                    };
                }).ToArray(), cancellationToken);
                await WriteJsonAsync(archive, "database-integrity.json", await DatabaseIntegrityAsync(cancellationToken), cancellationToken);
                await WriteJsonAsync(archive, "system.json", new
                {
                    os = RuntimeInformation.OSDescription,
                    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                    framework = RuntimeInformation.FrameworkDescription,
                    is64BitProcess = Environment.Is64BitProcess
                }, cancellationToken);
                await WriteJsonAsync(archive, "recent-errors.json", _recentErrors.Reverse().Take(100).Select(item => new
                {
                    account = HashAccount(item.AccountId),
                    item.Code,
                    feature = item.Feature.ToString(),
                    item.OccurredAt
                }).ToArray(), cancellationToken);
            }
            File.Move(temporary, destinationPath, true);
            var info = new FileInfo(destinationPath);
            using var verify = ZipFile.OpenRead(destinationPath);
            return new WhatsAppDiagnosticExportResult(destinationPath, info.Length, verify.Entries.Count);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static WhatsAppConnectorProtocolInfo ParseProtocol(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("protocolVersion", out var protocolElement)
            || !protocolElement.TryGetInt32(out var protocolVersion))
            return WhatsAppConnectorProtocolInfo.LegacyCompatible;

        var bridgeName = ReadString(data, "bridge", "WAFlow.WhatsApp.Bridge");
        var bridgeVersion = ReadString(data, "bridgeVersion", ReadString(data, "version", "unknown"));
        var connector = ReadString(data, "connector", "unknown");
        var connectorVersion = ReadString(data, "connectorVersion", "unknown");
        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (data.TryGetProperty("capabilities", out var capabilitiesElement) && capabilitiesElement.ValueKind == JsonValueKind.Object)
            foreach (var item in capabilitiesElement.EnumerateObject())
                if (item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) capabilities[item.Name] = item.Value.GetBoolean();

        var compatible = protocolVersion == CurrentProtocolVersion
            && connector.Equals(CurrentConnector, StringComparison.OrdinalIgnoreCase)
            && connectorVersion.Equals(CurrentConnectorVersion, StringComparison.OrdinalIgnoreCase);
        var code = protocolVersion != CurrentProtocolVersion
            ? "protocol_version_incompatible"
            : !connector.Equals(CurrentConnector, StringComparison.OrdinalIgnoreCase)
                ? "connector_incompatible"
                : !connectorVersion.Equals(CurrentConnectorVersion, StringComparison.OrdinalIgnoreCase)
                    ? "connector_version_unvalidated"
                    : "compatible";
        return new WhatsAppConnectorProtocolInfo(
            bridgeName, bridgeVersion, protocolVersion, connector, connectorVersion,
            capabilities.Count == 0 ? WhatsAppConnectorCapabilities.AllEnabled : capabilities,
            false, compatible, code);
    }

    private void ObserveConnection(AccountState state, JsonElement data)
    {
        var value = ReadString(data, "state", "unknown");
        if (value == "connected")
        {
            SetFeature(state, WhatsAppConnectorFeature.Transport, WhatsAppFeatureHealthState.Healthy, "connected");
            SetFeature(state, WhatsAppConnectorFeature.Session, WhatsAppFeatureHealthState.Healthy, "session_connected");
        }
        else if (value == "logged_out")
        {
            SetFeature(state, WhatsAppConnectorFeature.Transport, WhatsAppFeatureHealthState.Unavailable, "logged_out");
            SetFeature(state, WhatsAppConnectorFeature.Session, WhatsAppFeatureHealthState.Unavailable, "logged_out");
        }
        else if (value == "disconnected")
            SetFeature(state, WhatsAppConnectorFeature.Transport, WhatsAppFeatureHealthState.Unavailable, "disconnected");
        else
            SetFeature(state, WhatsAppConnectorFeature.Transport, WhatsAppFeatureHealthState.Unknown, value);
    }

    private void ObserveSync(AccountState state, JsonElement data)
    {
        var phase = ReadString(data, "phase", "history");
        var result = ReadString(data, "state", "unknown");
        var feature = phase.Contains("offline", StringComparison.OrdinalIgnoreCase)
            ? WhatsAppConnectorFeature.OfflineCatchup
            : WhatsAppConnectorFeature.HistorySync;
        var health = result == "complete" ? WhatsAppFeatureHealthState.Healthy
            : result is "failed" or "action_required" ? WhatsAppFeatureHealthState.Degraded
            : WhatsAppFeatureHealthState.Unknown;
        SetFeature(state, feature, health, $"sync_{result}");
        if (health == WhatsAppFeatureHealthState.Degraded) AddError(state.AccountId, $"sync_{phase}_{result}", feature);
    }

    private static void ObserveMessage(AccountState state, JsonElement data)
    {
        var isGroup = data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("isGroup", out var group)
            && group.ValueKind == JsonValueKind.True;
        SetFeature(state, isGroup ? WhatsAppConnectorFeature.GroupMessages : WhatsAppConnectorFeature.DirectMessages,
            WhatsAppFeatureHealthState.Healthy, "message_received");
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("kind", out var kind)
            && kind.GetString() is not (null or "text" or "unavailable" or "unknown"))
            SetFeature(state, WhatsAppConnectorFeature.MediaReceive, WhatsAppFeatureHealthState.Healthy, "media_received");
    }

    private void ObserveIssue(AccountState state, JsonElement data)
    {
        var code = ReadString(data, "code", "connector_issue");
        var feature = code.Contains("label", StringComparison.OrdinalIgnoreCase) ? WhatsAppConnectorFeature.Labels
            : code.Contains("history", StringComparison.OrdinalIgnoreCase) ? WhatsAppConnectorFeature.HistorySync
            : code.Contains("group", StringComparison.OrdinalIgnoreCase) ? WhatsAppConnectorFeature.Groups
            : code.Contains("read_receipt", StringComparison.OrdinalIgnoreCase) ? WhatsAppConnectorFeature.ReadReceipts
            : code.Contains("receipt", StringComparison.OrdinalIgnoreCase) || code.Contains("ack", StringComparison.OrdinalIgnoreCase) ? WhatsAppConnectorFeature.DeliveryReceipts
            : code.Contains("lid", StringComparison.OrdinalIgnoreCase) ? WhatsAppConnectorFeature.LidMapping
            : code.Contains("pin", StringComparison.OrdinalIgnoreCase) ? WhatsAppConnectorFeature.PinChat
            : code.Contains("number", StringComparison.OrdinalIgnoreCase) || code.Contains("target", StringComparison.OrdinalIgnoreCase) ? WhatsAppConnectorFeature.NumberValidation
            : code.Contains("media", StringComparison.OrdinalIgnoreCase) ? WhatsAppConnectorFeature.MediaReceive
            : code.Contains("qr", StringComparison.OrdinalIgnoreCase) ? WhatsAppConnectorFeature.QrPairing
            : WhatsAppConnectorFeature.Transport;
        SetFeature(state, feature, WhatsAppFeatureHealthState.Degraded, code);
        AddError(state.AccountId, code, feature);
    }

    private async Task<object> DatabaseIntegrityAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath)) return new { status = "missing", quickCheck = "not_run", tableCount = 0 };
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            var quickCheck = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken)) ?? "unknown";
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table';";
            var tableCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
            return new { status = quickCheck == "ok" ? "healthy" : "degraded", quickCheck, tableCount };
        }
        catch
        {
            return new { status = "unavailable", quickCheck = "failed", tableCount = 0 };
        }
    }

    private static async Task WriteJsonAsync(ZipArchive archive, string name, object value, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await JsonSerializer.SerializeAsync(output, value, value.GetType(), JsonOptions, cancellationToken);
    }

    private AccountState GetState(string accountId) => _accounts.GetOrAdd(NormalizeAccount(accountId), id =>
    {
        lock (_safetyFileGate)
            return new AccountState(id) { SafeMode = _persistedSafeModes.GetValueOrDefault(id) ?? WhatsAppConnectorSafeMode.Inactive };
    });
    private static string NormalizeAccount(string accountId) => string.IsNullOrWhiteSpace(accountId) ? "primary" : accountId.Trim();

    private static void SetFeature(AccountState state, WhatsAppConnectorFeature feature, WhatsAppFeatureHealthState health, string code)
    {
        var now = DateTimeOffset.UtcNow;
        state.Features[feature] = new WhatsAppFeatureHealth(feature, health, SanitizeCode(code), now);
        state.UpdatedAt = now;
    }

    private void EnterSafeMode(AccountState state, string code, bool blocksAutomaticText, bool blocksAutomaticMedia)
    {
        state.SafeMode = new WhatsAppConnectorSafeMode(true, SanitizeCode(code), DateTimeOffset.UtcNow, blocksAutomaticText, blocksAutomaticMedia);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        lock (_safetyFileGate)
        {
            _persistedSafeModes[state.AccountId] = state.SafeMode;
            PersistSafeModes();
        }
    }

    private void AddError(string accountId, string code, WhatsAppConnectorFeature feature)
    {
        _recentErrors.Enqueue(new DiagnosticError(NormalizeAccount(accountId), SanitizeCode(code), feature, DateTimeOffset.UtcNow));
        while (_recentErrors.Count > 200 && _recentErrors.TryDequeue(out _)) { }
    }

    private static string SanitizeCode(string code)
    {
        var normalized = new string((code ?? "unknown").Take(96).Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static string ReadString(JsonElement data, string property, string fallback) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string HashAccount(string accountId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"Relvyn diagnostics v1:{NormalizeAccount(accountId)}"));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string? CapabilityFor(WhatsAppConnectorFeature feature) => feature switch
    {
        WhatsAppConnectorFeature.Session => "sessionPersistence",
        WhatsAppConnectorFeature.QrPairing => "qrPairing",
        WhatsAppConnectorFeature.DirectMessages => "directMessages",
        WhatsAppConnectorFeature.GroupMessages => "groupMessages",
        WhatsAppConnectorFeature.HistorySync => "historySync",
        WhatsAppConnectorFeature.OfflineCatchup => "offlineCatchup",
        WhatsAppConnectorFeature.MediaReceive => "mediaReceive",
        WhatsAppConnectorFeature.TextSend => "textSend",
        WhatsAppConnectorFeature.MediaSend => "mediaSend",
        WhatsAppConnectorFeature.Reply => "reply",
        WhatsAppConnectorFeature.Revoke => "revoke",
        WhatsAppConnectorFeature.DeliveryReceipts => "deliveryReceipts",
        WhatsAppConnectorFeature.ReadReceipts => "readReceipts",
        WhatsAppConnectorFeature.NumberValidation => "numberValidation",
        WhatsAppConnectorFeature.PinChat => "pinChat",
        WhatsAppConnectorFeature.Groups => "groups",
        WhatsAppConnectorFeature.Labels => "labels",
        WhatsAppConnectorFeature.LidMapping => "lidMapping",
        WhatsAppConnectorFeature.OutboundGovernor => "outboundGovernor",
        WhatsAppConnectorFeature.Idempotency => "idempotency",
        _ => null
    };

    private static Dictionary<string, WhatsAppConnectorSafeMode> LoadSafeModes(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "whatsapp-connector-safety.json");
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var document = JsonSerializer.Deserialize<SafetyStateDocument>(File.ReadAllText(path));
            return (document?.Accounts ?? new Dictionary<string, WhatsAppConnectorSafeMode>())
                .Where(item => item.Value.Active)
                .ToDictionary(item => NormalizeAccount(item.Key), item => item.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void PersistSafeModes()
    {
        Directory.CreateDirectory(_dataRoot);
        var target = Path.Combine(_dataRoot, "whatsapp-connector-safety.json");
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SafetyStateDocument(1, _persistedSafeModes), JsonOptions));
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class AccountState(string accountId)
    {
        public string AccountId { get; } = accountId;
        public WhatsAppConnectorProtocolInfo Protocol { get; set; } = WhatsAppConnectorProtocolInfo.LegacyCompatible;
        public ConcurrentDictionary<WhatsAppConnectorFeature, WhatsAppFeatureHealth> Features { get; } = new();
        public WhatsAppConnectorSafeMode SafeMode { get; set; } = WhatsAppConnectorSafeMode.Inactive;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed record DiagnosticError(string AccountId, string Code, WhatsAppConnectorFeature Feature, DateTimeOffset OccurredAt);
    private sealed record SafetyStateDocument(int SchemaVersion, IReadOnlyDictionary<string, WhatsAppConnectorSafeMode> Accounts);

    private sealed record ScopedFeaturePolicy(bool AutomaticTextEnabled, bool AutomaticMediaEnabled, string Source)
    {
        public static ScopedFeaturePolicy Load(string dataRoot)
        {
            var path = Path.Combine(dataRoot, "whatsapp-connector-policy.json");
            if (!File.Exists(path)) return new(true, true, "embedded-default");
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
                    return new(true, true, "embedded-default-invalid-local-policy");
                var automaticText = !root.TryGetProperty("automaticTextEnabled", out var text) || text.ValueKind != JsonValueKind.False;
                var automaticMedia = !root.TryGetProperty("automaticMediaEnabled", out var media) || media.ValueKind != JsonValueKind.False;
                return new(automaticText, automaticMedia, "accepted-local-policy");
            }
            catch
            {
                return new(true, true, "embedded-default-unreadable-local-policy");
            }
        }
    }

    private sealed record CompatibilityManifest(
        bool Available,
        string Source,
        string BridgeVersion,
        int ProtocolVersion,
        string Connector,
        string ConnectorVersion,
        string MinRelvynVersion,
        string MaxRelvynVersion)
    {
        public static CompatibilityManifest Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "whatsapp-connector-compatibility.json");
            if (!File.Exists(path)) return Missing("manifest-missing-local-stable-preserved");
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
                    return Missing("manifest-invalid-local-stable-preserved");
                return new(
                    true,
                    "packaged-manifest",
                    ReadString(root, "bridgeVersion", ""),
                    root.TryGetProperty("protocolVersion", out var protocol) && protocol.TryGetInt32(out var version) ? version : 0,
                    ReadString(root, "connector", ""),
                    ReadString(root, "connectorVersion", ""),
                    ReadString(root, "minRelvynVersion", ""),
                    ReadString(root, "maxRelvynVersion", ""));
            }
            catch
            {
                return Missing("manifest-unreadable-local-stable-preserved");
            }
        }

        public WhatsAppConnectorProtocolInfo Apply(WhatsAppConnectorProtocolInfo protocol)
        {
            // A missing or damaged manifest must never turn a working installed
            // connector into a forced logout. The packaged stable pair remains in
            // place and protocol metadata still supplies the safe compatibility check.
            if (!Available || protocol.IsLegacyFallback || !protocol.IsCompatible) return protocol;
            var compatible = protocol.ProtocolVersion == ProtocolVersion
                && protocol.BridgeVersion.Equals(BridgeVersion, StringComparison.OrdinalIgnoreCase)
                && protocol.Connector.Equals(Connector, StringComparison.OrdinalIgnoreCase)
                && protocol.ConnectorVersion.Equals(ConnectorVersion, StringComparison.OrdinalIgnoreCase)
                && ApplicationVersionInRange();
            return compatible ? protocol : protocol with { IsCompatible = false, CompatibilityCode = "packaged_manifest_rejected_connector" };
        }

        private bool ApplicationVersionInRange()
        {
            var current = Assembly.GetEntryAssembly()?.GetName().Version ?? Assembly.GetExecutingAssembly().GetName().Version;
            if (current is null || !Version.TryParse(NormalizeMinimum(MinRelvynVersion), out var minimum)) return true;
            if (current < minimum) return false;
            var maxPrefix = (MaxRelvynVersion ?? "").Trim().TrimStart('v').Split('.');
            if (maxPrefix.Length >= 2 && int.TryParse(maxPrefix[0], out var major) && int.TryParse(maxPrefix[1], out var minor))
                return current.Major < major || current.Major == major && current.Minor <= minor;
            return true;
        }

        private static string NormalizeMinimum(string value)
        {
            var normalized = (value ?? "").Trim().TrimStart('v');
            return normalized.Count(ch => ch == '.') == 1 ? normalized + ".0" : normalized;
        }

        private static CompatibilityManifest Missing(string source) => new(false, source, "", 0, "", "", "", "");
    }
}
