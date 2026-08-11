using System.Text.Json.Serialization;

namespace WAFlow.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpTransportKind
{
    Stdio,
    StreamableHttp,
    Sse
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpAuthType
{
    None,
    Bearer,
    ApiKey,
    OAuth
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Degraded,
    Error,
    Disabled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpToolPermissionLevel
{
    ReadOnly,
    WriteLocal,
    ExternalAction,
    HighRisk
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpApprovalPolicy
{
    AlwaysAllow,
    AskEveryTime,
    Deny
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpContextPermission
{
    Allow,
    Deny,
    Ask
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpTaskStatus
{
    Pending,
    AwaitingApproval,
    Queued,
    Running,
    Waiting,
    NeedsInformation,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
    Interrupted
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourcingReadinessLevel
{
    Insufficient,
    AgentAvailable,
    HighConfidence
}

public static class McpContextKeys
{
    public const string CustomerBasicInfo = "customer_basic_info";
    public const string ProductRequirement = "product_requirement";
    public const string CurrentConversation = "current_conversation";
    public const string FullConversationHistory = "full_conversation_history";
    public const string Attachments = "attachments";
    public const string KnowledgeBase = "knowledge_base";
    public const string Opportunity = "opportunity";
    public const string InternalNotes = "internal_notes";

    public static readonly IReadOnlyList<string> All =
    [
        CustomerBasicInfo,
        ProductRequirement,
        CurrentConversation,
        FullConversationHistory,
        Attachments,
        KnowledgeBase,
        Opportunity,
        InternalNotes
    ];
}

public sealed class McpRetryPolicy
{
    public int MaxRetries { get; set; } = 2;
    public int BackoffMs { get; set; } = 800;

    [JsonIgnore] public int NormalizedRetries => Math.Clamp(MaxRetries, 0, 5);
    [JsonIgnore] public int NormalizedBackoffMs => Math.Clamp(BackoffMs, 200, 30_000);
}

public sealed class McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool AutoConnect { get; set; }
    public McpTransportKind Transport { get; set; } = McpTransportKind.StreamableHttp;
    public string Endpoint { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public string WorkingDirectory { get; set; } = "";
    public McpAuthType AuthType { get; set; }
    public string ApiKeyHeader { get; set; } = "X-API-Key";
    public string SecretEnvironmentVariable { get; set; } = "MCP_API_KEY";
    public string SecretRef { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public int TimeoutMs { get; set; } = 120_000;
    public McpRetryPolicy RetryPolicy { get; set; } = new();
    public Dictionary<string, McpContextPermission> ContextPermissions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [McpContextKeys.CustomerBasicInfo] = McpContextPermission.Ask,
            [McpContextKeys.ProductRequirement] = McpContextPermission.Allow,
            [McpContextKeys.CurrentConversation] = McpContextPermission.Ask,
            [McpContextKeys.FullConversationHistory] = McpContextPermission.Deny,
            [McpContextKeys.Attachments] = McpContextPermission.Ask,
            [McpContextKeys.KnowledgeBase] = McpContextPermission.Deny,
            [McpContextKeys.Opportunity] = McpContextPermission.Ask,
            [McpContextKeys.InternalNotes] = McpContextPermission.Deny
        };
    public McpConnectionState ConnectionState { get; set; } = McpConnectionState.Disconnected;
    public string LastErrorCode { get; set; } = "";
    public string LastErrorMessage { get; set; } = "";
    public int LastLatencyMs { get; set; }
    public int ToolCount { get; set; }
    public string ProtocolVersion { get; set; } = "";
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public int NormalizedTimeoutMs => Math.Clamp(TimeoutMs, 1_000, 30 * 60 * 1_000);
    [JsonIgnore] public string SecretTarget => string.IsNullOrWhiteSpace(SecretRef) ? $"WAFlow/MCP/{Id}" : SecretRef;
}

public sealed class McpServerCapabilities
{
    public string ServerId { get; set; } = "";
    public string ProtocolVersion { get; set; } = "";
    public bool SupportsTools { get; set; }
    public bool SupportsResources { get; set; }
    public bool SupportsPrompts { get; set; }
    public List<RegisteredMcpTool> Tools { get; set; } = [];
    public List<McpNamedCapability> Resources { get; set; } = [];
    public List<McpNamedCapability> Prompts { get; set; } = [];
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.Now.AddMinutes(15);
}

public sealed class McpNamedCapability
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Uri { get; set; } = "";
}

public sealed class RegisteredMcpTool
{
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string InputSchemaJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public McpToolPermissionLevel PermissionLevel { get; set; } = McpToolPermissionLevel.ReadOnly;
    public McpApprovalPolicy ApprovalPolicy { get; set; } = McpApprovalPolicy.AskEveryTime;
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string Id => $"{ServerId}::{Name}";
}

public sealed class McpPermissionAuditRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ServerId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public McpToolPermissionLevel PermissionLevel { get; set; } = McpToolPermissionLevel.ReadOnly;
    public McpApprovalPolicy ApprovalPolicy { get; set; } = McpApprovalPolicy.AskEveryTime;
    public bool Enabled { get; set; } = true;
    public string ChangedBy { get; set; } = "local_user";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class McpTaskMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskType { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public Dictionary<string, string> InputMapping { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AgentTaskSource
{
    public string Module { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string OpportunityId { get; set; } = "";
}

public sealed class AgentTaskTarget
{
    public string ServerId { get; set; } = "";
    public string ToolName { get; set; } = "";
}

public sealed class AgentAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string MimeType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public bool ExplicitlyShared { get; set; }
}

public sealed class AgentTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public AgentTaskSource Source { get; set; } = new();
    public string PayloadJson { get; set; } = "{}";
    public string ContextJson { get; set; } = "{}";
    public string TaskOverrideJson { get; set; } = "{}";
    public List<AgentAttachment> Attachments { get; set; } = [];
    public List<string> RequestedCapabilities { get; set; } = [];
    public List<string> SharedContextKeys { get; set; } = [];
    public AgentTaskTarget Target { get; set; } = new();
    public McpTaskStatus Status { get; set; } = McpTaskStatus.Pending;
    public string IdempotencyKey { get; set; } = "";
    public int RequirementVersionUsed { get; set; }
    public string ParentTaskId { get; set; } = "";
    public string ExternalTaskId { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public AgentTaskResult? Result { get; set; }
    public AgentTaskError? Error { get; set; }
}

public sealed class AgentTaskResult
{
    public string Summary { get; set; } = "";
    public string StructuredDataJson { get; set; } = "{}";
    public List<AgentResultItem> Items { get; set; } = [];
    public List<AgentAttachment> Attachments { get; set; } = [];
    public List<AgentCitation> Citations { get; set; } = [];
    public string RawJson { get; set; } = "";
    public ProductSourcingResult? ProductSourcing { get; set; }
    public AgentTaskResultMetadata Metadata { get; set; } = new();
    public bool UntrustedExternalData { get; set; } = true;
}

public sealed class AgentTaskResultMetadata
{
    public string ServerId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public long ExecutionTimeMs { get; set; }
    public int RequirementVersionUsed { get; set; }
    public int RequirementCollectedCount { get; set; }
    public List<string> MissingAtExecution { get; set; } = [];
}

public sealed class AgentResultItem
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string DataJson { get; set; } = "{}";
}

public sealed class AgentCitation
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class AgentTaskError
{
    public string Code { get; set; } = "EXECUTION_FAILED";
    public string Message { get; set; } = "Agent task failed.";
    public bool Retryable { get; set; }
    public string TechnicalDetails { get; set; } = "";
}

public sealed class McpTaskEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string EventType { get; set; } = "";
    public McpTaskStatus Status { get; set; }
    public string Approval { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string Detail { get; set; } = "";
    public long ExecutionTimeMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ProductRequirement
{
    public string? Product { get; set; }
    public string? Quantity { get; set; }
    public string? TargetPrice { get; set; }
    public string? Destination { get; set; }
    public string? LogisticsPreference { get; set; }
    public List<string> ProductImages { get; set; } = [];
    public string ProductIdentitySource { get; set; } = "";
    public int RequirementVersion { get; set; } = 1;
}

public sealed class RequirementCompleteness
{
    public int CollectedCount { get; set; }
    public int TotalCount { get; set; } = 5;
    public List<string> CollectedElements { get; set; } = [];
    public List<string> MissingElements { get; set; } = [];
    public bool ProductIdentifiable { get; set; }
}

public sealed class SourcingReadiness
{
    public int CollectedCount { get; set; }
    public int TotalCount { get; set; } = 5;
    public List<string> CollectedElements { get; set; } = [];
    public List<string> MissingElements { get; set; } = [];
    public bool ProductIdentifiable { get; set; }
    public SourcingReadinessLevel Readiness { get; set; } = SourcingReadinessLevel.Insufficient;
    public double Confidence { get; set; }

    [JsonIgnore] public bool CanUseAgent => Readiness is SourcingReadinessLevel.AgentAvailable or SourcingReadinessLevel.HighConfidence;
}

public sealed class ProductRequirementState
{
    public int Completeness { get; set; }
    public SourcingReadinessLevel Readiness { get; set; } = SourcingReadinessLevel.Insufficient;
    public List<string> MissingElements { get; set; } = [];
    public bool ProductIdentifiable { get; set; }
    public int RequirementVersion { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ProductSourcingTaskPayload
{
    public string TaskType { get; set; } = "product_sourcing";
    public ProductRequirement Requirement { get; set; } = new();
    public RequirementCompleteness RequirementCompleteness { get; set; } = new();
    public string CustomerContextJson { get; set; } = "{}";
    public string ConversationSummary { get; set; } = "";
    public string AdditionalInstructions { get; set; } = "";
    public List<AgentAttachment> Attachments { get; set; } = [];
}

public sealed class ProductSourcingTaskDraft
{
    public AgentTaskSource Source { get; set; } = new();
    public SourcingRequest Requirement { get; set; } = new();
    public AgentTaskTarget Target { get; set; } = new();
    public string CustomerName { get; set; } = "";
    public string CustomerContextJson { get; set; } = "{}";
    public string ConversationSummary { get; set; } = "";
    public string AdditionalInstructions { get; set; } = "";
    public string TaskOverrideJson { get; set; } = "{}";
    public List<string> SharedContextKeys { get; set; } = [];
    public List<AgentAttachment> Attachments { get; set; } = [];
    public string ParentTaskId { get; set; } = "";
}

public sealed class SourcingAgentRecommendation
{
    public bool ShouldShowAction { get; set; }
    public bool ButtonEnabled { get; set; }
    public string Status { get; set; } = "Need more information";
    public string Reason { get; set; } = "";
    public int RequirementVersion { get; set; }
    public int LastSourcingRequirementVersion { get; set; }
    public bool HasNewInformation { get; set; }
    public SourcingReadiness Readiness { get; set; } = new();
}

public sealed class ProductCandidate
{
    public string Title { get; set; } = "";
    public string Supplier { get; set; } = "";
    public string Price { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Moq { get; set; } = "";
    public string Url { get; set; } = "";
    public string Image { get; set; } = "";
    public string Location { get; set; } = "";
    public string Shipping { get; set; } = "";
    public string Notes { get; set; } = "";
    public double? Confidence { get; set; }
}

public sealed class ProductSourcingResult
{
    public string Summary { get; set; } = "";
    public List<ProductCandidate> Products { get; set; } = [];
    public string Recommendation { get; set; } = "";
    public List<string> MissingInformation { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public double? Confidence { get; set; }
    public List<AgentCitation> Citations { get; set; } = [];
}

public sealed class McpServerTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int ToolCount { get; set; }
    public int ResourceCount { get; set; }
    public int PromptCount { get; set; }
    public long LatencyMs { get; set; }
    public string ErrorCode { get; set; } = "";
    public McpServerCapabilities? Capabilities { get; set; }
}

public sealed class McpGatewaySettings
{
    public int GlobalConcurrency { get; set; } = 4;
    public int PerServerConcurrency { get; set; } = 2;
    public int ToolCacheTtlMinutes { get; set; } = 15;
    public int RawResponseLimitBytes { get; set; } = 256 * 1024;
    public int MaximumInputBytes { get; set; } = 512 * 1024;
    public int MaximumAttachmentBytes { get; set; } = 10 * 1024 * 1024;
    public int MaximumAttachmentCount { get; set; } = 5;
    public int SourcingReadinessThreshold { get; set; } = 3;
    public bool HumanConfirmationRequired { get; set; } = true;
    public bool AutomaticExecutionEnabled { get; set; }
}

public sealed class McpToolInvocationRequest
{
    public string ServerId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string TaskId { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public int TimeoutMs { get; set; }
}

public sealed class McpToolInvocationResult
{
    public string RawJson { get; set; } = "{}";
    public long ExecutionTimeMs { get; set; }
    public bool IsError { get; set; }
}

public sealed class McpConnectorExport
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; }
    public bool AutoConnect { get; set; }
    public McpTransportKind Transport { get; set; }
    public string Endpoint { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public string WorkingDirectory { get; set; } = "";
    public McpAuthType AuthType { get; set; }
    public string ApiKeyHeader { get; set; } = "X-API-Key";
    public string SecretEnvironmentVariable { get; set; } = "MCP_API_KEY";
    public List<string> Tags { get; set; } = [];
    public int TimeoutMs { get; set; }
    public McpRetryPolicy RetryPolicy { get; set; } = new();
    public Dictionary<string, McpContextPermission> ContextPermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<McpTaskMapping> Mappings { get; set; } = [];
}

public sealed class ExternalAgentWorkflowNodeConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "External Agent";
    public string TaskType { get; set; } = "product_sourcing";
    public string ServerId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public Dictionary<string, string> InputMapping { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SharedContextKeys { get; set; } = [McpContextKeys.ProductRequirement];
    public int TimeoutMs { get; set; } = 120_000;
    public bool AutomaticExecutionExplicitlyEnabled { get; set; }
    public bool HumanApprovalRequired { get; set; } = true;
}

public sealed class ExternalAgentWorkflowDecision
{
    public bool TriggerMatched { get; set; }
    public bool ShowAgentAction { get; set; }
    public bool CreateRecommendation { get; set; }
    public bool MayExecuteAutomatically { get; set; }
    public string Reason { get; set; } = "";
    public SourcingReadiness Readiness { get; set; } = new();
}

public static class SourcingReadinessPolicy
{
    private static readonly IReadOnlyDictionary<SourcingFieldKey, string> ElementNames =
        new Dictionary<SourcingFieldKey, string>
        {
            [SourcingFieldKey.ProductImage] = "product",
            [SourcingFieldKey.Quantity] = "quantity",
            [SourcingFieldKey.TargetPrice] = "targetPrice",
            [SourcingFieldKey.Destination] = "destination",
            [SourcingFieldKey.ShippingPreference] = "logisticsPreference"
        };

    private static readonly HashSet<string> VagueProductValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "product", "products", "item", "items", "goods", "something", "anything",
        "产品", "商品", "货物", "东西", "一些产品", "某个产品", "待定", "不确定"
    };

    public static SourcingReadiness Evaluate(SourcingRequest? request)
    {
        var collected = new List<string>();
        var missing = new List<string>();
        foreach (var pair in ElementNames)
        {
            if (request?.Fields.TryGetValue(pair.Key, out var value) == true && value.IsStructurallyValid)
                collected.Add(pair.Value);
            else
                missing.Add(pair.Value);
        }

        var identifiable = request?.Fields.TryGetValue(SourcingFieldKey.ProductImage, out var product) == true
                           && product.IsStructurallyValid
                           && IsProductIdentifiable(product.Value);
        var count = collected.Count;
        var level = count == 5 && identifiable
            ? SourcingReadinessLevel.HighConfidence
            : count >= 3 && identifiable
                ? SourcingReadinessLevel.AgentAvailable
                : SourcingReadinessLevel.Insufficient;
        var confidence = count switch
        {
            >= 5 => 1d,
            4 => .8d,
            3 => .6d,
            2 => .4d,
            1 => .2d,
            _ => 0d
        };
        return new SourcingReadiness
        {
            CollectedCount = count,
            TotalCount = 5,
            CollectedElements = collected,
            MissingElements = missing,
            ProductIdentifiable = identifiable,
            Readiness = level,
            Confidence = confidence
        };
    }

    public static RequirementCompleteness ToCompleteness(SourcingReadiness readiness) => new()
    {
        CollectedCount = readiness.CollectedCount,
        TotalCount = readiness.TotalCount,
        CollectedElements = [.. readiness.CollectedElements],
        MissingElements = [.. readiness.MissingElements],
        ProductIdentifiable = readiness.ProductIdentifiable
    };

    public static bool IsProductIdentifiable(string? value)
    {
        var text = string.Join(' ', (value ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length < 2 || VagueProductValues.Contains(text)) return false;
        if (text.Contains("[image]", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[图片]", StringComparison.OrdinalIgnoreCase)
            || Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return true;
        if (text.Contains("sku", StringComparison.OrdinalIgnoreCase)
            || text.Contains("model", StringComparison.OrdinalIgnoreCase)
            || text.Contains("型号", StringComparison.OrdinalIgnoreCase)
            || text.Contains("款号", StringComparison.OrdinalIgnoreCase))
            return true;
        return text.Any(char.IsLetter) || text.Any(character => character >= 0x4E00 && character <= 0x9FFF);
    }

    public static ProductRequirement ToProductRequirement(SourcingRequest request)
    {
        string? Value(SourcingFieldKey key) =>
            request.Fields.TryGetValue(key, out var field) && field.IsStructurallyValid ? field.Value : null;
        var product = Value(SourcingFieldKey.ProductImage);
        var productImages = new List<string>();
        if (!string.IsNullOrWhiteSpace(product)
            && (product.Contains("[image]", StringComparison.OrdinalIgnoreCase)
                || Uri.TryCreate(product, UriKind.Absolute, out _)))
            productImages.Add(product);
        return new ProductRequirement
        {
            Product = product,
            Quantity = Value(SourcingFieldKey.Quantity),
            TargetPrice = Value(SourcingFieldKey.TargetPrice),
            Destination = Value(SourcingFieldKey.Destination),
            LogisticsPreference = Value(SourcingFieldKey.ShippingPreference),
            ProductImages = productImages,
            ProductIdentitySource = request.Fields.TryGetValue(SourcingFieldKey.ProductImage, out var identity)
                ? identity.SourceMessageId
                : "",
            RequirementVersion = request.Version
        };
    }
}
