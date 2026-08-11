using WAFlow.Core.Domain;

namespace WAFlow.Core.Imports;

public enum ImportField
{
    Ignore, Custom, BuyerId, Name, Company, Country, WhatsApp, Email, ProductInterest, EstimatedOrderValue,
    CompanyScale, PurchasePower, ExplicitDemand, Source, Owner, Stage, Tags, Notes
}

public sealed class ImportSheet
{
    public string Name { get; set; } = "";
    public List<string> Headers { get; set; } = [];
    public List<Dictionary<string, string>> Rows { get; set; } = [];
    public int SanitizedFormulaCount { get; set; }
}

public sealed class ParsedImport
{
    public string FilePath { get; set; } = "";
    public string PreferredSheetName { get; set; } = "";
    public List<ImportSheet> Sheets { get; set; } = [];
}

public sealed class MappingRow
{
    public string Header { get; set; } = "";
    public string Sample { get; set; } = "";
    public ImportField Target { get; set; }
    public string DestinationLabel => Target switch
    {
        ImportField.Custom => $"自定义维度：{Header}",
        ImportField.BuyerId => "Buyer ID（统一客户标识）",
        _ => Target.ToString()
    };
}

public sealed class ImportPreviewRow
{
    public int RowNumber { get; set; }
    public Dictionary<ImportField, string> Values { get; set; } = [];
    public Dictionary<string, string> CustomValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string BuyerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
    public string Country { get; set; } = "";
    public string PhoneE164 { get; set; } = "";
    public bool PhoneValid { get; set; }
    public bool IsDuplicate { get; set; }
    public string DuplicateLeadId { get; set; } = "";
    public int? DuplicateRowNumber { get; set; }
    public string Changes { get; set; } = "";
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string StatusLabel => Errors.Count > 0 ? "已阻止" : IsDuplicate || DuplicateRowNumber is not null ? "更新已有客户" : "新增客户";
    public string WarningsLabel => string.Join("；", Warnings);
    public string ErrorsLabel => string.Join("；", Errors);
    public string ReviewMessage => Errors.Count > 0 ? ErrorsLabel : WarningsLabel;
}

public sealed record ImportCommitResult(int Total, int Created, int Updated, int InvalidPhones, int PendingWhatsAppChecks, int Failed);

public sealed record LeadsImportedEventArgs(IReadOnlyList<string> LeadIds, int Created, int Updated);

public sealed record ImportProgress(string Phase, int Completed, int Total)
{
    public int Percent => Total <= 0 ? 0 : Math.Clamp((int)Math.Round(Completed * 100d / Total), 0, 100);
    public string Label => Total <= 0 ? Phase : $"{Phase} {Completed:N0} / {Total:N0}";
}
