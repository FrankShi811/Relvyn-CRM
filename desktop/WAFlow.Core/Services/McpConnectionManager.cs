using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class McpGatewayException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }

    public McpGatewayException(string code, string message, bool retryable = false, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
    }
}

public sealed class McpConnectionManager : IAsyncDisposable
{
    private sealed record Session(McpClient Client, DateTimeOffset ConnectedAt);

    private readonly LocalRepository _repository;
    private readonly Func<string, ISecretStore> _secretStoreFactory;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public McpConnectionManager(LocalRepository repository, Func<string, ISecretStore>? secretStoreFactory = null)
    {
        _repository = repository;
        _secretStoreFactory = secretStoreFactory ?? (target => new WindowsCredentialStore(target));
    }

    public bool IsConnected(string serverId) => _sessions.ContainsKey(serverId);

    public async Task<McpServerCapabilities> ConnectAndDiscoverAsync(
        McpServerConfig server,
        int cacheTtlMinutes = 15,
        bool forceReconnect = false,
        CancellationToken cancellationToken = default)
    {
        if (!server.Enabled)
            throw new McpGatewayException("SERVER_DISABLED", "This MCP server is disabled.");
        ValidateServer(server);

        if (forceReconnect) await DisconnectAsync(server.Id);
        if (!_sessions.TryGetValue(server.Id, out var session))
        {
            server.ConnectionState = McpConnectionState.Connecting;
            await _repository.UpsertMcpServerAsync(server, cancellationToken);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(server.NormalizedTimeoutMs);
                var transport = CreateTransport(server);
                var options = new McpClientOptions
                {
                    InitializationTimeout = TimeSpan.FromMilliseconds(server.NormalizedTimeoutMs)
                };
                var client = await McpClient.CreateAsync(transport, options, NullLoggerFactory.Instance, timeout.Token);
                session = new Session(client, DateTimeOffset.Now);
                _sessions[server.Id] = session;
                server.ConnectionState = McpConnectionState.Connected;
                server.LastLatencyMs = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
                server.LastConnectedAt = DateTimeOffset.Now;
                server.LastSuccessAt = DateTimeOffset.Now;
                server.LastErrorCode = "";
                server.LastErrorMessage = "";
                server.ProtocolVersion = client.NegotiatedProtocolVersion ?? "";
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                await RecordConnectionFailureAsync(server, "CONNECTION_TIMEOUT", "The MCP server did not complete its handshake in time.", cancellationToken);
                throw new McpGatewayException("CONNECTION_TIMEOUT", server.LastErrorMessage, true, error);
            }
            catch (Exception error)
            {
                var mapped = MapException(error, "CONNECTION_FAILED", "Unable to connect to the MCP server.");
                await RecordConnectionFailureAsync(server, mapped.Code, mapped.Message, cancellationToken);
                throw mapped;
            }
            finally
            {
                stopwatch.Stop();
            }
        }

        try
        {
            var capabilities = await DiscoverAsync(server.Id, session.Client, cacheTtlMinutes, cancellationToken);
            server.ToolCount = capabilities.Tools.Count;
            server.ConnectionState = McpConnectionState.Connected;
            server.LastSuccessAt = DateTimeOffset.Now;
            server.ProtocolVersion = session.Client.NegotiatedProtocolVersion ?? server.ProtocolVersion;
            await _repository.UpsertMcpServerAsync(server, cancellationToken);
            return capabilities;
        }
        catch (Exception error)
        {
            var mapped = MapException(error, "DISCOVERY_FAILED", "Connected, but tool discovery failed.");
            server.ConnectionState = McpConnectionState.Degraded;
            server.LastErrorCode = mapped.Code;
            server.LastErrorMessage = mapped.Message;
            server.LastErrorAt = DateTimeOffset.Now;
            await _repository.UpsertMcpServerAsync(server, cancellationToken);
            throw mapped;
        }
    }

    public async Task<McpToolInvocationResult> InvokeAsync(
        McpToolInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(request.ServerId, out var session))
            throw new McpGatewayException("SERVER_DISCONNECTED", "Connect the MCP server before running this task.", true);
        Dictionary<string, object?>? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(request.ArgumentsJson, Json.Options) ?? [];
        }
        catch (JsonException error)
        {
            throw new McpGatewayException("INVALID_ARGUMENTS", "The mapped MCP tool arguments are not valid JSON.", false, error);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Clamp(request.TimeoutMs, 1_000, 30 * 60 * 1_000));
            var result = await session.Client.CallToolAsync(request.ToolName, arguments, null, null, timeout.Token);
            return new McpToolInvocationResult
            {
                RawJson = Json.Serialize(result),
                ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
                IsError = result.IsError == true
            };
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpGatewayException("TOOL_TIMEOUT", "The external Agent tool exceeded its time limit.", true, error);
        }
        catch (Exception error)
        {
            throw MapException(error, "TOOL_CALL_FAILED", "The external Agent tool call failed.");
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    public async Task DisconnectAsync(string serverId)
    {
        if (_sessions.TryRemove(serverId, out var session))
            await session.Client.DisposeAsync();
        var server = await _repository.GetMcpServerAsync(serverId);
        if (server is null) return;
        server.ConnectionState = server.Enabled ? McpConnectionState.Disconnected : McpConnectionState.Disabled;
        await _repository.UpsertMcpServerAsync(server);
    }

    private async Task<McpServerCapabilities> DiscoverAsync(
        string serverId,
        McpClient client,
        int cacheTtlMinutes,
        CancellationToken cancellationToken)
    {
        var capabilities = new McpServerCapabilities
        {
            ServerId = serverId,
            ProtocolVersion = client.NegotiatedProtocolVersion ?? "",
            DiscoveredAt = DateTimeOffset.Now,
            ExpiresAt = DateTimeOffset.Now.AddMinutes(Math.Clamp(cacheTtlMinutes, 1, 24 * 60))
        };

        if (client.ServerCapabilities.Tools is not null)
        {
            capabilities.SupportsTools = true;
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            capabilities.Tools = tools.Select(item => new RegisteredMcpTool
            {
                ServerId = serverId,
                Name = item.Name,
                Description = item.Description ?? "",
                InputSchemaJson = item.JsonSchema.GetRawText(),
                PermissionLevel = InferPermission(item.Name),
                ApprovalPolicy = McpApprovalPolicy.AskEveryTime,
                DiscoveredAt = capabilities.DiscoveredAt
            }).ToList();
        }

        if (client.ServerCapabilities.Resources is not null)
        {
            capabilities.SupportsResources = true;
            try
            {
                var resources = await client.ListResourcesAsync(cancellationToken: cancellationToken);
                capabilities.Resources = resources.Select(item => new McpNamedCapability
                {
                    Name = item.Name,
                    Description = item.Description ?? "",
                    Uri = item.Uri
                }).ToList();
            }
            catch (Exception) { capabilities.SupportsResources = false; }
        }

        if (client.ServerCapabilities.Prompts is not null)
        {
            capabilities.SupportsPrompts = true;
            try
            {
                var prompts = await client.ListPromptsAsync(cancellationToken: cancellationToken);
                capabilities.Prompts = prompts.Select(item => new McpNamedCapability
                {
                    Name = item.Name,
                    Description = item.Description ?? ""
                }).ToList();
            }
            catch (Exception) { capabilities.SupportsPrompts = false; }
        }

        await _repository.SaveMcpCapabilitiesAsync(capabilities, cancellationToken);
        return capabilities;
    }

    private IClientTransport CreateTransport(McpServerConfig server)
    {
        var secret = server.AuthType == McpAuthType.None ? null : _secretStoreFactory(server.SecretTarget).Read();
        if (server.AuthType != McpAuthType.None && string.IsNullOrWhiteSpace(secret))
            throw new McpGatewayException("AUTH_SECRET_MISSING", "Authentication is enabled, but no credential is stored for this MCP server.");
        if (server.Transport == McpTransportKind.Stdio)
        {
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            if (!string.IsNullOrWhiteSpace(secret))
                environment[server.SecretEnvironmentVariable] = secret;
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command,
                Arguments = server.Args,
                WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? null : server.WorkingDirectory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            });
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (server.AuthType is McpAuthType.Bearer or McpAuthType.OAuth) headers["Authorization"] = $"Bearer {secret}";
        if (server.AuthType == McpAuthType.ApiKey) headers[server.ApiKeyHeader] = secret!;
        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Endpoint, UriKind.Absolute),
            TransportMode = server.Transport == McpTransportKind.Sse
                ? HttpTransportMode.Sse
                : HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromMilliseconds(server.NormalizedTimeoutMs),
            AdditionalHeaders = headers
        });
    }

    public static void ValidateServer(McpServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.Name))
            throw new McpGatewayException("SERVER_NAME_REQUIRED", "Enter a name for the MCP server.");
        if (server.Transport == McpTransportKind.Stdio)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                throw new McpGatewayException("STDIO_COMMAND_REQUIRED", "Enter the executable used to start this MCP server.");
            if (server.Command.IndexOfAny(['&', '|', '<', '>', '\r', '\n']) >= 0)
                throw new McpGatewayException("UNSAFE_STDIO_COMMAND", "Shell operators are not allowed in an MCP stdio command. Keep arguments in the separate arguments list.");
            if (!string.IsNullOrWhiteSpace(server.WorkingDirectory) && !Directory.Exists(server.WorkingDirectory))
                throw new McpGatewayException("WORKING_DIRECTORY_NOT_FOUND", "The MCP server working directory does not exist.");
            if (server.AuthType != McpAuthType.None
                && (string.IsNullOrWhiteSpace(server.SecretEnvironmentVariable)
                    || server.SecretEnvironmentVariable.Any(character => !(char.IsLetterOrDigit(character) || character == '_'))))
                throw new McpGatewayException("INVALID_SECRET_ENVIRONMENT_VARIABLE", "The credential environment-variable name is invalid.");
            return;
        }

        if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
            throw new McpGatewayException("INVALID_ENDPOINT", "Enter an absolute HTTP or HTTPS MCP endpoint.");
        if (endpoint.Scheme == "http" && !endpoint.IsLoopback)
            throw new McpGatewayException("INSECURE_ENDPOINT", "Remote MCP servers must use HTTPS. HTTP is allowed only for loopback development servers.");
    }

    private async Task RecordConnectionFailureAsync(McpServerConfig server, string code, string message, CancellationToken cancellationToken)
    {
        server.ConnectionState = McpConnectionState.Error;
        server.LastErrorCode = code;
        server.LastErrorMessage = message;
        server.LastErrorAt = DateTimeOffset.Now;
        await _repository.UpsertMcpServerAsync(server, cancellationToken);
    }

    private static McpGatewayException MapException(Exception error, string fallbackCode, string fallbackMessage)
    {
        if (error is McpGatewayException gateway) return gateway;
        if (error is UnauthorizedAccessException)
            return new McpGatewayException("AUTHORIZATION_FAILED", "The MCP server rejected authentication or access.", false, error);
        if (error is HttpRequestException http)
            return new McpGatewayException("NETWORK_ERROR", $"The MCP server could not be reached: {http.Message}", true, error);
        if (error is IOException io)
            return new McpGatewayException("TRANSPORT_CLOSED", $"The MCP transport closed unexpectedly: {io.Message}", true, error);
        return new McpGatewayException(fallbackCode, $"{fallbackMessage} {error.Message}".Trim(), false, error);
    }

    private static McpToolPermissionLevel InferPermission(string toolName)
    {
        var name = toolName.ToLowerInvariant();
        if (name.Contains("delete") || name.Contains("payment") || name.Contains("purchase")) return McpToolPermissionLevel.HighRisk;
        if (name.Contains("send") || name.Contains("create") || name.Contains("update") || name.Contains("write")) return McpToolPermissionLevel.ExternalAction;
        return McpToolPermissionLevel.ReadOnly;
    }

    public async ValueTask DisposeAsync()
    {
        var sessions = _sessions.ToArray();
        _sessions.Clear();
        foreach (var pair in sessions)
            await pair.Value.Client.DisposeAsync();
    }
}
