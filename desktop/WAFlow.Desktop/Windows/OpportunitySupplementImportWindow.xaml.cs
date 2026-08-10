using System.Windows;
using Microsoft.Win32;
using WAFlow.Core;
using WAFlow.Core.Domain;

namespace WAFlow.Desktop.Windows;

public partial class OpportunitySupplementImportWindow : Window
{
    private readonly AppServices _services;
    private OpportunityImportPreview? _preview;

    public OpportunitySupplementImportWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择商机补充数据工作簿",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        FilePathText.Text = dialog.FileName;
        await BuildPreviewAsync(dialog.FileName);
    }

    private async Task BuildPreviewAsync(string filePath)
    {
        SetBusy(true);
        PreviewPanel.Visibility = Visibility.Collapsed;
        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            _preview = await Task.Run(() => _services.OpportunitySupplements.BuildPreviewAsync(filePath, progress));
            ShowPreview(_preview);
        }
        catch (Exception error)
        {
            _preview = null;
            StatusText.Text = "预览失败，数据库未发生变化。";
            MessageBox.Show(error.Message, "无法生成商机导入预览", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowPreview(OpportunityImportPreview preview)
    {
        TotalRowsText.Text = preview.TotalRows.ToString("N0");
        MatchedCustomersText.Text = preview.MatchedCustomers.ToString("N0");
        MatchedEventsText.Text = $"命中事件 {preview.MatchedEvents:N0} 条";
        DiscardedRowsText.Text = (preview.UnmatchedRows + preview.InvalidBuyerIdRows).ToString("N0");
        DuplicateEventsText.Text = $"重复事件 {preview.DuplicateEvents:N0} 条";
        ChangedCustomersText.Text = preview.ChangedCustomers.ToString("N0");
        DuplicateFileNotice.Visibility = preview.IsPreviouslyImportedFile ? Visibility.Visible : Visibility.Collapsed;
        var issues = new List<string>
        {
            $"未匹配并丢弃：{preview.UnmatchedRows:N0} 行",
            $"客户 ID 空白或异常：{preview.InvalidBuyerIdRows:N0} 行",
            $"客户主档客户 ID 冲突：{preview.BuyerIdConflicts:N0} 个",
            $"数据无变化客户：{preview.UnchangedCustomers:N0} 位"
        };
        if (preview.ConflictBuyerIds.Count > 0)
            issues.Add($"\n冲突客户 ID：\n{string.Join("\n", preview.ConflictBuyerIds.Take(30))}");
        if (preview.UnmatchedBuyerIds.Count > 0)
            issues.Add($"\n未匹配客户 ID（最多展示 30 个）：\n{string.Join("\n", preview.UnmatchedBuyerIds.Take(30))}");
        IssuesText.Text = string.Join("\n", issues);
        PreviewPanel.Visibility = Visibility.Visible;
        StatusText.Text = preview.IsPreviouslyImportedFile
            ? "重复文件已识别：零交易写入、零重复 Token。"
            : $"预览已完成：将新增 {preview.NewEvents.Count:N0} 条交易事件，并刷新 {preview.ChangedCustomers:N0} 位现有客户。";
        CommitButton.Content = preview.IsPreviouslyImportedFile
            ? "关闭"
            : $"确认导入并刷新 {preview.ChangedCustomers:N0} 位现有客户";
        CommitButton.IsEnabled = true;
    }

    private async void Commit_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        if (_preview.IsPreviouslyImportedFile)
        {
            DialogResult = false;
            return;
        }
        SetBusy(true);
        try
        {
            StatusText.Text = "正在事务化写入交易事件与客户商机快照…";
            var result = await Task.Run(() => _services.OpportunitySupplements.CommitAsync(_preview));
            _services.LeadAutomation.QueueOpportunitySupplementAnalysis(result.ChangedLeadIds);
            MessageBox.Show(
                $"商机补充数据导入完成。\n\n新增交易事件：{result.InsertedEvents:N0}\n刷新现有客户：{result.ChangedCustomers:N0}\n增量 AI 队列：{result.QueuedForAnalysis:N0}\n\n未创建客户，未覆盖客户主档。",
                "AI Sales OS",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception error)
        {
            StatusText.Text = "导入失败，事务已回滚。";
            MessageBox.Show(error.Message, "商机补充数据导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (IsVisible) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BrowseButton.IsEnabled = !busy;
        CommitButton.IsEnabled = !busy && _preview is not null;
    }
}
