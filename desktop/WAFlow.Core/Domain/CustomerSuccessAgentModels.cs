using System.Text.Json.Serialization;
using WAFlow.Core.Services;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerIdentityMatchResult
{
    ExactMatch,
    ConfirmedAliasMatch,
    UniqueInferredMatch,
    AmbiguousMatch,
    NoMatch,
    Conflict
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerIdentityMatchMethod
{
    ManualBinding,
    ExactBuyerId,
    ExactJid,
    ConfirmedE164,
    ConfirmedAlias,
    CountryAssistedUnique,
    UniqueDigitBody,
    CandidateOnly
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConversationAgentMode
{
    AutoOff,
    SuggestOnly,
    CopilotActive,
    AutoActive,
    // Legacy values below are retained so existing JSON rows remain readable.
    // Runtime state is stored in ConversationAgentState.RunState instead.
    IdentityResolutionRequired,
    HumanRequired,
    HumanActive,
    ResumeReview
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConversationAgentRunState
{
    Off,
    SuggestReady,
    CollabActive,
    AutoPreflight,
    AutoArmed,
    AutoProcessing,
    AutoSending,
    WaitingCustomer,
    TopicResolved,
    RiskInfoCollectionSent,
    WaitingHuman,
    PausedRisk,
    PausedError,
    HumanTakeover,
    Ended
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConversationTopicState
{
    Unknown,
    Open,
    WaitingCustomer,
    WaitingHuman,
    Resolved,
    Ended
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConversationRiskVerificationState
{
    None,
    OpenUnverified,
    AlreadyDiscussed,
    InformationCollectionSent,
    WaitingHuman,
    Resolved,
    Conflict
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConversationAgentAuditAction
{
    ModeConfigured,
    CollaborationStarted,
    CollaborationStopped,
    PreflightStarted,
    PreflightBlocked,
    HostingStarted,
    HostingStopped,
    HostingPaused,
    MessageQueued,
    MessageCoalesced,
    DuplicateMessageIgnored,
    ContextRead,
    ContextSafetyBlocked,
    TopicEvaluated,
    TopicResolved,
    RiskDetected,
    DraftGenerated,
    DraftInvalidated,
    SendStarted,
    SendCompleted,
    SendFailed,
    RiskInformationCollectionSent,
    ManualMessageDetected,
    HumanTakeover,
    RestartRecovered,
    ErrorPaused
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentQuestionSafety
{
    SafeToAnswer,
    DeferredHuman,
    ImmediateHuman
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerSuccessRunTrigger
{
    Manual,
    IncomingAutomation
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerSuccessRunStatus
{
    None,
    SuggestionReady,
    CopilotDraftReady,
    AutoReplyPending,
    AutoReplySent,
    HumanRequired,
    Blocked,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourcingRequestStatus
{
    Draft,
    Collecting,
    FieldConflict,
    Complete,
    HumanReview,
    Acknowledged,
    Submitted,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourcingFieldKey
{
    ProductImage,
    Quantity,
    TargetPrice,
    Destination,
    ShippingPreference
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HandoffStatus
{
    Open,
    TakenOver,
    Resolved,
    Resumed
}

public sealed class GlobalCustomerIdentity
{
    public string CustomerId { get; set; } = "";
    public string BuyerId { get; set; } = "";
    public string CanonicalKey { get; set; } = "";
    public string CanonicalName { get; set; } = "";
    public List<string> ConfirmedAliases { get; set; } = [];
    public List<string> LinkedAccountIds { get; set; } = [];
    public string PrimaryAccountId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerPhoneIdentity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string RawValue { get; set; } = "";
    public string Digits { get; set; } = "";
    public string CountryHint { get; set; } = "";
    public string E164 { get; set; } = "";
    public string Jid { get; set; } = "";
    public string Lid { get; set; } = "";
    public string SourceAccountId { get; set; } = "";
    public string SourceConversationId { get; set; } = "";
    public bool ManuallyConfirmed { get; set; }
    public double Confidence { get; set; }
    public CustomerIdentityMatchMethod Method { get; set; } = CustomerIdentityMatchMethod.CandidateOnly;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class WhatsAppIdentityLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string ContactJid { get; set; } = "";
    public string ContactLid { get; set; } = "";
    public string PhoneIdentityId { get; set; } = "";
    public CustomerIdentityMatchResult MatchResult { get; set; } = CustomerIdentityMatchResult.NoMatch;
    public CustomerIdentityMatchMethod MatchMethod { get; set; } = CustomerIdentityMatchMethod.CandidateOnly;
    public double Confidence { get; set; }
    public bool ManuallyConfirmed { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AccountPersona
{
    public string AccountId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RoleName { get; set; } = "AI 协作助手";
    public string Introduction { get; set; } =
        "I’m the AI assistant for this team. I can help understand your needs and coordinate next steps. A human colleague will confirm matters that require judgment.";
    public string DefaultLanguage { get; set; } = "en";
    public string Tone { get; set; } = "warm, professional, patient, natural and credible";
    public List<string> AllowedClaims { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AccountRelationshipMemory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string RelationshipStage { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Commitments { get; set; } = [];
    public List<string> KnownPreferences { get; set; } = [];
    public DateTimeOffset? LastInteractionAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerIdentityMatchLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string RawIdentity { get; set; } = "";
    public CustomerIdentityMatchResult Result { get; set; }
    public CustomerIdentityMatchMethod Method { get; set; }
    public List<string> CandidateCustomerIds { get; set; } = [];
    public string Reason { get; set; } = "";
    public double Confidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class GlobalCustomerAgentLock
{
    public string CustomerId { get; set; } = "";
    public string ActiveAccountId { get; set; } = "";
    public string ActiveConversationId { get; set; } = "";
    public string AcquiredBy { get; set; } = "";
    public DateTimeOffset AcquiredAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ConversationAgentState
{
    public int StateSchemaVersion { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = "local";
    public string UserId { get; set; } = "local";
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string OpportunityId { get; set; } = "";
    public string ContextNamespace { get; set; } = "";
    public string AssistantIdentity { get; set; } = BusinessRoleContextPolicy.DefaultAssistantIdentity;
    public ConversationAgentMode Mode { get; set; } = ConversationAgentMode.SuggestOnly;
    public ConversationAgentRunState RunState { get; set; } = ConversationAgentRunState.SuggestReady;
    public ConversationTopicState TopicState { get; set; } = ConversationTopicState.Unknown;
    public ConversationRiskVerificationState RiskState { get; set; } = ConversationRiskVerificationState.None;
    public string StateReason { get; set; } = "";
    public string PauseReason { get; set; } = "";
    public int PausedMessageCount { get; set; }
    public int AutomaticTurnCount { get; set; }
    public int MaxAutomaticTurns { get; set; } = 8;
    public long ContextVersion { get; set; }
    public string LastProcessedMessageId { get; set; } = "";
    public string LastCustomerMessageId { get; set; } = "";
    public string LastHumanMessageId { get; set; } = "";
    public string LastAgentMessageId { get; set; } = "";
    public string LastHoldingReplyMessageId { get; set; } = "";
    public CustomerSuccessRunStatus LastRunStatus { get; set; }
    public string LastRunDetail { get; set; } = "";
    public string LastRunError { get; set; } = "";
    public string LastSourcePreview { get; set; } = "";
    public string LastGeneratedReply { get; set; } = "";
    public string LastRunSummary { get; set; } = "";
    public string LastRecommendedAction { get; set; } = "";
    public string LastProviderMessageId { get; set; } = "";
    public string LastIdempotencyKey { get; set; } = "";
    public string LastDraftHash { get; set; } = "";
    public string LastRiskCategory { get; set; } = "";
    public string LastContextSafetyCheck { get; set; } = "";
    public List<string> LastSourceMessageIds { get; set; } = [];
    public List<string> LastCustomerBrainReferences { get; set; } = [];
    public List<string> LastKnowledgeReferences { get; set; } = [];
    public string PendingRunContextToken { get; set; } = "";
    public string HostingSessionToken { get; set; } = "";
    public DateTimeOffset? HostingStartedAt { get; set; }
    public DateTimeOffset? HostingEndedAt { get; set; }
    public DateTimeOffset? LastAgentActionAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public bool ExplicitResumeRequired { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ConversationAgentPreflightCheck
{
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Passed { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class ConversationAgentPreflightResult
{
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string OpportunityId { get; set; } = "";
    public string ContextNamespace { get; set; } = "";
    public List<ConversationAgentPreflightCheck> Checks { get; set; } = [];
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public bool Passed => Checks.Count > 0 && Checks.All(check => check.Passed);

    [JsonIgnore]
    public string FailureReason => string.Join("；", Checks.Where(check => !check.Passed).Select(check => check.Detail));
}

public sealed class ConversationAgentAuditEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = "local";
    public string UserId { get; set; } = "local";
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string OpportunityId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string ContextVersion { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public ConversationAgentAuditAction Action { get; set; }
    public ConversationAgentMode Mode { get; set; }
    public ConversationAgentRunState StateBefore { get; set; }
    public ConversationAgentRunState StateAfter { get; set; }
    public string Decision { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Model { get; set; } = "";
    public string PromptVersion { get; set; } = "conversation-agent-v0.3";
    public string FinalResult { get; set; } = "";
    public List<string> RetrievedCustomerIds { get; set; } = [];
    public List<string> CustomerBrainReferences { get; set; } = [];
    public List<string> KnowledgeReferences { get; set; } = [];
    public bool ContextSafetyPassed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerSuccessRunContextToken
{
    public string RunToken { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string IdentityLinkId { get; set; } = "";
    public string IdentityLinkToken { get; set; } = "";
    public string CustomerIdentityHash { get; set; } = "";
    public string ActiveFactSetToken { get; set; } = "";
    public string ConversationTargetToken { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string SourceMessageToken { get; set; } = "";
    public string LatestOutgoingMessageId { get; set; } = "";
    public string LatestOutgoingMessageToken { get; set; } = "";
    public string AgentLockToken { get; set; } = "";
    public string HostingSessionToken { get; set; } = "";
    public string ContextNamespace { get; set; } = "";
    public long ContextVersion { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class RelationshipMemory
{
    public string CustomerId { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Facts { get; set; } = [];
    public List<string> Preferences { get; set; } = [];
    public List<string> OpenQuestions { get; set; } = [];
    public List<string> Promises { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class SourcingFieldValue
{
    public SourcingFieldKey Field { get; set; }
    public string Value { get; set; } = "";
    public string NormalizedValue { get; set; } = "";
    public bool IsStructurallyValid { get; set; }
    public bool HumanConfirmed { get; set; }
    public string SourceAccountId { get; set; } = "";
    public string SourceConversationId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string EvidenceQuote { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class SourcingFieldConflict
{
    public SourcingFieldKey Field { get; set; }
    public List<SourcingFieldValue> Values { get; set; } = [];
    public string Resolution { get; set; } = "";
    public bool IsResolved { get; set; }
}

public sealed class SourcingRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public int Version { get; set; } = 1;
    public SourcingRequestStatus Status { get; set; } = SourcingRequestStatus.Draft;
    public Dictionary<SourcingFieldKey, SourcingFieldValue> Fields { get; set; } = [];
    public List<SourcingFieldConflict> Conflicts { get; set; } = [];
    public string Summary { get; set; } = "";
    public int LastSourcingRequirementVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public int Completeness => Fields.Values.Count(value => value.IsStructurallyValid) * 20;

    [JsonIgnore]
    public int CollectedCount => Fields.Values.Count(value => value.IsStructurallyValid);

    [JsonIgnore]
    public IReadOnlyList<SourcingFieldKey> MissingFields =>
        Enum.GetValues<SourcingFieldKey>().Where(field => !Fields.TryGetValue(field, out var value) || !value.IsStructurallyValid).ToList();

    [JsonIgnore]
    public SourcingReadiness Readiness => SourcingReadinessPolicy.Evaluate(this);

    [JsonIgnore]
    public RequirementCompleteness RequirementCompleteness => SourcingReadinessPolicy.ToCompleteness(Readiness);
}

public sealed class HumanHandoffEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string OriginalMessage { get; set; } = "";
    public string Language { get; set; } = "";
    public string ChineseAssistTranslation { get; set; } = "";
    public string HoldingReply { get; set; } = "";
    public string Reason { get; set; } = "";
    public AgentQuestionSafety Safety { get; set; } = AgentQuestionSafety.ImmediateHuman;
    public HandoffStatus Status { get; set; } = HandoffStatus.Open;
    public List<string> RelatedAccountIds { get; set; } = [];
    public int PausedMessageCount { get; set; }
    public string TakenOverBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class PendingQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string Question { get; set; } = "";
    public AgentQuestionSafety Safety { get; set; }
    public string ClassificationReason { get; set; } = "";
    public bool IsResolved { get; set; }
    public string Resolution { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerMergeAudit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceCustomerId { get; set; } = "";
    public string TargetCustomerId { get; set; } = "";
    public string IdentityLinkId { get; set; } = "";
    public string Action { get; set; } = "merge";
    public string Reason { get; set; } = "";
    public string Actor { get; set; } = "";
    public string BeforeJson { get; set; } = "";
    public string AfterJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AgentTurnLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string StateBefore { get; set; } = "";
    public string StateAfter { get; set; } = "";
    public CustomerIdentityMatchResult IdentityResult { get; set; } = CustomerIdentityMatchResult.NoMatch;
    public AgentQuestionSafety Safety { get; set; } = AgentQuestionSafety.SafeToAnswer;
    public string ContextHash { get; set; } = "";
    public string AiModel { get; set; } = "";
    public string Decision { get; set; } = "";
    public string OutputText { get; set; } = "";
    public string KnowledgeRetrievalId { get; set; } = "";
    public List<string> KnowledgeChunkIds { get; set; } = [];
    public string Error { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerIdentityResolution
{
    public CustomerIdentityMatchResult Result { get; set; }
    public CustomerIdentityMatchMethod Method { get; set; }
    public string CustomerId { get; set; } = "";
    public List<string> CandidateCustomerIds { get; set; } = [];
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";

    [JsonIgnore]
    public bool AllowsAutomation =>
        Result is CustomerIdentityMatchResult.ExactMatch
            or CustomerIdentityMatchResult.ConfirmedAliasMatch
            or CustomerIdentityMatchResult.UniqueInferredMatch;
}

public sealed class CustomerSuccessContext
{
    public string CustomerId { get; set; } = "";
    public Lead? Customer { get; set; }
    public GlobalCustomerIdentity? Identity { get; set; }
    public AccountPersona? Persona { get; set; }
    public BusinessRoleProfile WorkspaceProfile { get; set; } = new();
    public AccountRelationshipMemory? AccountRelationship { get; set; }
    public RelationshipMemory? GlobalRelationship { get; set; }
    public CustomerIntelligenceProfile? Brain { get; set; }
    public SourcingRequest? SourcingRequest { get; set; }
    public ConversationAgentState? AgentState { get; set; }
    public GlobalCustomerAgentLock? AgentLock { get; set; }
    public HumanHandoffEvent? OpenHandoff { get; set; }
    public List<WhatsAppIdentityLink> IdentityLinks { get; set; } = [];
    public List<WhatsAppMessage> Messages { get; set; } = [];
    public List<EmailMessage> EmailMessages { get; set; } = [];
    public OpportunitySnapshot? Opportunity { get; set; }
    public List<OpportunityTransactionEvent> OpportunityEvents { get; set; } = [];
    public List<PendingQuestion> PendingQuestions { get; set; } = [];
}

public sealed class CustomerSuccessFieldProposal
{
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public string EvidenceQuote { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class CustomerSuccessSourcingProposal
{
    public SourcingFieldKey Field { get; set; }
    public string Value { get; set; } = "";
    public string EvidenceQuote { get; set; } = "";
    public bool HumanConfirmed { get; set; }
}

public sealed class CustomerSuccessAgentDecision
{
    public string ReplyText { get; set; } = "";
    public string ReplyLanguage { get; set; } = "";
    public AgentQuestionSafety Safety { get; set; } = AgentQuestionSafety.SafeToAnswer;
    public string SafetyReason { get; set; } = "";
    public string ChineseSummary { get; set; } = "";
    public string CustomerIntent { get; set; } = "";
    public List<string> Signals { get; set; } = [];
    public List<CustomerSuccessSourcingProposal> SourcingFields { get; set; } = [];
    public string PendingQuestion { get; set; } = "";
    public string RecommendedNextAction { get; set; } = "";
    public List<CustomerSuccessFieldProposal> CrmProposals { get; set; } = [];
    public List<string> KnowledgeChunkIds { get; set; } = [];
    public List<KnowledgeRetrievalHit> KnowledgeCitations { get; set; } = [];
    public string KnowledgeRetrievalId { get; set; } = "";
    public bool KnowledgeSufficient { get; set; }
    public double Confidence { get; set; }
    public string Model { get; set; } = "";
    public string LatestIncomingMessageId { get; set; } = "";
    public List<string> SourceMessageIds { get; set; } = [];
    public ConversationTopicState TopicState { get; set; } = ConversationTopicState.Unknown;
    public string TopicDecisionReason { get; set; } = "";
    public bool ShouldReply { get; set; } = true;
    public bool IsRiskInformationCollection { get; set; }
    [JsonIgnore] public bool UsedSafeFallback { get; set; }
    [JsonIgnore] public string FallbackReason { get; set; } = "";
    public bool RequiresHuman => Safety == AgentQuestionSafety.ImmediateHuman;
}

public sealed class CustomerSuccessAgentRunResult
{
    public CustomerIdentityResolution Identity { get; set; } = new();
    public CustomerSuccessContext? Context { get; set; }
    public CustomerSuccessAgentDecision? Decision { get; set; }
    public SourcingRequest? SourcingRequest { get; set; }
    public HumanHandoffEvent? Handoff { get; set; }
    public ConversationAgentState? AgentState { get; set; }
    public KnowledgeRetrievalResult? KnowledgeRetrieval { get; set; }
    public CustomerSuccessRunContextToken? ContextToken { get; set; }
    public bool AutoReplyAllowed { get; set; }
    public string BlockReason { get; set; } = "";
}

public static class CustomerSuccessAgentLabels
{
    public static string Mode(ConversationAgentMode value) => value switch
    {
        ConversationAgentMode.AutoOff => "自动关闭",
        ConversationAgentMode.SuggestOnly => "仅建议",
        ConversationAgentMode.CopilotActive => "协作模式",
        ConversationAgentMode.AutoActive => "自动托管",
        ConversationAgentMode.IdentityResolutionRequired => "待确认客户身份",
        ConversationAgentMode.HumanRequired => "需要人工处理",
        ConversationAgentMode.HumanActive => "人工接管中",
        ConversationAgentMode.ResumeReview => "恢复前复核",
        _ => value.ToString()
    };

    public static string ModeHeadline(ConversationAgentMode value) => value switch
    {
        ConversationAgentMode.AutoOff => "关闭：不分析、不生成、不发送",
        ConversationAgentMode.SuggestOnly => "仅建议：由你手动生成并确认",
        ConversationAgentMode.CopilotActive => "协作模式：新消息自动生成草稿",
        ConversationAgentMode.AutoActive => "自动托管：配置完成，需点击“开始托管”才运行",
        _ => Mode(value)
    };

    public static string ModeTrigger(ConversationAgentMode value) => value switch
    {
        ConversationAgentMode.AutoOff => "触发：无",
        ConversationAgentMode.SuggestOnly => "触发：点击下方“立即生成建议”或会话输入区的“AI”",
        ConversationAgentMode.CopilotActive => "触发：客户每次发来新的文字消息",
        ConversationAgentMode.AutoActive => "触发：仅在当前会话已开始托管后监听新消息",
        _ => "触发：按当前人工处理状态执行"
    };

    public static string ModeOutput(ConversationAgentMode value) => value switch
    {
        ConversationAgentMode.AutoOff => "产出：保留历史，不生成新内容",
        ConversationAgentMode.SuggestOnly => "产出：显示在本卡片“最近一次 Agent 产出”，并填入输入框",
        ConversationAgentMode.CopilotActive => "产出：显示在本卡片“最近一次 Agent 产出”，点击后填入输入框",
        ConversationAgentMode.AutoActive => "产出：托管启动后显示判断、引用、发送结果或阻断原因",
        _ => "产出：显示人工接管与恢复状态"
    };

    public static string ModeSend(ConversationAgentMode value) => value switch
    {
        ConversationAgentMode.AutoOff => "发送：绝不自动发送",
        ConversationAgentMode.SuggestOnly => "发送：绝不自动发送，由你检查后点击发送",
        ConversationAgentMode.CopilotActive => "发送：绝不自动发送，由你检查、修改后点击发送",
        ConversationAgentMode.AutoActive => "发送：仅托管中且身份、上下文与安全校验全部通过时自动发送；高风险转人工",
        _ => "发送：AI 暂停，由人工处理"
    };

    public static string ModeStateReason(ConversationAgentMode value) => value switch
    {
        ConversationAgentMode.AutoOff => "Agent 已关闭；新消息只同步，不触发 AI。",
        ConversationAgentMode.SuggestOnly => "仅手动生成建议；AI 不会自动发送。",
        ConversationAgentMode.CopilotActive => "新消息自动生成待审核草稿；AI 不会自动发送。",
        ConversationAgentMode.AutoActive => "自动托管已配置；点击“开始托管”并通过前置检查后才会处理新消息。",
        _ => "当前由人工处理流程接管。"
    };

    public static string RunState(ConversationAgentRunState value) => value switch
    {
        ConversationAgentRunState.Off => "未运行",
        ConversationAgentRunState.SuggestReady => "可生成建议",
        ConversationAgentRunState.CollabActive => "协作中",
        ConversationAgentRunState.AutoPreflight => "托管检查中",
        ConversationAgentRunState.AutoArmed => "托管已就绪",
        ConversationAgentRunState.AutoProcessing => "托管处理中",
        ConversationAgentRunState.AutoSending => "托管发送中",
        ConversationAgentRunState.WaitingCustomer => "等待客户",
        ConversationAgentRunState.TopicResolved => "话题已结束",
        ConversationAgentRunState.RiskInfoCollectionSent => "风险信息已收集",
        ConversationAgentRunState.WaitingHuman => "等待人工",
        ConversationAgentRunState.PausedRisk => "风险暂停",
        ConversationAgentRunState.PausedError => "异常暂停",
        ConversationAgentRunState.HumanTakeover => "人工接管",
        ConversationAgentRunState.Ended => "已结束",
        _ => value.ToString()
    };

    public static string PrimaryAction(ConversationAgentState state) => state.RunState switch
    {
        ConversationAgentRunState.CollabActive => "停止协作",
        ConversationAgentRunState.AutoPreflight => "检查中",
        ConversationAgentRunState.AutoArmed or ConversationAgentRunState.AutoProcessing or ConversationAgentRunState.AutoSending or ConversationAgentRunState.WaitingCustomer => "托管中",
        ConversationAgentRunState.PausedRisk or ConversationAgentRunState.WaitingHuman or ConversationAgentRunState.RiskInfoCollectionSent => "已暂停",
        ConversationAgentRunState.HumanTakeover => "重新托管",
        ConversationAgentRunState.PausedError => "托管异常",
        ConversationAgentRunState.TopicResolved => "已结束",
        ConversationAgentRunState.Ended => "重新托管",
        _ => state.Mode switch
        {
            ConversationAgentMode.AutoOff => "启用 AI",
            ConversationAgentMode.SuggestOnly => "生成建议",
            ConversationAgentMode.CopilotActive => "开始协作",
            ConversationAgentMode.AutoActive => "开始托管",
            _ => "启用 AI"
        }
    };

    public static string RunStatus(CustomerSuccessRunStatus value) => value switch
    {
        CustomerSuccessRunStatus.None => "尚无产出",
        CustomerSuccessRunStatus.SuggestionReady => "手动建议已生成 · 待你确认",
        CustomerSuccessRunStatus.CopilotDraftReady => "协作草稿已生成 · 待你确认",
        CustomerSuccessRunStatus.AutoReplyPending => "自动回复已提交 · 等待服务端确认",
        CustomerSuccessRunStatus.AutoReplySent => "自动回复已由 WhatsApp 服务端确认",
        CustomerSuccessRunStatus.HumanRequired => "已阻断自动回复 · 转人工处理",
        CustomerSuccessRunStatus.Blocked => "本轮未生成或未发送",
        CustomerSuccessRunStatus.Failed => "本轮处理失败",
        _ => value.ToString()
    };

    public static string Match(CustomerIdentityMatchResult value) => value switch
    {
        CustomerIdentityMatchResult.ExactMatch => "精确匹配",
        CustomerIdentityMatchResult.ConfirmedAliasMatch => "确认别名匹配",
        CustomerIdentityMatchResult.UniqueInferredMatch => "唯一推断匹配",
        CustomerIdentityMatchResult.AmbiguousMatch => "匹配有歧义",
        CustomerIdentityMatchResult.NoMatch => "未匹配",
        CustomerIdentityMatchResult.Conflict => "身份冲突",
        _ => value.ToString()
    };
}
