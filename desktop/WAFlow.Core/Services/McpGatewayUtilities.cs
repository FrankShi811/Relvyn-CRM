using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public static partial class McpGatewaySecurity
{
    private static readonly HashSet<string> CustomerChannelTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "whatsapp", "email", "sms", "customer_message", "send_message", "reply_customer",
        "contact_customer", "send_mail", "send_email"
    };

    public static void ValidateInvocation(
        AgentTask task,
        RegisteredMcpTool tool,
        McpServerConfig server,
        McpGatewaySettings settings,
        string argumentsJson)
    {
        if (!server.Enabled) throw new McpGatewayException("SERVER_DISABLED", "The selected MCP server is disabled.");
        if (!tool.Enabled) throw new McpGatewayException("TOOL_DISABLED", "The selected MCP tool is disabled.");
        if (tool.ApprovalPolicy == McpApprovalPolicy.Deny)
            throw new McpGatewayException("TOOL_DENIED", "The selected MCP tool is denied by its permission policy.");
        if (settings.HumanConfirmationRequired && string.IsNullOrWhiteSpace(task.ApprovedBy))
            throw new McpGatewayException("HUMAN_APPROVAL_REQUIRED", "Review the task and confirm it before sending data to the external Agent.");
        foreach (var contextKey in task.SharedContextKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var permission = server.ContextPermissions.GetValueOrDefault(contextKey, McpContextPermission.Deny);
            if (permission == McpContextPermission.Deny)
                throw new McpGatewayException("CONTEXT_PERMISSION_DENIED", $"The selected Server is not allowed to receive '{contextKey}'.");
            if (permission == McpContextPermission.Ask && string.IsNullOrWhiteSpace(task.ApprovedBy))
                throw new McpGatewayException("CONTEXT_APPROVAL_REQUIRED", $"Human approval is required before sharing '{contextKey}'.");
        }
        if (task.Attachments.Any(item => item.ExplicitlyShared)
            && server.ContextPermissions.GetValueOrDefault(McpContextKeys.Attachments, McpContextPermission.Deny) == McpContextPermission.Deny)
            throw new McpGatewayException("ATTACHMENT_PERMISSION_DENIED", "The selected Server is not allowed to receive attachments.");
        if (task.Type.Equals("product_sourcing", StringComparison.OrdinalIgnoreCase)
            && CustomerChannelTerms.Any(term => tool.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            throw new McpGatewayException("CUSTOMER_CHANNEL_FORBIDDEN", "Product sourcing Agents cannot directly send WhatsApp, email, SMS, or other customer messages.");
        if (Encoding.UTF8.GetByteCount(argumentsJson) > Math.Clamp(settings.MaximumInputBytes, 16 * 1024, 4 * 1024 * 1024))
            throw new McpGatewayException("INPUT_TOO_LARGE", "The MCP request is larger than the configured input limit.");
        ValidateJson(argumentsJson, "INVALID_ARGUMENTS", "The MCP request must be a JSON object.");
        ValidateAgainstSchema(argumentsJson, tool.InputSchemaJson);
    }

    public static List<Dictionary<string, object?>> PrepareAttachments(
        IReadOnlyList<AgentAttachment> attachments,
        McpGatewaySettings settings)
    {
        if (attachments.Count > Math.Clamp(settings.MaximumAttachmentCount, 0, 20))
            throw new McpGatewayException("TOO_MANY_ATTACHMENTS", "Too many attachments were selected for this Agent task.");
        var prepared = new List<Dictionary<string, object?>>();
        foreach (var attachment in attachments)
        {
            if (!attachment.ExplicitlyShared) continue;
            if (string.IsNullOrWhiteSpace(attachment.LocalPath) || !File.Exists(attachment.LocalPath))
                throw new McpGatewayException("ATTACHMENT_NOT_FOUND", $"The selected attachment is unavailable: {attachment.Name}");
            var info = new FileInfo(attachment.LocalPath);
            if (info.Length > Math.Clamp(settings.MaximumAttachmentBytes, 64 * 1024, 50 * 1024 * 1024))
                throw new McpGatewayException("ATTACHMENT_TOO_LARGE", $"The selected attachment is too large: {attachment.Name}");
            var bytes = File.ReadAllBytes(info.FullName);
            prepared.Add(new Dictionary<string, object?>
            {
                ["id"] = attachment.Id,
                ["name"] = string.IsNullOrWhiteSpace(attachment.Name) ? info.Name : Path.GetFileName(attachment.Name),
                ["mimeType"] = attachment.MimeType,
                ["sizeBytes"] = bytes.LongLength,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                ["base64"] = Convert.ToBase64String(bytes)
            });
            CryptographicOperations.ZeroMemory(bytes);
        }
        return prepared;
    }

    public static string BoundAndSanitizeExternalResult(string rawJson, McpGatewaySettings settings)
    {
        var maximum = Math.Clamp(settings.RawResponseLimitBytes, 16 * 1024, 2 * 1024 * 1024);
        if (Encoding.UTF8.GetByteCount(rawJson) > maximum)
            throw new McpGatewayException("OUTPUT_TOO_LARGE", "The MCP response exceeded the configured safe display limit.");
        if (LocalPathPattern().IsMatch(rawJson) || rawJson.Contains("file://", StringComparison.OrdinalIgnoreCase))
            rawJson = LocalPathPattern().Replace(rawJson, "[local path removed]")
                .Replace("file://", "[local reference removed]", StringComparison.OrdinalIgnoreCase);
        return RedactSecrets(rawJson);
    }

    public static string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return SecretPattern().Replace(text, match => $"{match.Groups[1].Value}[redacted]");
    }

    public static void ValidateJson(string json, string code, string message)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new McpGatewayException(code, message);
        }
        catch (JsonException error)
        {
            throw new McpGatewayException(code, message, false, error);
        }
    }

    private static void ValidateAgainstSchema(string json, string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson) || schemaJson == "{}") return;
        try
        {
            using var payload = JsonDocument.Parse(json);
            using var schema = JsonDocument.Parse(schemaJson);
            var root = schema.RootElement;
            if (root.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var property in required.EnumerateArray())
                {
                    var name = property.GetString();
                    if (!string.IsNullOrWhiteSpace(name) && !payload.RootElement.TryGetProperty(name, out _))
                        throw new McpGatewayException("SCHEMA_VALIDATION_FAILED", $"The selected tool requires '{name}'. Review the task mapping before sending.");
                }
            }
            if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in payload.RootElement.EnumerateObject())
                {
                    if (!properties.TryGetProperty(property.Name, out var definition)) continue;
                    if (!definition.TryGetProperty("type", out var type)) continue;
                    var expected = type.GetString();
                    var valid = expected switch
                    {
                        "string" => property.Value.ValueKind == JsonValueKind.String,
                        "number" => property.Value.ValueKind == JsonValueKind.Number,
                        "integer" => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out _),
                        "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                        "array" => property.Value.ValueKind == JsonValueKind.Array,
                        "object" => property.Value.ValueKind == JsonValueKind.Object,
                        "null" => property.Value.ValueKind == JsonValueKind.Null,
                        _ => true
                    };
                    if (!valid)
                        throw new McpGatewayException("SCHEMA_VALIDATION_FAILED", $"'{property.Name}' does not match the selected tool's {expected} input type.");
                }
            }
        }
        catch (JsonException error)
        {
            throw new McpGatewayException("INVALID_TOOL_SCHEMA", "The selected MCP tool published an invalid input schema.", false, error);
        }
    }

    [GeneratedRegex(@"(?i)(authorization\s*[:=]\s*|bearer\s+|api[_-]?key\s*[:=]\s*|token\s*[:=]\s*|secret\s*[:=]\s*)[^\s,\""'}]+")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"(?i)(?:[a-z]:\\(?:[^\""\r\n\\]+\\)*[^\""\r\n\\]*|/(?:users|home|var|tmp)/[^\""\r\n\s]+)")]
    private static partial Regex LocalPathPattern();
}

public static class McpInputMapper
{
    public static string Map(AgentTask task, McpTaskMapping? mapping, IReadOnlyList<Dictionary<string, object?>> attachments)
    {
        var payload = JsonNode.Parse(task.PayloadJson) as JsonObject ?? new JsonObject();
        var context = JsonNode.Parse(task.ContextJson) as JsonObject ?? new JsonObject();
        var taskOverride = JsonNode.Parse(task.TaskOverrideJson) as JsonObject ?? new JsonObject();
        Merge(payload, taskOverride);
        payload["context"] = context.DeepClone();
        payload["attachments"] = JsonSerializer.SerializeToNode(attachments, Json.Options);
        payload["relvynTask"] = new JsonObject
        {
            ["taskId"] = task.Id,
            ["taskType"] = task.Type,
            ["requirementVersionUsed"] = task.RequirementVersionUsed,
            ["partialRequirementExpected"] = task.Type.Equals("product_sourcing", StringComparison.OrdinalIgnoreCase)
        };

        if (mapping is null || mapping.InputMapping.Count == 0) return payload.ToJsonString(Json.Options);
        var root = new JsonObject { ["payload"] = payload.DeepClone(), ["context"] = context.DeepClone() };
        var mapped = new JsonObject();
        foreach (var pair in mapping.InputMapping)
            mapped[pair.Key] = ResolvePath(root, pair.Value)?.DeepClone();
        return mapped.ToJsonString(Json.Options);
    }

    private static JsonNode? ResolvePath(JsonObject root, string expression)
    {
        var path = expression.Trim();
        if (path.StartsWith("{{") && path.EndsWith("}}")) path = path[2..^2].Trim();
        if (!path.StartsWith("payload.", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("context.", StringComparison.OrdinalIgnoreCase))
            path = "payload." + path;
        JsonNode? node = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            node = node is JsonObject item && item.TryGetPropertyValue(segment, out var child) ? child : null;
        return node;
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
        {
            if (pair.Value is JsonObject sourceObject && target[pair.Key] is JsonObject targetObject)
                Merge(targetObject, sourceObject);
            else
                target[pair.Key] = pair.Value?.DeepClone();
        }
    }
}

public static class McpResultNormalizer
{
    public static AgentTaskResult NormalizeProductSourcing(
        string rawJson,
        string serverId,
        string toolName,
        long executionTimeMs,
        int requirementVersion,
        RequirementCompleteness completeness)
    {
        var structured = ExtractStructuredPayload(rawJson);
        ProductSourcingResult result;
        try
        {
            result = Json.Deserialize<ProductSourcingResult>(structured) ?? new ProductSourcingResult();
        }
        catch (JsonException)
        {
            result = new ProductSourcingResult { Summary = ExtractText(rawJson) };
        }
        if (string.IsNullOrWhiteSpace(result.Summary))
            result.Summary = result.Products.Count > 0
                ? $"{result.Products.Count} candidate products returned."
                : "The external Agent returned a result.";
        if (result.MissingInformation.Count == 0)
            result.MissingInformation = [.. completeness.MissingElements];

        return new AgentTaskResult
        {
            Summary = result.Summary,
            StructuredDataJson = structured,
            ProductSourcing = result,
            Citations = result.Citations,
            RawJson = rawJson,
            UntrustedExternalData = true,
            Metadata = new AgentTaskResultMetadata
            {
                ServerId = serverId,
                ToolName = toolName,
                ExecutionTimeMs = executionTimeMs,
                RequirementVersionUsed = requirementVersion,
                RequirementCollectedCount = completeness.CollectedCount,
                MissingAtExecution = [.. completeness.MissingElements]
            }
        };
    }

    public static bool RequestsMoreInformation(AgentTaskResult result)
    {
        try
        {
            using var document = JsonDocument.Parse(result.StructuredDataJson);
            if (document.RootElement.TryGetProperty("status", out var status)
                && status.GetString()?.Equals("needs_information", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        catch (JsonException) { }
        return result.ProductSourcing is { Products.Count: 0, MissingInformation.Count: > 0 };
    }

    private static string ExtractStructuredPayload(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (root.TryGetProperty("structuredContent", out var structured)
                && structured.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return structured.GetRawText();
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (!item.TryGetProperty("text", out var text)) continue;
                    var value = text.GetString();
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    try
                    {
                        using var nested = JsonDocument.Parse(value);
                        return nested.RootElement.GetRawText();
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch (JsonException) { }
        return "{}";
    }

    private static string ExtractText(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                return string.Join("\n", content.EnumerateArray()
                    .Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : null)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            }
        }
        catch (JsonException) { }
        return "The external Agent returned an unstructured response.";
    }
}
