using System.Windows;
using System.Windows.Controls;
using WAFlow.Core.Domain;
using WAFlow.Core.Services;

namespace WAFlow.Desktop.Windows;

public partial class McpServerEditorWindow : Window
{
    public McpServerConfig Server { get; }
    public string Credential => CredentialBox.Password;

    public McpServerEditorWindow(McpServerConfig server)
    {
        InitializeComponent();
        Server = server;
        TransportBox.ItemsSource = Enum.GetValues<McpTransportKind>();
        AuthTypeBox.ItemsSource = Enum.GetValues<McpAuthType>();
        foreach (var box in ContextPermissionBoxes()) box.ItemsSource = Enum.GetValues<McpContextPermission>();
        NameBox.Text = server.Name;
        DescriptionBox.Text = server.Description;
        EnabledBox.IsChecked = server.Enabled;
        AutoConnectBox.IsChecked = server.AutoConnect;
        TransportBox.SelectedItem = server.Transport;
        EndpointBox.Text = server.Endpoint;
        CommandBox.Text = server.Command;
        ArgumentsBox.Text = string.Join(Environment.NewLine, server.Args);
        WorkingDirectoryBox.Text = server.WorkingDirectory;
        AuthTypeBox.SelectedItem = server.AuthType;
        ApiKeyHeaderBox.Text = server.ApiKeyHeader;
        SecretEnvironmentBox.Text = server.SecretEnvironmentVariable;
        TimeoutBox.Text = Math.Max(1, server.TimeoutMs / 1000).ToString();
        CustomerPermissionBox.SelectedItem = Permission(McpContextKeys.CustomerBasicInfo, McpContextPermission.Ask);
        ProductPermissionBox.SelectedItem = Permission(McpContextKeys.ProductRequirement, McpContextPermission.Allow);
        ConversationPermissionBox.SelectedItem = Permission(McpContextKeys.CurrentConversation, McpContextPermission.Ask);
        HistoryPermissionBox.SelectedItem = Permission(McpContextKeys.FullConversationHistory, McpContextPermission.Deny);
        AttachmentsPermissionBox.SelectedItem = Permission(McpContextKeys.Attachments, McpContextPermission.Ask);
        KnowledgePermissionBox.SelectedItem = Permission(McpContextKeys.KnowledgeBase, McpContextPermission.Deny);
        OpportunityPermissionBox.SelectedItem = Permission(McpContextKeys.Opportunity, McpContextPermission.Ask);
        NotesPermissionBox.SelectedItem = Permission(McpContextKeys.InternalNotes, McpContextPermission.Deny);
        Loaded += (_, _) => RefreshPanels();
    }

    private void TransportBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPanels();
    private void AuthTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPanels();

    private void RefreshPanels()
    {
        if (!IsInitialized) return;
        var transport = TransportBox.SelectedItem is McpTransportKind selected ? selected : McpTransportKind.StreamableHttp;
        var stdio = transport == McpTransportKind.Stdio;
        HttpPanel.Visibility = stdio ? Visibility.Collapsed : Visibility.Visible;
        StdioPanel.Visibility = stdio ? Visibility.Visible : Visibility.Collapsed;
        SecretEnvironmentPanel.Visibility = stdio ? Visibility.Visible : Visibility.Collapsed;
        TransportTitleText.Text = stdio ? "Local stdio process" : "HTTP Endpoint";
        TransportHelpText.Text = stdio
            ? "Relvyn 直接启动可执行文件，不经过 shell；参数逐行传递，环境变量按最小白名单构建。"
            : "远程 Server 必须使用 HTTPS；HTTP 只允许本机 loopback。Streamable HTTP 是推荐模式，SSE 仅用于旧 Server。";
        var auth = AuthTypeBox.SelectedItem is McpAuthType selectedAuth ? selectedAuth : McpAuthType.None;
        CredentialBox.IsEnabled = auth != McpAuthType.None;
        CredentialLabelText.Text = auth switch
        {
            McpAuthType.Bearer => "Bearer token",
            McpAuthType.ApiKey => "API key",
            McpAuthType.OAuth => "OAuth access token",
            _ => "Credential（无需填写）"
        };
        ApiKeyHeaderPanel.Visibility = !stdio && auth == McpAuthType.ApiKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TimeoutBox.Text.Trim(), out var seconds) || seconds is < 1 or > 1800)
        {
            MessageBox.Show("单次超时必须是 1–1800 秒之间的整数。", "MCP Server", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Server.Name = NameBox.Text.Trim();
        Server.Description = DescriptionBox.Text.Trim();
        Server.Enabled = EnabledBox.IsChecked == true;
        Server.AutoConnect = AutoConnectBox.IsChecked == true;
        Server.Transport = TransportBox.SelectedItem is McpTransportKind transport ? transport : McpTransportKind.StreamableHttp;
        Server.Endpoint = EndpointBox.Text.Trim();
        Server.Command = CommandBox.Text.Trim();
        Server.Args = ArgumentsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Server.WorkingDirectory = WorkingDirectoryBox.Text.Trim();
        Server.AuthType = AuthTypeBox.SelectedItem is McpAuthType auth ? auth : McpAuthType.None;
        Server.ApiKeyHeader = string.IsNullOrWhiteSpace(ApiKeyHeaderBox.Text) ? "X-API-Key" : ApiKeyHeaderBox.Text.Trim();
        Server.SecretEnvironmentVariable = string.IsNullOrWhiteSpace(SecretEnvironmentBox.Text) ? "MCP_API_KEY" : SecretEnvironmentBox.Text.Trim();
        Server.TimeoutMs = seconds * 1000;
        Server.ContextPermissions = new Dictionary<string, McpContextPermission>(StringComparer.OrdinalIgnoreCase)
        {
            [McpContextKeys.CustomerBasicInfo] = SelectedPermission(CustomerPermissionBox),
            [McpContextKeys.ProductRequirement] = SelectedPermission(ProductPermissionBox),
            [McpContextKeys.CurrentConversation] = SelectedPermission(ConversationPermissionBox),
            [McpContextKeys.FullConversationHistory] = SelectedPermission(HistoryPermissionBox),
            [McpContextKeys.Attachments] = SelectedPermission(AttachmentsPermissionBox),
            [McpContextKeys.KnowledgeBase] = SelectedPermission(KnowledgePermissionBox),
            [McpContextKeys.Opportunity] = SelectedPermission(OpportunityPermissionBox),
            [McpContextKeys.InternalNotes] = SelectedPermission(NotesPermissionBox)
        };
        try
        {
            McpConnectionManager.ValidateServer(Server);
            DialogResult = true;
        }
        catch (McpGatewayException error)
        {
            MessageBox.Show($"[{error.Code}] {error.Message}", "MCP Server", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private McpContextPermission Permission(string key, McpContextPermission fallback) =>
        Server.ContextPermissions.GetValueOrDefault(key, fallback);

    private static McpContextPermission SelectedPermission(ComboBox box) =>
        box.SelectedItem is McpContextPermission permission ? permission : McpContextPermission.Deny;

    private IEnumerable<ComboBox> ContextPermissionBoxes()
    {
        yield return CustomerPermissionBox;
        yield return ProductPermissionBox;
        yield return ConversationPermissionBox;
        yield return HistoryPermissionBox;
        yield return AttachmentsPermissionBox;
        yield return KnowledgePermissionBox;
        yield return OpportunityPermissionBox;
        yield return NotesPermissionBox;
    }
}
