using System.Text.Json;
using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

public enum WhatsAppFeatureHealthState
{
    Healthy,
    Degraded,
    Unavailable,
    Suspended,
    Unknown
}

public enum WhatsAppConnectorFeature
{
    Transport,
    Session,
    QrPairing,
    DirectMessages,
    GroupMessages,
    HistorySync,
    OfflineCatchup,
    MediaReceive,
    TextSend,
    MediaSend,
    Reply,
    Revoke,
    DeliveryReceipts,
    ReadReceipts,
    NumberValidation,
    PinChat,
    Groups,
    Labels,
    LidMapping,
    OutboundGovernor,
    Idempotency
}

public sealed record WhatsAppFeatureHealth(
    WhatsAppConnectorFeature Feature,
    WhatsAppFeatureHealthState State,
    string Code,
    DateTimeOffset UpdatedAt);

public sealed record WhatsAppConnectorProtocolInfo(
    string BridgeName,
    string BridgeVersion,
    int ProtocolVersion,
    string Connector,
    string ConnectorVersion,
    IReadOnlyDictionary<string, bool> Capabilities,
    bool IsLegacyFallback,
    bool IsCompatible,
    string CompatibilityCode)
{
    public static WhatsAppConnectorProtocolInfo LegacyCompatible { get; } = new(
        "WAFlow.WhatsApp.Bridge",
        "legacy",
        1,
        "baileys",
        "legacy",
        WhatsAppConnectorCapabilities.AllEnabled,
        true,
        true,
        "legacy_metadata_absent");
}

public sealed record WhatsAppConnectorSafeMode(
    bool Active,
    string ReasonCode,
    DateTimeOffset? ActivatedAt,
    bool BlocksAutomaticText,
    bool BlocksAutomaticMedia)
{
    public static WhatsAppConnectorSafeMode Inactive { get; } = new(false, "", null, false, false);
}

public sealed record WhatsAppConnectorSnapshot(
    string AccountId,
    WhatsAppConnectorProtocolInfo Protocol,
    IReadOnlyList<WhatsAppFeatureHealth> Features,
    WhatsAppConnectorSafeMode SafeMode,
    string ConnectionState,
    DateTimeOffset UpdatedAt);

public sealed record WhatsAppDiagnosticExportResult(string Path, long Bytes, int Entries);

public interface IWhatsAppConnector
{
    event EventHandler<WhatsAppBridgeEvent>? EventReceived;
    string ActiveAccountId { get; }
    bool IsConnectedFor(string accountId);
    string ConnectionStateFor(string accountId);
    bool HasStoredSession(string accountId);
    Task StartAsync(string accountId = "primary", CancellationToken cancellationToken = default);
    Task<JsonElement> ConnectAsync(string accountId, CancellationToken cancellationToken = default);
    Task<JsonElement> DisconnectAsync(string accountId, CancellationToken cancellationToken = default);
    Task<JsonElement> LogoutAsync(string accountId, CancellationToken cancellationToken = default);
    Task<JsonElement> SendTextAsync(string accountId, string phone, string text, OutboundSendOptions options, CancellationToken cancellationToken = default);
    Task<JsonElement> SendMediaAsync(string accountId, string phone, string path, string caption, OutboundSendOptions options, CancellationToken cancellationToken = default);
    Task<JsonElement> ValidateNumberAsync(string accountId, string phone, CancellationToken cancellationToken = default);
    Task<JsonElement> SendReplyTextAsync(string accountId, string phone, string text, string quotedMessageId, string quotedText, bool quotedFromMe, CancellationToken cancellationToken = default);
    Task<JsonElement> SendReplyMediaAsync(string accountId, string phone, string path, string caption, string quotedMessageId, string quotedText, bool quotedFromMe, CancellationToken cancellationToken = default);
    Task<JsonElement> RevokeMessageAsync(string accountId, string phone, string messageId, CancellationToken cancellationToken = default);
    Task<JsonElement> SetChatPinnedAsync(string accountId, string phone, bool pinned, CancellationToken cancellationToken = default);
    Task<JsonElement> UpsertLabelAsync(string accountId, WhatsAppLabel label, CancellationToken cancellationToken = default);
    Task<JsonElement> CreateLabelAsync(string accountId, string name, int color, CancellationToken cancellationToken = default);
    Task<JsonElement> SetChatLabelAsync(string accountId, string phone, string labelId, bool add, CancellationToken cancellationToken = default);
    Task<WhatsAppGroupCreateResult> CreateGroupAsync(string accountId, WhatsAppGroupCreateRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> SyncNowAsync(string accountId, CancellationToken cancellationToken = default);
    Task<JsonElement> CatchUpHistoryAsync(string accountId, CancellationToken cancellationToken = default);
    Task<JsonElement> CatchUpHistoryAsync(string accountId, IReadOnlyCollection<WhatsAppHistoryCursor> cursors, CancellationToken cancellationToken = default);
    WhatsAppConnectorSnapshot GetConnectorSnapshot(string accountId);
    Task<WhatsAppDiagnosticExportResult> ExportDiagnosticsAsync(string destinationPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Additive connector boundary. It delegates to the battle-tested manager so
/// introducing the seam does not alter any existing Inbox caller or workflow.
/// </summary>
public sealed class WhatsAppConnectorFacade(WhatsAppConnectionManager manager) : IWhatsAppConnector
{
    public event EventHandler<WhatsAppBridgeEvent>? EventReceived
    {
        add => manager.EventReceived += value;
        remove => manager.EventReceived -= value;
    }

    public string ActiveAccountId => manager.ActiveAccountId;
    public bool IsConnectedFor(string accountId) => manager.IsConnectedFor(accountId);
    public string ConnectionStateFor(string accountId) => manager.ConnectionStateFor(accountId);
    public bool HasStoredSession(string accountId) => manager.HasStoredSession(accountId);
    public Task StartAsync(string accountId = "primary", CancellationToken cancellationToken = default) => manager.StartAsync(accountId, cancellationToken);
    public Task<JsonElement> ConnectAsync(string accountId, CancellationToken cancellationToken = default) => manager.ConnectAsync(accountId, cancellationToken);
    public Task<JsonElement> DisconnectAsync(string accountId, CancellationToken cancellationToken = default) => manager.DisconnectAsync(accountId, cancellationToken);
    public Task<JsonElement> LogoutAsync(string accountId, CancellationToken cancellationToken = default) => manager.LogoutAsync(accountId, cancellationToken);
    public Task<JsonElement> SendTextAsync(string accountId, string phone, string text, OutboundSendOptions options, CancellationToken cancellationToken = default) => manager.SendTextAsync(accountId, phone, text, options, cancellationToken);
    public Task<JsonElement> SendMediaAsync(string accountId, string phone, string path, string caption, OutboundSendOptions options, CancellationToken cancellationToken = default) => manager.SendMediaAsync(accountId, phone, path, caption, options, cancellationToken);
    public Task<JsonElement> ValidateNumberAsync(string accountId, string phone, CancellationToken cancellationToken = default) => manager.ValidateNumberAsync(accountId, phone, cancellationToken);
    public Task<JsonElement> SendReplyTextAsync(string accountId, string phone, string text, string quotedMessageId, string quotedText, bool quotedFromMe, CancellationToken cancellationToken = default) => manager.SendReplyTextAsync(accountId, phone, text, quotedMessageId, quotedText, quotedFromMe, cancellationToken);
    public Task<JsonElement> SendReplyMediaAsync(string accountId, string phone, string path, string caption, string quotedMessageId, string quotedText, bool quotedFromMe, CancellationToken cancellationToken = default) => manager.SendReplyMediaAsync(accountId, phone, path, caption, quotedMessageId, quotedText, quotedFromMe, cancellationToken);
    public Task<JsonElement> RevokeMessageAsync(string accountId, string phone, string messageId, CancellationToken cancellationToken = default) => manager.RevokeMessageAsync(accountId, phone, messageId, cancellationToken);
    public Task<JsonElement> SetChatPinnedAsync(string accountId, string phone, bool pinned, CancellationToken cancellationToken = default) => manager.SetChatPinnedAsync(accountId, phone, pinned, cancellationToken);
    public Task<JsonElement> UpsertLabelAsync(string accountId, WhatsAppLabel label, CancellationToken cancellationToken = default) => manager.UpsertLabelAsync(accountId, label, cancellationToken);
    public Task<JsonElement> CreateLabelAsync(string accountId, string name, int color, CancellationToken cancellationToken = default) => manager.CreateLabelAsync(accountId, name, color, cancellationToken);
    public Task<JsonElement> SetChatLabelAsync(string accountId, string phone, string labelId, bool add, CancellationToken cancellationToken = default) => manager.SetChatLabelAsync(accountId, phone, labelId, add, cancellationToken);
    public Task<WhatsAppGroupCreateResult> CreateGroupAsync(string accountId, WhatsAppGroupCreateRequest request, CancellationToken cancellationToken = default) => manager.CreateGroupAsync(accountId, request, cancellationToken);
    public Task<JsonElement> SyncNowAsync(string accountId, CancellationToken cancellationToken = default) => manager.SyncNowAsync(accountId, cancellationToken);
    public Task<JsonElement> CatchUpHistoryAsync(string accountId, CancellationToken cancellationToken = default) => manager.CatchUpHistoryAsync(accountId, cancellationToken);
    public Task<JsonElement> CatchUpHistoryAsync(string accountId, IReadOnlyCollection<WhatsAppHistoryCursor> cursors, CancellationToken cancellationToken = default) => manager.CatchUpHistoryAsync(accountId, cursors, cancellationToken);
    public WhatsAppConnectorSnapshot GetConnectorSnapshot(string accountId) => manager.GetConnectorSnapshot(accountId);
    public Task<WhatsAppDiagnosticExportResult> ExportDiagnosticsAsync(string destinationPath, CancellationToken cancellationToken = default) => manager.ExportDiagnosticsAsync(destinationPath, cancellationToken);
}

public static class WhatsAppConnectorCapabilities
{
    public static IReadOnlyDictionary<string, bool> AllEnabled { get; } = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["multiAccount"] = true,
        ["qrPairing"] = true,
        ["sessionPersistence"] = true,
        ["directMessages"] = true,
        ["groupMessages"] = true,
        ["historySync"] = true,
        ["offlineCatchup"] = true,
        ["mediaReceive"] = true,
        ["textSend"] = true,
        ["mediaSend"] = true,
        ["reply"] = true,
        ["revoke"] = true,
        ["deliveryReceipts"] = true,
        ["readReceipts"] = true,
        ["numberValidation"] = true,
        ["pinChat"] = true,
        ["groups"] = true,
        ["labels"] = true,
        ["lidMapping"] = true,
        ["outboundGovernor"] = true,
        ["idempotency"] = true
    };
}
