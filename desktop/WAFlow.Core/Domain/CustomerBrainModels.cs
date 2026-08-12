using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntelligenceStatementNature
{
    Fact,
    Inference,
    Recommendation,
    InformationGap
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiRecommendationStatus
{
    Proposed,
    Accepted,
    Dismissed,
    InProgress,
    Completed,
    Failed,
    Superseded
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SalesActionStatus
{
    Planned,
    Approved,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LearningObservationStatus
{
    Pending,
    Observed,
    Expired
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerBrainRunStatus
{
    Queued,
    Collecting,
    Understanding,
    EvaluatingOpportunity,
    Recommending,
    Succeeded,
    RetryableFailed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerBrainDecisionStatus
{
    NotAnalyzed,
    Current,
    Stale,
    RetryableFailed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerContextStatus
{
    NotGenerated,
    Current,
    Stale,
    Generating,
    NotConfigured,
    RetryableFailed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FollowUpTaskStatus
{
    Proposed,
    Open,
    InProgress,
    Completed,
    Dismissed,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FollowUpPriority
{
    Low,
    Normal,
    High,
    Urgent
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CustomerCommitmentStatus
{
    Active,
    Completed
}

public sealed class CustomerIntelligenceStatement
{
    public IntelligenceStatementNature Nature { get; set; }
    public string Topic { get; set; } = "";
    public string Text { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceId { get; set; } = "";
    public double Confidence { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerIntelligenceCoverage
{
    public bool HasCrmData { get; set; }
    public bool HasWhatsAppHistory { get; set; }
    public bool HasEmailHistory { get; set; }
    public bool HasLeadAnalysis { get; set; }
    public bool HasCustomerReport { get; set; }
    public bool HasCampaignHistory { get; set; }

    [JsonIgnore]
    public int Percentage
    {
        get
        {
            var values = new[] { HasCrmData, HasWhatsAppHistory, HasEmailHistory, HasLeadAnalysis, HasCustomerReport, HasCampaignHistory };
            return (int)Math.Round(values.Count(value => value) / (double)values.Length * 100);
        }
    }
}

public sealed class CustomerConversationContext
{
    public CustomerContextStatus Status { get; set; } = CustomerContextStatus.NotGenerated;
    public string Overview { get; set; } = "";
    public List<string> AttitudesAndInterests { get; set; } = [];
    public List<string> PersonalityTraits { get; set; } = [];
    public List<string> CommunicationStyle { get; set; } = [];
    public List<string> ConcernsAndObjections { get; set; } = [];
    public List<string> PurchaseSignals { get; set; } = [];
    public string RelationshipState { get; set; } = "";
    public string RecommendedApproach { get; set; } = "";
    public List<CustomerIntelligenceStatement> Inferences { get; set; } = [];
    public int WhatsAppMessageCount { get; set; }
    public int EmailMessageCount { get; set; }
    public string SourceSnapshotHash { get; set; } = "";
    public string ManualNotesHash { get; set; } = "";
    public DateTimeOffset? LastWhatsAppAt { get; set; }
    public DateTimeOffset? LastEmailAt { get; set; }
    public string AiModel { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonIgnore]
    public bool HasContent => !string.IsNullOrWhiteSpace(Overview)
        || AttitudesAndInterests.Count > 0
        || PersonalityTraits.Count > 0
        || CommunicationStyle.Count > 0
        || ConcernsAndObjections.Count > 0
        || PurchaseSignals.Count > 0
        || !string.IsNullOrWhiteSpace(RelationshipState)
        || !string.IsNullOrWhiteSpace(RecommendedApproach);
}

public sealed class CustomerConversationContextResult
{
    public string Overview { get; set; } = "";
    public List<string> AttitudesAndInterests { get; set; } = [];
    public List<string> PersonalityTraits { get; set; } = [];
    public List<string> CommunicationStyle { get; set; } = [];
    public List<string> ConcernsAndObjections { get; set; } = [];
    public List<string> PurchaseSignals { get; set; } = [];
    public string RelationshipState { get; set; } = "";
    public string RecommendedApproach { get; set; } = "";
    public List<CustomerIntelligenceStatement> Inferences { get; set; } = [];
    public List<CustomerCommitmentCandidate> Commitments { get; set; } = [];
}

public sealed class CustomerCommitmentCandidate
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string SourceChannel { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string Evidence { get; set; } = "";
    public DateTimeOffset? DueAt { get; set; }
    public double Confidence { get; set; }

    [JsonIgnore]
    public DateTimeOffset SourceOccurredAt { get; set; }
}

public sealed class CustomerIntelligenceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public int Version { get; set; } = 1;
    public string CustomerName { get; set; } = "";
    public string Summary { get; set; } = "";
    public string CustomerType { get; set; } = "";
    public List<string> BusinessModels { get; set; } = [];
    public List<string> PurchaseMotivations { get; set; } = [];
    public List<string> PainPoints { get; set; } = [];
    public List<string> OpportunitySignals { get; set; } = [];
    public List<string> Risks { get; set; } = [];
    public string NextBestAction { get; set; } = "";
    public string SuggestedTalkTrack { get; set; } = "";
    public double Confidence { get; set; }
    public int PurchaseProbability { get; set; }
    public LeadStage SuggestedStage { get; set; } = LeadStage.New;
    public CustomerBrainDecisionStatus DecisionStatus { get; set; } = CustomerBrainDecisionStatus.NotAnalyzed;
    public string DecisionSourceSnapshotHash { get; set; } = "";
    public string LastBrainRunId { get; set; } = "";
    public DateTimeOffset? LastBrainAnalyzedAt { get; set; }
    public string AiModel { get; set; } = "";
    public CustomerIntelligenceCoverage Coverage { get; set; } = new();
    public List<CustomerIntelligenceStatement> Statements { get; set; } = [];
    public CustomerConversationContext ConversationContext { get; set; } = new();
    public ProductRequirementState ProductRequirementState { get; set; } = new();
    public string KnowledgeRetrievalId { get; set; } = "";
    public List<KnowledgeRetrievalHit> KnowledgeReferences { get; set; } = [];
    public string SourceSnapshotHash { get; set; } = "";
    public DateTimeOffset SourceCapturedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string VersionLabel => $"Brain V{Version}";
    [JsonIgnore] public string CoverageLabel => $"数据覆盖 {Coverage.Percentage}%";
    [JsonIgnore] public bool HasCurrentDecision =>
        DecisionStatus == CustomerBrainDecisionStatus.Current
        && !string.IsNullOrWhiteSpace(DecisionSourceSnapshotHash)
        && string.Equals(DecisionSourceSnapshotHash, SourceSnapshotHash, StringComparison.Ordinal);
}

public sealed class CustomerUnderstandingResult
{
    public string CustomerDna { get; set; } = "";
    public string ProfileSummary { get; set; } = "";
    public string CustomerType { get; set; } = "";
    public List<string> BusinessModels { get; set; } = [];
    public List<string> PainPoints { get; set; } = [];
    public List<string> PurchaseMotivations { get; set; } = [];
    public List<string> InformationGaps { get; set; } = [];
    public List<CustomerIntelligenceStatement> Statements { get; set; } = [];
}

public sealed class CustomerOpportunityEvaluation
{
    public int PurchaseProbability { get; set; }
    public double Confidence { get; set; }
    public LeadStage SuggestedStage { get; set; } = LeadStage.New;
    public List<string> PositiveSignals { get; set; } = [];
    public List<string> RiskSignals { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public string Rationale { get; set; } = "";
}

public sealed class CustomerSalesRecommendation
{
    public string NextBestAction { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string SuggestedTalkTrack { get; set; } = "";
    public List<string> QuestionsToVerify { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public int DueInHours { get; set; } = 24;
    public FollowUpPriority Priority { get; set; } = FollowUpPriority.Normal;
}

public sealed class CustomerBrainRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public CustomerBrainRunStatus Status { get; set; } = CustomerBrainRunStatus.Queued;
    public string AiModel { get; set; } = "";
    public string SourceSnapshotHash { get; set; } = "";
    public string SourceSnapshotJson { get; set; } = "";
    public string UnderstandingJson { get; set; } = "";
    public string OpportunityJson { get; set; } = "";
    public string RecommendationJson { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AiRecommendationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string RecommendationType { get; set; } = "next_best_action";
    public string Title { get; set; } = "";
    public string Action { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string SuggestedTalkTrack { get; set; } = "";
    public List<string> Evidence { get; set; } = [];
    public double Confidence { get; set; }
    public AiRecommendationStatus Status { get; set; } = AiRecommendationStatus.Proposed;
    public string SourceProfileId { get; set; } = "";
    public int SourceProfileVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerBehaviorEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SourceType { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class SalesActionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string Description { get; set; } = "";
    public string Owner { get; set; } = "";
    public SalesActionStatus Status { get; set; } = SalesActionStatus.Planned;
    public DateTimeOffset? DueAt { get; set; }
    public LeadStage? BaselineStage { get; set; }
    public DateTimeOffset? BaselineCapturedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string ExecutionChannel { get; set; } = "";
    public string ExecutedContent { get; set; } = "";
    public string ExecutedSourceId { get; set; } = "";
    public DateTimeOffset? CompletedAt { get; set; }
    public string Outcome { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AiLearningFeedback
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public bool Helpful { get; set; }
    public string FeedbackSource { get; set; } = "human";
    public string Note { get; set; } = "";
    public LearningObservationStatus ObservationStatus { get; set; } = LearningObservationStatus.Pending;
    public string Channel { get; set; } = "";
    public DateTimeOffset? ActionAt { get; set; }
    public LeadStage? BaselineStage { get; set; }
    public LeadStage? ObservedStage { get; set; }
    public bool Replied { get; set; }
    public DateTimeOffset? FirstReplyAt { get; set; }
    public double? ReplyLatencyMinutes { get; set; }
    public bool StageProgressed { get; set; }
    public int StageDelta { get; set; }
    public bool Converted { get; set; }
    public bool RepeatPurchase { get; set; }
    public string TalkTrack { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public DateTimeOffset? ObservationWindowEndsAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class TalkTrackPerformance
{
    public string Key { get; set; } = "";
    public string Channel { get; set; } = "";
    public string TalkTrack { get; set; } = "";
    public int SentCount { get; set; }
    public int Replies { get; set; }
    public int StageProgressions { get; set; }
    public int Deals { get; set; }
    public double? AverageReplyMinutes { get; set; }

    [JsonIgnore] public double ResponseRate => SentCount == 0 ? 0 : Math.Round(100d * Replies / SentCount, 1);
    [JsonIgnore] public double ProgressionRate => SentCount == 0 ? 0 : Math.Round(100d * StageProgressions / SentCount, 1);
    [JsonIgnore] public double DealRate => SentCount == 0 ? 0 : Math.Round(100d * Deals / SentCount, 1);
    [JsonIgnore] public bool HasReliableSample => SentCount >= 3;
}

public sealed class FollowUpTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public FollowUpPriority Priority { get; set; } = FollowUpPriority.Normal;
    public FollowUpTaskStatus Status { get; set; } = FollowUpTaskStatus.Proposed;
    public DateTimeOffset DueAt { get; set; } = DateTimeOffset.Now.AddDays(1);
    public string SourceType { get; set; } = "customer_brain";
    public string SourceId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class CustomerCommitment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public CustomerCommitmentStatus Status { get; set; } = CustomerCommitmentStatus.Active;
    public DateTimeOffset? DueAt { get; set; }
    public string SourceChannel { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string Evidence { get; set; } = "";
    public double Confidence { get; set; }
    public DateTimeOffset SourceOccurredAt { get; set; }
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public string CompletionNote { get; set; } = "";

    [JsonIgnore]
    public bool IsOverdue => Status == CustomerCommitmentStatus.Active
        && DueAt is { } dueAt
        && dueAt < DateTimeOffset.Now;

    [JsonIgnore]
    public string DueLabel => DueAt is null
        ? "未设期限"
        : IsOverdue
            ? $"已逾期 · {DueAt.Value.LocalDateTime:MM-dd HH:mm}"
            : $"计划完成 · {DueAt.Value.LocalDateTime:MM-dd HH:mm}";
}

public sealed class CustomerCommitmentSummary
{
    public string CustomerId { get; set; } = "";
    public int ActiveCount { get; set; }
    public int OverdueCount { get; set; }
    public DateTimeOffset? NextDueAt { get; set; }
    public string FirstTitle { get; set; } = "";

    [JsonIgnore]
    public string MarkerLabel => ActiveCount == 0
        ? ""
        : OverdueCount > 0
            ? $"逾期 {OverdueCount} · 共 {ActiveCount}"
            : NextDueAt is null
                ? $"待履约 {ActiveCount}"
                : $"待履约 {ActiveCount} · {NextDueAt.Value.LocalDateTime:MM-dd}";

    [JsonIgnore]
    public string State => OverdueCount > 0 ? "overdue" : ActiveCount > 0 ? "active" : "none";
}

public sealed class CustomerEventLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceId { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class TodayBriefItem
{
    public string CustomerId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public FollowUpPriority Priority { get; set; } = FollowUpPriority.Normal;
    public FollowUpTaskStatus Status { get; set; } = FollowUpTaskStatus.Proposed;
    public DateTimeOffset DueAt { get; set; } = DateTimeOffset.Now;
    public int PurchaseProbability { get; set; }
    public double Confidence { get; set; }
    public LeadStage SuggestedStage { get; set; } = LeadStage.New;
    public string Category { get; set; } = "follow_up";
    public string SourceAccountId { get; set; } = "";
    public string SourceConversationId { get; set; } = "";

    [JsonIgnore] public string CustomerLabel => $"客户：{(string.IsNullOrWhiteSpace(CustomerName) ? "未命名客户" : CustomerName)}";
    [JsonIgnore] public string ActionLabel => $"下一步：{(string.IsNullOrWhiteSpace(Action) ? "打开客户资料并确认下一步" : Action)}";
    [JsonIgnore] public string ReasonLabel => $"处理依据：{(string.IsNullOrWhiteSpace(Reason) ? "暂无补充说明" : Reason)}";

    [JsonIgnore] public string PriorityLabel => Priority switch
    {
        FollowUpPriority.Urgent => "紧急",
        FollowUpPriority.High => "高优先",
        FollowUpPriority.Normal => "普通",
        _ => "低优先"
    };

    [JsonIgnore] public string CategoryLabel => Category switch
    {
        "handoff" => "人工接管",
        "sourcing_ready" => "搜品已就绪",
        "sourcing_complete" => "客户需求完整",
        "cross_account" => "跨账号跟进",
        "knowledge_review" => "知识待审核",
        "knowledge_conflict" => "知识冲突",
        "knowledge_candidate" => "候选待审批",
        _ => PriorityLabel
    };

    [JsonIgnore] public string DueLabel
    {
        get
        {
            var now = DateTimeOffset.Now;
            var overdue = now - DueAt;
            if (overdue >= TimeSpan.FromMinutes(1))
            {
                if (overdue < TimeSpan.FromHours(1)) return $"已逾期 {Math.Max(1, (int)Math.Ceiling(overdue.TotalMinutes))} 分钟";
                if (overdue < TimeSpan.FromDays(1)) return $"已逾期 {Math.Max(1, (int)Math.Ceiling(overdue.TotalHours))} 小时";
                return $"已逾期 {Math.Max(1, (int)Math.Ceiling(overdue.TotalDays))} 天";
            }
            if (DueAt <= now.AddMinutes(5)) return "现在处理";
            if (DueAt.Date == now.Date) return $"今天 {DueAt:HH:mm}";
            if (DueAt.Date == now.Date.AddDays(1)) return $"明天 {DueAt:HH:mm}";
            return DueAt.ToString("MM-dd HH:mm");
        }
    }
}

public sealed class PersonalLearningSummary
{
    public int Proposed { get; set; }
    public int Accepted { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Dismissed { get; set; }
    public int FeedbackCount { get; set; }
    public int HelpfulFeedback { get; set; }
    public int Executed { get; set; }
    public int AwaitingOutcome { get; set; }
    public int ObservedActions { get; set; }
    public int Replies { get; set; }
    public int StageProgressions { get; set; }
    public int Deals { get; set; }
    public int RepeatPurchases { get; set; }
    public double? AverageReplyMinutes { get; set; }
    public List<TalkTrackPerformance> TopTalkTracks { get; set; } = [];
    public string StrategyReview { get; set; } = "";

    [JsonIgnore] public double CompletionRate => Accepted == 0 ? 0 : Math.Round(100d * Completed / Accepted, 1);
    [JsonIgnore] public double HelpfulRate => FeedbackCount == 0 ? 0 : Math.Round(100d * HelpfulFeedback / FeedbackCount, 1);
    [JsonIgnore] public double ResponseRate => Executed == 0 ? 0 : Math.Round(100d * Replies / Executed, 1);
    [JsonIgnore] public double ProgressionRate => Executed == 0 ? 0 : Math.Round(100d * StageProgressions / Executed, 1);
    [JsonIgnore] public double DealRate => Executed == 0 ? 0 : Math.Round(100d * Deals / Executed, 1);
}

public sealed class TodayBriefSnapshot
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;
    public int OverdueCount { get; set; }
    public int DueTodayCount { get; set; }
    public int InProgressCount { get; set; }
    public int HumanHandoffCount { get; set; }
    public int SourcingCompleteCount { get; set; }
    [JsonIgnore] public int SourcingReadyCount => SourcingCompleteCount;
    public int CrossAccountFollowUpCount { get; set; }
    public int KnowledgeReviewCount { get; set; }
    public int KnowledgeConflictCount { get; set; }
    public int KnowledgeCandidateCount { get; set; }
    public List<TodayBriefItem> Items { get; set; } = [];
    public PersonalLearningSummary Learning { get; set; } = new();
}
