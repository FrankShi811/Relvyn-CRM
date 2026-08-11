using Microsoft.Data.Sqlite;
using WAFlow.Core.Domain;

namespace WAFlow.Core.Infrastructure;

public sealed partial class LocalRepository
{
    public async Task<List<McpServerConfig>> GetMcpServersAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<McpServerConfig>();
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM mcp_servers ORDER BY name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<McpServerConfig>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<McpServerConfig?> GetMcpServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM mcp_servers WHERE id=$id";
        command.Parameters.AddWithValue("$id", serverId);
        return Json.Deserialize<McpServerConfig>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task UpsertMcpServerAsync(McpServerConfig server, CancellationToken cancellationToken = default)
    {
        server.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO mcp_servers(id,name,enabled,transport,connection_state,updated_at,data_json)
            VALUES($id,$name,$enabled,$transport,$state,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,enabled=excluded.enabled,transport=excluded.transport,
              connection_state=excluded.connection_state,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", server.Id);
        command.Parameters.AddWithValue("$name", server.Name);
        command.Parameters.AddWithValue("$enabled", server.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$transport", server.Transport.ToString());
        command.Parameters.AddWithValue("$state", server.ConnectionState.ToString());
        command.Parameters.AddWithValue("$updated", server.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(server));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteMcpServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM mcp_servers WHERE id=$id";
        command.Parameters.AddWithValue("$id", serverId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveMcpCapabilitiesAsync(McpServerCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        foreach (var discovered in capabilities.Tools)
        {
            await using var read = db.CreateCommand();
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = "SELECT data_json FROM mcp_tools_cache WHERE server_id=$server AND tool_name=$tool";
            read.Parameters.AddWithValue("$server", capabilities.ServerId);
            read.Parameters.AddWithValue("$tool", discovered.Name);
            if (Json.Deserialize<RegisteredMcpTool>(await read.ExecuteScalarAsync(cancellationToken) as string) is { } existing)
            {
                discovered.Enabled = existing.Enabled;
                discovered.PermissionLevel = existing.PermissionLevel;
                discovered.ApprovalPolicy = existing.ApprovalPolicy;
                discovered.Tags = existing.Tags;
            }

            await using var tool = db.CreateCommand();
            tool.Transaction = (SqliteTransaction)transaction;
            tool.CommandText = """
                INSERT INTO mcp_tools_cache(server_id,tool_name,enabled,permission_level,approval_policy,discovered_at,data_json)
                VALUES($server,$tool,$enabled,$permission,$approval,$discovered,$json)
                ON CONFLICT(server_id,tool_name) DO UPDATE SET enabled=excluded.enabled,permission_level=excluded.permission_level,
                  approval_policy=excluded.approval_policy,discovered_at=excluded.discovered_at,data_json=excluded.data_json
                """;
            tool.Parameters.AddWithValue("$server", capabilities.ServerId);
            tool.Parameters.AddWithValue("$tool", discovered.Name);
            tool.Parameters.AddWithValue("$enabled", discovered.Enabled ? 1 : 0);
            tool.Parameters.AddWithValue("$permission", discovered.PermissionLevel.ToString());
            tool.Parameters.AddWithValue("$approval", discovered.ApprovalPolicy.ToString());
            tool.Parameters.AddWithValue("$discovered", discovered.DiscoveredAt.ToString("O"));
            tool.Parameters.AddWithValue("$json", Json.Serialize(discovered));
            await tool.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var remove = db.CreateCommand())
        {
            remove.Transaction = (SqliteTransaction)transaction;
            var names = capabilities.Tools.Select((_, index) => $"$name{index}").ToList();
            remove.CommandText = names.Count == 0
                ? "DELETE FROM mcp_tools_cache WHERE server_id=$server"
                : $"DELETE FROM mcp_tools_cache WHERE server_id=$server AND tool_name NOT IN ({string.Join(',', names)})";
            remove.Parameters.AddWithValue("$server", capabilities.ServerId);
            for (var index = 0; index < capabilities.Tools.Count; index++)
                remove.Parameters.AddWithValue(names[index], capabilities.Tools[index].Name);
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cache = db.CreateCommand())
        {
            cache.Transaction = (SqliteTransaction)transaction;
            cache.CommandText = """
                INSERT INTO mcp_capabilities_cache(server_id,expires_at,discovered_at,data_json)
                VALUES($server,$expires,$discovered,$json)
                ON CONFLICT(server_id) DO UPDATE SET expires_at=excluded.expires_at,discovered_at=excluded.discovered_at,data_json=excluded.data_json
                """;
            cache.Parameters.AddWithValue("$server", capabilities.ServerId);
            cache.Parameters.AddWithValue("$expires", capabilities.ExpiresAt.ToString("O"));
            cache.Parameters.AddWithValue("$discovered", capabilities.DiscoveredAt.ToString("O"));
            cache.Parameters.AddWithValue("$json", Json.Serialize(capabilities));
            await cache.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<McpServerCapabilities?> GetMcpCapabilitiesAsync(string serverId, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM mcp_capabilities_cache WHERE server_id=$server";
        command.Parameters.AddWithValue("$server", serverId);
        return Json.Deserialize<McpServerCapabilities>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<List<RegisteredMcpTool>> GetMcpToolsAsync(string? serverId = null, CancellationToken cancellationToken = default)
    {
        var items = new List<RegisteredMcpTool>();
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(serverId)
            ? "SELECT data_json FROM mcp_tools_cache ORDER BY server_id,tool_name COLLATE NOCASE"
            : "SELECT data_json FROM mcp_tools_cache WHERE server_id=$server ORDER BY tool_name COLLATE NOCASE";
        if (!string.IsNullOrWhiteSpace(serverId)) command.Parameters.AddWithValue("$server", serverId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<RegisteredMcpTool>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<RegisteredMcpTool?> GetMcpToolAsync(string serverId, string toolName, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM mcp_tools_cache WHERE server_id=$server AND tool_name=$tool";
        command.Parameters.AddWithValue("$server", serverId);
        command.Parameters.AddWithValue("$tool", toolName);
        return Json.Deserialize<RegisteredMcpTool>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task UpsertMcpToolAsync(RegisteredMcpTool tool, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO mcp_tools_cache(server_id,tool_name,enabled,permission_level,approval_policy,discovered_at,data_json)
            VALUES($server,$tool,$enabled,$permission,$approval,$discovered,$json)
            ON CONFLICT(server_id,tool_name) DO UPDATE SET enabled=excluded.enabled,permission_level=excluded.permission_level,
              approval_policy=excluded.approval_policy,discovered_at=excluded.discovered_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$server", tool.ServerId);
        command.Parameters.AddWithValue("$tool", tool.Name);
        command.Parameters.AddWithValue("$enabled", tool.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$permission", tool.PermissionLevel.ToString());
        command.Parameters.AddWithValue("$approval", tool.ApprovalPolicy.ToString());
        command.Parameters.AddWithValue("$discovered", tool.DiscoveredAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(tool));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddMcpPermissionAuditAsync(McpPermissionAuditRecord item, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        await using var snapshot = db.CreateCommand();
        snapshot.Transaction = (SqliteTransaction)transaction;
        snapshot.CommandText = """
            INSERT INTO mcp_permissions(id,server_id,scope_type,scope_key,updated_at,data_json)
            VALUES($id,$server,'tool',$tool,$updated,$json)
            ON CONFLICT(server_id,scope_type,scope_key) DO UPDATE SET
              id=excluded.id,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        snapshot.Parameters.AddWithValue("$id", item.Id);
        snapshot.Parameters.AddWithValue("$server", item.ServerId);
        snapshot.Parameters.AddWithValue("$tool", item.ToolName);
        snapshot.Parameters.AddWithValue("$updated", item.CreatedAt.ToString("O"));
        snapshot.Parameters.AddWithValue("$json", Json.Serialize(item));
        await snapshot.ExecuteNonQueryAsync(cancellationToken);

        await using var audit = db.CreateCommand();
        audit.Transaction = (SqliteTransaction)transaction;
        audit.CommandText = """
            INSERT INTO mcp_permission_events(id,server_id,scope_type,scope_key,created_at,data_json)
            VALUES($id,$server,'tool',$tool,$created,$json)
            """;
        audit.Parameters.AddWithValue("$id", item.Id);
        audit.Parameters.AddWithValue("$server", item.ServerId);
        audit.Parameters.AddWithValue("$tool", item.ToolName);
        audit.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        audit.Parameters.AddWithValue("$json", Json.Serialize(item));
        await audit.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<List<McpPermissionAuditRecord>> GetMcpPermissionAuditAsync(
        string serverId,
        string? toolName = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<McpPermissionAuditRecord>();
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(toolName)
            ? "SELECT data_json FROM mcp_permission_events WHERE server_id=$server AND scope_type='tool' ORDER BY created_at DESC"
            : "SELECT data_json FROM mcp_permission_events WHERE server_id=$server AND scope_type='tool' AND scope_key=$tool ORDER BY created_at DESC";
        command.Parameters.AddWithValue("$server", serverId);
        if (!string.IsNullOrWhiteSpace(toolName)) command.Parameters.AddWithValue("$tool", toolName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<McpPermissionAuditRecord>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<AgentTask?> GetMcpTaskByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM mcp_tasks WHERE idempotency_key=$key";
        command.Parameters.AddWithValue("$key", idempotencyKey);
        return Json.Deserialize<AgentTask>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<AgentTask?> GetMcpTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM mcp_tasks WHERE id=$id";
        command.Parameters.AddWithValue("$id", taskId);
        return Json.Deserialize<AgentTask>(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    public async Task<List<AgentTask>> GetMcpTasksAsync(string? customerId = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        var items = new List<AgentTask>();
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(customerId)
            ? "SELECT data_json FROM mcp_tasks ORDER BY created_at DESC LIMIT $limit"
            : "SELECT data_json FROM mcp_tasks WHERE customer_id=$customer ORDER BY created_at DESC LIMIT $limit";
        if (!string.IsNullOrWhiteSpace(customerId)) command.Parameters.AddWithValue("$customer", customerId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<AgentTask>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task UpsertMcpTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        task.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO mcp_tasks(id,task_type,server_id,tool_name,customer_id,status,idempotency_key,requirement_version,created_at,updated_at,data_json)
            VALUES($id,$type,$server,$tool,$customer,$status,$key,$version,$created,$updated,$json)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,server_id=excluded.server_id,tool_name=excluded.tool_name,
              requirement_version=excluded.requirement_version,updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$type", task.Type);
        command.Parameters.AddWithValue("$server", task.Target.ServerId);
        command.Parameters.AddWithValue("$tool", task.Target.ToolName);
        command.Parameters.AddWithValue("$customer", task.Source.CustomerId);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$key", task.IdempotencyKey);
        command.Parameters.AddWithValue("$version", task.RequirementVersionUsed);
        command.Parameters.AddWithValue("$created", task.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", task.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(task));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddMcpTaskEventAsync(McpTaskEvent item, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO mcp_task_events(id,task_id,server_id,tool_name,customer_id,event_type,status,error_code,created_at,data_json)
            VALUES($id,$task,$server,$tool,$customer,$type,$status,$error,$created,$json)
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$task", item.TaskId);
        command.Parameters.AddWithValue("$server", item.ServerId);
        command.Parameters.AddWithValue("$tool", item.ToolName);
        command.Parameters.AddWithValue("$customer", item.CustomerId);
        command.Parameters.AddWithValue("$type", item.EventType);
        command.Parameters.AddWithValue("$status", item.Status.ToString());
        command.Parameters.AddWithValue("$error", item.ErrorCode);
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(item));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<McpTaskEvent>> GetMcpTaskEventsAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var items = new List<McpTaskEvent>();
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT data_json FROM mcp_task_events WHERE task_id=$task ORDER BY created_at";
        command.Parameters.AddWithValue("$task", taskId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<McpTaskEvent>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }

    public async Task<List<AgentTask>> MarkMcpTasksInterruptedAfterRestartAsync(CancellationToken cancellationToken = default)
    {
        var tasks = (await GetMcpTasksAsync(limit: 1000, cancellationToken: cancellationToken))
            .Where(item => item.Status is McpTaskStatus.Running or McpTaskStatus.Waiting or McpTaskStatus.Queued)
            .ToList();
        foreach (var task in tasks)
        {
            task.Status = McpTaskStatus.Interrupted;
            task.Error = new AgentTaskError
            {
                Code = "PROCESS_RESTARTED",
                Message = "Relvyn restarted while the external Agent task was active. Review before retrying.",
                Retryable = true
            };
            await UpsertMcpTaskAsync(task, cancellationToken);
            await AddMcpTaskEventAsync(new McpTaskEvent
            {
                TaskId = task.Id,
                ServerId = task.Target.ServerId,
                ToolName = task.Target.ToolName,
                CustomerId = task.Source.CustomerId,
                EventType = "mcp.task.interrupted",
                Status = task.Status,
                ErrorCode = task.Error.Code,
                Detail = task.Error.Message
            }, cancellationToken);
        }
        return tasks;
    }

    public async Task UpsertMcpMappingAsync(McpTaskMapping mapping, CancellationToken cancellationToken = default)
    {
        mapping.UpdatedAt = DateTimeOffset.Now;
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO mcp_mappings(id,task_type,server_id,tool_name,enabled,updated_at,data_json)
            VALUES($id,$type,$server,$tool,$enabled,$updated,$json)
            ON CONFLICT(task_type,server_id,tool_name) DO UPDATE SET id=excluded.id,enabled=excluded.enabled,
              updated_at=excluded.updated_at,data_json=excluded.data_json
            """;
        command.Parameters.AddWithValue("$id", mapping.Id);
        command.Parameters.AddWithValue("$type", mapping.TaskType);
        command.Parameters.AddWithValue("$server", mapping.ServerId);
        command.Parameters.AddWithValue("$tool", mapping.ToolName);
        command.Parameters.AddWithValue("$enabled", mapping.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updated", mapping.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", Json.Serialize(mapping));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<McpTaskMapping>> GetMcpMappingsAsync(string? taskType = null, CancellationToken cancellationToken = default)
    {
        var items = new List<McpTaskMapping>();
        await using var db = Open();
        await db.OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(taskType)
            ? "SELECT data_json FROM mcp_mappings ORDER BY task_type,server_id,tool_name"
            : "SELECT data_json FROM mcp_mappings WHERE task_type=$type ORDER BY server_id,tool_name";
        if (!string.IsNullOrWhiteSpace(taskType)) command.Parameters.AddWithValue("$type", taskType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (Json.Deserialize<McpTaskMapping>(reader.GetString(0)) is { } item) items.Add(item);
        return items;
    }
}
