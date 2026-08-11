using System.Windows;
using System.Windows.Controls;
using System.IO;
using Microsoft.Win32;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Windows;

public partial class McpAgentGatewayWindow : Window
{
    private readonly AppServices _services;
    private List<ServerRow> _servers = [];
    private List<ToolRow> _tools = [];
    private List<TaskRow> _tasks = [];

    public McpAgentGatewayWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        PermissionBox.ItemsSource = Enum.GetValues<McpToolPermissionLevel>();
        ApprovalBox.ItemsSource = Enum.GetValues<McpApprovalPolicy>();
        Loaded += async (_, _) => await ReloadAsync();
        ToolList.SelectionChanged += ToolList_SelectionChanged;
    }

    private McpServerConfig? SelectedServer => (ServerList.SelectedItem as ServerRow)?.Model;
    private RegisteredMcpTool? SelectedTool => (ToolList.SelectedItem as ToolRow)?.Model;

    private async Task ReloadAsync(string? selectServerId = null)
    {
        var selected = selectServerId ?? SelectedServer?.Id;
        _servers = (await _services.McpAgents.GetServersAsync()).Select(server => new ServerRow(server)).ToList();
        ServerList.ItemsSource = _servers;
        ServerList.SelectedItem = _servers.FirstOrDefault(item => item.Model.Id == selected) ?? _servers.FirstOrDefault();
        _tasks = (await _services.McpAgents.GetTasksAsync(limit: 300)).Select(task => new TaskRow(task)).ToList();
        TaskList.ItemsSource = _tasks;
        RefreshServerHeader();
        await LoadToolsAsync();
    }

    private async Task LoadToolsAsync()
    {
        var server = SelectedServer;
        _tools = server is null
            ? []
            : (await _services.McpAgents.GetToolsAsync(server.Id)).Select(tool => new ToolRow(tool)).ToList();
        ToolList.ItemsSource = _tools;
    }

    private void RefreshServerHeader()
    {
        var server = SelectedServer;
        SelectedServerNameText.Text = server?.Name ?? "选择一个 Server";
        SelectedServerStatusText.Text = server is null
            ? "添加或选择左侧连接后查看工具。"
            : $"{server.ConnectionState} · {server.ToolCount} tools · protocol {(string.IsNullOrWhiteSpace(server.ProtocolVersion) ? "等待握手" : server.ProtocolVersion)}";
        TestButton.IsEnabled = server is not null;
        DisconnectButton.IsEnabled = server is not null && server.ConnectionState is McpConnectionState.Connected or McpConnectionState.Degraded;
        RefreshButton.IsEnabled = server is not null;
    }

    private async void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshServerHeader();
        await LoadToolsAsync();
    }

    private async void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var editor = new McpServerEditorWindow(new McpServerConfig()) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try
        {
            await _services.McpAgents.SaveServerAsync(editor.Server, editor.Credential);
            await ReloadAsync(editor.Server.Id);
            StatusText.Text = "Server 已保存。请先测试连接并检查发现的工具与权限。";
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        var clone = WAFlow.Core.Infrastructure.Json.Deserialize<McpServerConfig>(WAFlow.Core.Infrastructure.Json.Serialize(server)) ?? server;
        var editor = new McpServerEditorWindow(clone) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try
        {
            await _services.McpAgents.SaveServerAsync(editor.Server, editor.Credential);
            await ReloadAsync(editor.Server.Id);
            StatusText.Text = "Server 设置已保存。现有凭据在留空时保持不变。";
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        if (MessageBox.Show($"删除“{server.Name}”及其工具缓存和保存的凭据？历史任务审计会保留。", "删除 MCP Server", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            await _services.McpAgents.DeleteServerAsync(server.Id);
            await ReloadAsync();
            StatusText.Text = "Server、工具缓存和对应凭据已删除；历史任务审计已保留。";
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void ExportServer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        var picker = new SaveFileDialog
        {
            Title = "导出不含凭据的 MCP Connector",
            Filter = "Relvyn MCP Connector|*.relvyn-mcp.json|JSON|*.json",
            FileName = $"{string.Concat(server.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character))}.relvyn-mcp.json"
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllTextAsync(picker.FileName, await _services.McpAgents.ExportConnectorAsync(server.Id));
            StatusText.Text = "Connector 已导出；文件不包含 API Key、token、password、OAuth refresh token 或 secret reference。";
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void ImportServer_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "导入 Relvyn MCP Connector", Filter = "Relvyn MCP Connector|*.relvyn-mcp.json;*.json|所有文件|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var imported = await _services.McpAgents.ImportConnectorAsync(await File.ReadAllTextAsync(picker.FileName));
            await ReloadAsync(imported.Id);
            StatusText.Text = "Connector 已导入。凭据不会从文件导入，请编辑 Server 后单独填写并测试连接。";
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        TestButton.IsEnabled = false;
        StatusText.Text = "正在握手并发现 Tools / Resources / Prompts…";
        try
        {
            var result = await _services.McpAgents.TestConnectionAsync(server.Id);
            StatusText.Text = result.Success
                ? $"连接成功：{result.ToolCount} tools、{result.ResourceCount} resources、{result.PromptCount} prompts，{result.LatencyMs} ms。"
                : $"连接失败 [{result.ErrorCode}]：{result.Message}";
            await ReloadAsync(server.Id);
        }
        catch (Exception error) { ShowError(error); }
        finally { TestButton.IsEnabled = SelectedServer is not null; }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        try
        {
            await _services.McpAgents.DisconnectAsync(server.Id);
            await ReloadAsync(server.Id);
            StatusText.Text = "Server 已断开。Relvyn 本地功能和历史任务不受影响。";
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void RefreshTools_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在重新读取 Server 能力…";
        try
        {
            var capabilities = await _services.McpAgents.RefreshToolsAsync(server.Id);
            StatusText.Text = $"已刷新：发现 {capabilities.Tools.Count} 个工具。原有工具权限已保留。";
            await ReloadAsync(server.Id);
        }
        catch (Exception error) { ShowError(error); }
        finally { RefreshButton.IsEnabled = SelectedServer is not null; }
    }

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tool = SelectedTool;
        PermissionBox.SelectedItem = tool?.PermissionLevel;
        ApprovalBox.SelectedItem = tool?.ApprovalPolicy;
        ToolEnabledBox.IsChecked = tool?.Enabled == true;
    }

    private async void SaveToolPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTool is not { } tool) return;
        tool.PermissionLevel = PermissionBox.SelectedItem is McpToolPermissionLevel permission ? permission : tool.PermissionLevel;
        tool.ApprovalPolicy = ApprovalBox.SelectedItem is McpApprovalPolicy approval ? approval : tool.ApprovalPolicy;
        tool.Enabled = ToolEnabledBox.IsChecked == true;
        try
        {
            await _services.McpAgents.UpdateToolPolicyAsync(tool);
            await LoadToolsAsync();
            StatusText.Text = $"已保存 {tool.Name} 的权限。Product Sourcing 仍强制禁止客户渠道发送工具。";
        }
        catch (Exception error) { ShowError(error); }
    }

    private void TestTool_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server || SelectedTool is not { } tool) return;
        new McpToolTestWindow(_services, server, tool) { Owner = this }.ShowDialog();
        _ = ReloadAsync(server.Id);
    }

    private void ViewTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskRow row) return;
        new AgentTaskDetailsWindow(row.Model) { Owner = this }.ShowDialog();
    }

    private void ShowError(Exception error)
    {
        var code = error is McpGatewayException gateway ? $"[{gateway.Code}] " : "";
        StatusText.Text = code + error.Message;
        MessageBox.Show(code + error.Message, "MCP Agent Gateway", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record ServerRow(McpServerConfig Model)
    {
        public string Name => Model.Name;
        public string Status => Model.ConnectionState.ToString();
        public string TransportLabel => Model.Transport switch
        {
            McpTransportKind.Stdio => "stdio · local process",
            McpTransportKind.Sse => "SSE · legacy HTTP",
            _ => "Streamable HTTP"
        };
        public string Detail => Model.Transport == McpTransportKind.Stdio ? Model.Command : Model.Endpoint;
    }

    private sealed record ToolRow(RegisteredMcpTool Model)
    {
        public string Name => Model.Name;
        public string Description => Model.Description;
        public McpToolPermissionLevel PermissionLevel => Model.PermissionLevel;
        public McpApprovalPolicy ApprovalPolicy => Model.ApprovalPolicy;
        public string EnabledLabel => Model.Enabled ? "Enabled" : "Disabled";
    }

    private sealed record TaskRow(AgentTask Model)
    {
        public string CreatedLabel => Model.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");
        public string Title => Model.Title;
        public string TargetLabel => $"{Model.Target.ServerId} / {Model.Target.ToolName}";
        public string VersionLabel => $"v{Model.RequirementVersionUsed}";
        public string StatusLabel => Model.Status.ToString();
    }
}
