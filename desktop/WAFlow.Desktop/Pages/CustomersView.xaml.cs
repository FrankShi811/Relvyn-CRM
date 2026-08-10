using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Imports;
using WAFlow.Desktop.Windows;

namespace WAFlow.Desktop.Pages;

public partial class CustomersView : UserControl, IRefreshableView
{
    private readonly AppServices _services;
    private readonly List<DataGridColumn> _customColumns = [];
    private readonly HashSet<string> _checkedLeadIds = new(StringComparer.OrdinalIgnoreCase);
    private List<CustomerRow> _filteredRows = [];
    private List<CustomerRow> _visibleRows = [];
    private IReadOnlyDictionary<string, CustomerDimension> _dimensionsBySortKey =
        new Dictionary<string, CustomerDimension>(StringComparer.OrdinalIgnoreCase);
    private bool _updatingCustomFilter;
    private bool _updatingCategoryFilter;
    private bool _updatingSelectionUi;
    private string? _sortKey;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private int _currentPage = 1;
    private int _pageSize = 30;
    private int _dimensionCount;
    public event EventHandler? ImportRequested;
    public event EventHandler? DataChanged;

    public CustomersView(AppServices services)
    {
        InitializeComponent(); _services = services;
        _services.WhatsAppNumberValidation.StatusChanged += WhatsAppNumberValidation_StatusChanged;
        GradeFilter.ItemsSource = new[] { "全部等级", "A", "B", "C", "D" }; GradeFilter.SelectedIndex = 0;
        StageFilter.ItemsSource = new[] { new StageOption("全部阶段", null) }.Concat(Enum.GetValues<LeadStage>().Select(x => new StageOption(Labels.Stage(x), x))).ToList(); StageFilter.SelectedIndex = 0;
        CategoryPreferenceFilter.ItemsSource = new[] { new CategoryPreferenceOption("全部一级品类", null) }; CategoryPreferenceFilter.SelectedIndex = 0;
        CustomFieldFilter.ItemsSource = new[] { new DimensionOption("全部表格维度", null) }; CustomFieldFilter.DisplayMemberPath = nameof(DimensionOption.Label); CustomFieldFilter.SelectedIndex = 0;
        PageSizeBox.ItemsSource = new[] { new PageSizeOption("10 条/页", 10), new PageSizeOption("30 条/页", 30), new PageSizeOption("50 条/页", 50) };
        PageSizeBox.SelectedIndex = 1;
    }

    private void WhatsAppNumberValidation_StatusChanged(object? sender, WAFlow.Core.Services.WhatsAppNumberValidationChanged e) =>
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var row in _filteredRows.Where(item => item.Id.Equals(e.LeadId, StringComparison.OrdinalIgnoreCase)))
                row.UpdateWhatsAppRegistration(e);
        });

    public async Task RefreshAsync()
    {
        var grade = GradeFilter.SelectedIndex <= 0 ? null : GradeFilter.SelectedItem as string;
        var stage = (StageFilter.SelectedItem as StageOption)?.Value;
        var leads = (await _services.Repository.GetLeadsAsync(SearchBox.Text, grade, stage)).ToList();
        UpdateCategoryPreferenceFilter(leads);
        var category = (CategoryPreferenceFilter.SelectedItem as CategoryPreferenceOption)?.Value;
        if (!string.IsNullOrWhiteSpace(category))
            leads = leads.Where(lead => CustomerDimensionCatalog.ResolvePrimaryCategoryPreference(lead)
                    .Equals(category, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        var dimensions = CustomerDimensionCatalog.Build(leads)
            .Where(dimension => !CustomerDimensionCatalog.IsPrimaryCategoryPreference(dimension))
            .ToList();
        _dimensionsBySortKey = dimensions.ToDictionary(
            dimension => dimension.SortKey,
            dimension => dimension,
            StringComparer.OrdinalIgnoreCase);
        UpdateCustomFieldFilter(dimensions);
        var selectedDimension = (CustomFieldFilter.SelectedItem as DimensionOption)?.Key;
        RenderCustomColumns(selectedDimension is null
            ? dimensions
            : dimensions.Where(dimension => dimension.Key.Equals(selectedDimension, StringComparison.CurrentCultureIgnoreCase)));
        _dimensionCount = dimensions.Count;
        var whatsappLabelsByLead = await _services.Repository.GetWhatsAppLabelsByLeadIdsAsync(leads.Select(lead => lead.Id));
        _filteredRows = leads.Select(lead => new CustomerRow(
            lead,
            whatsappLabelsByLead.TryGetValue(lead.Id, out var labels) ? labels : [],
            _checkedLeadIds.Contains(lead.Id),
            RowSelectionChanged)).ToList();
        ApplyCurrentSort();
        ApplyPagination();
        EditButton.IsEnabled = CustomerGrid.SelectedItem is CustomerRow;
    }

    private void UpdateCategoryPreferenceFilter(IReadOnlyList<Lead> leads)
    {
        var selected = (CategoryPreferenceFilter.SelectedItem as CategoryPreferenceOption)?.Value;
        var values = leads
            .Select(CustomerDimensionCatalog.ResolvePrimaryCategoryPreference)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var options = new[] { new CategoryPreferenceOption("全部一级品类", null) }
            .Concat(values.Select(value => new CategoryPreferenceOption(value, value)))
            .ToList();
        _updatingCategoryFilter = true;
        try
        {
            CategoryPreferenceFilter.ItemsSource = options;
            CategoryPreferenceFilter.SelectedItem = string.IsNullOrWhiteSpace(selected)
                ? options[0]
                : options.FirstOrDefault(option => option.Value?.Equals(selected, StringComparison.CurrentCultureIgnoreCase) == true) ?? options[0];
        }
        finally { _updatingCategoryFilter = false; }
    }

    private void UpdateCustomFieldFilter(IReadOnlyList<CustomerDimension> dimensions)
    {
        var selected = (CustomFieldFilter.SelectedItem as DimensionOption)?.Key;
        _updatingCustomFilter = true;
        var options = new[] { new DimensionOption("全部表格维度", null) }
            .Concat(dimensions.Select(dimension => new DimensionOption(dimension.Label, dimension.Key))).ToList();
        CustomFieldFilter.ItemsSource = options;
        CustomFieldFilter.SelectedItem = selected is null
            ? options[0]
            : options.FirstOrDefault(option => option.Key?.Equals(selected, StringComparison.CurrentCultureIgnoreCase) == true) ?? options[0];
        _updatingCustomFilter = false;
    }

    private void RenderCustomColumns(IEnumerable<CustomerDimension> dimensions)
    {
        foreach (var column in _customColumns) CustomerGrid.Columns.Remove(column);
        _customColumns.Clear();
        foreach (var dimension in dimensions)
        {
            var column = new DataGridTextColumn
            {
                Header = new TextBlock
                {
                    Text = dimension.Label, ToolTip = dimension.ToolTip, TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150
                },
                Width = new DataGridLength(165),
                SortMemberPath = dimension.SortKey,
                Binding = new Binding(nameof(Lead.CustomFields))
                {
                    Converter = CustomFieldValueConverter.Instance,
                    ConverterParameter = dimension
                },
                ElementStyle = (Style)FindResource("CustomerCellText")
            };
            _customColumns.Add(column);
            CustomerGrid.Columns.Add(column);
        }
    }

    private void CustomerGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        var key = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(key)) return;
        e.Handled = true;
        _sortDirection = _sortKey == key && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortKey = key;
        foreach (var column in CustomerGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = _sortDirection;
        ApplyCurrentSort();
        _currentPage = 1;
        ApplyPagination();
    }

    private void ApplyCurrentSort()
    {
        if (string.IsNullOrWhiteSpace(_sortKey)) return;
        var key = _sortKey;
        _filteredRows.Sort((left, right) =>
        {
            var leftValue = SortValue(left, key);
            var rightValue = SortValue(right, key);
            var leftBlank = string.IsNullOrWhiteSpace(leftValue);
            var rightBlank = string.IsNullOrWhiteSpace(rightValue);
            if (leftBlank || rightBlank)
            {
                if (leftBlank && rightBlank) return StringComparer.CurrentCultureIgnoreCase.Compare(left.DisplayName, right.DisplayName);
                return leftBlank ? 1 : -1;
            }
            var comparison = CompareValues(leftValue, rightValue);
            if (_sortDirection == ListSortDirection.Descending) comparison = -comparison;
            return comparison != 0 ? comparison : StringComparer.CurrentCultureIgnoreCase.Compare(left.DisplayName, right.DisplayName);
        });
    }

    private void ApplyPagination()
    {
        var total = _filteredRows.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)_pageSize));
        _currentPage = Math.Clamp(_currentPage, 1, totalPages);
        var startIndex = (_currentPage - 1) * _pageSize;
        _visibleRows = _filteredRows.Skip(startIndex).Take(_pageSize).ToList();
        CustomerGrid.ItemsSource = null;
        CustomerGrid.ItemsSource = _visibleRows;
        RestoreSortGlyph();

        var first = total == 0 ? 0 : startIndex + 1;
        var last = total == 0 ? 0 : startIndex + _visibleRows.Count;
        ListStatsText.Text = $"{total:N0} 位客户 · {_dimensionCount:N0} 个原表维度 · 第 {_currentPage:N0} / {totalPages:N0} 页";
        PageRangeText.Text = total == 0 ? "暂无客户" : $"显示第 {first:N0}–{last:N0} 位，共 {total:N0} 位";
        PageStatusText.Text = $"第 {_currentPage:N0} / {totalPages:N0} 页";
        PreviousPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _currentPage < totalPages;
        CustomerGrid.SelectedItem = null;
        EditButton.IsEnabled = false;
        UpdateSelectionUi();
    }

    private string SortValue(CustomerRow row, string key)
    {
        if (key.StartsWith("custom:", StringComparison.Ordinal)
            && _dimensionsBySortKey.TryGetValue(key, out var dimension))
            return CustomerDimensionCatalog.ResolveValue(row.CustomFields, dimension);
        return key switch
        {
            nameof(CustomerRow.DisplayName) => row.DisplayName,
            nameof(CustomerRow.BuyerId) => row.BuyerId,
            nameof(CustomerRow.Company) => row.Company,
            nameof(CustomerRow.Email) => row.Email,
            nameof(CustomerRow.Country) => row.Country,
            nameof(CustomerRow.PhoneE164) => row.PhoneE164,
            nameof(CustomerRow.PhoneState) => row.PhoneState,
            nameof(CustomerRow.TagsLabel) => row.TagsLabel,
            nameof(CustomerRow.WhatsAppLabelsLabel) => row.WhatsAppLabelsLabel,
            nameof(CustomerRow.Owner) => row.Owner,
            nameof(CustomerRow.Grade) => row.Grade,
            "Stage" => ((int)row.Lead.Stage).ToString("D2", CultureInfo.InvariantCulture),
            nameof(CustomerRow.PrimaryCategoryPreference) => row.PrimaryCategoryPreference,
            _ => ""
        };
    }

    private static int CompareValues(string left, string right)
    {
        var leftNumber = decimal.TryParse(left.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedLeft);
        var rightNumber = decimal.TryParse(right.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedRight);
        if (leftNumber && rightNumber) return parsedLeft.CompareTo(parsedRight);
        if (DateTimeOffset.TryParse(left, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var leftDate)
            && DateTimeOffset.TryParse(right, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var rightDate))
            return leftDate.CompareTo(rightDate);
        return StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
    }

    private void RestoreSortGlyph()
    {
        if (string.IsNullOrWhiteSpace(_sortKey)) return;
        foreach (var column in CustomerGrid.Columns)
            column.SortDirection = column.SortMemberPath == _sortKey ? _sortDirection : null;
    }

    private static bool IsBuyerNicknameDimension(string header)
    {
        return ImportService.ResolveField(header) == ImportField.Name
            && (header.Contains("nickname", StringComparison.OrdinalIgnoreCase)
                || header.Contains("昵称", StringComparison.CurrentCultureIgnoreCase));
    }

    private static string CustomerDisplayName(Lead lead)
    {
        if (!string.IsNullOrWhiteSpace(lead.Name)) return lead.Name;
        var buyerNickname = lead.CustomFields
            .Where(pair => IsBuyerNicknameDimension(pair.Key))
            .Select(pair => pair.Value?.Trim() ?? "")
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(buyerNickname)) return buyerNickname;
        return string.IsNullOrWhiteSpace(lead.Company) ? "未命名客户" : lead.Company;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);
    private void CustomerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => EditButton.IsEnabled = CustomerGrid.SelectedItem is CustomerRow;
    private async void CustomerGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (CustomerGrid.SelectedItem is CustomerRow) await EditSelectedAsync(); }
    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditSelectedAsync();
    private async Task EditSelectedAsync()
    {
        if (CustomerGrid.SelectedItem is not CustomerRow selected) return;
        var current = await _services.Repository.GetLeadAsync(selected.Id);
        if (current is null) { await RefreshAsync(); return; }
        var window = new CustomerEditWindow(_services, current) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true)
        {
            await RefreshAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RowSelectionChanged(CustomerRow row, bool isSelected)
    {
        if (isSelected) _checkedLeadIds.Add(row.Id); else _checkedLeadIds.Remove(row.Id);
        if (!_updatingSelectionUi) UpdateSelectionUi();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingSelectionUi) return;
        var select = SelectAllCheckBox.IsChecked == true;
        _updatingSelectionUi = true;
        try
        {
            foreach (var row in _visibleRows)
            {
                row.IsSelected = select;
                if (select) _checkedLeadIds.Add(row.Id); else _checkedLeadIds.Remove(row.Id);
            }
        }
        finally
        {
            _updatingSelectionUi = false;
            UpdateSelectionUi();
        }
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _checkedLeadIds.Clear();
        _updatingSelectionUi = true;
        try { foreach (var row in _filteredRows) row.IsSelected = false; }
        finally { _updatingSelectionUi = false; UpdateSelectionUi(); }
    }

    private void UpdateSelectionUi()
    {
        var visibleSelected = _visibleRows.Count(row => row.IsSelected);
        _updatingSelectionUi = true;
        SelectAllCheckBox.IsEnabled = _visibleRows.Count > 0;
        SelectAllCheckBox.IsChecked = _visibleRows.Count == 0 || visibleSelected == 0 ? false : visibleSelected == _visibleRows.Count ? true : null;
        _updatingSelectionUi = false;
        SelectedCountText.Text = $"已选 {_checkedLeadIds.Count:N0} 位";
        DeleteSelectedButton.IsEnabled = _checkedLeadIds.Count > 0;
        ClearSelectionButton.IsEnabled = _checkedLeadIds.Count > 0;
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var ids = _checkedLeadIds.ToList();
        if (ids.Count == 0) return;
        var visibleNames = _visibleRows.Where(row => row.IsSelected).Select(row => row.DisplayName).Where(name => !string.IsNullOrWhiteSpace(name)).Take(5).ToList();
        var examples = visibleNames.Count == 0 ? "" : $"\n\n包含：{string.Join("、", visibleNames)}{(ids.Count > visibleNames.Count ? " 等" : "")}";
        var message = $"确定删除选中的 {ids.Count:N0} 位客户吗？{examples}\n\n客户资料、AI 分析、草稿和未发送的群发任务将被删除；WhatsApp 会话与消息历史会保留，但不再关联这些客户。此操作无法撤销。";
        if (MessageBox.Show(message, "删除所选客户", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        DeleteSelectedButton.IsEnabled = false;
        ClearSelectionButton.IsEnabled = false;
        try
        {
            var deleted = await _services.Repository.DeleteLeadsAsync(ids);
            _checkedLeadIds.Clear();
            await RefreshAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
            MessageBox.Show($"已删除 {deleted:N0} 位客户。\nWhatsApp 会话和消息历史已保留。", "删除完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "批量删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateSelectionUi();
        }
    }

    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded && !_updatingCustomFilter && !_updatingCategoryFilter) { _currentPage = 1; await RefreshAsync(); } }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { _currentPage = 1; await RefreshAsync(); } }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _updatingCustomFilter = true;
        try
        {
            GradeFilter.SelectedIndex = 0;
            StageFilter.SelectedIndex = 0;
            CategoryPreferenceFilter.SelectedIndex = 0;
            CustomFieldFilter.SelectedIndex = 0;
        }
        finally
        {
            _updatingCustomFilter = false;
        }
        _currentPage = 1;
        await RefreshAsync();
    }
    private void PageSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSizeBox.SelectedItem is not PageSizeOption option || _pageSize == option.Value) return;
        _pageSize = option.Value;
        _currentPage = 1;
        if (IsLoaded) ApplyPagination();
    }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) { if (_currentPage > 1) { _currentPage--; ApplyPagination(); } }
    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_filteredRows.Count / (double)_pageSize));
        if (_currentPage < totalPages) { _currentPage++; ApplyPagination(); }
    }
    private sealed record StageOption(string Label, LeadStage? Value);
    private sealed record CategoryPreferenceOption(string Label, string? Value);
    private sealed record DimensionOption(string Label, string? Key);
    private sealed record PageSizeOption(string Label, int Value);

    private sealed class CustomerRow : INotifyPropertyChanged
    {
        private readonly Action<CustomerRow, bool> _selectionChanged;
        private bool _isSelected;

        public CustomerRow(Lead lead, IEnumerable<WhatsAppLabel> whatsappLabels, bool isSelected, Action<CustomerRow, bool> selectionChanged)
        {
            Lead = lead;
            WhatsAppLabels = whatsappLabels
                .Where(label => !label.Deleted && !string.IsNullOrWhiteSpace(label.Name))
                .GroupBy(label => $"{label.AccountId}\u001f{label.Id}", StringComparer.OrdinalIgnoreCase)
                .Select(group => WhatsAppLabelChip.From(group.OrderByDescending(label => label.UpdatedAt).First()))
                .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(label => label.AccountId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _isSelected = isSelected;
            _selectionChanged = selectionChanged;
        }

        public Lead Lead { get; }
        public string Id => Lead.Id;
        public string DisplayName => CustomerDisplayName(Lead);
        public string BuyerId => Lead.BuyerId;
        public string Company => Lead.Company;
        public string Email => Lead.Email;
        public string Country => Lead.Country;
        public string PhoneE164 => Lead.PhoneE164;
        public string PhoneState => Lead.PhoneState;
        public string TagsLabel => Lead.TagsLabel;
        public IReadOnlyList<WhatsAppLabelChip> WhatsAppLabels { get; }
        public IReadOnlyList<WhatsAppLabelChip> VisibleWhatsAppLabels => WhatsAppLabels.Take(2).ToList();
        public Visibility AdditionalWhatsAppLabelsVisibility => WhatsAppLabels.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        public string AdditionalWhatsAppLabelsText => WhatsAppLabels.Count > 2 ? $"+{WhatsAppLabels.Count - 2}" : "";
        public string WhatsAppLabelsLabel => string.Join(", ", WhatsAppLabels.Select(label => label.Name));
        public string WhatsAppLabelsToolTip => WhatsAppLabels.Count == 0 ? "尚未同步 WhatsApp 标签" : string.Join("、", WhatsAppLabels.Select(label => label.Name));
        public string Owner => Lead.Owner;
        public string Grade => Lead.Grade;
        public string StageLabel => Lead.StageLabel;
        public string PrimaryCategoryPreference => CustomerDimensionCatalog.ResolvePrimaryCategoryPreference(Lead);
        public IReadOnlyDictionary<string, string> CustomFields => Lead.CustomFields;
        public void UpdateWhatsAppRegistration(WAFlow.Core.Services.WhatsAppNumberValidationChanged state)
        {
            Lead.PhoneE164 = state.Phone;
            Lead.WhatsAppRegistrationStatus = state.Status;
            Lead.WhatsAppRegistrationCheckedAt = state.CheckedAt;
            Lead.WhatsAppRegistrationError = state.Error;
            if (state.Status is WhatsAppRegistrationStatus.Registered or WhatsAppRegistrationStatus.NotRegistered)
                Lead.WhatsAppRegistrationPhone = state.Phone;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PhoneE164)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PhoneState)));
        }
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                _selectionChanged(this, value);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class CustomFieldValueConverter : IValueConverter
    {
        public static readonly CustomFieldValueConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not IReadOnlyDictionary<string, string> fields || parameter is not CustomerDimension dimension) return "";
            return CustomerDimensionCatalog.ResolveValue(fields, dimension);
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }
}
