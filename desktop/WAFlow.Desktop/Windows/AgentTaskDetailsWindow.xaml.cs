using System.Windows;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Desktop.Windows;

public partial class AgentTaskDetailsWindow : Window
{
    public AgentTaskDetailsWindow(AgentTask task)
    {
        InitializeComponent();
        TitleText.Text = task.Title;
        MetaText.Text = $"{task.Target.ServerId} / {task.Target.ToolName} · {task.CreatedAt.LocalDateTime:g}";
        StatusText.Text = task.Status.ToString();
        SummaryText.Text = task.Result?.Summary ?? task.Error?.Message ?? "尚未产生结果。";
        var metadata = task.Result?.Metadata;
        VersionText.Text = metadata is null
            ? $"Requirement v{task.RequirementVersionUsed}"
            : $"Based on requirement v{metadata.RequirementVersionUsed} · {metadata.RequirementCollectedCount}/5 collected at execution · Missing at search time: {Join(metadata.MissingAtExecution)}";
        ApprovalText.Text = string.IsNullOrWhiteSpace(task.ApprovedBy)
            ? "Not approved"
            : $"Approved by {task.ApprovedBy} at {task.ApprovedAt?.LocalDateTime:g}";
        ContextText.Text = $"Shared context: {Join(task.SharedContextKeys)} · Explicit attachments: {task.Attachments.Count(item => item.ExplicitlyShared)} · Task Override: {(task.TaskOverrideJson == "{}" ? "none" : "present")}";
        var missing = task.Result?.ProductSourcing?.MissingInformation ?? [];
        var assumptions = task.Result?.ProductSourcing?.Assumptions ?? [];
        MissingText.Text = missing.Count == 0 && assumptions.Count == 0
            ? "No missing information or assumptions were reported."
            : $"Missing information: {Join(missing)}\nAssumptions: {Join(assumptions)}";
        MissingCard.Visibility = missing.Count == 0 && assumptions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ResultJsonBox.Text = task.Result is null ? "{}" : Json.Serialize(task.Result);
    }

    private static string Join(IEnumerable<string> items) =>
        string.Join(" · ", items.Where(item => !string.IsNullOrWhiteSpace(item)).DefaultIfEmpty("none"));

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
