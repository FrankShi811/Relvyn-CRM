using System.IO;
using System.Windows;
using Microsoft.Win32;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Desktop.Windows;

public partial class ProductSourcingReviewWindow : Window
{
    private readonly AppServices _services;
    private readonly SourcingRequest _sourceRequirement;
    private readonly AgentTaskSource _source;
    private readonly string _customerName;
    private readonly AgentTask? _refinementParent;
    private readonly List<AgentAttachment> _attachments = [];
    private bool _loaded;

    public AgentTask? CompletedTask { get; private set; }

    public ProductSourcingReviewWindow(
        AppServices services,
        SourcingRequest requirement,
        AgentTaskSource source,
        string customerName,
        AgentTask? refinementParent = null)
    {
        InitializeComponent();
        _services = services;
        _sourceRequirement = requirement;
        _source = source;
        _customerName = string.IsNullOrWhiteSpace(customerName) ? "Unnamed customer" : customerName;
        _refinementParent = refinementParent;
        CustomerText.Text = $"Customer: {_customerName}";
        ProductBox.Text = Value(SourcingFieldKey.ProductImage);
        QuantityBox.Text = Value(SourcingFieldKey.Quantity);
        TargetPriceBox.Text = Value(SourcingFieldKey.TargetPrice);
        DestinationBox.Text = Value(SourcingFieldKey.Destination);
        LogisticsBox.Text = Value(SourcingFieldKey.ShippingPreference);
        Loaded += async (_, _) =>
        {
            _loaded = true;
            await LoadAgentsAsync();
            RefreshReview();
        };
    }

    private AgentChoice? SelectedAgent => AgentBox.SelectedItem as AgentChoice;

    private string Value(SourcingFieldKey key) =>
        _sourceRequirement.Fields.TryGetValue(key, out var field) ? field.Value : "";

    private async Task LoadAgentsAsync()
    {
        var agents = (await _services.McpAgents.GetAvailableAgentsAsync())
            .Select(item => new AgentChoice(item.Server, item.Tool)).ToList();
        AgentBox.ItemsSource = agents;
        AgentBox.SelectedItem = agents.Count == 1 ? agents[0] : null;
        AgentStatusText.Text = agents.Count switch
        {
            0 => "没有已连接且允许使用的 Tool。请先管理连接、测试 Server 并检查 Tool 权限。",
            1 => "已预选唯一可用 Agent；仍需核对并确认。",
            _ => $"{agents.Count} 个可用 Tool。请选择最适合当前采购任务的 Agent。"
        };
    }

    private SourcingRequest BuildEffectiveRequirement()
    {
        var clone = Json.Deserialize<SourcingRequest>(Json.Serialize(_sourceRequirement)) ?? new SourcingRequest
        {
            CustomerId = _sourceRequirement.CustomerId,
            Version = _sourceRequirement.Version
        };
        clone.Fields ??= [];
        Put(clone, SourcingFieldKey.ProductImage, ProductBox.Text);
        Put(clone, SourcingFieldKey.Quantity, QuantityBox.Text);
        Put(clone, SourcingFieldKey.TargetPrice, TargetPriceBox.Text);
        Put(clone, SourcingFieldKey.Destination, DestinationBox.Text);
        Put(clone, SourcingFieldKey.ShippingPreference, LogisticsBox.Text);
        return clone;
    }

    private static void Put(SourcingRequest request, SourcingFieldKey key, string text)
    {
        var value = text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            request.Fields.Remove(key);
            return;
        }
        var field = new SourcingFieldValue
        {
            Field = key,
            Value = value,
            NormalizedValue = value.ToLowerInvariant(),
            HumanConfirmed = true,
            EvidenceQuote = "Task Override entered during human review",
            SourceMessageId = "task_override",
            ObservedAt = DateTimeOffset.Now
        };
        field.IsStructurallyValid = WAFlow.Core.Services.SourcingRequestService.Validate(field);
        request.Fields[key] = field;
    }

    private void RefreshReview()
    {
        if (!_loaded) return;
        var requirement = BuildEffectiveRequirement();
        var readiness = requirement.Readiness;
        ReadinessText.Text = readiness.Readiness switch
        {
            SourcingReadinessLevel.HighConfidence => "5 / 5 · Complete",
            SourcingReadinessLevel.AgentAvailable => $"{readiness.CollectedCount} / 5 · Ready for Agent",
            _ => $"{readiness.CollectedCount} / 5 · Not ready"
        };
        ReadinessDetailText.Text = !readiness.ProductIdentifiable
            ? $"{readiness.CollectedCount} elements collected. Product information is still required before sourcing."
            : readiness.CanUseAgent
                ? $"Missing: {string.Join(", ", readiness.MissingElements.DefaultIfEmpty("none"))}. Partial requirements are explicitly included in the MCP payload."
                : "Continue collecting information. At least 3 elements are required.";
        var choice = SelectedAgent;
        AgentStatusText.Text = choice is null
            ? "Select a connected Server / Tool."
            : $"{choice.Server.Name} · {choice.Tool.Name} · {choice.Tool.PermissionLevel} · {choice.Tool.ApprovalPolicy}";
        var shared = ShareCustomerBox.IsChecked == true ? new[] { McpContextKeys.CustomerBasicInfo, McpContextKeys.ProductRequirement } : new[] { McpContextKeys.ProductRequirement };
        PreviewBox.Text = Json.Serialize(new
        {
            taskType = "product_sourcing",
            target = choice is null ? null : new { server = choice.Server.Name, tool = choice.Tool.Name },
            customer = ShareCustomerBox.IsChecked == true ? new { name = _customerName, id = _source.CustomerId, conversationId = _source.ConversationId } : null,
            requirement = SourcingReadinessPolicy.ToProductRequirement(requirement),
            requirementCompleteness = SourcingReadinessPolicy.ToCompleteness(readiness),
            additionalInstructions = InstructionsBox.Text.Trim(),
            sharedContext = shared,
            attachments = _attachments.Select(item => new { item.Name, item.MimeType, item.SizeBytes, item.ExplicitlyShared }),
            taskOverride = true,
            requirementVersionUsed = requirement.Version
        });
        SendButton.IsEnabled = choice is not null && readiness.CanUseAgent && ConfirmBox.IsChecked == true;
    }

    private void ReviewInput_Changed(object sender, RoutedEventArgs e) => RefreshReview();
    private void ConfirmBox_Changed(object sender, RoutedEventArgs e) => RefreshReview();

    private async void ManageAgents_Click(object sender, RoutedEventArgs e)
    {
        new McpAgentGatewayWindow(_services) { Owner = this }.ShowDialog();
        await LoadAgentsAsync();
        RefreshReview();
    }

    private void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "选择明确同意共享给外部 Agent 的附件",
            Multiselect = true,
            Filter = "图片与文档|*.png;*.jpg;*.jpeg;*.webp;*.pdf;*.txt;*.csv;*.xlsx|所有文件|*.*"
        };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FileNames)
        {
            if (_attachments.Any(item => item.LocalPath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            var info = new FileInfo(path);
            _attachments.Add(new AgentAttachment
            {
                Name = info.Name,
                LocalPath = info.FullName,
                SizeBytes = info.Length,
                MimeType = MimeType(info.Extension),
                ExplicitlyShared = true
            });
        }
        AttachmentText.Text = _attachments.Count == 0
            ? "未选择附件。"
            : $"将明确共享 {_attachments.Count} 个附件：{string.Join("、", _attachments.Select(item => item.Name))}";
        ConfirmBox.IsChecked = false;
        RefreshReview();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAgent is not { } choice) return;
        SendButton.IsEnabled = false;
        StatusText.Text = "正在创建已批准的 AgentTask 并调用所选 Tool…";
        try
        {
            var effective = BuildEffectiveRequirement();
            var shared = ShareCustomerBox.IsChecked == true
                ? new List<string> { McpContextKeys.CustomerBasicInfo, McpContextKeys.ProductRequirement }
                : new List<string> { McpContextKeys.ProductRequirement };
            var context = ShareCustomerBox.IsChecked == true
                ? Json.Serialize(new { customerName = _customerName, customerId = _source.CustomerId, conversationId = _source.ConversationId })
                : "{}";
            var draft = new ProductSourcingTaskDraft
            {
                Source = _source,
                Requirement = effective,
                Target = new AgentTaskTarget { ServerId = choice.Server.Id, ToolName = choice.Tool.Name },
                CustomerName = _customerName,
                CustomerContextJson = context,
                AdditionalInstructions = InstructionsBox.Text.Trim(),
                TaskOverrideJson = Json.Serialize(new
                {
                    taskOverride = new
                    {
                        enteredBy = Environment.UserName,
                        source = "human_review",
                        doesNotUpdateCustomerBrain = true
                    }
                }),
                SharedContextKeys = shared,
                Attachments = _attachments,
                ParentTaskId = _refinementParent?.Id ?? ""
            };
            if (_refinementParent is null)
            {
                var task = await _services.McpAgents.BuildProductSourcingTaskAsync(draft);
                CompletedTask = await _services.McpAgents.SubmitApprovedAsync(task, Environment.UserName);
            }
            else
            {
                CompletedTask = await _services.McpAgents.RefineProductSourcingAsync(
                    _refinementParent,
                    draft,
                    Environment.UserName);
            }
            if (CompletedTask.Status is McpTaskStatus.Completed or McpTaskStatus.NeedsInformation)
            {
                DialogResult = true;
                return;
            }
            StatusText.Text = $"Task {CompletedTask.Status}: {CompletedTask.Error?.Message}";
            MessageBox.Show(StatusText.Text, "Sourcing Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception error)
        {
            var code = error is WAFlow.Core.Services.McpGatewayException gateway ? $"[{gateway.Code}] " : "";
            StatusText.Text = code + error.Message;
            MessageBox.Show(StatusText.Text, "Sourcing Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshReview();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string MimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".csv" => "text/csv",
        ".txt" => "text/plain",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream"
    };

    private sealed record AgentChoice(McpServerConfig Server, RegisteredMcpTool Tool)
    {
        public string DisplayName => $"{Server.Name} · {Tool.Name}";
    }
}
