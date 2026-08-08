using System.Collections.Concurrent;
using System.Text.Json;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record CustomerSuccessAgentRunCompletedEvent(
    string AccountId,
    string ConversationId,
    CustomerSuccessRunStatus Status);

/// <summary>
/// A conversation whose offline-backlog message was withheld from automatic
/// sending. <paramref name="DraftGenerated"/> is false when the per-catch-up
/// draft budget was already spent (PRD v0.4 F5.5).
/// </summary>
public sealed record CustomerSuccessOfflineBacklogEvent(
    string AccountId,
    string ConversationId,
    bool DraftGenerated);

/// <summary>What an offline-backlog message is permitted to do.</summary>
public enum OfflineBacklogDisposition
{
    /// <summary>Generate a draft for confirmation, never send.</summary>
    DraftOnly,

    /// <summary>Budget spent: record the conversation, do not call the model.</summary>
    SummaryOnly,

    /// <summary>The gate is switched off; treat the message as live.</summary>
    GateDisabled
}

public interface ICustomerSuccessMessageSender
{
    Task<JsonElement> SendTextAsync(
        string accountId,
        string phone,
        string text,
        OutboundSendOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class CustomerSuccessAgentCoordinator : IDisposable
{
    private const string ContextChangedMessage = "上下文已变化，请重新生成";

    private readonly LocalRepository _repository;
    private readonly WhatsAppSyncService _sync;
    private readonly ICustomerSuccessMessageSender _connections;
    private readonly CustomerSuccessAgentService _agent;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Conversations already drafted in the current offline catch-up window, per
    /// account. A set rather than a counter because the budget in PRD F5.5 is
    /// fifty *conversations*: one customer who sent forty messages during the
    /// outage must not starve thirty-nine others.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _backlogDraftedConversations =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<CustomerSuccessAgentRunCompletedEvent>? RunCompleted;

    /// <summary>Raised when backlog messages were withheld from automatic sending.</summary>
    public event EventHandler<CustomerSuccessOfflineBacklogEvent>? OfflineBacklogDeferred;

    public CustomerSuccessAgentCoordinator(
        LocalRepository repository,
        WhatsAppSyncService sync,
        ICustomerSuccessMessageSender connections,
        CustomerSuccessAgentService agent)
    {
        _repository = repository;
        _sync = sync;
        _connections = connections;
        _agent = agent;
        _sync.MessageSynchronized += OnMessageSynchronized;
        _sync.OfflineCatchupChanged += OnOfflineCatchupChanged;
    }

    /// <summary>
    /// Resets on both edges of the catch-up window, not just the opening one.
    /// The age threshold can classify a straggler as backlog long after any
    /// catch-up — clock skew, a delayed stanza — and without a closing reset
    /// those would slowly consume the budget of a long-lived session until
    /// drafting stopped altogether, silently and indefinitely. Overshooting the
    /// budget by a few drafts is the recoverable direction.
    /// </summary>
    private void OnOfflineCatchupChanged(object? sender, WhatsAppOfflineCatchupEvent e) =>
        _backlogDraftedConversations[e.AccountId] = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

    private void OnMessageSynchronized(object? sender, WhatsAppMessageSyncedEvent e)
    {
        var message = e.Message;
        if (message.IsGroup) return;
        if (message.Direction == WhatsAppMessageDirection.Outgoing && !message.IsRevoked)
        {
            _ = ReconcileOutgoingStatusAsync(message, _shutdown.Token);
            return;
        }
        if (message.Direction != WhatsAppMessageDirection.Incoming || message.IsStatusUpdate ||
            message.IsRevoked || string.IsNullOrWhiteSpace(message.Body)) return;
        _ = HandleAsync(message, e.Arrival, _shutdown.Token);
    }

    /// <summary>
    /// Decides what a backlog message is allowed to do this catch-up window.
    ///
    /// Three outcomes, in order of preference: generate a draft the user
    /// confirms; record a summary without spending an LLM call once the budget
    /// is gone; or — when the gate is switched off — behave exactly as before.
    /// </summary>
    private async Task<OfflineBacklogDisposition> ResolveBacklogDispositionAsync(
        WhatsAppMessage message,
        CancellationToken cancellationToken)
    {
        AgentAutomationSettings automation;
        try
        {
            automation = (await _repository.GetAppSettingsAsync(cancellationToken)).AgentAutomation
                         ?? new AgentAutomationSettings();
        }
        catch
        {
            // Fail closed: if the gate's own configuration cannot be read, hold
            // the message back rather than send on an unverified assumption.
            automation = new AgentAutomationSettings();
        }
        if (!automation.OfflineBacklogGateEnabled) return OfflineBacklogDisposition.GateDisabled;

        var drafted = _backlogDraftedConversations.GetOrAdd(
            message.AccountId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        // A conversation already inside the budget keeps drafting for every
        // further message it receives; only a *new* conversation spends a slot.
        if (drafted.ContainsKey(message.ConversationId)) return OfflineBacklogDisposition.DraftOnly;
        if (drafted.Count >= automation.NormalizedDraftLimit()) return OfflineBacklogDisposition.SummaryOnly;
        drafted.TryAdd(message.ConversationId, 0);
        return OfflineBacklogDisposition.DraftOnly;
    }

    private async Task RecordBacklogSummaryAsync(WhatsAppMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _repository.LogEventAsync(
                "customer_success_offline_backlog_deferred",
                null,
                null,
                Json.Serialize(new
                {
                    message.AccountId,
                    message.ConversationId,
                    sourceMessageId = message.Id,
                    message.Timestamp,
                    notSentReason = "offline_backlog_draft_limit"
                }),
                cancellationToken);
        }
        catch
        {
            // Diagnostics must never take down the sync loop.
        }
        OfflineBacklogDeferred?.Invoke(this, new CustomerSuccessOfflineBacklogEvent(
            message.AccountId, message.ConversationId, false));
    }

    private async Task ReconcileOutgoingStatusAsync(WhatsAppMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var state = await _repository.GetConversationAgentStateAsync(
                message.AccountId, message.ConversationId, cancellationToken);
            if (state is null ||
                string.IsNullOrWhiteSpace(state.LastProviderMessageId) ||
                !state.LastProviderMessageId.Equals(message.ProviderMessageId, StringComparison.OrdinalIgnoreCase))
                return;

            if (message.Status == WhatsAppMessageStatus.Failed)
            {
                var status = state.LastRunStatus == CustomerSuccessRunStatus.HumanRequired
                    ? CustomerSuccessRunStatus.HumanRequired
                    : CustomerSuccessRunStatus.Failed;
                await _agent.UpdateRunOutcomeAsync(
                    message.AccountId, message.ConversationId, status,
                    state.LastRunStatus == CustomerSuccessRunStatus.HumanRequired
                        ? "高风险问题仍由人工处理；占位回复发送失败。"
                        : "WhatsApp 后续回执确认自动回复发送失败。",
                    state.LastProviderMessageId,
                    message.FailureReason,
                    cancellationToken);
                RaiseRunCompleted(message, status);
                return;
            }

            if (message.Status is not WhatsAppMessageStatus.Sent and
                not WhatsAppMessageStatus.Delivered and
                not WhatsAppMessageStatus.Read)
                return;
            var reconciledStatus = state.LastRunStatus == CustomerSuccessRunStatus.HumanRequired
                ? CustomerSuccessRunStatus.HumanRequired
                : CustomerSuccessRunStatus.AutoReplySent;
            var detail = state.LastRunStatus == CustomerSuccessRunStatus.HumanRequired
                ? $"高风险问题仍由人工处理；占位回复状态：{message.Status}。"
                : $"WhatsApp 后续回执已确认自动回复状态：{message.Status}。";
            await _agent.UpdateRunOutcomeAsync(
                message.AccountId, message.ConversationId, reconciledStatus, detail,
                state.LastProviderMessageId, cancellationToken: cancellationToken);
            RaiseRunCompleted(message, reconciledStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // A late receipt must never interrupt the primary WhatsApp sync loop.
        }
    }

    // Single overload on purpose: a convenience wrapper defaulting to
    // MessageArrival.Live would be the fail-open direction, and the smoke tests
    // reach this by name through reflection.
    private async Task HandleAsync(WhatsAppMessage message, MessageArrival arrival, CancellationToken cancellationToken)
    {
        CustomerSuccessAgentRunResult? result = null;
        var expectedCustomerId = "";
        var transportAcknowledged = false;
        var committedCustomerId = "";
        try
        {
            var conversation = (await _repository.GetWhatsAppConversationsAsync(message.AccountId, cancellationToken))
                .FirstOrDefault(item => item.Id == message.ConversationId);
            if (conversation is null) return;
            var state = await _repository.GetConversationAgentStateAsync(message.AccountId, message.ConversationId, cancellationToken);
            if (state?.Mode is not ConversationAgentMode.CopilotActive and not ConversationAgentMode.AutoActive &&
                state?.Mode is not ConversationAgentMode.HumanRequired and not ConversationAgentMode.HumanActive and not ConversationAgentMode.ResumeReview)
                return;
            expectedCustomerId = state.CustomerId;
            var requestedMode = state.Mode;
            var backlogGated = false;
            if (arrival == MessageArrival.OfflineBacklog)
            {
                var disposition = await ResolveBacklogDispositionAsync(message, cancellationToken);
                if (disposition == OfflineBacklogDisposition.SummaryOnly)
                {
                    await RecordBacklogSummaryAsync(message, cancellationToken);
                    await TryUpdateRunOutcomeAsync(
                        null,
                        expectedCustomerId,
                        message,
                        CustomerSuccessRunStatus.Blocked,
                        "离线期间堆积的消息数量超过本次补齐的草稿上限，未生成草稿，也未发送。",
                        cancellationToken: cancellationToken);
                    RaiseRunCompleted(message, CustomerSuccessRunStatus.Blocked);
                    return;
                }
                if (disposition == OfflineBacklogDisposition.DraftOnly)
                {
                    // Downgrading the mode — rather than adding a parallel
                    // "draft only" path — reuses the copilot flow that is already
                    // proven not to send, so there is no second place where a
                    // send could slip through.
                    backlogGated = true;
                    if (requestedMode == ConversationAgentMode.AutoActive)
                        requestedMode = ConversationAgentMode.CopilotActive;
                }
            }
            result = await _agent.AnalyzeAsync(
                message.AccountId, message.ConversationId, conversation.Phone, conversation.DisplayName,
                sourceMessageId: message.Id,
                trigger: CustomerSuccessRunTrigger.IncomingAutomation,
                cancellationToken: cancellationToken);
            if (result.Decision is null)
            {
                var status = requestedMode is ConversationAgentMode.HumanRequired or ConversationAgentMode.HumanActive or ConversationAgentMode.ResumeReview
                    ? CustomerSuccessRunStatus.HumanRequired
                    : CustomerSuccessRunStatus.Blocked;
                await TryUpdateRunOutcomeAsync(
                    result,
                    expectedCustomerId,
                    message,
                    status,
                    string.IsNullOrWhiteSpace(result.BlockReason) ? "本轮没有生成回复。" : result.BlockReason,
                    cancellationToken: cancellationToken);
                RaiseRunCompleted(message, status);
                return;
            }

            if (result.Handoff is not null && requestedMode != ConversationAgentMode.AutoActive)
            {
                await TryUpdateRunOutcomeAsync(
                    result,
                    expectedCustomerId,
                    message,
                    CustomerSuccessRunStatus.HumanRequired,
                    "检测到高风险问题，协作草稿未发送，已转人工处理。",
                    cancellationToken: cancellationToken);
                RaiseRunCompleted(message, CustomerSuccessRunStatus.HumanRequired);
                return;
            }

            if (requestedMode == ConversationAgentMode.CopilotActive)
            {
                if (backlogGated)
                {
                    await TryUpdateRunOutcomeAsync(
                        result,
                        expectedCustomerId,
                        message,
                        CustomerSuccessRunStatus.CopilotDraftReady,
                        "这条消息是电脑离线期间堆积的，已生成待确认草稿，未自动发送。",
                        cancellationToken: cancellationToken);
                    // Raised only now: before AnalyzeAsync there is nothing for the
                    // user to confirm, and the run can still end without a draft.
                    OfflineBacklogDeferred?.Invoke(this, new CustomerSuccessOfflineBacklogEvent(
                        message.AccountId, message.ConversationId, true));
                }
                RaiseRunCompleted(message, CustomerSuccessRunStatus.CopilotDraftReady);
                return;
            }

            var shouldSendHolding = requestedMode == ConversationAgentMode.AutoActive &&
                                    result.Handoff is not null &&
                                    string.IsNullOrWhiteSpace(result.AgentState?.LastHoldingReplyMessageId);
            if (!result.AutoReplyAllowed && !shouldSendHolding)
            {
                var detail = string.IsNullOrWhiteSpace(result.BlockReason)
                    ? "自动回复未通过账号锁或安全校验，消息未发送。"
                    : result.BlockReason;
                await TryUpdateRunOutcomeAsync(
                    result,
                    expectedCustomerId,
                    message,
                    CustomerSuccessRunStatus.Blocked,
                    detail,
                    cancellationToken: cancellationToken);
                RaiseRunCompleted(message, CustomerSuccessRunStatus.Blocked);
                return;
            }
            if (result.ContextToken is null)
                throw new InvalidOperationException(ContextChangedMessage);
            if (shouldSendHolding)
                await EnsureHoldingContextCurrentAsync(result, message, cancellationToken);
            var capturedIdentityLink = await _repository.GetWhatsAppIdentityLinkAsync(
                result.ContextToken.AccountId,
                result.ContextToken.ConversationId,
                cancellationToken);
            if (capturedIdentityLink is null || !capturedIdentityLink.IsActive)
                throw new InvalidOperationException(ContextChangedMessage);
            var acknowledgedSendBindingToken = BuildAcknowledgedSendBindingToken(capturedIdentityLink);
            var verifiedConversation = await _agent.EnsureRunContextCurrentAsync(
                result.ContextToken,
                requireAutoLock: !shouldSendHolding,
                requireProcessedState: true,
                cancellationToken);
            // Last line of defence for the offline gate. The mode downgrade above
            // is what normally stops a backlog reply, but that decision lives in a
            // local read taken before the analysis; this one is local to the send
            // itself, so no future branch can reach WhatsApp behind the gate's back.
            if (backlogGated) throw new InvalidOperationException(ContextChangedMessage);
            // The run token makes the key stable across an RPC timeout retry for
            // the same generated reply, and different for a regenerated one.
            var response = await _connections.SendTextAsync(
                message.AccountId,
                verifiedConversation.Phone,
                result.Decision.ReplyText,
                OutboundSendOptions.ForAgent(message.ConversationId, result.ContextToken.RunToken),
                cancellationToken);
            var providerMessageId = ReadProviderId(response);
            var targetVerified = ReadBool(response, "targetVerified");
            var providerStatus = ReadNumericStatus(response);
            if (string.IsNullOrWhiteSpace(providerMessageId))
                throw new InvalidOperationException("WhatsApp 未返回服务端消息 ID，AI 回复未确认发出。");
            if (!targetVerified)
                throw new InvalidOperationException("WhatsApp 未确认目标联系人，AI 回复未发出。");
            transportAcknowledged = true;

            var postSendContextCurrent = true;
            try
            {
                if (shouldSendHolding)
                    await EnsureHoldingContextCurrentAsync(result, message, CancellationToken.None);
                await _agent.EnsureRunContextCurrentAsync(
                    result.ContextToken,
                    requireAutoLock: !shouldSendHolding,
                    requireProcessedState: true,
                    CancellationToken.None);
            }
            catch (InvalidOperationException error) when (error.Message == ContextChangedMessage)
            {
                postSendContextCurrent = false;
            }
            catch (OperationCanceledException)
            {
                postSendContextCurrent = false;
            }
            catch (Exception)
            {
                postSendContextCurrent = false;
            }

            var acknowledgedAt = ReadTimestamp(response) ?? DateTimeOffset.Now;
            var messageStatus = WhatsAppStatusFromNumeric(providerStatus);
            var acknowledgedCommit = await _repository.PersistAcknowledgedOutgoingWhatsAppAsync(
                new WhatsAppConversation
                {
                    Id = verifiedConversation.Id,
                    AccountId = verifiedConversation.AccountId,
                    Jid = verifiedConversation.Jid,
                    Phone = verifiedConversation.Phone,
                    IsGroup = verifiedConversation.IsGroup,
                    DisplayName = verifiedConversation.DisplayName,
                    LastMessage = result.Decision.ReplyText,
                    LastMessageAt = acknowledgedAt,
                    UnreadCount = verifiedConversation.UnreadCount,
                    LastReadAt = verifiedConversation.LastReadAt,
                    IsPinned = verifiedConversation.IsPinned,
                    PinnedAt = verifiedConversation.PinnedAt
                },
                new WhatsAppMessage
                {
                    Id = $"{message.AccountId}:{providerMessageId}",
                    ProviderMessageId = providerMessageId,
                    AccountId = message.AccountId,
                    ConversationId = message.ConversationId,
                    Jid = verifiedConversation.Jid,
                    Phone = verifiedConversation.Phone,
                    Direction = WhatsAppMessageDirection.Outgoing,
                    Status = messageStatus,
                    Kind = "text",
                    Body = result.Decision.ReplyText,
                    Timestamp = acknowledgedAt,
                    StatusUpdatedAt = acknowledgedAt,
                    DeliveredAt = messageStatus is WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read
                        ? acknowledgedAt
                        : null,
                    ReadAt = messageStatus == WhatsAppMessageStatus.Read ? acknowledgedAt : null,
                    FailedAt = messageStatus == WhatsAppMessageStatus.Failed ? acknowledgedAt : null,
                    Source = shouldSendHolding ? "customer_success_holding" : "customer_success_auto"
                },
                result.ContextToken.CustomerId,
                acknowledgedSendBindingToken,
                postSendContextCurrent,
                updateLeadConnection: providerStatus is >= 2 and <= 4,
                expectedCustomerIdentityHash: result.ContextToken.CustomerIdentityHash,
                expectedActiveFactSetToken: result.ContextToken.ActiveFactSetToken,
                expectedRunContextToken: result.ContextToken.RunToken,
                expectedConversationTargetToken: result.ContextToken.ConversationTargetToken,
                expectedSourceMessageId: result.ContextToken.SourceMessageId,
                expectedSourceMessageToken: result.ContextToken.SourceMessageToken,
                cancellationToken: CancellationToken.None);
            committedCustomerId = acknowledgedCommit.AttributedCustomerId;
            if (acknowledgedCommit.ContextChanged ||
                string.IsNullOrWhiteSpace(acknowledgedCommit.AttributedCustomerId) ||
                !acknowledgedCommit.AttributedCustomerId.Equals(
                    result.ContextToken.CustomerId,
                    StringComparison.OrdinalIgnoreCase))
            {
                await LogUnattributedAcknowledgedContextChangeAsync(
                    message,
                    result,
                    acknowledgedCommit.ContextChangeReason,
                    providerMessageId,
                    providerStatus,
                    targetVerified);
                RaiseRunCompleted(message, CustomerSuccessRunStatus.AutoReplyPending);
                return;
            }
            var attributedCustomerId = acknowledgedCommit.AttributedCustomerId;
            var confirmedByServer = providerStatus is >= 2 and <= 4;
            var runStatus = shouldSendHolding
                ? CustomerSuccessRunStatus.HumanRequired
                : confirmedByServer
                    ? CustomerSuccessRunStatus.AutoReplySent
                    : CustomerSuccessRunStatus.AutoReplyPending;
            var runDetail = shouldSendHolding
                ? confirmedByServer
                    ? "高风险问题已转人工，占位回复已由 WhatsApp 服务端确认。"
                    : "高风险问题已转人工，占位回复已提交，等待 WhatsApp 服务端确认。"
                : confirmedByServer
                    ? "自动回复已通过目标校验，并由 WhatsApp 服务端确认。"
                    : "自动回复已取得消息 ID，等待 WhatsApp 服务端状态确认。";
            var updatedState = await _repository.TryUpdateConversationAgentRunOutcomeAsync(
                message.AccountId,
                message.ConversationId,
                attributedCustomerId,
                result.ContextToken.RunToken,
                runStatus,
                runDetail,
                providerMessageId,
                holdingReplyMessageId: shouldSendHolding ? providerMessageId : "",
                cancellationToken: CancellationToken.None);
            if (updatedState is null)
            {
                await LogContextChangedAuditAsync(
                    message,
                    result,
                    "post_send_state_claim_rejected",
                    "发送后状态提交时检测到客户重绑定或新一轮已接管；保留发送审计，不覆盖当前客户状态。",
                    providerMessageId,
                    providerStatus,
                    targetVerified,
                    CancellationToken.None);
                RaiseRunCompleted(message, CustomerSuccessRunStatus.AutoReplyPending);
                return;
            }
            if (result.Decision.KnowledgeCitations.Count > 0)
            {
                foreach (var citation in result.Decision.KnowledgeCitations)
                {
                    await _repository.SaveKnowledgeUsageOutcomeAsync(new KnowledgeUsageOutcome
                    {
                        Id = $"{providerMessageId}:{citation.ChunkId}",
                        RetrievalLogId = result.Decision.KnowledgeRetrievalId,
                        ChunkId = citation.ChunkId,
                        CustomerId = attributedCustomerId,
                        SourceMessageId = providerMessageId,
                        ActuallySent = confirmedByServer,
                        ObservationNote = confirmedByServer
                            ? "知识辅助回复已由 WhatsApp 服务端确认；后续回复和阶段结果需另行观察。"
                            : "已取得消息 ID，但服务端状态尚未确认；不计入真实发送样本。"
                    }, CancellationToken.None);
                }
            }
            await _repository.LogEventAsync(
                confirmedByServer
                    ? shouldSendHolding ? "customer_success_holding_reply_sent" : "customer_success_auto_reply_sent"
                    : shouldSendHolding ? "customer_success_holding_reply_pending" : "customer_success_auto_reply_pending",
                attributedCustomerId, null,
                Json.Serialize(new { message.AccountId, message.ConversationId, sourceMessageId = message.Id, providerMessageId, providerStatus, targetVerified = true }),
                CancellationToken.None);
            RaiseRunCompleted(message, runStatus);
        }
        catch (Exception ex)
        {
            if (transportAcknowledged)
            {
                if (!string.IsNullOrWhiteSpace(committedCustomerId))
                {
                    try
                    {
                        await _repository.LogEventAsync(
                            "customer_success_ack_postprocess_failed",
                            committedCustomerId,
                            null,
                            Json.Serialize(new
                            {
                                message.AccountId,
                                message.ConversationId,
                                sourceMessageId = message.Id,
                                committedCustomerId,
                                error = ex.Message
                            }),
                            CancellationToken.None);
                    }
                    catch
                    {
                        // The transport ACK must remain non-retryable even if local diagnostics also fail.
                    }
                }
                RaiseRunCompleted(message, CustomerSuccessRunStatus.AutoReplyPending);
                return;
            }
            if (ex.Message == ContextChangedMessage)
            {
                await LogContextChangedAuditAsync(
                    message,
                    result,
                    "pre_send_context_changed",
                    "客户上下文已变化，本轮已关闭发送且未覆盖当前客户状态。",
                    cancellationToken: CancellationToken.None);
                RaiseRunCompleted(message, CustomerSuccessRunStatus.Failed);
                return;
            }
            // A governor refusal is not a failure of this run: nothing was sent,
            // the reply is intact, and the only question is when to try again.
            // Recording it as Failed would bury the reason in an error string and
            // make the throttle look like a bug.
            if (ex is WhatsAppBridgeException { IsOutboundBlocked: true } blocked)
            {
                var retryAfter = blocked.RetryAfter;
                var detail = retryAfter is null || OutboundBlockCodes.IsHardStop(blocked.Code)
                    ? blocked.Message
                    : $"{blocked.Message}约 {Math.Max(1, (int)Math.Ceiling(retryAfter.Value.TotalSeconds))} 秒后可重试。";
                await TryUpdateRunOutcomeAsync(
                    result,
                    expectedCustomerId,
                    message,
                    CustomerSuccessRunStatus.Blocked,
                    detail,
                    error: blocked.Code,
                    cancellationToken: CancellationToken.None);
                await _repository.LogEventAsync(
                    "customer_success_outbound_blocked",
                    result?.ContextToken?.CustomerId ?? expectedCustomerId,
                    null,
                    Json.Serialize(new
                    {
                        message.AccountId,
                        message.ConversationId,
                        sourceMessageId = message.Id,
                        code = blocked.Code,
                        retryAfterMs = (int?)retryAfter?.TotalMilliseconds
                    }),
                    CancellationToken.None);
                RaiseRunCompleted(message, CustomerSuccessRunStatus.Blocked);
                return;
            }
            var outcomeUpdated = await TryUpdateRunOutcomeAsync(
                result,
                expectedCustomerId,
                message,
                CustomerSuccessRunStatus.Failed,
                "Agent 处理失败，自动发送未获确认。",
                error: ex.Message,
                cancellationToken: CancellationToken.None);
            if (!outcomeUpdated &&
                (result?.ContextToken is not null || !string.IsNullOrWhiteSpace(expectedCustomerId)))
            {
                await LogContextChangedAuditAsync(
                    message,
                    result,
                    "failure_outcome_state_claim_rejected",
                    "失败状态提交时检测到客户重绑定或新一轮已接管；未覆盖当前客户状态。",
                    cancellationToken: CancellationToken.None);
                RaiseRunCompleted(message, CustomerSuccessRunStatus.Failed);
                return;
            }
            await _repository.SaveAgentTurnLogAsync(new AgentTurnLog
            {
                CustomerId = result?.ContextToken?.CustomerId ?? expectedCustomerId,
                AccountId = message.AccountId,
                ConversationId = message.ConversationId,
                SourceMessageId = message.Id,
                Error = ex.Message,
                Decision = "auto_reply_failed"
            }, CancellationToken.None);
            RaiseRunCompleted(message, CustomerSuccessRunStatus.Failed);
        }
    }

    private async Task<bool> TryUpdateRunOutcomeAsync(
        CustomerSuccessAgentRunResult? result,
        string fallbackCustomerId,
        WhatsAppMessage message,
        CustomerSuccessRunStatus status,
        string detail,
        string providerMessageId = "",
        string error = "",
        CancellationToken cancellationToken = default)
    {
        var customerId = result?.ContextToken?.CustomerId ?? fallbackCustomerId;
        if (string.IsNullOrWhiteSpace(customerId)) return false;
        return await _repository.TryUpdateConversationAgentRunOutcomeAsync(
            message.AccountId,
            message.ConversationId,
            customerId,
            result?.ContextToken?.RunToken ?? "",
            status,
            detail,
            providerMessageId,
            error,
            cancellationToken: cancellationToken) is not null;
    }

    private async Task EnsureHoldingContextCurrentAsync(
        CustomerSuccessAgentRunResult result,
        WhatsAppMessage message,
        CancellationToken cancellationToken)
    {
        var customerId = result.ContextToken?.CustomerId ?? throw new InvalidOperationException(ContextChangedMessage);
        var currentState = await _repository.GetConversationAgentStateAsync(
            message.AccountId,
            message.ConversationId,
            cancellationToken);
        var currentHandoff = await _repository.GetOpenHumanHandoffAsync(customerId, cancellationToken);
        if (currentState?.Mode != ConversationAgentMode.HumanRequired ||
            currentHandoff is null || result.Handoff is null ||
            !currentHandoff.Id.Equals(result.Handoff.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(ContextChangedMessage);
    }

    private async Task LogContextChangedAuditAsync(
        WhatsAppMessage message,
        CustomerSuccessAgentRunResult? result,
        string decision,
        string detail,
        string providerMessageId = "",
        int providerStatus = 0,
        bool targetVerified = false,
        CancellationToken cancellationToken = default)
    {
        var token = result?.ContextToken;
        await _repository.LogEventAsync(
            "customer_success_context_changed_fail_closed",
            token?.CustomerId,
            null,
            Json.Serialize(new
            {
                message.AccountId,
                message.ConversationId,
                sourceMessageId = message.Id,
                token?.RunToken,
                token?.IdentityLinkId,
                token?.IdentityLinkToken,
                token?.CustomerIdentityHash,
                token?.ActiveFactSetToken,
                providerMessageId,
                providerStatus,
                targetVerified,
                decision,
                detail
            }),
            cancellationToken);
        await _repository.SaveAgentTurnLogAsync(new AgentTurnLog
        {
            CustomerId = token?.CustomerId ?? "",
            AccountId = message.AccountId,
            ConversationId = message.ConversationId,
            SourceMessageId = message.Id,
            Decision = decision,
            Error = detail
        }, cancellationToken);
    }

    private async Task LogUnattributedAcknowledgedContextChangeAsync(
        WhatsAppMessage message,
        CustomerSuccessAgentRunResult result,
        string detail,
        string providerMessageId,
        int providerStatus,
        bool targetVerified)
    {
        try
        {
            await _repository.LogEventAsync(
                "customer_success_context_changed_fail_closed",
                null,
                null,
                Json.Serialize(new
                {
                    message.AccountId,
                    message.ConversationId,
                    sourceMessageId = message.Id,
                    result.ContextToken?.RunToken,
                    providerMessageId,
                    providerStatus,
                    targetVerified,
                    decision = "post_send_context_changed_unattributed",
                    detail = string.IsNullOrWhiteSpace(detail)
                        ? "WhatsApp 已确认发送，但客户上下文已变化；消息永久按未归属保存。"
                        : detail
                }),
                CancellationToken.None);
        }
        catch
        {
            // The final-unbound ACK remains authoritative if this global,
            // deliberately customer-free diagnostic cannot be written.
        }
    }

    private void RaiseRunCompleted(WhatsAppMessage message, CustomerSuccessRunStatus status) =>
        RunCompleted?.Invoke(this, new CustomerSuccessAgentRunCompletedEvent(message.AccountId, message.ConversationId, status));

    private static string ReadProviderId(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return "";
        foreach (var name in new[] { "messageId", "id", "providerMessageId" })
            if (value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String)
                return item.GetString() ?? "";
        return "";
    }

    private static bool ReadBool(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(name, out var item) &&
               item.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               item.GetBoolean();
    }

    private static int ReadNumericStatus(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty("status", out var item) &&
               item.ValueKind == JsonValueKind.Number &&
               item.TryGetInt32(out var numeric)
            ? numeric
            : 1;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("timestamp", out var item)) return null;
        if (item.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(item.GetString(), out var parsed)) return parsed;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var numeric)) return null;
        try
        {
            return numeric > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                : DateTimeOffset.FromUnixTimeSeconds(numeric);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static WhatsAppMessageStatus WhatsAppStatusFromNumeric(int status) => status switch
    {
        <= 0 => WhatsAppMessageStatus.Failed,
        1 => WhatsAppMessageStatus.Pending,
        2 => WhatsAppMessageStatus.Sent,
        3 => WhatsAppMessageStatus.Delivered,
        _ => WhatsAppMessageStatus.Read
    };

    private static string BuildAcknowledgedSendBindingToken(WhatsAppIdentityLink link) => string.Join("|",
        link.Id,
        link.CustomerId,
        link.ContactJid,
        link.ContactLid,
        link.PhoneIdentityId,
        link.MatchResult,
        link.MatchMethod,
        link.ManuallyConfirmed,
        link.UpdatedAt.ToUniversalTime().ToString("O"));

    public void Dispose()
    {
        _sync.MessageSynchronized -= OnMessageSynchronized;
        _sync.OfflineCatchupChanged -= OnOfflineCatchupChanged;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
