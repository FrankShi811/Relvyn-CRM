using System.Windows;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Windows;

public partial class CustomerReportComparisonWindow : Window
{
    public CustomerReportComparisonWindow(CustomerAnalysisReport previous, CustomerAnalysisReport current)
    {
        InitializeComponent();
        DataContext = CustomerReportComparisonViewModel.Create(previous, current);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class CustomerReportComparisonViewModel
{
    public required CustomerReportComparisonColumn Previous { get; init; }
    public required CustomerReportComparisonColumn Current { get; init; }
    public string GradeDelta { get; init; } = "";
    public string ScoreDelta { get; init; } = "";
    public string ProbabilityDelta { get; init; } = "";

    public static CustomerReportComparisonViewModel Create(CustomerAnalysisReport previous, CustomerAnalysisReport current)
    {
        var oldOpportunity = previous.Report.OpportunityJudgment;
        var newOpportunity = current.Report.OpportunityJudgment;
        return new CustomerReportComparisonViewModel
        {
            Previous = CustomerReportComparisonColumn.Create(previous),
            Current = CustomerReportComparisonColumn.Create(current),
            GradeDelta = $"{oldOpportunity.Grade} → {newOpportunity.Grade}",
            ScoreDelta = $"{oldOpportunity.AiScore} → {newOpportunity.AiScore} ({Delta(newOpportunity.AiScore - oldOpportunity.AiScore)})",
            ProbabilityDelta = $"{oldOpportunity.DealProbability}% → {newOpportunity.DealProbability}% ({Delta(newOpportunity.DealProbability - oldOpportunity.DealProbability)}%)"
        };
    }

    private static string Delta(int value) => value > 0 ? $"+{value}" : value.ToString();
}

public sealed class CustomerReportComparisonColumn
{
    public string VersionLabel { get; init; } = "";
    public string Positioning { get; init; } = "—";
    public string TypeAndStage { get; init; } = "—";
    public string ValueJudgment { get; init; } = "—";
    public string SalesRecommendation { get; init; } = "—";
    public string PositiveFactors { get; init; } = "—";
    public string NegativeFactors { get; init; } = "—";

    public static CustomerReportComparisonColumn Create(CustomerAnalysisReport report)
    {
        var summary = report.Report.ExecutiveSummary;
        return new CustomerReportComparisonColumn
        {
            VersionLabel = report.VersionLabel,
            Positioning = Value(summary.OneLinePositioning),
            TypeAndStage = $"{Value(summary.CustomerType)} · {Value(summary.BusinessStage)}",
            ValueJudgment = Value(summary.OverallValueJudgment),
            SalesRecommendation = Value(summary.CurrentSalesRecommendation),
            PositiveFactors = Bullets(report.Report.OpportunityJudgment.PositiveFactors),
            NegativeFactors = Bullets(report.Report.OpportunityJudgment.NegativeFactors)
        };
    }

    private static string Value(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string Bullets(IEnumerable<string> items) =>
        !items.Any() ? "—" : string.Join(Environment.NewLine, items.Select(item => $"• {item}"));
}
