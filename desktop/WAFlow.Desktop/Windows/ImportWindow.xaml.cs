using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WAFlow.Core;
using WAFlow.Core.Imports;

namespace WAFlow.Desktop.Windows;

public partial class ImportWindow : Window
{
    private readonly AppServices _services;
    private ParsedImport? _parsed;
    private List<MappingRow> _mapping = [];
    private List<ImportPreviewRow> _preview = [];
    private int _step;

    public IReadOnlyList<ImportFieldOption> ImportFieldOptions { get; } =
    [
        new("不映射系统字段（仍保留原列）", ImportField.Ignore),
        new("保留为原表维度", ImportField.Custom),
        new("客户 ID / Buyer ID", ImportField.BuyerId),
        new("客户姓名", ImportField.Name),
        new("公司", ImportField.Company),
        new("国家 / 地区", ImportField.Country),
        new("WhatsApp 号码", ImportField.WhatsApp),
        new("邮箱", ImportField.Email),
        new("产品兴趣", ImportField.ProductInterest),
        new("预计订单金额", ImportField.EstimatedOrderValue),
        new("公司规模", ImportField.CompanyScale),
        new("购买力", ImportField.PurchasePower),
        new("明确需求", ImportField.ExplicitDemand),
        new("来源", ImportField.Source),
        new("负责人", ImportField.Owner),
        new("销售阶段", ImportField.Stage),
        new("标签", ImportField.Tags),
        new("备注", ImportField.Notes)
    ];

    public ImportWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title="选择客户表", Filter="Excel / CSV (*.xlsx;*.csv)|*.xlsx;*.csv", Multiselect=false };
        if (dialog.ShowDialog(this) == true) FilePathText.Text = dialog.FileName;
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            if (_step == 0)
            {
                var filePath = FilePathText.Text;
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    MessageBox.Show("请先选择文件。", "AI Sales OS");
                    return;
                }

                ShowProgress("正在后台解析文件…", 0, indeterminate:true);
                _parsed = await Task.Run(() => _services.Imports.Parse(filePath));
                SheetCombo.ItemsSource = _parsed.Sheets;
                SheetCombo.SelectedItem = _parsed.Sheets.FirstOrDefault(sheet => sheet.Name.Equals(_parsed.PreferredSheetName, StringComparison.OrdinalIgnoreCase)) ?? _parsed.Sheets[0];
                ShowStep(1);
                return;
            }

            if (_parsed is null || SheetCombo.SelectedItem is not ImportSheet sheet) return;
            if (_step == 1)
            {
                MappingGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                MappingGrid.CommitEdit(DataGridEditingUnit.Row, true);
                ShowProgress("正在分析新增、更新、冲突和号码风险…", 0, indeterminate: true);
                var previewProgress = CreateProgress();
                _preview = await Task.Run(() => _services.Imports.BuildPreviewAsync(sheet, _mapping, previewProgress));
                PreviewGrid.ItemsSource = _preview;
                PreviewCreatedText.Text = _preview.Count(row => row.Errors.Count == 0 && !row.IsDuplicate && row.DuplicateRowNumber is null).ToString("N0");
                PreviewUpdatedText.Text = _preview.Count(row => row.Errors.Count == 0 && (row.IsDuplicate || row.DuplicateRowNumber is not null)).ToString("N0");
                PreviewWarningText.Text = _preview.Count(row => row.Warnings.Count > 0).ToString("N0");
                PreviewBlockedText.Text = _preview.Count(row => row.Errors.Count > 0).ToString("N0");
                ShowStep(2);
                return;
            }

            if (_preview.Count == 0)
            {
                MessageBox.Show("请先生成并检查变更预览。", "AI Sales OS", MessageBoxButton.OK, MessageBoxImage.Information);
                ShowStep(1);
                return;
            }

            var fileName = Path.GetFileName(_parsed.FilePath);
            var progress = CreateProgress();
            var allowStageChange = AllowStageChangeBox.IsChecked == true;
            var allowOwnerChange = AllowOwnerChangeBox.IsChecked == true;
            var outcome = await Task.Run(async () =>
            {
                var commit = await _services.Imports.CommitAsync(
                    fileName,
                    _preview,
                    allowStageChange,
                    allowOwnerChange,
                    progress);
                var demosRemoved = commit.Created + commit.Updated > 0
                    ? await _services.Repository.RemoveDemoLeadsIfRealDataExistsAsync()
                    : 0;
                return (Commit: commit, DemosRemoved: demosRemoved);
            });
            var result = outcome.Commit;
            _services.WhatsAppNumberValidation.NotifyPendingWork();
            var cleanupText = outcome.DemosRemoved > 0 ? $"\n\u5df2\u81ea\u52a8\u6e05\u7406 {outcome.DemosRemoved} \u6761\u6f14\u793a\u5ba2\u6237\u3002" : "";

            MessageBox.Show(
                $"\u5bfc\u5165\u5b8c\u6210\n\u5904\u7406 {result.Total:N0} \u884c \u00b7 \u65b0\u5efa {result.Created:N0} \u00b7 \u66f4\u65b0 {result.Updated:N0}\n\u5df2\u52a0\u5165 WhatsApp \u771f\u5b9e\u53f7\u7801\u68c0\u6d4b {result.PendingWhatsAppChecks:N0} \u4e2a \u00b7 \u683c\u5f0f\u98ce\u9669 {result.InvalidPhones:N0} \u00b7 \u5931\u8d25 {result.Failed:N0}\n\n\u53ea\u6709 WhatsApp \u660e\u786e\u8fd4\u56de\u5df2\u6ce8\u518c\u624d\u4f1a\u6807\u8bb0\u201c\u6709\u6548\u201d\uff1b\u8d26\u53f7\u672a\u8fde\u63a5\u6216\u7f51\u7edc\u5f02\u5e38\u4f1a\u4fdd\u7559\u5f85\u91cd\u8bd5\u3002\n\u539f\u5de5\u4f5c\u8868\u7684 {sheet.Headers.Count} \u5217\u5df2\u5168\u90e8\u4fdd\u7559\u4e3a\u5ba2\u6237\u7ef4\u5ea6\u3002\n\u8d1f\u8d23\u4eba\uff1a{(allowOwnerChange ? "\u5141\u8bb8\u66f4\u65b0" : "\u5df2\u4fdd\u62a4")} \u00b7 \u9500\u552e\u9636\u6bb5\uff1a{(allowStageChange ? "\u5141\u8bb8\u66f4\u65b0" : "\u5df2\u4fdd\u62a4")}{cleanupText}",
                "AI Sales OS", MessageBoxButton.OK, result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            HideProgress();
        }
    }

    private void SheetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SheetCombo.SelectedItem is not ImportSheet sheet) return;
        SheetStatsText.Text = $"{sheet.Name} · {sheet.Rows.Count:N0} 行 × {sheet.Headers.Count:N0} 列";
        ColumnSummaryText.Text = string.Join("　·　", sheet.Headers.Select(CompactHeader));
        _mapping = _services.Imports.SuggestMapping(sheet)
            .Select(row => new MappingRow { Header = row.Header, Sample = row.Sample, Target = row.Target })
            .ToList();
        MappingGrid.ItemsSource = _mapping;
        _preview = [];
        PreviewGrid.ItemsSource = null;
    }

    private static string CompactHeader(string header)
    {
        var firstLine = header.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? header.Trim();
        return firstLine.Length <= 32 ? firstLine : firstLine[..31] + "…";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 2)
        {
            _preview = [];
            PreviewGrid.ItemsSource = null;
            ShowStep(1);
        }
        else if (_step == 1)
        {
            ShowStep(0);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowStep(int step)
    {
        _step = step;
        SelectPanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        SheetPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = step switch
        {
            0 => "解析文件",
            1 => "生成变更预览",
            _ => "确认并开始导入"
        };
        Step1Circle.Background = Brush(step >= 0);
        Step2Circle.Background = Brush(step >= 1);
        Step3Circle.Background = Brush(step >= 2);
        static System.Windows.Media.Brush Brush(bool active) => new System.Windows.Media.SolidColorBrush(active ? System.Windows.Media.Color.FromRgb(15,143,104) : System.Windows.Media.Color.FromRgb(203,215,209));
    }

    private IProgress<ImportProgress> CreateProgress() => new Progress<ImportProgress>(value => ShowProgress(value.Label, value.Percent));

    private void SetBusy(bool busy)
    {
        NextButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        SheetCombo.IsEnabled = !busy;
        MappingGrid.IsEnabled = !busy;
        AllowOwnerChangeBox.IsEnabled = !busy;
        AllowStageChangeBox.IsEnabled = !busy;
    }

    private void ShowProgress(string text, int percent, bool indeterminate = false)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ImportProgressText.Text = text;
        ImportProgressBar.IsIndeterminate = indeterminate;
        if (!indeterminate) ImportProgressBar.Value = percent;
    }

    private void HideProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        ImportProgressBar.IsIndeterminate = false;
        ImportProgressBar.Value = 0;
    }

    public sealed record ImportFieldOption(string Label, ImportField Value);
}
