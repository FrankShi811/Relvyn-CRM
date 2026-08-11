using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using WAFlow.Core;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Windows;

public partial class McpToolTestWindow : Window
{
    private readonly AppServices _services;
    private readonly McpServerConfig _server;
    private readonly RegisteredMcpTool _tool;
    private readonly List<SchemaField> _fields = [];

    public McpToolTestWindow(AppServices services, McpServerConfig server, RegisteredMcpTool tool)
    {
        InitializeComponent();
        _services = services;
        _server = server;
        _tool = tool;
        ToolTitleText.Text = tool.Name;
        ToolMetaText.Text = $"{server.Name} · {tool.PermissionLevel} · {tool.ApprovalPolicy}\n{tool.Description}";
        ParseSchema();
        FieldItems.ItemsSource = _fields;
        NoFieldsText.Visibility = _fields.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArgumentsBox.Text = "{}";
        StatusText.Text = "输入仅用于本次测试；每次执行都需要人工确认。";
    }

    private void ParseSchema()
    {
        try
        {
            using var document = JsonDocument.Parse(_tool.InputSchemaJson);
            var required = document.RootElement.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(item => item.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
                : [];
            if (!document.RootElement.TryGetProperty("properties", out var properties)) return;
            foreach (var property in properties.EnumerateObject())
            {
                var type = property.Value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "any" : "any";
                _fields.Add(new SchemaField(property.Name, type, required.Contains(property.Name)));
            }
        }
        catch (JsonException)
        {
            StatusText.Text = "Tool 发布的 JSON Schema 无法解析；执行会被 Gateway 拒绝。";
        }
    }

    private void BuildJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var payload = new JsonObject();
            foreach (var field in _fields)
            {
                var text = field.Value.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (field.Required) payload[field.Name] = null;
                    continue;
                }
                payload[field.Name] = field.Type switch
                {
                    "number" when double.TryParse(text, out var number) => JsonValue.Create(number),
                    "integer" when long.TryParse(text, out var integer) => JsonValue.Create(integer),
                    "boolean" when bool.TryParse(text, out var boolean) => JsonValue.Create(boolean),
                    "object" or "array" => JsonNode.Parse(text),
                    _ => JsonValue.Create(text)
                };
            }
            ArgumentsBox.Text = payload.ToJsonString(WAFlow.Core.Infrastructure.Json.Options);
            ConfirmBox.IsChecked = false;
        }
        catch (Exception error)
        {
            MessageBox.Show($"无法生成 JSON：{error.Message}", "Tool Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Confirm_Changed(object sender, RoutedEventArgs e) => RunButton.IsEnabled = ConfirmBox.IsChecked == true;

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        StatusText.Text = "正在执行已人工批准的 Tool 测试…";
        try
        {
            var result = await _services.McpAgents.TestToolAsync(
                _server.Id,
                _tool.Name,
                ArgumentsBox.Text,
                Environment.UserName);
            ResultBox.Text = result.RawJson;
            ExecutionTimeText.Text = $"{result.ExecutionTimeMs} ms";
            StatusText.Text = result.IsError ? "Tool 返回 error result。" : "执行完成。";
        }
        catch (Exception error)
        {
            ResultBox.Text = error.Message;
            StatusText.Text = error is WAFlow.Core.Services.McpGatewayException gateway ? $"[{gateway.Code}] {gateway.Message}" : error.Message;
        }
        finally
        {
            ConfirmBox.IsChecked = false;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class SchemaField(string name, string type, bool required) : INotifyPropertyChanged
    {
        private string _value = "";
        public string Name { get; } = name;
        public string Type { get; } = type;
        public bool Required { get; } = required;
        public string Label => Required ? $"{Name} *" : Name;
        public string TypeLabel => Type;
        public string Value { get => _value; set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
