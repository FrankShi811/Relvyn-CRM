using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

/// <summary>
/// How a WhatsApp message reached this desktop.
///
/// Before this enum the sync service carried a single <c>historical</c> boolean
/// that was true only for <c>history:*</c> sources. Everything else — including
/// the burst WhatsApp flushes when the desktop comes back online — counted as a
/// live message and triggered the customer-success agent, one automatic reply
/// per AutoActive conversation, with no throttle in between.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageArrival
{
    /// <summary>Real-time delivery while this desktop was already connected.</summary>
    Live,

    /// <summary>Queued by WhatsApp while the desktop was offline and flushed on reconnect.</summary>
    OfflineBacklog,

    /// <summary>Bulk history sync (initial pairing or an explicit catch-up request).</summary>
    HistorySync
}

/// <summary>
/// Classifies bridge message events into <see cref="MessageArrival"/>.
///
/// Deliberately pure and static so the rules can be covered by the smoke tests
/// without a bridge process, a socket or a database.
/// </summary>
public static class WhatsAppMessageArrivalClassifier
{
    public const int MinimumOfflineGraceMinutes = 1;
    public const int MaximumOfflineGraceMinutes = 120;
    public const int DefaultOfflineGraceMinutes = 10;

    /// <summary>Baileys labels offline stanzas <c>append</c> and live ones <c>notify</c>.</summary>
    private const string OfflineBacklogSource = "append";

    public static int NormalizeGraceMinutes(int minutes) =>
        Math.Clamp(minutes <= 0 ? DefaultOfflineGraceMinutes : minutes,
            MinimumOfflineGraceMinutes,
            MaximumOfflineGraceMinutes);

    /// <summary>
    /// Two independent signals, either of which is enough to withhold automation:
    ///
    ///   1. the bridge source label, and
    ///   2. the message age.
    ///
    /// The label alone is not trustworthy — it depends on Baileys' behaviour for
    /// a given stanza — so a message older than the grace window is treated as
    /// backlog even when it arrived labelled as live. The timestamp is the
    /// deterministic signal, the label is the fast one.
    /// </summary>
    public static MessageArrival Classify(
        string? source,
        DateTimeOffset timestamp,
        DateTimeOffset now,
        int offlineGraceMinutes = DefaultOfflineGraceMinutes)
    {
        var normalized = (source ?? "").Trim();
        if (normalized.StartsWith("history:", StringComparison.OrdinalIgnoreCase))
            return MessageArrival.HistorySync;
        if (normalized.Equals(OfflineBacklogSource, StringComparison.OrdinalIgnoreCase))
            return MessageArrival.OfflineBacklog;

        var grace = TimeSpan.FromMinutes(NormalizeGraceMinutes(offlineGraceMinutes));
        // A future-dated timestamp (clock skew between phone and desktop) must
        // never be read as "very old", so only compare in one direction.
        return timestamp < now - grace ? MessageArrival.OfflineBacklog : MessageArrival.Live;
    }
}

/// <summary>Which subsystem asked for an outbound send. Must match bridge ORIGINS.</summary>
public static class OutboundOrigin
{
    public const string Human = "human";
    public const string AiAuto = "ai_auto";
    public const string Campaign = "campaign";

    public static bool IsKnown(string? value) =>
        value is Human or AiAuto or Campaign;

    public static string Normalize(string? value) => IsKnown(value) ? value! : Human;
}

/// <summary>
/// Per-send metadata the bridge governor needs.
///
/// <paramref name="IdempotencyKey"/> closes the duplicate-send window created by
/// the 45 second RPC timeout in <c>WhatsAppBridgeClient.SendCommandAsync</c>: a
/// send may well have reached WhatsApp before C# gives up on it, and the retry
/// would otherwise produce a second message.
/// </summary>
public sealed record OutboundSendOptions(string Origin, string IdempotencyKey = "")
{
    public static readonly OutboundSendOptions Human = new(OutboundOrigin.Human);

    public static OutboundSendOptions ForAgent(string conversationId, string runToken) =>
        new(OutboundOrigin.AiAuto, BuildKey("agent", conversationId, runToken));

    /// <summary>
    /// Deliberately not keyed on the attempt number. The window this closes is an
    /// RPC timeout on a send WhatsApp actually accepted, and the scheduler's next
    /// attempt is a different attempt number — including it would guarantee a
    /// fresh key and a duplicate message. The bridge's 10 minute TTL is what
    /// separates "the same send, retried" from "a genuinely new send".
    /// </summary>
    public static OutboundSendOptions ForCampaign(string campaignId, string recipientId) =>
        new(OutboundOrigin.Campaign, BuildKey("campaign", campaignId, recipientId));

    /// <summary>
    /// Keys are capped at 200 characters by the bridge idempotency store. The
    /// tail is the part that distinguishes two sends, so an over-long key hashes
    /// the prefix rather than truncating the suffix away — truncation would make
    /// two different replies collide and silently swallow the second.
    /// </summary>
    public static string BuildKey(string scope, string first, string second)
    {
        var key = $"{scope}:{first}:{second}";
        if (key.Length <= 200) return key;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first)))[..32];
        var hashed = $"{scope}:{digest}:{second}";
        return hashed.Length <= 200 ? hashed : hashed[^200..];
    }
}

/// <summary>
/// Refusal codes raised by the bridge outbound governor. They arrive as the
/// <c>code</c> of a <c>WhatsAppBridgeException</c>; every one of them means the
/// message was <b>not</b> sent.
/// </summary>
public static class OutboundBlockCodes
{
    public const string MinGap = "outbound_min_gap";
    public const string BurstExhausted = "outbound_burst_exhausted";
    public const string HourlyCap = "outbound_hourly_cap_reached";
    public const string DailyCap = "outbound_daily_cap_reached";
    public const string AiDailyCap = "outbound_ai_daily_cap_reached";
    public const string SuspendedRateLimited = "outbound_suspended_rate_limited";
    public const string SuspendedAccountRisk = "outbound_suspended_account_risk";
    public const string QueueFull = "outbound_queue_full";
    public const string Aborted = "outbound_aborted";
    public const string CatchUpInProgress = "catchup_in_progress";

    public static bool IsBlocked(string? code) => code is
        MinGap or BurstExhausted or HourlyCap or DailyCap or AiDailyCap or
        SuspendedRateLimited or SuspendedAccountRisk or QueueFull or Aborted or CatchUpInProgress;

    /// <summary>
    /// True when waiting is pointless — the budget will not free up on its own
    /// inside a retry window, so the caller must stop rather than reschedule.
    /// </summary>
    public static bool IsHardStop(string? code) => code is
        DailyCap or AiDailyCap or SuspendedAccountRisk;

    public static string Describe(string? code) => code switch
    {
        MinGap => "两条消息之间的最小间隔未到，已延后发送。",
        BurstExhausted => "短时间内发送过于集中，已延后发送。",
        HourlyCap => "本小时发送量已达上限，已延后发送。",
        DailyCap => "本账号今日发送量已达上限，今天不再自动发送。",
        AiDailyCap => "AI 自动回复的今日额度已用尽；人工发送不受影响。",
        SuspendedRateLimited => "WhatsApp 正在限流本账号，已暂停发送并等待自动恢复。",
        SuspendedAccountRisk => "WhatsApp 疑似限制本账号，已停止发送，需人工确认后恢复。",
        QueueFull => "发送队列已满，请稍后重试。",
        Aborted => "发送已被取消。",
        CatchUpInProgress => "正在补齐离线消息，补齐完成前不发送。",
        _ => "发送被账号发送闸门拒绝。"
    };
}

/// <summary>
/// Mirrors <c>DEFAULT_OUTBOUND_CONFIG</c> in <c>bridge/src/outbound-governor.mjs</c>.
/// Persisted inside <c>app_settings</c>; the bridge is the enforcement point, this
/// is only the desktop's copy of the knobs.
/// </summary>
public sealed class OutboundGovernorSettings
{
    public bool Enabled { get; set; } = true;
    public int MinGapMs { get; set; } = 3000;
    public int JitterMs { get; set; } = 4000;
    public int BurstCapacity { get; set; } = 5;
    public int RefillPerMinute { get; set; } = 12;
    public int HourlyCap { get; set; } = 120;
    public int DailyCap { get; set; } = 400;
    public double AiDailyCapRatio { get; set; } = 0.5;
    public int NewAccountWarmupDays { get; set; } = 7;

    /// <summary>
    /// Must stay well below the 45 second RPC timeout. The queue wait is only the
    /// first part of a send — resolving the JID, the device fanout and the target
    /// verification all happen afterwards — so the ceiling leaves room for that
    /// tail. Queueing past the timeout would leave C# believing a send failed
    /// while the bridge still delivers it.
    /// </summary>
    public int MaxQueueWaitMs { get; set; } = 25000;

    public OutboundGovernorSettings Normalized() => new()
    {
        Enabled = Enabled,
        MinGapMs = Math.Clamp(MinGapMs, 1000, 30000),
        JitterMs = Math.Clamp(JitterMs, 0, 60000),
        BurstCapacity = Math.Clamp(BurstCapacity, 1, 50),
        RefillPerMinute = Math.Clamp(RefillPerMinute, 1, 600),
        HourlyCap = Math.Clamp(HourlyCap, 1, 5000),
        DailyCap = Math.Clamp(DailyCap, 1, 20000),
        AiDailyCapRatio = Math.Clamp(AiDailyCapRatio, 0d, 1d),
        NewAccountWarmupDays = Math.Clamp(NewAccountWarmupDays, 0, 60),
        MaxQueueWaitMs = Math.Clamp(MaxQueueWaitMs, 0, 25000)
    };

    /// <summary>
    /// A dictionary rather than an anonymous type: the bridge command is built by
    /// serializing this into the outer payload, and a dictionary's JSON shape does
    /// not depend on how the declared type is inferred at the call site.
    /// </summary>
    public Dictionary<string, object> ToBridgePayload()
    {
        var normalized = Normalized();
        return new Dictionary<string, object>
        {
            ["enabled"] = normalized.Enabled,
            ["minGapMs"] = normalized.MinGapMs,
            ["jitterMs"] = normalized.JitterMs,
            ["burstCapacity"] = normalized.BurstCapacity,
            ["refillPerMinute"] = normalized.RefillPerMinute,
            ["hourlyCap"] = normalized.HourlyCap,
            ["dailyCap"] = normalized.DailyCap,
            ["aiDailyCapRatio"] = normalized.AiDailyCapRatio,
            ["newAccountWarmupDays"] = normalized.NewAccountWarmupDays,
            ["maxQueueWaitMs"] = normalized.MaxQueueWaitMs
        };
    }
}

/// <summary>Desktop-side automation guardrails (PRD v0.4 §5).</summary>
public sealed class AgentAutomationSettings
{
    /// <summary>Short customer-message merge window before an Agent run starts.</summary>
    public int MessageCoalescingSeconds { get; set; } = 10;

    /// <summary>Maximum customer turns an unattended hosting session may answer.</summary>
    public int MaxAutomaticTurns { get; set; } = 8;

    /// <summary>
    /// When on, messages classified as <see cref="MessageArrival.OfflineBacklog"/>
    /// never auto-send: they are downgraded to a draft the user confirms.
    ///
    /// Defaults to on. The failure modes are asymmetric — a withheld reply costs
    /// a manual click, an unwanted burst of automated replies after three days
    /// offline costs account trust and cannot be undone.
    /// </summary>
    public bool OfflineBacklogGateEnabled { get; set; } = true;

    /// <summary>Messages older than this are backlog even if labelled live.</summary>
    public int OfflineGraceMinutes { get; set; } = WhatsAppMessageArrivalClassifier.DefaultOfflineGraceMinutes;

    /// <summary>
    /// Cap on conversations that get an LLM-generated draft per catch-up window.
    /// Coming back from three days offline must not cost hundreds of calls.
    /// </summary>
    public int OfflineBacklogDraftLimit { get; set; } = 50;

    public int NormalizedGraceMinutes() =>
        WhatsAppMessageArrivalClassifier.NormalizeGraceMinutes(OfflineGraceMinutes);

    public int NormalizedDraftLimit() => Math.Clamp(OfflineBacklogDraftLimit, 0, 1000);

    public int NormalizedCoalescingSeconds() => Math.Clamp(MessageCoalescingSeconds, 8, 15);

    public int NormalizedMaxAutomaticTurns() => Math.Clamp(MaxAutomaticTurns, 1, 32);
}

/// <summary>Read model over the bridge's <c>outbound_status</c> snapshot.</summary>
public sealed record OutboundGovernorStatus(
    bool Enabled,
    int DailyTotal,
    int DailyCap,
    int AiDailyCount,
    int AiDailyCap,
    int HourlyCount,
    int HourlyCap,
    int QueueDepth,
    bool Suspended,
    string SuspendReason,
    bool SuspendIndefinite,
    bool WarmupActive)
{
    public static readonly OutboundGovernorStatus Unknown =
        new(false, 0, 0, 0, 0, 0, 0, 0, false, "", false, false);

    public int RemainingToday => Math.Max(0, DailyCap - DailyTotal);
    public int RemainingAiToday => Math.Max(0, AiDailyCap - AiDailyCount);

    public static OutboundGovernorStatus FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return Unknown;
        var aiCount = 0;
        if (element.TryGetProperty("dailyCounts", out var counts)
            && counts.ValueKind == JsonValueKind.Object
            && counts.TryGetProperty(OutboundOrigin.AiAuto, out var aiElement)
            && aiElement.TryGetInt32(out var parsedAi))
            aiCount = parsedAi;
        return new OutboundGovernorStatus(
            Bool(element, "enabled"),
            Int(element, "dailyTotal"),
            Int(element, "dailyCap"),
            aiCount,
            Int(element, "aiDailyCap"),
            Int(element, "hourlyCount"),
            Int(element, "hourlyCap"),
            Int(element, "queueDepth"),
            Bool(element, "suspended"),
            Text(element, "suspendReason"),
            Bool(element, "suspendIndefinite"),
            Bool(element, "warmupActive"));
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
