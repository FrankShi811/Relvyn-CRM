using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeDocumentStatus
{
    Uploading,
    Processing,
    ReadyForReview,
    Active,
    Disabled,
    Failed,
    Outdated,
    Conflicted,
    Deleted
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeScopeKind
{
    Global,
    Account,
    Customer,
    Conversation,
    Temporary
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeCategory
{
    DhgatePolicy,
    CustomerSuccessSop,
    SourcingRequirement,
    ProductKnowledge,
    ShippingKnowledge,
    SalesScript,
    ObjectionHandling,
    CustomerCase,
    Faq,
    TrainingMaterial,
    AnalysisTemplate,
    ReportTemplate,
    CustomerSpecific,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeSourceKind
{
    ApprovedDocument,
    VerifiedInteractionMemory,
    OutcomeValidatedPractice,
    AiDraft
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeUsageMode
{
    StyleReference,
    ExactTemplate,
    PolicyReference,
    AnalysisReference,
    Excluded
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeRiskLevel
{
    None,
    Low,
    Medium,
    High,
    Blocked
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeConflictStatus
{
    Open,
    Resolved,
    Ignored
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeCandidateStatus
{
    Proposed,
    Approved,
    Rejected,
    Published
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeEvidenceLevel
{
    ApprovedStatic,
    VerifiedInteraction,
    PreliminaryObservation,
    OutcomeValidated
}

public sealed class KnowledgeScope
{
    public KnowledgeScopeKind Kind { get; set; } = KnowledgeScopeKind.Global;
    public string AccountId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string TemporaryTaskId { get; set; } = "";

    [JsonIgnore]
    public string Label => Kind switch
    {
        KnowledgeScopeKind.Global => "全局",
        KnowledgeScopeKind.Account => $"账号 · {Fallback(AccountId)}",
        KnowledgeScopeKind.Customer => $"客户 · {Fallback(CustomerId)}",
        KnowledgeScopeKind.Conversation => $"会话 · {Fallback(ConversationId)}",
        KnowledgeScopeKind.Temporary => $"临时任务 · {Fallback(TemporaryTaskId)}",
        _ => Kind.ToString()
    };

    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "未指定" : value;
}

public sealed class KnowledgeDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string MimeType { get; set; } = "";
    public KnowledgeDocumentStatus Status { get; set; } = KnowledgeDocumentStatus.Uploading;
    public KnowledgeCategory Category { get; set; } = KnowledgeCategory.Other;
    public KnowledgeSourceKind SourceKind { get; set; } = KnowledgeSourceKind.ApprovedDocument;
    public KnowledgeUsageMode UsageMode { get; set; } = KnowledgeUsageMode.StyleReference;
    public KnowledgeEvidenceLevel EvidenceLevel { get; set; } = KnowledgeEvidenceLevel.ApprovedStatic;
    public KnowledgeScope Scope { get; set; } = new();
    public string Summary { get; set; } = "";
    public string DetectedLanguage { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<string> RiskFlags { get; set; } = [];
    public KnowledgeRiskLevel RiskLevel { get; set; }
    public int CurrentVersion { get; set; }
    public int ChunkCount { get; set; }
    public string CurrentVersionId { get; set; } = "";
    public string ProcessingError { get; set; } = "";
    public bool UserApproved { get; set; }
    public bool IsExactTemplate { get; set; }
    public DateTimeOffset? EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveUntil { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string StatusLabel => KnowledgeLabels.Status(Status);
    [JsonIgnore] public string CategoryLabel => KnowledgeLabels.Category(Category);
    [JsonIgnore] public string ScopeLabel => Scope.Label;
    [JsonIgnore] public string VersionLabel => CurrentVersion <= 0 ? "尚无版本" : $"V{CurrentVersion}";
    [JsonIgnore] public bool CanActivate =>
        Status is KnowledgeDocumentStatus.ReadyForReview or KnowledgeDocumentStatus.Disabled or KnowledgeDocumentStatus.Outdated
        && RiskLevel is not KnowledgeRiskLevel.High and not KnowledgeRiskLevel.Blocked
        && SourceKind != KnowledgeSourceKind.AiDraft
        && ChunkCount > 0;
}

public sealed class KnowledgeDocumentVersion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DocumentId { get; set; } = "";
    public int Version { get; set; } = 1;
    public string OriginalFileName { get; set; } = "";
    public string StoredFilePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long FileSize { get; set; }
    public string ParserName { get; set; } = "";
    public string ParserVersion { get; set; } = "1";
    public string ExtractedText { get; set; } = "";
    public string ExtractionSummary { get; set; } = "";
    public List<string> ChapterTitles { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public int ChunkCount { get; set; }
    public KnowledgeDocumentStatus Status { get; set; } = KnowledgeDocumentStatus.Processing;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class KnowledgeChunk
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DocumentId { get; set; } = "";
    public string VersionId { get; set; } = "";
    public int DocumentVersion { get; set; }
    public int Ordinal { get; set; }
    public string Content { get; set; } = "";
    public string NormalizedText { get; set; } = "";
    public string Heading { get; set; } = "";
    public string Locator { get; set; } = "";
    public int? PageNumber { get; set; }
    public string TableName { get; set; } = "";
    public int? RowNumber { get; set; }
    public List<string> Keywords { get; set; } = [];
    public List<double> Embedding { get; set; } = [];
    public string EmbeddingProvider { get; set; } = "";
    public string EmbeddingVersion { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Language { get; set; } = "";
    public KnowledgeCategory Category { get; set; } = KnowledgeCategory.Other;
    public KnowledgeSourceKind SourceKind { get; set; } = KnowledgeSourceKind.ApprovedDocument;
    public KnowledgeUsageMode UsageMode { get; set; } = KnowledgeUsageMode.StyleReference;
    public KnowledgeEvidenceLevel EvidenceLevel { get; set; } = KnowledgeEvidenceLevel.ApprovedStatic;
    public KnowledgeScope Scope { get; set; } = new();
    public KnowledgeRiskLevel RiskLevel { get; set; }
    public bool HasOpenConflict { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class KnowledgeTag
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class KnowledgeDocumentTag
{
    public string DocumentId { get; set; } = "";
    public string TagId { get; set; } = "";
}

public sealed class KnowledgeConflict
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DocumentId { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public string ConflictingDocumentId { get; set; } = "";
    public string ConflictingVersionId { get; set; } = "";
    public string ConflictingChunkId { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Detail { get; set; } = "";
    public KnowledgeConflictStatus Status { get; set; } = KnowledgeConflictStatus.Open;
    public string Resolution { get; set; } = "";
    public string ResolvedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class KnowledgeRetrievalRequest
{
    public string Query { get; set; } = "";
    public string TenantId { get; set; } = "local";
    public string UserId { get; set; } = "local";
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string TemporaryTaskId { get; set; } = "";
    public string CustomerIntent { get; set; } = "";
    public string CustomerStage { get; set; } = "";
    public string Language { get; set; } = "";
    public string SourcingMissingFields { get; set; } = "";
    public string UsageContext { get; set; } = "";
    public int Limit { get; set; } = 8;
    public double MinimumScore { get; set; } = 0.18;
    public List<string> ExcludedDocumentIds { get; set; } = [];
    public List<string> ExcludedChunkIds { get; set; } = [];
}

public sealed class KnowledgeRetrievalHit
{
    public string ChunkId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string VersionId { get; set; } = "";
    public int DocumentVersion { get; set; }
    public string DocumentTitle { get; set; } = "";
    public string Content { get; set; } = "";
    public string Heading { get; set; } = "";
    public string Locator { get; set; } = "";
    public KnowledgeCategory Category { get; set; }
    public KnowledgeSourceKind SourceKind { get; set; }
    public KnowledgeUsageMode UsageMode { get; set; }
    public KnowledgeEvidenceLevel EvidenceLevel { get; set; }
    public KnowledgeScope Scope { get; set; } = new();
    public double KeywordScore { get; set; }
    public double VectorScore { get; set; }
    public double ScopeScore { get; set; }
    public double FreshnessScore { get; set; }
    public double RelevanceScore { get; set; }
    public bool HasOpenConflict { get; set; }
    public bool IsOutdated { get; set; }
    public List<string> MatchedTerms { get; set; } = [];

    [JsonIgnore] public string CitationLabel =>
        $"{DocumentTitle} · V{DocumentVersion}{(string.IsNullOrWhiteSpace(Locator) ? "" : $" · {Locator}")}";
}

public sealed class KnowledgeRetrievalResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public KnowledgeRetrievalRequest Request { get; set; } = new();
    public List<KnowledgeRetrievalHit> Hits { get; set; } = [];
    public List<string> ConflictWarnings { get; set; } = [];
    public List<string> RiskWarnings { get; set; } = [];
    public bool SufficientToAnswer { get; set; }
    public string InsufficiencyReason { get; set; } = "";
    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class KnowledgeRetrievalLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Query { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string UsageContext { get; set; } = "";
    public bool SufficientToAnswer { get; set; }
    public List<string> RetrievedChunkIds { get; set; } = [];
    public List<string> UsedChunkIds { get; set; } = [];
    public List<string> ConflictWarnings { get; set; } = [];
    public string ResultJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class KnowledgeUsageOutcome
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RetrievalLogId { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public bool ActuallySent { get; set; }
    public bool CustomerReplied { get; set; }
    public bool StageProgressed { get; set; }
    public bool Converted { get; set; }
    public bool RepeatPurchase { get; set; }
    public string ObservationNote { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class KnowledgeFeedback
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RetrievalLogId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public bool Helpful { get; set; }
    public bool ExcludedForCurrentConversation { get; set; }
    public string Note { get; set; } = "";
    public string Actor { get; set; } = "user";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class KnowledgeCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public KnowledgeCategory Category { get; set; } = KnowledgeCategory.SalesScript;
    public KnowledgeSourceKind SourceKind { get; set; } = KnowledgeSourceKind.VerifiedInteractionMemory;
    public KnowledgeEvidenceLevel EvidenceLevel { get; set; } = KnowledgeEvidenceLevel.PreliminaryObservation;
    public KnowledgeScope Scope { get; set; } = new();
    public KnowledgeCandidateStatus Status { get; set; } = KnowledgeCandidateStatus.Proposed;
    public int SampleSize { get; set; }
    public int Replies { get; set; }
    public int StageProgressions { get; set; }
    public int Conversions { get; set; }
    public int RepeatPurchases { get; set; }
    public List<string> SourceIds { get; set; } = [];
    public string ReviewNote { get; set; } = "";
    public string ReviewedBy { get; set; } = "";
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string EvidenceLabel => EvidenceLevel switch
    {
        KnowledgeEvidenceLevel.OutcomeValidated => "结果已验证",
        KnowledgeEvidenceLevel.VerifiedInteraction => "真实互动",
        KnowledgeEvidenceLevel.PreliminaryObservation => "初步观察",
        _ => "人工批准资料"
    };
}

public static class KnowledgeLabels
{
    public static string Status(KnowledgeDocumentStatus value) => value switch
    {
        KnowledgeDocumentStatus.Uploading => "上传中",
        KnowledgeDocumentStatus.Processing => "处理中",
        KnowledgeDocumentStatus.ReadyForReview => "待审核",
        KnowledgeDocumentStatus.Active => "已启用",
        KnowledgeDocumentStatus.Disabled => "已停用",
        KnowledgeDocumentStatus.Failed => "处理失败",
        KnowledgeDocumentStatus.Outdated => "已过期",
        KnowledgeDocumentStatus.Conflicted => "存在冲突",
        KnowledgeDocumentStatus.Deleted => "已删除",
        _ => value.ToString()
    };

    public static string Category(KnowledgeCategory value) => value switch
    {
        KnowledgeCategory.DhgatePolicy => "平台政策",
        KnowledgeCategory.CustomerSuccessSop => "客户成功 SOP",
        KnowledgeCategory.SourcingRequirement => "客户需求采集规范",
        KnowledgeCategory.ProductKnowledge => "产品知识",
        KnowledgeCategory.ShippingKnowledge => "物流知识",
        KnowledgeCategory.SalesScript => "销售话术",
        KnowledgeCategory.ObjectionHandling => "异议处理",
        KnowledgeCategory.CustomerCase => "客户案例",
        KnowledgeCategory.Faq => "常见问题",
        KnowledgeCategory.TrainingMaterial => "培训材料",
        KnowledgeCategory.AnalysisTemplate => "分析模板",
        KnowledgeCategory.ReportTemplate => "报告模板",
        KnowledgeCategory.CustomerSpecific => "客户专属",
        _ => "其他"
    };
}
