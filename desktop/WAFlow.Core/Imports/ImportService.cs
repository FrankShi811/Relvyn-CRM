using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ClosedXML.Excel;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;

namespace WAFlow.Core.Imports;

public sealed class ImportService
{
    public const long MaxBytes = 200L * 1024 * 1024;
    public const long MaxCells = 5_000_000;
    public const int WriteBatchSize = 500;
    private readonly LocalRepository _repository;

    public event EventHandler<LeadsImportedEventArgs>? LeadsImported;

    public ImportService(LocalRepository repository)
    {
        _repository = repository;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ParsedImport Parse(string filePath)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists) throw new FileNotFoundException("导入文件不存在。", filePath);
        if (file.Length == 0) throw new InvalidDataException("导入文件为空。");
        if (file.Length > MaxBytes) throw new InvalidDataException("文件超过 200MB 资源保护上限。");
        var extension = file.Extension.ToLowerInvariant();
        if (extension is not (".xlsx" or ".csv")) throw new InvalidDataException("仅支持 .xlsx 或 .csv 文件。");
        using var snapshot = OpenSharedReadSnapshot(filePath, file.Length);
        var result = extension == ".xlsx" ? ParseXlsx(snapshot) : ParseCsv(snapshot);
        result.FilePath = filePath;
        if (result.Sheets.Count == 0) throw new InvalidDataException("文件中没有非空工作表或数据行。");
        return result;
    }

    public List<MappingRow> SuggestMapping(ImportSheet sheet)
    {
        var seen = new HashSet<ImportField>();
        return sheet.Headers.Select(header =>
        {
            var target = FieldAliases.Suggest(header);
            if (target is not (ImportField.Ignore or ImportField.Custom) && !seen.Add(target)) target = ImportField.Custom;
            return new MappingRow { Header = header, Sample = sheet.Rows.FirstOrDefault()?.GetValueOrDefault(header) ?? "", Target = target };
        }).ToList();
    }

    public static ImportField ResolveField(string header) => FieldAliases.Suggest(header);

    public static bool IsCoreDimension(string header) =>
        ResolveField(header) is not (ImportField.Ignore or ImportField.Custom);

    public async Task<List<ImportPreviewRow>> BuildPreviewAsync(ImportSheet sheet, IEnumerable<MappingRow> mapping, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var selected = mapping.Where(m => m.Target != ImportField.Ignore).ToList();
        var coreMap = selected.Where(m => m.Target != ImportField.Custom).ToDictionary(m => m.Header, m => m.Target);
        // Every source column is retained under its original header. Recognized columns are
        // additionally projected into CRM core fields so the rest of the product can use them.
        var customHeaders = sheet.Headers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        progress?.Report(new("正在读取已有客户", 0, sheet.Rows.Count));
        var existing = await _repository.GetLeadsAsync(cancellationToken: cancellationToken);
        var byBuyerId = existing
            .Select(lead => (Key: BuyerIdentity.Normalize(BuyerIdentity.Resolve(lead)), Lead: lead))
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Lead).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var byPhone = BuildPhoneIndex(existing.Where(l => l.PhoneValid && !string.IsNullOrWhiteSpace(l.PhoneE164)), lead => lead.PhoneE164);
        var byCompositeIdentity = existing
            .Select(lead => (Key: BuildCompositeIdentity(lead.CustomFields, lead.Name, lead.PhoneE164), Lead: lead))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Lead, StringComparer.OrdinalIgnoreCase);
        var byIdentity = existing
            .Select(lead => (Key: BuildImportIdentity(lead.CustomFields, lead.Name), Lead: lead))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Lead, StringComparer.OrdinalIgnoreCase);
        var claimedExistingLeadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstRowsByBuyerId = new Dictionary<string, ImportPreviewRow>(StringComparer.OrdinalIgnoreCase);
        var output = new List<ImportPreviewRow>(sheet.Rows.Count);
        for (var i = 0; i < sheet.Rows.Count; i++)
        {
            var values = new Dictionary<ImportField, string>();
            foreach (var pair in coreMap)
            {
                var value = NormalizeCoreValue(pair.Value, sheet.Rows[i].GetValueOrDefault(pair.Key, ""));
                values[pair.Value] = value;
            }
            var customValues = customHeaders.ToDictionary(header => header, header => sheet.Rows[i].GetValueOrDefault(header, "").Trim(), StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(values.GetValueOrDefault(ImportField.Name)) && string.IsNullOrWhiteSpace(values.GetValueOrDefault(ImportField.Company)))
            {
                var fallback = customHeaders
                    .Select(header => customValues.GetValueOrDefault(header, ""))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 160)
                    ?? $"导入行 {i + 2}";
                values[ImportField.Name] = fallback;
            }
            var country = values.GetValueOrDefault(ImportField.Country, "");
            var rawPhone = values.GetValueOrDefault(ImportField.WhatsApp, "");
            var normalized = PhoneNormalizer.Normalize(rawPhone, country);
            var item = new ImportPreviewRow
            {
                RowNumber = i + 2, Values = values, CustomValues = customValues,
                BuyerId = BuyerIdentity.Canonicalize(values.GetValueOrDefault(ImportField.BuyerId, "")),
                Name = values.GetValueOrDefault(ImportField.Name, ""),
                Company = values.GetValueOrDefault(ImportField.Company, ""), Country = country,
                PhoneE164 = normalized.E164.Length > 0 ? normalized.E164 : rawPhone, PhoneValid = normalized.Valid
            };
            var buyerKey = BuyerIdentity.Normalize(item.BuyerId);
            var importIdentity = BuildImportIdentity(customValues);
            var compositeIdentity = BuildCompositeIdentity(customValues, item.Name, item.PhoneE164);
            if (!normalized.Valid) item.Warnings.Add(string.IsNullOrWhiteSpace(values.GetValueOrDefault(ImportField.WhatsApp))
                ? "未提供 WhatsApp 号码"
                : "WhatsApp 号码格式无效；已保留表格号码且仅补 + 号");
            Lead? duplicate = null;
            if (buyerKey.Length > 0 && firstRowsByBuyerId.TryGetValue(buyerKey, out var firstBuyerRow))
            {
                item.IsDuplicate = true;
                item.DuplicateRowNumber = firstBuyerRow.RowNumber;
                item.Changes = "同一 Buyer ID，更新同一客户主记录";
                if (firstBuyerRow.Errors.Count > 0)
                    item.Errors.AddRange(firstBuyerRow.Errors);
            }
            else if (buyerKey.Length > 0 && byBuyerId.TryGetValue(buyerKey, out var buyerMatches))
            {
                if (buyerMatches.Count == 1)
                    duplicate = buyerMatches[0];
                else
                    item.Errors.Add($"Buyer ID“{item.BuyerId}”已对应 {buyerMatches.Count} 个客户，必须先人工处理身份冲突。");
            }
            if (item.DuplicateRowNumber is null
                && item.Errors.Count == 0
                && duplicate is null
                && compositeIdentity is not null
                && byCompositeIdentity.TryGetValue(compositeIdentity, out var compositeDuplicate)
                && !claimedExistingLeadIds.Contains(compositeDuplicate.Id))
            {
                duplicate = compositeDuplicate;
            }
            if (item.DuplicateRowNumber is null && item.Errors.Count == 0 && duplicate is null)
                duplicate = item.PhoneValid
                ? FindUniquePhoneMatch(byPhone, item.PhoneE164, lead => lead.PhoneE164, lead => !claimedExistingLeadIds.Contains(lead.Id))
                : null;
            if (item.DuplicateRowNumber is null
                && item.Errors.Count == 0
                && duplicate is null
                && importIdentity is not null
                && byIdentity.TryGetValue(importIdentity, out var identityDuplicate)
                && !claimedExistingLeadIds.Contains(identityDuplicate.Id))
            {
                duplicate = identityDuplicate;
            }
            if (duplicate is not null && buyerKey.Length > 0)
            {
                var existingBuyerKey = BuyerIdentity.Normalize(BuyerIdentity.Resolve(duplicate));
                if (existingBuyerKey.Length > 0 && !existingBuyerKey.Equals(buyerKey, StringComparison.OrdinalIgnoreCase))
                {
                    item.Errors.Add(
                        $"Buyer ID“{item.BuyerId}”与电话命中的客户 Buyer ID“{BuyerIdentity.Resolve(duplicate)}”冲突，已阻止自动合并。");
                    duplicate = null;
                }
            }
            if (duplicate is not null)
            {
                item.IsDuplicate = true; item.DuplicateLeadId = duplicate.Id; item.Changes = BuildChanges(duplicate, values, customValues, normalized);
                claimedExistingLeadIds.Add(duplicate.Id);
            }
            if (buyerKey.Length > 0 && !firstRowsByBuyerId.ContainsKey(buyerKey))
                firstRowsByBuyerId[buyerKey] = item;
            if (item.Errors.Count == 0 && string.IsNullOrWhiteSpace(item.Changes))
                item.Changes = item.IsDuplicate || item.DuplicateRowNumber is not null
                    ? "匹配已有客户；没有非空字段变化"
                    : "新增客户并保留全部原表维度";
            // Buyer ID is the authoritative business identity. Only when it is absent do
            // we use an unambiguous normalized phone or stable prior-import row identity.
            output.Add(item);
            if ((i + 1) % 250 == 0 || i + 1 == sheet.Rows.Count) progress?.Report(new("正在生成重复与风险预览", i + 1, sheet.Rows.Count));
        }
        return output;
    }

    private static Dictionary<string, List<T>> BuildPhoneIndex<T>(IEnumerable<T> items, Func<T, string> phoneSelector) where T : class
    {
        var index = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        foreach (var item in items) AddPhoneIndex(index, item, phoneSelector);
        return index;
    }

    private static void AddPhoneIndex<T>(Dictionary<string, List<T>> index, T item, Func<T, string> phoneSelector) where T : class
    {
        foreach (var key in PhoneLookupKeys(phoneSelector(item)))
        {
            if (!index.TryGetValue(key, out var values)) index[key] = values = [];
            values.Add(item);
        }
    }

    private static T? FindUniquePhoneMatch<T>(Dictionary<string, List<T>> index, string phone, Func<T, string> phoneSelector, Func<T, bool>? include = null) where T : class
    {
        var target = PhoneIdentity.Digits(phone);
        if (target.Length < 8) return null;
        var candidates = PhoneLookupKeys(target)
            .Where(index.ContainsKey)
            .SelectMany(key => index[key])
            .Distinct()
            .Where(item => include?.Invoke(item) ?? true)
            .Where(item => PhoneIdentity.IsMatch(phoneSelector(item), target))
            .Select(item => new { Item = item, Difference = Math.Abs(PhoneIdentity.Digits(phoneSelector(item)).Length - target.Length) })
            .ToList();
        if (candidates.Count == 0) return null;
        var bestDifference = candidates.Min(candidate => candidate.Difference);
        var best = candidates.Where(candidate => candidate.Difference == bestDifference).Select(candidate => candidate.Item).Distinct().ToList();
        return best.Count == 1 ? best[0] : null;
    }

    private static IEnumerable<string> PhoneLookupKeys(string phone)
    {
        var digits = PhoneIdentity.Digits(phone);
        for (var skipped = 0; skipped <= 4 && digits.Length - skipped >= 8; skipped++)
            yield return digits[skipped..];
    }

    private static string? BuildImportIdentity(IReadOnlyDictionary<string, string> fields, string? fallbackName = null)
    {
        foreach (var pair in fields)
        {
            if (FieldAliases.Suggest(pair.Key) != ImportField.Name || string.IsNullOrWhiteSpace(pair.Value)) continue;
            var normalized = new string(pair.Value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (normalized.Length >= 2) return "name:" + normalized;
        }
        if (string.IsNullOrWhiteSpace(fallbackName)) return null;
        var fallback = new string(fallbackName.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return fallback.Length >= 2 ? "name:" + fallback : null;
    }

    private static string? BuildCompositeIdentity(IReadOnlyDictionary<string, string> fields, string? fallbackName, string? phone)
    {
        var name = BuildImportIdentity(fields, fallbackName);
        var digits = PhoneIdentity.Digits(phone);
        return name is not null && digits.Length >= 8 ? $"{name}|phone:{digits}" : null;
    }

    private static string NormalizeCoreValue(ImportField field, string value)
    {
        var trimmed = value.Trim();
        if (field != ImportField.Country) return trimmed;
        return trimmed.ToUpperInvariant() is "0" or "#N/A" or "N/A" or "NA" or "NULL" or "-" or "--" ? "" : trimmed;
    }

    public async Task<ImportCommitResult> CommitAsync(string fileName, IReadOnlyList<ImportPreviewRow> preview, bool allowStageChange, bool allowOwnerChange, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var created = 0; var updated = 0; var failed = 0;
        progress?.Report(new("正在准备批量写入", 0, preview.Count));
        var existing = (await _repository.GetLeadsAsync(cancellationToken: cancellationToken)).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var importedRows = new Dictionary<int, Lead>();
        var pending = new Dictionary<string, Lead>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < preview.Count; index++)
        {
            var row = preview[index];
            if (row.Errors.Count > 0) { failed++; continue; }
            try
            {
                Lead lead;
                if (row.DuplicateRowNumber is int duplicateRow && importedRows.TryGetValue(duplicateRow, out var importedLead))
                {
                    lead = importedLead;
                    ApplyImportedValues(lead, row.Values, row.CustomValues, row.PhoneE164, row.PhoneValid, allowStageChange, allowOwnerChange, isNew:false);
                    updated++;
                }
                else if (row.IsDuplicate)
                {
                    if (!existing.TryGetValue(row.DuplicateLeadId, out lead!)) throw new InvalidOperationException("重复客户已不存在。");
                    ApplyImportedValues(lead, row.Values, row.CustomValues, row.PhoneE164, row.PhoneValid, allowStageChange, allowOwnerChange, isNew:false);
                    updated++;
                }
                else
                {
                    lead = new Lead();
                    ApplyImportedValues(lead, row.Values, row.CustomValues, row.PhoneE164, row.PhoneValid, true, true, isNew:true);
                    created++;
                }
                if (!row.IsDuplicate && row.DuplicateRowNumber is null)
                {
                    LeadScoringService.ResetToAiBaseline(
                        lead,
                        "等待 AI 分析",
                        row.PhoneValid ? "等待客户回复或手动运行 AI 分析。" : "核对 WhatsApp 号码后再触达。");
                    lead.AnalysisStatus = AnalysisStatus.NotRun;
                }
                importedRows[row.RowNumber] = lead;
                pending[lead.Id] = lead;
            }
            catch { failed++; }
            if ((index + 1) % 250 == 0 || index + 1 == preview.Count) progress?.Report(new("正在准备批量写入", index + 1, preview.Count));
        }
        var writeProgress = new Progress<int>(completed => progress?.Report(new("正在分批写入本地数据库", completed, pending.Count)));
        await _repository.UpsertLeadsAsync(pending.Values.ToList(), WriteBatchSize, writeProgress, cancellationToken);
        progress?.Report(new("\u6b63\u5728\u540c\u6b65 WhatsApp \u5efa\u8054\u60c5\u51b5", 0, pending.Count));
        await _repository.SynchronizeLeadConnectionsFromInboxAsync(pending.Values.ToList(), cancellationToken);
        var invalid = preview.Count(x => !x.PhoneValid);
        await _repository.SaveImportSummaryAsync(fileName, preview.Count, created, updated, invalid, cancellationToken);
        var pendingWhatsAppChecks = pending.Values.Count(lead =>
            lead.PhoneValid
            && !string.IsNullOrWhiteSpace(lead.PhoneE164)
            && lead.WhatsAppRegistrationStatus == WhatsAppRegistrationStatus.Pending);
        await _repository.LogEventAsync("import_committed", null, null, $"{fileName}; total={preview.Count}; created={created}; updated={updated}; invalid={invalid}; whatsapp_checks={pendingWhatsAppChecks}", cancellationToken);
        LeadsImported?.Invoke(this, new LeadsImportedEventArgs(pending.Keys.ToList(), created, updated));
        return new(preview.Count, created, updated, invalid, pendingWhatsAppChecks, failed);
    }

    private static string BuildChanges(Lead lead, IReadOnlyDictionary<ImportField, string> values, IReadOnlyDictionary<string, string> customValues, NormalizedPhone normalized)
    {
        var changes = new List<string>();
        Add(ImportField.BuyerId, "Buyer ID", lead.BuyerId);
        Add(ImportField.Name, "姓名", lead.Name); Add(ImportField.Company, "公司", lead.Company); Add(ImportField.Country, "国家", lead.Country);
        Add(ImportField.Email, "邮箱", lead.Email); Add(ImportField.ProductInterest, "意向产品", lead.ProductInterest);
        if (normalized.E164.Length > 0 && normalized.E164 != lead.PhoneE164) changes.Add("号码");
        var customChanges = customValues.Count(pair => !string.IsNullOrWhiteSpace(pair.Value) && (!lead.CustomFields.TryGetValue(pair.Key, out var current) || current != pair.Value));
        if (customChanges > 0) changes.Add($"自定义维度 {customChanges} 项");
        return changes.Count == 0 ? "无字段变化" : string.Join("、", changes);
        void Add(ImportField field, string label, string current) { if (values.TryGetValue(field, out var value) && value.Length > 0 && value != current) changes.Add(label); }
    }

    private static void ApplyImportedValues(Lead lead, IReadOnlyDictionary<ImportField, string> values, IReadOnlyDictionary<string, string> customValues, string phone, bool phoneValid, bool allowStageChange, bool allowOwnerChange, bool isNew)
    {
        if (values.TryGetValue(ImportField.BuyerId, out var importedBuyerId))
        {
            var incomingBuyerId = BuyerIdentity.Canonicalize(importedBuyerId);
            var currentBuyerId = BuyerIdentity.Resolve(lead);
            if (isNew
                || currentBuyerId.Length == 0
                || !BuyerIdentity.Normalize(currentBuyerId).Equals(BuyerIdentity.Normalize(incomingBuyerId), StringComparison.OrdinalIgnoreCase))
            {
                lead.BuyerId = incomingBuyerId;
            }
        }
        SetExact(ImportField.Name, x => lead.Name = x);
        SetExact(ImportField.Company, x => lead.Company = x); SetExact(ImportField.Country, x => lead.Country = x);
        SetExact(ImportField.Email, x => lead.Email = x); SetExact(ImportField.ProductInterest, x => lead.ProductInterest = x); SetExact(ImportField.Source, x => lead.Source = x);
        if (values.ContainsKey(ImportField.WhatsApp))
        {
            lead.PhoneE164 = phone;
            lead.PhoneValid = phoneValid;
            lead.QueueWhatsAppRegistrationCheck();
        }
        if (values.TryGetValue(ImportField.EstimatedOrderValue, out var amount))
            lead.EstimatedOrderValue = decimal.TryParse(amount.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAmount) ? Math.Max(0, parsedAmount) : 0;
        if (values.TryGetValue(ImportField.CompanyScale, out var scale)) lead.CompanyScale = LeadScoringService.ParseSignal(scale);
        if (values.TryGetValue(ImportField.PurchasePower, out var power)) lead.PurchasePower = LeadScoringService.ParseSignal(power);
        if (values.TryGetValue(ImportField.ExplicitDemand, out var explicitDemand)) lead.ExplicitDemand = ParseBool(explicitDemand);
        if (values.TryGetValue(ImportField.Tags, out var tags)) lead.Tags = tags.Split([',','，',';','；','|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (allowStageChange && values.TryGetValue(ImportField.Stage, out var stage)) lead.Stage = StageParser.Parse(stage);
        if (allowOwnerChange) SetExact(ImportField.Owner, x => lead.Owner = x);
        MergeCustomDimensions(lead, customValues, isNew);
        BuyerIdentity.Synchronize(lead);
        lead.RegisteredOrConsulted = lead.ExplicitDemand || !string.IsNullOrWhiteSpace(lead.ProductInterest);
        SetExact(ImportField.Notes, x => lead.ManualNotes = x);
        return;
        void SetExact(ImportField field, Action<string> apply) { if (values.TryGetValue(field, out var value)) apply(value.Trim()); }
    }

    private static void MergeCustomDimensions(Lead lead, IReadOnlyDictionary<string, string> incoming, bool isNew)
    {
        if (isNew)
        {
            lead.CustomFields = incoming.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return;
        }

        // Reimports are schema merges, not replacements: incoming columns overwrite the
        // same dimension, new columns extend the schema, and columns absent from the new
        // spreadsheet remain untouched. Canonical aliases share one semantic dimension.
        foreach (var pair in incoming)
        {
            var semanticField = FieldAliases.Suggest(pair.Key);
            if (semanticField is ImportField.Ignore or ImportField.Custom)
            {
                lead.CustomFields[pair.Key] = pair.Value;
                continue;
            }

            var equivalentKeys = lead.CustomFields.Keys
                .Where(key => FieldAliases.Suggest(key) == semanticField)
                .ToList();
            if (equivalentKeys.Count == 0)
            {
                lead.CustomFields[pair.Key] = pair.Value;
                continue;
            }

            foreach (var key in equivalentKeys)
                lead.CustomFields[key] = pair.Value;
        }
    }

    private static bool ParseBool(string value) => value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "是" or "有" or "明确";

    private static ParsedImport ParseCsv(MemoryStream snapshot)
    {
        var bytes = snapshot.ToArray();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.GetEncoding("GB18030").GetString(bytes); }
        text = text.TrimStart('\uFEFF');
        var matrix = Csv.Parse(text);
        var sanitized = 0;
        for (var row = 0; row < matrix.Count; row++)
            for (var column = 0; column < matrix[row].Count; column++)
                matrix[row][column] = Sanitize(matrix[row][column], ref sanitized);
        var sheet = BuildSheet("CSV", matrix);
        if (sheet is not null) sheet.SanitizedFormulaCount = sanitized;
        return new ParsedImport { PreferredSheetName = sheet?.Name ?? "", Sheets = sheet is null ? [] : [sheet] };
    }

    private static ParsedImport ParseXlsx(MemoryStream snapshot)
    {
        snapshot.Position = 0;
        if (snapshot.ReadByte() != 0x50 || snapshot.ReadByte() != 0x4B) throw new InvalidDataException("\u6269\u5c55\u540d\u4e3a .xlsx\uff0c\u4f46\u6587\u4ef6\u5185\u5bb9\u4e0d\u662f\u6709\u6548\u7684 XLSX\u3002");
        AssertSafeXlsx(snapshot);
        var preferredSheetName = ReadActiveSheetName(snapshot) ?? "";
        snapshot.Position = 0;
        using var workbook = new XLWorkbook(snapshot);
        var sheets = new List<ImportSheet>();
        foreach (var worksheet in workbook.Worksheets)
        {
            var range = worksheet.RangeUsed();
            if (range is null) continue;
            var matrix = new List<List<string>>();
            var formulaCount = 0;
            var rows = range.Rows().ToList();
            var sourceHeaders = rows[0].Cells().Select(cell => cell.GetFormattedString()).ToList();
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var values = new List<string>();
                var cells = rows[rowIndex].Cells().ToList();
                for (var columnIndex = 0; columnIndex < cells.Count; columnIndex++)
                {
                    var cell = cells[columnIndex];
                    if (cell.HasFormula) { values.Add("'=" + cell.FormulaA1); formulaCount++; }
                    else if (rowIndex > 0
                             && columnIndex < sourceHeaders.Count
                             && FieldAliases.Suggest(sourceHeaders[columnIndex]) == ImportField.WhatsApp
                             && cell.DataType == XLDataType.Number)
                    {
                        values.Add(Sanitize(cell.GetDouble().ToString("0", CultureInfo.InvariantCulture), ref formulaCount));
                    }
                    else values.Add(Sanitize(cell.GetFormattedString(), ref formulaCount));
                }
                matrix.Add(values);
            }
            if (BuildSheet(worksheet.Name, matrix) is { } parsed) { parsed.SanitizedFormulaCount += formulaCount; sheets.Add(parsed); }
        }
        return new ParsedImport { PreferredSheetName = preferredSheetName, Sheets = sheets };
    }

    private static string? ReadActiveSheetName(Stream snapshot)
    {
        snapshot.Position = 0;
        using var archive = new ZipArchive(snapshot, ZipArchiveMode.Read, leaveOpen:true);
        var entry = archive.GetEntry("xl/workbook.xml");
        if (entry is null) return null;
        using var entryStream = entry.Open();
        var document = XDocument.Load(entryStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var activeTab = (int?)document.Descendants(ns + "workbookView").FirstOrDefault()?.Attribute("activeTab") ?? 0;
        var sheets = document.Descendants(ns + "sheet").ToList();
        return activeTab >= 0 && activeTab < sheets.Count ? (string?)sheets[activeTab].Attribute("name") : null;
    }

    private static void AssertSafeXlsx(Stream snapshot)
    {
        snapshot.Position = 0;
        using var archive = new ZipArchive(snapshot, ZipArchiveMode.Read, leaveOpen:true);
        if (archive.Entries.Count is 0 or > 2000) throw new InvalidDataException("XLSX 压缩包条目数量异常。");
        long compressed = 0; long uncompressed = 0;
        foreach (var entry in archive.Entries)
        {
            compressed += Math.Max(0, entry.CompressedLength); uncompressed += Math.Max(0, entry.Length);
            if (uncompressed > 512L * 1024 * 1024 || uncompressed / (double)Math.Max(1, compressed) > 200d) throw new InvalidDataException("XLSX 解压体积或压缩比超过资源保护上限。");
        }
    }

    private static MemoryStream OpenSharedReadSnapshot(string filePath, long expectedLength)
    {
        IOException? lastError = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var source = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 128 * 1024,
                    FileOptions.SequentialScan);
                var snapshot = new MemoryStream((int)Math.Min(Math.Max(expectedLength, 0), int.MaxValue));
                source.CopyTo(snapshot);
                snapshot.Position = 0;
                return snapshot;
            }
            catch (IOException error)
            {
                lastError = error;
                if (attempt == 4) break;
                Thread.Sleep(150 * attempt);
            }
        }
        throw new IOException("\u65e0\u6cd5\u8bfb\u53d6\u8868\u683c\u3002\u8bf7\u7b49\u5f85 WPS/Excel \u4fdd\u5b58\u5b8c\u6210\u540e\u91cd\u8bd5\uff1b\u8868\u683c\u4fdd\u6301\u6253\u5f00\u4e0d\u5f71\u54cd\u5bfc\u5165\u3002", lastError);
    }

    private static ImportSheet? BuildSheet(string name, IReadOnlyList<List<string>> matrix)
    {
        var nonEmpty = matrix.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (nonEmpty.Count == 0) return null;
        var headers = UniqueHeaders(nonEmpty[0]);
        var rows = nonEmpty.Skip(1).Select(row => headers.Select((h, i) => new { h, v = i < row.Count ? row[i].Trim() : "" }).ToDictionary(x => x.h, x => x.v)).ToList();
        if ((long)Math.Max(1, rows.Count) * Math.Max(1, headers.Count) > MaxCells) throw new InvalidDataException($"工作表 {name} 超过 {MaxCells:N0} 个单元格资源保护上限；请拆分为多个文件导入。");
        return new ImportSheet { Name = name, Headers = headers, Rows = rows };
    }

    private static List<string> UniqueHeaders(IReadOnlyList<string> input)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return input.Select((value, index) =>
        {
            var cleaned = CustomerDimensionCatalog.NormalizeForStorage(value);
            var baseName = string.IsNullOrWhiteSpace(cleaned) ? $"未命名列 {index + 1}" : cleaned;
            counts[baseName] = counts.GetValueOrDefault(baseName) + 1;
            return counts[baseName] == 1 ? baseName : $"{baseName} ({counts[baseName]})";
        }).ToList();
    }

    private static string Sanitize(string value, ref int count)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0 && "=+@-".Contains(trimmed[0])) { count++; return "'" + trimmed; }
        return trimmed;
    }
}

internal static class FieldAliases
{
    private static readonly Dictionary<ImportField, string[]> Aliases = new()
    {
        [ImportField.BuyerId]=["buyerid","buyeridentifier","buyeraccountid","dhgatebuyerid","customerid","customeridentifier","买家id","客户id","采购商id","买家编号","客户编号","采购商编号"],
        [ImportField.Name]=["name","fullname","contactname","buyername","buyernickname","姓名","联系人","客户姓名","买家姓名","买家昵称"],
        [ImportField.Company]=["company","companyname","business","organization","公司","公司名称","企业名称"],
        [ImportField.Country]=["country","market","region","countryemail","国家","国家地区","国家邮箱","市场","地区"],
        [ImportField.WhatsApp]=["whatsapp","whatsappnumber","whatsapp号码","phone","mobile","tel","电话","电话号码","手机号","手机","联系电话","号码"],
        [ImportField.Email]=["email","emailaddress","mail","邮箱","电子邮箱"],
        [ImportField.ProductInterest]=["productinterest","interestedproduct","product","sku","产品兴趣","意向产品","产品","询盘产品"],
        [ImportField.EstimatedOrderValue]=["estimatedordervalue","estimatedvalue","ordervalue","dealvalue","budget","采购金额","订单金额","预计订单额","预计金额","预算"],
        [ImportField.CompanyScale]=["companyscale","employees","companysize","公司规模","员工人数","企业规模"],
        [ImportField.PurchasePower]=["purchasepower","buyingpower","采购能力","购买力"],
        [ImportField.ExplicitDemand]=["explicitdemand","demand","requirement","明确需求","需求","采购需求"],
        [ImportField.Source]=["source","leadsource","channel","来源","线索来源","渠道"],
        [ImportField.Owner]=["owner","现owner","currentowner","assignee","salesowner","负责人","销售负责人","跟进人"],
        [ImportField.Stage]=["stage","leadstage","status","阶段","商机阶段","跟进阶段","状态"],
        [ImportField.Tags]=["tags","tag","labels","标签","客户标签"],
        [ImportField.Notes]=["notes","note","remark","comments","备注","说明"]
    };
    private static readonly Dictionary<string, ImportField> Lookup = Aliases.SelectMany(p => p.Value.Select(v => (key: Normalize(v), p.Key))).ToDictionary(x => x.key, x => x.Key);
    public static ImportField Suggest(string header)
    {
        var normalized = Normalize(header);
        if (Lookup.TryGetValue(normalized, out var field)) return field;
        foreach (var segment in header.Split(['/','|','｜',':','：'])) if (Lookup.TryGetValue(Normalize(segment), out field)) return field;
        var prefix = Lookup
            .Where(pair => pair.Key.Length >= 3 && normalized.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Key.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(prefix.Key)) return prefix.Value;
        return ImportField.Custom;
    }
    private static string Normalize(string value) =>
        CustomerDimensionCatalog.NormalizeSemanticKey(value);
}

internal static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (cell.Length > 1024 * 1024) throw new InvalidDataException("CSV 单个字段超过 1MB 安全限制。");
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); if (row.Any(x => x.Length > 0)) rows.Add(row); row = [];
            }
            else cell.Append(c);
        }
        row.Add(cell.ToString()); if (row.Any(x => x.Length > 0)) rows.Add(row);
        if (quoted) throw new InvalidDataException("CSV 引号未闭合。");
        return rows;
    }
}
