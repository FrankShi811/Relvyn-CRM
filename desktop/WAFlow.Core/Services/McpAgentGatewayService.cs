using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class McpAgentGatewayService : IAsyncDisposable
{
    private readonly LocalRepository _repository;
    private readonly SourcingRequestService _sourcingRequests;
    private readonly Func<string, ISecretStore> _secretStoreFactory;
    private readonly McpConnectionManager _connections;
    private readonly SemaphoreSlim _globalLimit = new(4, 4);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _serverLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeTasks = new(StringComparer.OrdinalIgnoreCase);

    public McpAgentGatewayService(
        LocalRepository repository,
        SourcingRequestService sourcingRequests,
        Func<string, ISecretStore>? secretStoreFactory = null,
        McpConnectionManager? connections = null)
    {
        _repository = repository;
        _sourcingRequests = sourcingRequests;
        _secretStoreFactory = secretStoreFactory ?? (target => new WindowsCredentialStore(target));
        _connections = connections ?? new McpConnectionManager(repository, _secretStoreFactory);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _repository.MarkMcpTasksInterruptedAfterRestartAsync(cancellationToken);
        var settings = await _repository.GetAppSettingsAsync(cancellationToken);
        if (!settings.McpAgentGatewayEnabled) return;
        foreach (var server in (await _repository.GetMcpServersAsync(cancellationToken)).Where(item => item.Enabled && item.AutoConnect))
        {
            try
            {
                await _connections.ConnectAndDiscoverAsync(
                    server,
                    settings.McpAgentGateway.ToolCacheTtlMinutes,
                    cancellationToken: cancellationToken);
            }
            catch (Exception)
            {
                // A disconnected third-party server must never prevent Relvyn from starting.
            }
        }
    }

    public Task<List<McpServerConfig>> GetServersAsync(CancellationToken cancellationToken = default) =>
        _repository.GetMcpServersAsync(cancellationToken);

    public Task<List<RegisteredMcpTool>> GetToolsAsync(string? serverId = null, CancellationToken cancellationToken = default) =>
        _repository.GetMcpToolsAsync(serverId, cancellationToken);

    public Task<List<AgentTask>> GetTasksAsync(string? customerId = null, int limit = 200, CancellationToken cancellationToken = default) =>
        _repository.GetMcpTasksAsync(customerId, limit, cancellationToken);

    public async Task SaveServerAsync(
        McpServerConfig server,
        string? credential = null,
        CancellationToken cancellationToken = default)
    {
        McpConnectionManager.ValidateServer(server);
        if (!string.IsNullOrWhiteSpace(credential)) _secretStoreFactory(server.SecretTarget).Save(credential);
        if (!server.Enabled) server.ConnectionState = McpConnectionState.Disabled;
        await _repository.UpsertMcpServerAsync(server, cancellationToken);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var server = await _repository.GetMcpServerAsync(serverId, cancellationToken);
        if (server is null) return;
        await _connections.DisconnectAsync(serverId);
        if (_secretStoreFactory(server.SecretTarget) is WindowsCredentialStore credentials) credentials.Delete();
        await _repository.DeleteMcpServerAsync(serverId, cancellationToken);
    }

    public async Task<McpServerTestResult> TestConnectionAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var server = await _repository.GetMcpServerAsync(serverId, cancellationToken)
                     ?? throw new McpGatewayException("SERVER_NOT_FOUND", "The selected MCP server no longer exists.");
        var started = DateTimeOffset.Now;
        try
        {
            var settings = await _repository.GetAppSettingsAsync(cancellationToken);
            var capabilities = await _connections.ConnectAndDiscoverAsync(
                server,
                settings.McpAgentGateway.ToolCacheTtlMinutes,
                forceReconnect: true,
                cancellationToken);
            return new McpServerTestResult
            {
                Success = true,
                Message = $"Connected. {capabilities.Tools.Count} tools discovered.",
                ToolCount = capabilities.Tools.Count,
                ResourceCount = capabilities.Resources.Count,
                PromptCount = capabilities.Prompts.Count,
                LatencyMs = (long)(DateTimeOffset.Now - started).TotalMilliseconds,
                Capabilities = capabilities
            };
        }
        catch (McpGatewayException error)
        {
            return new McpServerTestResult
            {
                Success = false,
                Message = error.Message,
                ErrorCode = error.Code,
                LatencyMs = (long)(DateTimeOffset.Now - started).TotalMilliseconds
            };
        }
    }

    public async Task<McpServerCapabilities> RefreshToolsAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var server = await _repository.GetMcpServerAsync(serverId, cancellationToken)
                     ?? throw new McpGatewayException("SERVER_NOT_FOUND", "The selected MCP server no longer exists.");
        var settings = await _repository.GetAppSettingsAsync(cancellationToken);
        return await _connections.ConnectAndDiscoverAsync(
            server,
            settings.McpAgentGateway.ToolCacheTtlMinutes,
            forceReconnect: false,
            cancellationToken);
    }

    public Task DisconnectAsync(string serverId) => _connections.DisconnectAsync(serverId);

    public async Task<string> ExportConnectorAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var server = await _repository.GetMcpServerAsync(serverId, cancellationToken)
                     ?? throw new McpGatewayException("SERVER_NOT_FOUND", "The selected MCP server no longer exists.");
        var mappings = (await _repository.GetMcpMappingsAsync(cancellationToken: cancellationToken))
            .Where(item => item.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase)).ToList();
        return Json.Serialize(new McpConnectorExport
        {
            Name = server.Name,
            Description = server.Description,
            Enabled = server.Enabled,
            AutoConnect = server.AutoConnect,
            Transport = server.Transport,
            Endpoint = server.Endpoint,
            Command = server.Command,
            Args = [.. server.Args],
            WorkingDirectory = server.WorkingDirectory,
            AuthType = server.AuthType,
            ApiKeyHeader = server.ApiKeyHeader,
            SecretEnvironmentVariable = server.SecretEnvironmentVariable,
            Tags = [.. server.Tags],
            TimeoutMs = server.TimeoutMs,
            RetryPolicy = server.RetryPolicy,
            ContextPermissions = new Dictionary<string, McpContextPermission>(server.ContextPermissions, StringComparer.OrdinalIgnoreCase),
            Mappings = mappings.Select(item => new McpTaskMapping
            {
                TaskType = item.TaskType,
                ToolName = item.ToolName,
                InputMapping = new Dictionary<string, string>(item.InputMapping, StringComparer.OrdinalIgnoreCase),
                Enabled = item.Enabled
            }).ToList()
        });
    }

    public async Task<McpServerConfig> ImportConnectorAsync(string json, CancellationToken cancellationToken = default)
    {
        McpGatewaySecurity.ValidateJson(json, "INVALID_CONNECTOR", "Connector import must be a JSON object.");
        var connector = Json.Deserialize<McpConnectorExport>(json)
                        ?? throw new McpGatewayException("INVALID_CONNECTOR", "Connector import is empty or unsupported.");
        if (connector.FormatVersion != 1)
            throw new McpGatewayException("UNSUPPORTED_CONNECTOR_VERSION", "This connector export version is not supported.");
        var server = new McpServerConfig
        {
            Name = connector.Name,
            Description = connector.Description,
            Enabled = connector.Enabled,
            AutoConnect = connector.AutoConnect,
            Transport = connector.Transport,
            Endpoint = connector.Endpoint,
            Command = connector.Command,
            Args = [.. connector.Args],
            WorkingDirectory = connector.WorkingDirectory,
            AuthType = connector.AuthType,
            ApiKeyHeader = connector.ApiKeyHeader,
            SecretEnvironmentVariable = connector.SecretEnvironmentVariable,
            Tags = [.. connector.Tags],
            TimeoutMs = connector.TimeoutMs,
            RetryPolicy = connector.RetryPolicy,
            ContextPermissions = new Dictionary<string, McpContextPermission>(connector.ContextPermissions, StringComparer.OrdinalIgnoreCase),
            ConnectionState = McpConnectionState.Disconnected,
            SecretRef = ""
        };
        await SaveServerAsync(server, cancellationToken: cancellationToken);
        foreach (var mapping in connector.Mappings)
        {
            mapping.Id = Guid.NewGuid().ToString("N");
            mapping.ServerId = server.Id;
            await _repository.UpsertMcpMappingAsync(mapping, cancellationToken);
        }
        return server;
    }

    public async Task<McpToolInvocationResult> TestToolAsync(
        string serverId,
        string toolName,
        string argumentsJson,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        var appSettings = await _repository.GetAppSettingsAsync(cancellationToken);
        var server = await _repository.GetMcpServerAsync(serverId, cancellationToken)
                     ?? throw new McpGatewayException("SERVER_NOT_FOUND", "The selected MCP server no longer exists.");
        var tool = await _repository.GetMcpToolAsync(serverId, toolName, cancellationToken)
                   ?? throw new McpGatewayException("TOOL_NOT_FOUND", "The selected MCP tool is no longer available.");
        var task = new AgentTask
        {
            Type = "developer_tool_test",
            Title = $"Tool test · {toolName}",
            Target = new AgentTaskTarget { ServerId = serverId, ToolName = toolName },
            Status = McpTaskStatus.Running,
            ApprovedBy = approvedBy,
            ApprovedAt = DateTimeOffset.Now,
            StartedAt = DateTimeOffset.Now,
            IdempotencyKey = $"tool-test:{Guid.NewGuid():N}"
        };
        McpGatewaySecurity.ValidateInvocation(task, tool, server, appSettings.McpAgentGateway, argumentsJson);
        await _repository.UpsertMcpTaskAsync(task, cancellationToken);
        await RecordEventAsync(task, "mcp.tool.test.started", "Human-approved Tool Explorer test started.", cancellationToken);
        try
        {
            if (!_connections.IsConnected(serverId))
                await _connections.ConnectAndDiscoverAsync(server, appSettings.McpAgentGateway.ToolCacheTtlMinutes, cancellationToken: cancellationToken);
            var result = await _connections.InvokeAsync(new McpToolInvocationRequest
            {
                ServerId = serverId,
                ToolName = toolName,
                ArgumentsJson = argumentsJson,
                TaskId = task.Id,
                ApprovedBy = approvedBy,
                TimeoutMs = server.NormalizedTimeoutMs
            }, cancellationToken);
            result.RawJson = McpGatewaySecurity.BoundAndSanitizeExternalResult(result.RawJson, appSettings.McpAgentGateway);
            task.Status = result.IsError ? McpTaskStatus.Failed : McpTaskStatus.Completed;
            task.CompletedAt = DateTimeOffset.Now;
            task.Result = result.IsError ? null : new AgentTaskResult
            {
                Summary = "Tool Explorer test completed.",
                RawJson = result.RawJson,
                StructuredDataJson = result.RawJson,
                Metadata = new AgentTaskResultMetadata { ServerId = serverId, ToolName = toolName, ExecutionTimeMs = result.ExecutionTimeMs }
            };
            task.Error = result.IsError ? new AgentTaskError { Code = "AGENT_REPORTED_ERROR", Message = "The MCP tool returned an error result." } : null;
            await _repository.UpsertMcpTaskAsync(task, cancellationToken);
            await RecordEventAsync(task, result.IsError ? "mcp.tool.test.failed" : "mcp.tool.test.completed", task.Result?.Summary ?? task.Error!.Message, cancellationToken);
            return result;
        }
        catch (McpGatewayException error)
        {
            task.Status = error.Code == "TOOL_TIMEOUT" ? McpTaskStatus.TimedOut : McpTaskStatus.Failed;
            task.CompletedAt = DateTimeOffset.Now;
            task.Error = new AgentTaskError { Code = error.Code, Message = error.Message, Retryable = error.Retryable };
            await _repository.UpsertMcpTaskAsync(task, cancellationToken);
            await RecordEventAsync(task, "mcp.tool.test.failed", error.Message, cancellationToken);
            throw;
        }
    }

    public async Task<List<AgentTask>> RetryWaitingTasksAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var waiting = (await _repository.GetMcpTasksAsync(limit: 1000, cancellationToken: cancellationToken))
            .Where(task => task.Target.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase)
                           && task.Status == McpTaskStatus.Waiting
                           && !string.IsNullOrWhiteSpace(task.ApprovedBy))
            .OrderBy(task => task.CreatedAt)
            .ToList();
        var completed = new List<AgentTask>();
        foreach (var task in waiting)
            completed.Add(await ExecuteAsync(task, cancellationToken));
        return completed;
    }

    public async Task UpdateToolPolicyAsync(
        RegisteredMcpTool tool,
        string changedBy = "local_user",
        CancellationToken cancellationToken = default)
    {
        await _repository.UpsertMcpToolAsync(tool, cancellationToken);
        await _repository.AddMcpPermissionAuditAsync(new McpPermissionAuditRecord
        {
            ServerId = tool.ServerId,
            ToolName = tool.Name,
            PermissionLevel = tool.PermissionLevel,
            ApprovalPolicy = tool.ApprovalPolicy,
            Enabled = tool.Enabled,
            ChangedBy = changedBy
        }, cancellationToken);
    }

    public SourcingAgentRecommendation EvaluateSourcingAction(SourcingRequest request)
    {
        var readiness = request.Readiness;
        var status = readiness.Readiness switch
        {
            SourcingReadinessLevel.HighConfidence => "Complete",
            SourcingReadinessLevel.AgentAvailable => "Ready for Agent",
            _ => "Need more information"
        };
        var reason = !readiness.ProductIdentifiable && readiness.CollectedCount >= 3
            ? $"{readiness.CollectedCount} elements collected. Product information is still required before sourcing."
            : readiness.CanUseAgent
                ? readiness.MissingElements.Count == 0
                    ? "The requirement is complete. Choose an Agent and review the task before sending."
                    : $"{readiness.MissingElements.Count} details are still missing. You can search now or continue collecting information."
                : $"Collect at least {Math.Max(0, 3 - readiness.CollectedCount)} more sourcing details, including an identifiable product.";
        return new SourcingAgentRecommendation
        {
            ShouldShowAction = readiness.CollectedCount >= 3 || readiness.ProductIdentifiable,
            ButtonEnabled = readiness.CanUseAgent,
            Status = status,
            Reason = reason,
            RequirementVersion = request.Version,
            LastSourcingRequirementVersion = request.LastSourcingRequirementVersion,
            HasNewInformation = request.Version > request.LastSourcingRequirementVersion,
            Readiness = readiness
        };
    }

    public async Task<List<(McpServerConfig Server, RegisteredMcpTool Tool)>> GetAvailableAgentsAsync(
        CancellationToken cancellationToken = default)
    {
        var servers = (await _repository.GetMcpServersAsync(cancellationToken))
            .Where(server => server.Enabled && server.ConnectionState is McpConnectionState.Connected or McpConnectionState.Degraded)
            .ToDictionary(server => server.Id, StringComparer.OrdinalIgnoreCase);
        var tools = (await _repository.GetMcpToolsAsync(cancellationToken: cancellationToken))
            .Where(tool => tool.Enabled && tool.ApprovalPolicy != McpApprovalPolicy.Deny)
            .Where(tool => !IsCustomerChannelTool(tool.Name))
            .ToList();
        return tools.Where(tool => servers.ContainsKey(tool.ServerId))
            .Select(tool => (servers[tool.ServerId], tool))
            .ToList();
    }

    public async Task<AgentTask> BuildProductSourcingTaskAsync(
        ProductSourcingTaskDraft draft,
        CancellationToken cancellationToken = default)
    {
        var readiness = draft.Requirement.Readiness;
        if (!readiness.CanUseAgent)
        {
            var message = readiness.CollectedCount >= 3 && !readiness.ProductIdentifiable
                ? "Product information is still required before sourcing."
                : "At least 3 of 5 sourcing elements and an identifiable product are required.";
            throw new McpGatewayException("SOURCING_NOT_READY", message);
        }
        if (string.IsNullOrWhiteSpace(draft.Target.ServerId) || string.IsNullOrWhiteSpace(draft.Target.ToolName))
            throw new McpGatewayException("AGENT_SELECTION_REQUIRED", "Choose the MCP server and tool that should handle this sourcing task.");
        var server = await _repository.GetMcpServerAsync(draft.Target.ServerId, cancellationToken)
                     ?? throw new McpGatewayException("SERVER_NOT_FOUND", "The selected MCP server no longer exists.");
        var tool = await _repository.GetMcpToolAsync(draft.Target.ServerId, draft.Target.ToolName, cancellationToken)
                   ?? throw new McpGatewayException("TOOL_NOT_FOUND", "The selected MCP tool is no longer available. Refresh the server tools.");
        if (!server.Enabled || !tool.Enabled || tool.ApprovalPolicy == McpApprovalPolicy.Deny)
            throw new McpGatewayException("AGENT_NOT_AVAILABLE", "The selected MCP Agent is not currently available.");
        if (IsCustomerChannelTool(tool.Name))
            throw new McpGatewayException("CUSTOMER_CHANNEL_FORBIDDEN", "Sourcing tasks cannot target a tool that directly messages customers.");

        McpGatewaySecurity.ValidateJson(draft.CustomerContextJson, "INVALID_CONTEXT", "Customer context must be a JSON object.");
        McpGatewaySecurity.ValidateJson(draft.TaskOverrideJson, "INVALID_TASK_OVERRIDE", "Task Override must be a JSON object.");
        var productRequirement = SourcingReadinessPolicy.ToProductRequirement(draft.Requirement);
        var completeness = SourcingReadinessPolicy.ToCompleteness(readiness);
        var payload = new ProductSourcingTaskPayload
        {
            Requirement = productRequirement,
            RequirementCompleteness = completeness,
            CustomerContextJson = draft.CustomerContextJson,
            ConversationSummary = draft.ConversationSummary,
            AdditionalInstructions = draft.AdditionalInstructions,
            Attachments = draft.Attachments.Select(item => new AgentAttachment
            {
                Id = item.Id,
                Name = item.Name,
                MimeType = item.MimeType,
                SizeBytes = item.SizeBytes,
                Sha256 = item.Sha256,
                ExplicitlyShared = item.ExplicitlyShared
            }).ToList()
        };
        var idempotency = CreateIdempotencyKey(draft, productRequirement, completeness);
        return new AgentTask
        {
            Type = "product_sourcing",
            Title = $"Product sourcing · {(string.IsNullOrWhiteSpace(productRequirement.Product) ? "Identifiable product" : productRequirement.Product)}",
            Description = $"Partial requirements are expected. {completeness.CollectedCount}/5 collected; missing: {string.Join(", ", completeness.MissingElements)}.",
            Source = draft.Source,
            PayloadJson = Json.Serialize(payload),
            ContextJson = draft.CustomerContextJson,
            TaskOverrideJson = draft.TaskOverrideJson,
            Attachments = draft.Attachments,
            RequestedCapabilities = ["product_sourcing", "best_effort_search"],
            SharedContextKeys = [.. draft.SharedContextKeys.Distinct(StringComparer.OrdinalIgnoreCase)],
            Target = draft.Target,
            Status = McpTaskStatus.AwaitingApproval,
            IdempotencyKey = idempotency,
            RequirementVersionUsed = draft.Requirement.Version,
            ParentTaskId = draft.ParentTaskId
        };
    }

    public async Task<AgentTask> SubmitApprovedAsync(
        AgentTask task,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(approvedBy))
            throw new McpGatewayException("HUMAN_APPROVAL_REQUIRED", "A named human confirmation is required before calling an external Agent.");
        if (!task.Type.Equals("product_sourcing", StringComparison.OrdinalIgnoreCase))
            throw new McpGatewayException("UNSUPPORTED_TASK_TYPE", "This task type is not supported by the current Gateway executor.");
        var duplicate = await _repository.GetMcpTaskByIdempotencyKeyAsync(task.IdempotencyKey, cancellationToken);
        if (duplicate is not null && duplicate.Status is not McpTaskStatus.Failed and not McpTaskStatus.Cancelled and not McpTaskStatus.TimedOut)
            return duplicate;

        task.ApprovedBy = approvedBy.Trim();
        task.ApprovedAt = DateTimeOffset.Now;
        task.Status = McpTaskStatus.Queued;
        task.QueuedAt = DateTimeOffset.Now;
        try
        {
            await _repository.UpsertMcpTaskAsync(task, cancellationToken);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            var racedTask = await _repository.GetMcpTaskByIdempotencyKeyAsync(task.IdempotencyKey, cancellationToken);
            if (racedTask is not null) return racedTask;
            throw;
        }
        await RecordEventAsync(task, "mcp.task.approved", "Human reviewed and approved the exact server, tool, context, attachments, and task payload.", cancellationToken);
        await _sourcingRequests.MarkRequirementVersionUsedAsync(task.Source.CustomerId, task.RequirementVersionUsed, cancellationToken);
        return await ExecuteAsync(task, cancellationToken);
    }

    public async Task<AgentTask> RefineProductSourcingAsync(
        AgentTask previousTask,
        ProductSourcingTaskDraft updatedDraft,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        if (updatedDraft.Requirement.Version <= previousTask.RequirementVersionUsed)
            throw new McpGatewayException("NO_NEW_REQUIREMENT_INFORMATION", "No newer sourcing requirement is available to refine this result.");
        updatedDraft.ParentTaskId = previousTask.Id;
        var task = await BuildProductSourcingTaskAsync(updatedDraft, cancellationToken);
        return await SubmitApprovedAsync(task, approvedBy, cancellationToken);
    }

    public async Task<AgentTask> CancelAsync(string taskId, string actor, CancellationToken cancellationToken = default)
    {
        var task = await _repository.GetMcpTaskAsync(taskId, cancellationToken)
                   ?? throw new McpGatewayException("TASK_NOT_FOUND", "The selected Agent task no longer exists.");
        if (task.Status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled) return task;
        if (_activeTasks.TryGetValue(taskId, out var source)) source.Cancel();
        task.Status = McpTaskStatus.Cancelled;
        task.Error = new AgentTaskError { Code = "CANCELLED_BY_USER", Message = $"Cancelled by {actor}." };
        task.CompletedAt = DateTimeOffset.Now;
        await _repository.UpsertMcpTaskAsync(task, cancellationToken);
        await RecordEventAsync(task, "mcp.task.cancelled", task.Error.Message, cancellationToken);
        return task;
    }

    private async Task<AgentTask> ExecuteAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var appSettings = await _repository.GetAppSettingsAsync(cancellationToken);
        if (!appSettings.McpAgentGatewayEnabled)
            throw new McpGatewayException("GATEWAY_DISABLED", "MCP & Agents is disabled in Settings.");
        var gatewaySettings = appSettings.McpAgentGateway;
        var server = await _repository.GetMcpServerAsync(task.Target.ServerId, cancellationToken)
                     ?? throw new McpGatewayException("SERVER_NOT_FOUND", "The selected MCP server no longer exists.");
        var tool = await _repository.GetMcpToolAsync(task.Target.ServerId, task.Target.ToolName, cancellationToken)
                   ?? throw new McpGatewayException("TOOL_NOT_FOUND", "The selected MCP tool is no longer available.");
        var mapping = (await _repository.GetMcpMappingsAsync(task.Type, cancellationToken))
            .FirstOrDefault(item => item.Enabled
                                    && item.ServerId.Equals(task.Target.ServerId, StringComparison.OrdinalIgnoreCase)
                                    && item.ToolName.Equals(task.Target.ToolName, StringComparison.OrdinalIgnoreCase));
        var preparedAttachments = McpGatewaySecurity.PrepareAttachments(task.Attachments, gatewaySettings);
        var argumentsJson = McpInputMapper.Map(task, mapping, preparedAttachments);
        McpGatewaySecurity.ValidateInvocation(task, tool, server, gatewaySettings, argumentsJson);
        var serverLimit = _serverLimits.GetOrAdd(server.Id, _ => new SemaphoreSlim(
            Math.Clamp(gatewaySettings.PerServerConcurrency, 1, 8),
            Math.Clamp(gatewaySettings.PerServerConcurrency, 1, 8)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_activeTasks.TryAdd(task.Id, linked))
            throw new McpGatewayException("TASK_ALREADY_RUNNING", "This Agent task is already running.");

        var globalAcquired = false;
        var serverAcquired = false;
        try
        {
            await _globalLimit.WaitAsync(linked.Token);
            globalAcquired = true;
            await serverLimit.WaitAsync(linked.Token);
            serverAcquired = true;
            task.Status = McpTaskStatus.Running;
            task.StartedAt = DateTimeOffset.Now;
            await _repository.UpsertMcpTaskAsync(task, linked.Token);
            await RecordEventAsync(task, "mcp.task.running", "External Agent invocation started.", linked.Token);
            McpGatewayException? lastError = null;
            for (var attempt = 0; attempt <= server.RetryPolicy.NormalizedRetries; attempt++)
            {
                try
                {
                    if (!_connections.IsConnected(server.Id))
                        await _connections.ConnectAndDiscoverAsync(server, gatewaySettings.ToolCacheTtlMinutes, cancellationToken: linked.Token);
                    var invocation = await _connections.InvokeAsync(new McpToolInvocationRequest
                    {
                        ServerId = server.Id,
                        ToolName = tool.Name,
                        ArgumentsJson = argumentsJson,
                        TaskId = task.Id,
                        ApprovedBy = task.ApprovedBy,
                        TimeoutMs = server.NormalizedTimeoutMs
                    }, linked.Token);
                    if (invocation.IsError)
                        throw new McpGatewayException("AGENT_REPORTED_ERROR", "The MCP tool returned an error result.");
                    var raw = McpGatewaySecurity.BoundAndSanitizeExternalResult(invocation.RawJson, gatewaySettings);
                    var payload = Json.Deserialize<ProductSourcingTaskPayload>(task.PayloadJson) ?? new ProductSourcingTaskPayload();
                    task.Result = McpResultNormalizer.NormalizeProductSourcing(
                        raw,
                        server.Id,
                        tool.Name,
                        invocation.ExecutionTimeMs,
                        task.RequirementVersionUsed,
                        payload.RequirementCompleteness);
                    task.Status = McpResultNormalizer.RequestsMoreInformation(task.Result)
                        ? McpTaskStatus.NeedsInformation
                        : McpTaskStatus.Completed;
                    task.CompletedAt = DateTimeOffset.Now;
                    task.Error = null;
                    await _repository.UpsertMcpTaskAsync(task, linked.Token);
                    await RecordEventAsync(task, task.Status == McpTaskStatus.NeedsInformation
                        ? "mcp.task.needs_information"
                        : "mcp.task.completed", task.Result.Summary, linked.Token);
                    return task;
                }
                catch (McpGatewayException error) when (error.Retryable && attempt < server.RetryPolicy.NormalizedRetries)
                {
                    lastError = error;
                    await RecordEventAsync(task, "mcp.task.retry", $"Retry {attempt + 1}: {error.Code}", linked.Token);
                    var jitter = Random.Shared.Next(0, Math.Max(50, server.RetryPolicy.NormalizedBackoffMs / 3));
                    await Task.Delay(server.RetryPolicy.NormalizedBackoffMs * (attempt + 1) + jitter, linked.Token);
                    await _connections.DisconnectAsync(server.Id);
                }
                catch (McpGatewayException error)
                {
                    lastError = error;
                    break;
                }
            }
            var failure = lastError ?? new McpGatewayException("EXECUTION_FAILED", "The external Agent task failed.");
            var waitForConnection = failure.Retryable && failure.Code is
                "NETWORK_ERROR" or "TRANSPORT_CLOSED" or "SERVER_DISCONNECTED" or "CONNECTION_TIMEOUT";
            task.Status = waitForConnection
                ? McpTaskStatus.Waiting
                : failure.Code == "TOOL_TIMEOUT" ? McpTaskStatus.TimedOut : McpTaskStatus.Failed;
            task.Error = new AgentTaskError
            {
                Code = failure.Code,
                Message = failure.Message,
                Retryable = failure.Retryable,
                TechnicalDetails = McpGatewaySecurity.RedactSecrets(failure.InnerException?.GetType().Name ?? "")
            };
            task.CompletedAt = waitForConnection ? null : DateTimeOffset.Now;
            await _repository.UpsertMcpTaskAsync(task, cancellationToken);
            await RecordEventAsync(task, waitForConnection ? "mcp.task.waiting" : "mcp.task.failed", task.Error.Message, cancellationToken);
            return task;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            task.Status = McpTaskStatus.Cancelled;
            task.Error = new AgentTaskError { Code = "CANCELLED", Message = "The Agent task was cancelled." };
            task.CompletedAt = DateTimeOffset.Now;
            await _repository.UpsertMcpTaskAsync(task, CancellationToken.None);
            await RecordEventAsync(task, "mcp.task.cancelled", task.Error.Message, CancellationToken.None);
            return task;
        }
        finally
        {
            if (serverAcquired) serverLimit.Release();
            if (globalAcquired) _globalLimit.Release();
            _activeTasks.TryRemove(task.Id, out _);
        }
    }

    private async Task RecordEventAsync(AgentTask task, string eventType, string detail, CancellationToken cancellationToken)
    {
        await _repository.AddMcpTaskEventAsync(new McpTaskEvent
        {
            TaskId = task.Id,
            ServerId = task.Target.ServerId,
            ToolName = task.Target.ToolName,
            CustomerId = task.Source.CustomerId,
            EventType = eventType,
            Status = task.Status,
            Approval = string.IsNullOrWhiteSpace(task.ApprovedBy) ? "not_approved" : $"approved_by:{task.ApprovedBy}",
            ErrorCode = task.Error?.Code ?? "",
            Detail = McpGatewaySecurity.RedactSecrets(detail),
            ExecutionTimeMs = task.Result?.Metadata.ExecutionTimeMs ?? 0
        }, cancellationToken);
        if (string.IsNullOrWhiteSpace(task.Source.CustomerId)) return;
        await _repository.UpsertCustomerEventAsync(new CustomerEventLogEntry
        {
            CustomerId = task.Source.CustomerId,
            EventType = eventType,
            Title = task.Status switch
            {
                McpTaskStatus.Completed => "External Agent completed",
                McpTaskStatus.NeedsInformation => "External Agent needs information",
                McpTaskStatus.Failed or McpTaskStatus.TimedOut => "External Agent task failed",
                _ => "External Agent task updated"
            },
            Detail = McpGatewaySecurity.RedactSecrets(detail),
            SourceType = "mcp_agent_task",
            SourceId = task.Id,
            OccurredAt = DateTimeOffset.Now
        }, cancellationToken);
    }

    private static bool IsCustomerChannelTool(string toolName) =>
        toolName.Contains("whatsapp", StringComparison.OrdinalIgnoreCase)
        || toolName.Contains("send_message", StringComparison.OrdinalIgnoreCase)
        || toolName.Contains("send_email", StringComparison.OrdinalIgnoreCase)
        || toolName.Contains("reply_customer", StringComparison.OrdinalIgnoreCase)
        || toolName.Contains("sms", StringComparison.OrdinalIgnoreCase);

    private static string CreateIdempotencyKey(
        ProductSourcingTaskDraft draft,
        ProductRequirement requirement,
        RequirementCompleteness completeness)
    {
        var canonical = Json.Serialize(new
        {
            draft.Source.CustomerId,
            draft.Source.ConversationId,
            draft.Target.ServerId,
            draft.Target.ToolName,
            requirement,
            completeness,
            draft.AdditionalInstructions,
            draft.TaskOverrideJson,
            attachmentIds = draft.Attachments.Where(item => item.ExplicitlyShared).Select(item => item.Id).Order()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _activeTasks.Values) source.Cancel();
        foreach (var source in _activeTasks.Values) source.Dispose();
        _activeTasks.Clear();
        await _connections.DisposeAsync();
        _globalLimit.Dispose();
        foreach (var item in _serverLimits.Values) item.Dispose();
        _serverLimits.Clear();
    }
}
