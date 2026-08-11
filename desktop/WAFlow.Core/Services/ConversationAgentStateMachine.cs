using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

/// <summary>
/// Owns the conversation-level lifecycle. Agent mode is configuration only;
/// RunState is the only source of truth for whether background automation may act.
/// </summary>
public static class ConversationAgentStateMachine
{
    public const int CurrentSchemaVersion = 3;

    public static ConversationAgentState NormalizeLegacyState(ConversationAgentState state)
    {
        state.TenantId = string.IsNullOrWhiteSpace(state.TenantId) ? "local" : state.TenantId;
        state.UserId = string.IsNullOrWhiteSpace(state.UserId) ? "local" : state.UserId;
        state.ContextNamespace = string.IsNullOrWhiteSpace(state.ContextNamespace)
            ? BuildContextNamespace(state.TenantId, state.UserId, state.CustomerId, state.AccountId, state.ConversationId)
            : state.ContextNamespace;
        state.AssistantIdentity = string.IsNullOrWhiteSpace(state.AssistantIdentity)
            ? BusinessRoleContextPolicy.DefaultAssistantIdentity
            : state.AssistantIdentity;
        state.MaxAutomaticTurns = Math.Clamp(state.MaxAutomaticTurns <= 0 ? 8 : state.MaxAutomaticTurns, 1, 32);
        state.LastSourceMessageIds ??= [];
        state.LastCustomerBrainReferences ??= [];
        state.LastKnowledgeReferences ??= [];

        if (state.StateSchemaVersion >= CurrentSchemaVersion)
        {
            return state;
        }

        var legacyRuntimeMode = state.Mode is
            ConversationAgentMode.IdentityResolutionRequired or
            ConversationAgentMode.HumanRequired or
            ConversationAgentMode.HumanActive or
            ConversationAgentMode.ResumeReview;

        if (state.Mode is ConversationAgentMode.IdentityResolutionRequired)
        {
            state.Mode = ConversationAgentMode.SuggestOnly;
            state.RunState = ConversationAgentRunState.WaitingHuman;
            state.PauseReason = "旧版本状态迁移：客户身份需要人工确认。";
        }
        else if (state.Mode is ConversationAgentMode.HumanRequired)
        {
            state.Mode = ConversationAgentMode.AutoActive;
            state.RunState = ConversationAgentRunState.WaitingHuman;
            state.PauseReason = string.IsNullOrWhiteSpace(state.StateReason) ? "旧版本状态迁移：等待人工处理。" : state.StateReason;
        }
        else if (state.Mode is ConversationAgentMode.HumanActive)
        {
            state.Mode = ConversationAgentMode.AutoActive;
            state.RunState = ConversationAgentRunState.HumanTakeover;
            state.PauseReason = string.IsNullOrWhiteSpace(state.StateReason) ? "旧版本状态迁移：人工已接管。" : state.StateReason;
        }
        else if (state.Mode is ConversationAgentMode.ResumeReview)
        {
            state.Mode = ConversationAgentMode.AutoActive;
            state.RunState = ConversationAgentRunState.WaitingHuman;
            state.PauseReason = string.IsNullOrWhiteSpace(state.StateReason) ? "旧版本状态迁移：重新托管前需要复核。" : state.StateReason;
        }

        if (!legacyRuntimeMode)
        {
            state.RunState = state.Mode switch
            {
                ConversationAgentMode.AutoOff => ConversationAgentRunState.Off,
                ConversationAgentMode.SuggestOnly => ConversationAgentRunState.SuggestReady,
                ConversationAgentMode.CopilotActive => ConversationAgentRunState.CollabActive,
                // Old AutoActive rows were previously considered live. For safe
                // restart recovery they are intentionally not re-armed.
                ConversationAgentMode.AutoActive => ConversationAgentRunState.Ended,
                _ => ConversationAgentRunState.Off
            };
            if (state.Mode == ConversationAgentMode.AutoActive)
            {
                state.StateReason = "应用升级或重启后未自动恢复旧托管；请复核最近消息并手动重新托管。";
                state.ExplicitResumeRequired = true;
            }
        }

        state.StateSchemaVersion = CurrentSchemaVersion;
        Touch(state);
        return state;
    }

    public static ConversationAgentState ConfigureMode(ConversationAgentState state, ConversationAgentMode mode, string reason)
    {
        mode = NormalizeConfiguredMode(mode);
        state.StateSchemaVersion = CurrentSchemaVersion;
        state.Mode = mode;
        state.RunState = mode switch
        {
            ConversationAgentMode.AutoOff => ConversationAgentRunState.Off,
            ConversationAgentMode.SuggestOnly => ConversationAgentRunState.SuggestReady,
            ConversationAgentMode.CopilotActive => ConversationAgentRunState.Off,
            ConversationAgentMode.AutoActive => ConversationAgentRunState.Off,
            _ => ConversationAgentRunState.Off
        };
        state.StateReason = reason;
        state.PauseReason = "";
        state.PendingRunContextToken = "";
        state.HostingSessionToken = "";
        state.LastDraftHash = "";
        state.LastSourceMessageIds = [];
        state.HostingStartedAt = null;
        state.HostingEndedAt = DateTimeOffset.Now;
        state.ExplicitResumeRequired = false;
        Touch(state);
        return state;
    }

    public static ConversationAgentState BeginPreflight(ConversationAgentState state, string reason)
    {
        EnsureMode(state, ConversationAgentMode.AutoActive, "只有自动托管模式可以开始托管。");
        state.RunState = ConversationAgentRunState.AutoPreflight;
        state.StateReason = reason;
        state.PauseReason = "";
        state.PendingRunContextToken = "";
        Touch(state);
        return state;
    }

    public static ConversationAgentState Arm(ConversationAgentState state, string contextToken, string reason)
    {
        EnsureState(state, ConversationAgentRunState.AutoPreflight, "必须先完成托管前置检查。");
        if (string.IsNullOrWhiteSpace(contextToken))
        {
            throw new InvalidOperationException("托管上下文令牌不能为空。");
        }

        state.RunState = ConversationAgentRunState.AutoArmed;
        state.HostingSessionToken = contextToken;
        state.PendingRunContextToken = "";
        state.ContextNamespace = BuildContextNamespace(
            state.TenantId, state.UserId, state.CustomerId, state.AccountId, state.ConversationId);
        state.AutomaticTurnCount = 0;
        state.LastDraftHash = "";
        state.LastSourceMessageIds = [];
        state.HostingStartedAt = DateTimeOffset.Now;
        state.HostingEndedAt = null;
        state.StateReason = reason;
        state.PauseReason = "";
        state.ExplicitResumeRequired = false;
        Touch(state);
        return state;
    }

    public static ConversationAgentState StartCollaboration(ConversationAgentState state, string reason)
    {
        EnsureMode(state, ConversationAgentMode.CopilotActive, "只有协作模式可以开始协作。");
        state.RunState = ConversationAgentRunState.CollabActive;
        state.HostingStartedAt = DateTimeOffset.Now;
        state.HostingEndedAt = null;
        state.StateReason = reason;
        Touch(state);
        return state;
    }

    public static ConversationAgentState StopCollaboration(ConversationAgentState state, string reason)
    {
        EnsureMode(state, ConversationAgentMode.CopilotActive, "只有协作模式可以停止协作。");
        state.RunState = ConversationAgentRunState.Off;
        state.StateReason = reason;
        state.PendingRunContextToken = "";
        state.HostingSessionToken = "";
        state.LastDraftHash = "";
        state.HostingEndedAt = DateTimeOffset.Now;
        Touch(state);
        return state;
    }

    public static ConversationAgentState BeginProcessing(ConversationAgentState state, string sourceMessageId, long contextVersion)
    {
        if (!AllowsAutoProcessing(state))
        {
            throw new InvalidOperationException("当前会话未处于可处理新消息的托管状态。");
        }

        state.RunState = ConversationAgentRunState.AutoProcessing;
        state.LastCustomerMessageId = sourceMessageId;
        state.ContextVersion = Math.Max(state.ContextVersion + 1, contextVersion);
        state.LastSourceMessageIds = [sourceMessageId];
        state.LastDraftHash = "";
        state.LastAgentActionAt = DateTimeOffset.Now;
        Touch(state);
        return state;
    }

    public static ConversationAgentState BeginSending(
        ConversationAgentState state,
        string idempotencyKey,
        string draftHash = "")
    {
        EnsureState(state, ConversationAgentRunState.AutoProcessing, "只有最新上下文处理完成后才能发送。");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new InvalidOperationException("自动发送幂等键不能为空。");
        }

        state.RunState = ConversationAgentRunState.AutoSending;
        state.LastIdempotencyKey = idempotencyKey;
        state.LastDraftHash = draftHash;
        state.LastAgentActionAt = DateTimeOffset.Now;
        Touch(state);
        return state;
    }

    public static ConversationAgentState InvalidateDraft(ConversationAgentState state, string reason)
    {
        state.LastGeneratedReply = "";
        state.LastDraftHash = "";
        state.PendingRunContextToken = "";
        state.StateReason = reason;
        if (state.Mode == ConversationAgentMode.AutoActive && !string.IsNullOrWhiteSpace(state.HostingSessionToken))
            state.RunState = ConversationAgentRunState.AutoArmed;
        else if (state.Mode == ConversationAgentMode.CopilotActive)
            state.RunState = ConversationAgentRunState.CollabActive;
        Touch(state);
        return state;
    }

    public static ConversationAgentState WaitForCustomer(ConversationAgentState state, string agentMessageId, string reason)
    {
        state.RunState = ConversationAgentRunState.WaitingCustomer;
        state.LastAgentMessageId = agentMessageId;
        state.AutomaticTurnCount++;
        state.StateReason = reason;
        state.TopicState = ConversationTopicState.WaitingCustomer;
        state.LastAgentActionAt = DateTimeOffset.Now;
        Touch(state);
        return state;
    }

    public static ConversationAgentState MarkTopicResolved(ConversationAgentState state, string reason)
    {
        state.RunState = ConversationAgentRunState.TopicResolved;
        state.TopicState = ConversationTopicState.Resolved;
        state.StateReason = reason;
        state.PendingRunContextToken = "";
        state.HostingSessionToken = "";
        state.HostingEndedAt = DateTimeOffset.Now;
        Touch(state);
        return state;
    }

    public static ConversationAgentState MarkRiskInformationCollectionSent(ConversationAgentState state, string agentMessageId, string reason)
    {
        state.RunState = ConversationAgentRunState.RiskInfoCollectionSent;
        state.RiskState = ConversationRiskVerificationState.InformationCollectionSent;
        state.LastAgentMessageId = agentMessageId;
        state.LastHoldingReplyMessageId = agentMessageId;
        state.StateReason = reason;
        state.LastAgentActionAt = DateTimeOffset.Now;
        state.ExplicitResumeRequired = true;
        Touch(state);
        return state;
    }

    public static ConversationAgentState WaitForHuman(ConversationAgentState state, string reason)
    {
        state.RunState = ConversationAgentRunState.WaitingHuman;
        state.TopicState = ConversationTopicState.WaitingHuman;
        state.RiskState = state.RiskState == ConversationRiskVerificationState.None
            ? ConversationRiskVerificationState.WaitingHuman
            : state.RiskState;
        state.PauseReason = reason;
        state.PendingRunContextToken = "";
        state.HostingSessionToken = "";
        state.ExplicitResumeRequired = true;
        Touch(state);
        return state;
    }

    public static ConversationAgentState PauseError(ConversationAgentState state, string reason)
    {
        state.RunState = ConversationAgentRunState.PausedError;
        state.PauseReason = reason;
        state.PendingRunContextToken = "";
        state.HostingSessionToken = "";
        state.ExplicitResumeRequired = true;
        Touch(state);
        return state;
    }

    public static ConversationAgentState HumanTakeover(ConversationAgentState state, string humanMessageId, string reason)
    {
        state.RunState = ConversationAgentRunState.HumanTakeover;
        state.LastHumanMessageId = humanMessageId;
        state.PauseReason = reason;
        state.LastGeneratedReply = "";
        state.LastDraftHash = "";
        state.LastSourceMessageIds = [];
        state.PendingRunContextToken = "";
        state.HostingSessionToken = "";
        state.HostingEndedAt = DateTimeOffset.Now;
        state.ExplicitResumeRequired = true;
        Touch(state);
        return state;
    }

    public static ConversationAgentState Stop(ConversationAgentState state, string reason)
    {
        state.RunState = ConversationAgentRunState.Ended;
        state.StateReason = reason;
        state.PendingRunContextToken = "";
        state.HostingSessionToken = "";
        state.HostingEndedAt = DateTimeOffset.Now;
        Touch(state);
        return state;
    }

    public static bool AllowsAutoProcessing(ConversationAgentState state) =>
        state.Mode == ConversationAgentMode.AutoActive &&
        state.RunState is ConversationAgentRunState.AutoArmed or ConversationAgentRunState.WaitingCustomer;

    public static bool AllowsCollaboration(ConversationAgentState state) =>
        state.Mode == ConversationAgentMode.CopilotActive && state.RunState == ConversationAgentRunState.CollabActive;

    public static bool IsHosting(ConversationAgentState state) =>
        state.Mode == ConversationAgentMode.AutoActive && state.RunState is
            ConversationAgentRunState.AutoPreflight or
            ConversationAgentRunState.AutoArmed or
            ConversationAgentRunState.AutoProcessing or
            ConversationAgentRunState.AutoSending or
            ConversationAgentRunState.WaitingCustomer;

    public static bool HasReachedAutomaticTurnLimit(ConversationAgentState state) =>
        state.AutomaticTurnCount >= Math.Clamp(state.MaxAutomaticTurns <= 0 ? 8 : state.MaxAutomaticTurns, 1, 32);

    public static string BuildContextNamespace(
        string tenantId,
        string userId,
        string customerId,
        string accountId,
        string conversationId) =>
        string.Join(':', new[]
        {
            string.IsNullOrWhiteSpace(tenantId) ? "local" : tenantId.Trim(),
            string.IsNullOrWhiteSpace(userId) ? "local" : userId.Trim(),
            customerId.Trim(),
            accountId.Trim(),
            conversationId.Trim()
        });

    private static ConversationAgentMode NormalizeConfiguredMode(ConversationAgentMode mode) => mode switch
    {
        ConversationAgentMode.IdentityResolutionRequired or
        ConversationAgentMode.HumanRequired or
        ConversationAgentMode.HumanActive or
        ConversationAgentMode.ResumeReview => ConversationAgentMode.SuggestOnly,
        _ => mode
    };

    private static void EnsureMode(ConversationAgentState state, ConversationAgentMode expected, string message)
    {
        if (state.Mode != expected)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void EnsureState(ConversationAgentState state, ConversationAgentRunState expected, string message)
    {
        if (state.RunState != expected)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Touch(ConversationAgentState state) => state.UpdatedAt = DateTimeOffset.Now;
}
