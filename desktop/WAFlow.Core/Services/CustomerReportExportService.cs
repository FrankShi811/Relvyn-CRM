using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;
using WpColor = DocumentFormat.OpenXml.Wordprocessing.Color;

namespace WAFlow.Core.Services;

public sealed class CustomerReportExportService
{
    private readonly LocalRepository _repository;
    private readonly Func<CancellationToken, Task>? _beforeFinalValidation;

    public CustomerReportExportService(
        LocalRepository repository,
        Func<CancellationToken, Task>? beforeFinalValidation = null)
    {
        _repository = repository;
        _beforeFinalValidation = beforeFinalValidation;
    }

    public async Task ExportWordAsync(CustomerAnalysisReport report, string path, CancellationToken cancellationToken = default)
    {
        await ExportAsync(report, path, ".docx", "Word", WordCustomerReportRenderer.Render, cancellationToken);
    }

    public async Task ExportPdfAsync(CustomerAnalysisReport report, string path, CancellationToken cancellationToken = default)
    {
        await ExportAsync(report, path, ".pdf", "PDF", PdfCustomerReportRenderer.Render, cancellationToken);
    }

    private async Task ExportAsync(
        CustomerAnalysisReport report,
        string path,
        string extension,
        string format,
        Action<CustomerAnalysisReport, string> render,
        CancellationToken cancellationToken)
    {
        var current = await ResolveCurrentReportAsync(report, cancellationToken);
        EnsureExportable(current, path, extension);
        var targetPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(targetPath)!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}.rendering{extension}");
        var backupPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.backup");
        var replacedTarget = false;
        var hadExistingTarget = File.Exists(targetPath);
        var exportCommitted = false;
        try
        {
            await Task.Run(() => render(current, temporaryPath), cancellationToken);
            if (_beforeFinalValidation is not null)
                await _beforeFinalValidation(cancellationToken);

            current = await ResolveCurrentReportAsync(report, cancellationToken);
            EnsureExportable(current, targetPath, extension);
            if (hadExistingTarget)
                File.Replace(temporaryPath, targetPath, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, targetPath);
            replacedTarget = true;

            var recorded = await AppendExportHistoryAsync(current, targetPath, format, cancellationToken);
            exportCommitted = true;
            await RecordExportAuditAsync(recorded.Report, recorded.Export, cancellationToken);
        }
        catch
        {
            if (replacedTarget && !exportCommitted)
            {
                if (hadExistingTarget && File.Exists(backupPath))
                    File.Replace(backupPath, targetPath, null, ignoreMetadataErrors: true);
                else if (File.Exists(targetPath))
                    File.Delete(targetPath);
            }
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    private async Task<CustomerAnalysisReport> ResolveCurrentReportAsync(
        CustomerAnalysisReport report,
        CancellationToken cancellationToken)
    {
        var current = await CustomerAnalysisFreshness.GetCurrentAsync(
            _repository,
            report.Id,
            cancellationToken);
        return current ?? throw new InvalidOperationException("报告不存在或已被删除，不能导出。");
    }

    private async Task<(CustomerAnalysisReport Report, CustomerReportExportRecord Export)> AppendExportHistoryAsync(
        CustomerAnalysisReport report,
        string path,
        string format,
        CancellationToken cancellationToken)
    {
        var current = await ResolveCurrentReportAsync(report, cancellationToken);
        EnsureExportable(current, path, Path.GetExtension(path));
        var info = new FileInfo(path);
        var export = new CustomerReportExportRecord { Format = format, FilePath = info.FullName, FileSize = info.Length };
        if (!await _repository.AppendCustomerReportExportAsync(current.Id, export, cancellationToken))
            throw new InvalidOperationException(CustomerAnalysisFreshness.StaleReason);
        return (current, export);
    }

    private async Task RecordExportAuditAsync(
        CustomerAnalysisReport report,
        CustomerReportExportRecord export,
        CancellationToken cancellationToken)
    {
        await _repository.SaveCustomerAnalysisStepAsync(new CustomerAnalysisReportStep
        {
            ReportId = report.Id, StepKey = "format_rendering", Sequence = 6,
            Status = CustomerReportStepStatus.Succeeded,
            ResultJson = Json.Serialize(export)
        }, cancellationToken);
        await _repository.LogEventAsync(
            "customer_intelligence_report_exported",
            report.CustomerId,
            null,
            $"report_id={report.Id};version={report.Version};format={export.Format};path={export.FilePath}",
            cancellationToken);
    }

    private static void EnsureExportable(CustomerAnalysisReport report, string path, string extension)
    {
        if (report.Status == CustomerReportStatus.Stale)
            throw new InvalidOperationException(CustomerAnalysisFreshness.StaleReason);
        if (report.Status != CustomerReportStatus.Succeeded) throw new InvalidOperationException("报告尚未生成完成，不能导出。");
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"导出路径必须使用 {extension} 扩展名。", nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }
}

internal static class WordCustomerReportRenderer
{
    private const string Blue = "2E74B5";
    private const string DarkBlue = "1F4D78";
    private const string Navy = "0B2545";
    private const string Muted = "5F6B75";
    private const string Light = "F2F4F7";
    private const string Green = "0F8F6A";
    private const string Red = "9B1C1C";

    public static void Render(CustomerAnalysisReport source, string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        AddStyles(main);
        AddNumbering(main);
        var body = main.Document.Body!;
        AddCover(body, source);
        AddPageBreak(body);
        AddReport(body, source);
        body.Append(CreateSectionProperties(main, source));
        main.Document.Save();
    }

    private static void AddCover(Body body, CustomerAnalysisReport source)
    {
        body.Append(Paragraph("AI SALES OS", 11, Green, true, JustificationValues.Center, before: 1800, after: 260));
        body.Append(Paragraph("客户背景调查报告", 30, Navy, true, JustificationValues.Center, after: 120));
        body.Append(Paragraph("Customer Intelligence Report", 14, DarkBlue, false, JustificationValues.Center, after: 520));
        body.Append(Paragraph(source.CustomerName, 20, Navy, true, JustificationValues.Center, after: 120));
        body.Append(Paragraph($"{source.SourceSnapshot.Lead.Country}  ·  V{source.Version}  ·  {source.CreatedTime.LocalDateTime:yyyy年MM月dd日}", 10.5, Muted, false, JustificationValues.Center, after: 900));
        body.Append(Paragraph("由 AI Sales OS 基于 CRM、WhatsApp、Lead Intelligence、自动化触达与客户历史轨迹生成", 9.5, Muted, false, JustificationValues.Center, after: 80));
        body.Append(Paragraph($"AI 模型：{source.AiModel}", 9, Muted, false, JustificationValues.Center, after: 80));
        body.Append(Paragraph("内部销售决策资料 · 请结合销售人员判断使用", 9, Red, false, JustificationValues.Center));
    }

    private static void AddReport(Body body, CustomerAnalysisReport source)
    {
        var lead = source.SourceSnapshot.Lead;
        var report = source.Report;
        body.Append(Heading("客户概览", 1));
        body.Append(KeyValueTable([
            ("客户", source.CustomerName), ("国家", lead.Country), ("WhatsApp", lead.PhoneE164),
            ("当前等级", report.OpportunityJudgment.Grade), ("AI评分", $"{report.OpportunityJudgment.AiScore}/100"),
            ("成交概率", $"{report.OpportunityJudgment.DealProbability}%"), ("CRM阶段", lead.StageLabel), ("负责人", lead.Owner)
        ]));
        body.Append(Callout(report.ExecutiveSummary.OneLinePositioning, "E8F5F0", Green));
        body.Append(Heading("1. 客户概览（Executive Summary）", 1));
        body.Append(KeyValueTable([
            ("客户类型", report.ExecutiveSummary.CustomerType), ("商业阶段", report.ExecutiveSummary.BusinessStage),
            ("综合价值判断", report.ExecutiveSummary.OverallValueJudgment), ("当前销售建议", report.ExecutiveSummary.CurrentSalesRecommendation)
        ], 1900));

        body.Append(Heading("2. 客户基础画像", 1));
        body.Append(KeyValueTable([
            ("客户类型", report.BasicProfile.CustomerType), ("商业模式", Join(report.BasicProfile.BusinessModels)),
            ("产品方向", report.BasicProfile.ProductDirection), ("经营规模", report.BasicProfile.OperatingScale),
            ("发展阶段", report.BasicProfile.DevelopmentStage)
        ], 1900));

        body.Append(Heading("3. 客户商业背景分析", 1));
        body.Append(Heading("当前业务模式", 2)); body.Append(BodyParagraph(report.BusinessBackground.CurrentBusinessModel));
        AddBullets(body, "核心优势", report.BusinessBackground.CoreAdvantages);
        AddBullets(body, "当前限制", report.BusinessBackground.CurrentLimitations);
        AddBullets(body, "未来增长空间", report.BusinessBackground.GrowthOpportunities);

        body.Append(Heading("4. 当前痛点分析", 1));
        AddBullets(body, "表层痛点（客户表达或直接事实）", report.PainAnalysis.SurfacePains);
        AddBullets(body, "深层商业问题（AI判断）", report.PainAnalysis.DeepBusinessProblems, "FFF8E8");

        body.Append(Heading("5. 购买动机分析", 1));
        AddBullets(body, "产生兴趣的原因", report.PurchaseMotivation.InterestReasons);
        AddBullets(body, "当前触发事件", report.PurchaseMotivation.TriggerEvents);
        AddBullets(body, "决策关键因素", report.PurchaseMotivation.DecisionFactors);

        body.Append(Heading("6. WhatsApp沟通分析", 1));
        body.Append(KeyValueTable([
            ("沟通积极度", report.WhatsAppAnalysis.EngagementLevel),
            ("关注主题", Join(report.WhatsAppAnalysis.FocusTopics)),
            ("需求与合作信号", Join(report.WhatsAppAnalysis.PurchaseSignals)),
            ("主要顾虑", Join(report.WhatsAppAnalysis.Concerns))
        ], 1900));
        foreach (var quote in report.WhatsAppAnalysis.Quotes.Take(8))
        {
            body.Append(Heading("客户原话", 3));
            body.Append(Callout($"“{quote.Original}”\n中文含义：{quote.ChineseMeaning}\nAI分析：{quote.AiAnalysis}", "F4F6F9", DarkBlue));
        }

        body.Append(Heading("7. AI商机判断", 1));
        body.Append(ScoreCard(report.OpportunityJudgment));
        body.Append(DimensionTable(report.OpportunityJudgment.DimensionScores));
        AddBullets(body, "正向因素", report.OpportunityJudgment.PositiveFactors, "E8F5F0");
        AddBullets(body, "负向因素", report.OpportunityJudgment.NegativeFactors, "FDECEC");

        body.Append(Heading("8. 产品匹配分析", 1));
        AddBullets(body, "高匹配点", report.ProductFit.HighMatchPoints);
        AddBullets(body, "低匹配点", report.ProductFit.LowMatchPoints);
        AddBullets(body, "需要验证的问题", report.ProductFit.QuestionsToValidate, "FFF8E8");

        body.Append(Heading("9. 销售推进建议", 1));
        body.Append(SalesActionTable(report.SalesStrategy.Actions));
        body.Append(Heading("推荐话术", 2)); body.Append(Callout(report.SalesStrategy.RecommendedTalkTrack, "E8F5F0", Green));
        AddBullets(body, "待解决问题", report.SalesStrategy.PendingQuestions);

        body.Append(Heading("10. 风险分析", 1));
        AddBullets(body, "成交风险", report.RiskAnalysis.DealRisks, "FDECEC");
        AddBullets(body, "使用风险", report.RiskAnalysis.AdoptionRisks, "FFF8E8");
        AddBullets(body, "流失风险", report.RiskAnalysis.ChurnRisks, "FDECEC");

        body.Append(Heading("11. AI总结", 1));
        body.Append(Callout(report.ManagementSummary, "E8EEF5", Navy));
        body.Append(Heading("证据说明", 2));
        body.Append(BodyParagraph("事实来自 CRM、Excel 导入字段、WhatsApp 原始消息、Lead Intelligence、自动化触达及系统审计轨迹；AI判断与销售建议基于上述事实推导，不会写回或覆盖原始客户数据。"));
        body.Append(Callout($"数据覆盖：WhatsApp {source.SourceSnapshot.WhatsAppMessages.Count} 条 · 邮件 {source.SourceSnapshot.EmailMessages.Count} 条 · 自动化触达 {source.SourceSnapshot.CampaignTouches.Count} 次 · 客户轨迹 {source.SourceSnapshot.Timeline.Count} 条 · 历史 AI 分析 {source.SourceSnapshot.LeadAnalysisHistory.Count} 次\n快照时间：{source.SourceSnapshot.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm}", "F4F6F9", DarkBlue));
        body.Append(Heading("证据账本（精选）", 2));
        foreach (var statement in report.EvidenceLedger.Where(item => !string.IsNullOrWhiteSpace(item.Statement)).Take(18))
            body.Append(Callout($"[{(string.IsNullOrWhiteSpace(statement.Nature) ? "事实" : statement.Nature)}] {statement.Statement}\n来源：{statement.Source} · 证据：{statement.Evidence}", "F4F6F9", DarkBlue));
        body.Append(Heading("业务知识引用", 2));
        if (report.KnowledgeReferences.Count == 0)
            body.Append(BodyParagraph("本版本未引用已批准业务知识。", Muted));
        else
            foreach (var reference in report.KnowledgeReferences.Take(20))
                body.Append(Callout($"{reference.CitationLabel}\n作用域：{reference.Scope.Label} · 相关度：{reference.RelevanceScore:P0}\n{reference.Content}", "F4F6F9", DarkBlue));
    }

    private static void AddBullets(Body body, string title, IEnumerable<string> items, string? fill = null)
    {
        body.Append(Heading(title, 2));
        var values = items.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (values.Count == 0) { body.Append(BodyParagraph("暂无充分信息。", Muted)); return; }
        if (fill is not null)
        {
            body.Append(Callout(string.Join("\n", values.Select(value => $"• {value}")), fill, fill == "FDECEC" ? Red : Navy));
            return;
        }
        foreach (var item in values) body.Append(Bullet(item));
    }

    private static Paragraph Heading(string text, int level) => new(
        new ParagraphProperties(new ParagraphStyleId { Val = $"Heading{Math.Clamp(level, 1, 3)}" }, new KeepNext()),
        Run(text));

    private static Paragraph BodyParagraph(string text, string color = "1F2933") => new(
        new ParagraphProperties(new SpacingBetweenLines { After = "120", Line = "264", LineRule = LineSpacingRuleValues.Auto }),
        Run(text, 11, color));

    private static Paragraph Bullet(string text) => new(
        new ParagraphProperties(
            new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 }),
            new SpacingBetweenLines { After = "160", Line = "280", LineRule = LineSpacingRuleValues.Auto }),
        Run(text, 11, "1F2933"));

    private static Paragraph Paragraph(string text, double size, string color, bool bold, JustificationValues align, int before = 0, int after = 0)
    {
        return new Paragraph(
            new ParagraphProperties(new Justification { Val = align }, new SpacingBetweenLines { Before = before.ToString(), After = after.ToString() }),
            Run(text, size, color, bold));
    }

    private static Run Run(string text, double size = 11, string color = "1F2933", bool bold = false)
    {
        var properties = new RunProperties(
            new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft YaHei" },
            new FontSize { Val = ((int)Math.Round(size * 2)).ToString() },
            new FontSizeComplexScript { Val = ((int)Math.Round(size * 2)).ToString() },
            new WpColor { Val = color });
        if (bold) properties.AppendChild(new Bold());
        return new Run(properties, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Table KeyValueTable(IEnumerable<(string Label, string Value)> items, int labelWidth = 1550)
    {
        var rows = items.Chunk(2).ToList();
        var widths = new[] { labelWidth, 4680 - labelWidth, labelWidth, 4680 - labelWidth };
        var table = TableBase(widths, Light);
        foreach (var pair in rows)
        {
            var row = new TableRow();
            for (var i = 0; i < 2; i++)
            {
                if (i < pair.Length)
                {
                    row.Append(Cell(pair[i].Label, widths[i * 2], Light, true, DarkBlue));
                    row.Append(Cell(pair[i].Value, widths[i * 2 + 1], "FFFFFF", false, "1F2933"));
                }
                else
                {
                    row.Append(Cell("", widths[i * 2], Light)); row.Append(Cell("", widths[i * 2 + 1], "FFFFFF"));
                }
            }
            table.Append(row);
        }
        return table;
    }

    private static Table ScoreCard(CustomerOpportunityJudgment opportunity)
    {
        var widths = new[] { 3120, 3120, 3120 };
        var table = TableBase(widths, "E8F5F0");
        var row = new TableRow();
        row.Append(Cell($"AI评分\n{opportunity.AiScore}/100", widths[0], "E8F5F0", true, Green, 14));
        row.Append(Cell($"客户等级\n{opportunity.Grade}级", widths[1], "E8EEF5", true, Navy, 14));
        row.Append(Cell($"成交概率\n{opportunity.DealProbability}%", widths[2], "FFF8E8", true, "7A5A00", 14));
        table.Append(row); return table;
    }

    private static Table DimensionTable(IEnumerable<LeadFactor> factors)
    {
        var widths = new[] { 2600, 1100, 1100, 4560 };
        var table = TableBase(widths, Light);
        table.Append(new TableRow(Cell("评分维度", widths[0], Light, true, DarkBlue), Cell("得分", widths[1], Light, true, DarkBlue), Cell("满分", widths[2], Light, true, DarkBlue), Cell("依据", widths[3], Light, true, DarkBlue)));
        foreach (var factor in factors)
            table.Append(new TableRow(Cell(DimensionName(factor.Key), widths[0]), Cell(factor.Score.ToString(), widths[1]), Cell(factor.MaxScore.ToString(), widths[2]), Cell(factor.Rationale, widths[3])));
        return table;
    }

    private static Table SalesActionTable(IEnumerable<CustomerSalesAction> actions)
    {
        var widths = new[] { 1150, 2750, 2750, 2710 };
        var table = TableBase(widths, Light);
        table.Append(new TableRow(Cell("时间", widths[0], Light, true, DarkBlue), Cell("行动", widths[1], Light, true, DarkBlue), Cell("依据", widths[2], Light, true, DarkBlue), Cell("成功标准", widths[3], Light, true, DarkBlue)));
        foreach (var item in actions)
            table.Append(new TableRow(Cell(item.Timeframe, widths[0], "E8F5F0", true, Green), Cell(item.Action, widths[1]), Cell(item.Rationale, widths[2]), Cell(item.SuccessCriterion, widths[3])));
        return table;
    }

    private static Table Callout(string text, string fill, string color)
    {
        var table = TableBase([9360], fill);
        table.Append(new TableRow(Cell(text, 9360, fill, false, color, 11)));
        return table;
    }

    private static Table TableBase(IReadOnlyList<int> widths, string headerFill)
    {
        var table = new Table();
        table.Append(new TableProperties(
            new TableWidth { Width = "9360", Type = TableWidthUnitValues.Dxa },
            new TableIndentation { Width = 120, Type = TableWidthUnitValues.Dxa },
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "D7DEE3" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "D7DEE3" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "D7DEE3" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "D7DEE3" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "E5E9EC" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "E5E9EC" })),
            new TableGrid(widths.Select(width => new GridColumn { Width = width.ToString() })));
        return table;
    }

    private static TableCell Cell(string text, int width, string fill = "FFFFFF", bool bold = false, string color = "1F2933", double size = 10)
    {
        var cell = new TableCell();
        cell.Append(new TableCellProperties(
            new TableCellWidth { Width = width.ToString(), Type = TableWidthUnitValues.Dxa },
            new Shading { Fill = fill, Val = ShadingPatternValues.Clear },
            new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center },
            new TableCellMargin(
                new TopMargin { Width = "100", Type = TableWidthUnitValues.Dxa }, new BottomMargin { Width = "100", Type = TableWidthUnitValues.Dxa },
                new StartMargin { Width = "120", Type = TableWidthUnitValues.Dxa }, new EndMargin { Width = "120", Type = TableWidthUnitValues.Dxa })));
        foreach (var line in text.Replace("\r", "").Split('\n'))
            cell.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "40", Line = "240", LineRule = LineSpacingRuleValues.Auto }), Run(line, size, color, bold)));
        return cell;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var normal = new Style(new StyleName { Val = "Normal" }, new StyleParagraphProperties(new SpacingBetweenLines { After = "120", Line = "264", LineRule = LineSpacingRuleValues.Auto }),
            new StyleRunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft YaHei" }, new FontSize { Val = "22" }, new WpColor { Val = "1F2933" })) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true };
        part.Styles = new Styles(normal, HeadingStyle("Heading1", "heading 1", 32, Blue, 320, 160), HeadingStyle("Heading2", "heading 2", 26, Blue, 240, 120), HeadingStyle("Heading3", "heading 3", 24, DarkBlue, 160, 80));
        part.Styles.Save();
    }

    private static Style HeadingStyle(string id, string name, int halfPoints, string color, int before, int after) => new(
        new StyleName { Val = name }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" },
        new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = before.ToString(), After = after.ToString() }),
        new StyleRunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft YaHei" }, new Bold(), new WpColor { Val = color }, new FontSize { Val = halfPoints.ToString() }))
        { Type = StyleValues.Paragraph, StyleId = id };

    private static void AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();
        var level = new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "•" },
            new LevelJustification { Val = LevelJustificationValues.Left }, new PreviousParagraphProperties(new Indentation { Left = "720", Hanging = "360" })) { LevelIndex = 0 };
        var abstractNum = new AbstractNum(level) { AbstractNumberId = 1 };
        var instance = new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 };
        part.Numbering = new Numbering(abstractNum, instance); part.Numbering.Save();
    }

    private static SectionProperties CreateSectionProperties(MainDocumentPart main, CustomerAnalysisReport report)
    {
        var header = main.AddNewPart<HeaderPart>();
        header.Header = new Header(new Paragraph(new ParagraphProperties(new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "D7DEE3" })), Run("AI SALES OS · 客户智能分析中心", 8.5, Muted, true)));
        header.Header.Save();
        var footer = main.AddNewPart<FooterPart>();
        var pageField = new Run(new FieldChar { FieldCharType = FieldCharValues.Begin });
        var code = new Run(new FieldCode(" PAGE "));
        var separate = new Run(new FieldChar { FieldCharType = FieldCharValues.Separate });
        var number = Run("1", 8.5, Muted);
        var end = new Run(new FieldChar { FieldCharType = FieldCharValues.End });
        footer.Footer = new Footer(new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Right }), Run($"{report.CustomerName} · V{report.Version} · 第 ", 8.5, Muted), pageField, code, separate, number, end, Run(" 页", 8.5, Muted)));
        footer.Footer.Save();
        return new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
            new PageSize { Width = 12240, Height = 15840 },
            new PageMargin { Top = 1440, Right = 1440, Bottom = 1440, Left = 1440, Header = 708, Footer = 708, Gutter = 0 });
    }

    private static void AddPageBreak(Body body) => body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
    private static string Join(IEnumerable<string> values) => string.Join("；", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string DimensionName(string key) => key switch
    {
        "paid_marketing_willingness" => "增长投入意愿", "supply_stability" => "运营与交付稳定性", "ecommerce_foundation" => "相关业务基础",
        "private_traffic" => "客户触达能力", "existing_sales" => "商业验证程度", "materials_readiness" => "合作准备度", _ => key
    };
}

internal static class PdfCustomerReportRenderer
{
    private static readonly object FontLock = new();

    public static void Render(CustomerAnalysisReport report, string path)
    {
        EnsureFonts();
        using var document = new PdfDocument();
        document.Info.Title = $"{report.CustomerName} - 客户背景调查报告";
        document.Info.Author = "AI Sales OS";
        var canvas = new PdfReportCanvas(document, report);
        canvas.DrawCover();
        canvas.DrawReport();
        canvas.Finish();
        document.Save(path);
    }

    private static void EnsureFonts()
    {
        lock (FontLock)
        {
            if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new BundledChineseFontResolver();
        }
    }

    private sealed class BundledChineseFontResolver : IFontResolver
    {
        private const string RegularFace = "NotoSansCJKscRegular";
        private const string BoldFace = "NotoSansCJKscBold";
        private static readonly Lazy<byte[]> RegularFont = new(() => ReadResource("WAFlow.Assets.Fonts.NotoSansCJKsc-Regular.otf"));
        private static readonly Lazy<byte[]> BoldFont = new(() => ReadResource("WAFlow.Assets.Fonts.NotoSansCJKsc-Bold.otf"));

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) => new(isBold ? BoldFace : RegularFace);

        public byte[]? GetFont(string faceName) => faceName switch
        {
            BoldFace => BoldFont.Value,
            RegularFace => RegularFont.Value,
            _ => null
        };

        private static byte[] ReadResource(string resourceName)
        {
            using var stream = typeof(CustomerReportExportService).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Bundled PDF font resource is missing: {resourceName}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    private sealed class PdfReportCanvas
    {
        private readonly PdfDocument _document;
        private readonly CustomerAnalysisReport _source;
        private readonly XFont _body = new("AISalesOS", 9.5, XFontStyleEx.Regular);
        private readonly XFont _small = new("AISalesOS", 8, XFontStyleEx.Regular);
        private readonly XFont _bold = new("AISalesOS", 10, XFontStyleEx.Bold);
        private readonly XFont _h1 = new("AISalesOS", 16, XFontStyleEx.Bold);
        private readonly XFont _h2 = new("AISalesOS", 12, XFontStyleEx.Bold);
        private XGraphics? _gfx;
        private PdfPage? _page;
        private double _y;
        private int _pageNumber;
        private const double Left = 54;
        private const double Right = 54;
        private const double Top = 56;
        private const double Bottom = 58;

        public PdfReportCanvas(PdfDocument document, CustomerAnalysisReport source) { _document = document; _source = source; }

        public void DrawCover()
        {
            NewPage(false);
            var width = _page!.Width.Point;
            _gfx!.DrawString("AI SALES OS", new XFont("AISalesOS", 11, XFontStyleEx.Bold), new XSolidBrush(XColor.FromArgb(15, 143, 106)), new XRect(0, 155, width, 30), XStringFormats.Center);
            _gfx.DrawString("客户背景调查报告", new XFont("AISalesOS", 29, XFontStyleEx.Bold), new XSolidBrush(XColor.FromArgb(11, 37, 69)), new XRect(0, 205, width, 50), XStringFormats.Center);
            _gfx.DrawString("Customer Intelligence Report", new XFont("AISalesOS", 13, XFontStyleEx.Regular), new XSolidBrush(XColor.FromArgb(31, 77, 120)), new XRect(0, 260, width, 30), XStringFormats.Center);
            _gfx.DrawString(_source.CustomerName, new XFont("AISalesOS", 20, XFontStyleEx.Bold), new XSolidBrush(XColor.FromArgb(11, 37, 69)), new XRect(0, 355, width, 36), XStringFormats.Center);
            _gfx.DrawString($"{_source.SourceSnapshot.Lead.Country}  ·  V{_source.Version}  ·  {_source.CreatedTime.LocalDateTime:yyyy年MM月dd日}", _body, XBrushes.DimGray, new XRect(0, 398, width, 30), XStringFormats.Center);
            DrawWrapped("由 AI Sales OS 基于 CRM、WhatsApp、Lead Intelligence、自动化触达与客户历史轨迹生成", _small, XBrushes.DimGray, 92, 515, width - 184, 16, XStringFormats.Center);
            _gfx.DrawString($"AI 模型：{_source.AiModel}", _small, XBrushes.DimGray, new XRect(0, 558, width, 20), XStringFormats.Center);
            _gfx.DrawString("内部销售决策资料 · 请结合销售人员判断使用", _small, new XSolidBrush(XColor.FromArgb(155, 28, 28)), new XRect(0, 620, width, 20), XStringFormats.Center);
        }

        public void DrawReport()
        {
            NewPage();
            var report = _source.Report;
            Heading("客户概览", 1);
            ScoreCards(report.OpportunityJudgment);
            Callout(report.ExecutiveSummary.OneLinePositioning, XColor.FromArgb(232, 245, 240), XColor.FromArgb(15, 103, 81));
            Section("1. 客户概览（Executive Summary）", [
                $"客户类型：{report.ExecutiveSummary.CustomerType}", $"商业阶段：{report.ExecutiveSummary.BusinessStage}",
                $"综合价值判断：{report.ExecutiveSummary.OverallValueJudgment}", $"当前销售建议：{report.ExecutiveSummary.CurrentSalesRecommendation}"]);
            Section("2. 客户基础画像", [
                $"客户类型：{report.BasicProfile.CustomerType}", $"商业模式：{Join(report.BasicProfile.BusinessModels)}",
                $"产品方向：{report.BasicProfile.ProductDirection}", $"经营规模：{report.BasicProfile.OperatingScale}", $"当前发展阶段：{report.BasicProfile.DevelopmentStage}"]);
            Heading("3. 客户商业背景分析", 1);
            LabelText("当前业务模式", report.BusinessBackground.CurrentBusinessModel);
            BulletGroup("核心优势", report.BusinessBackground.CoreAdvantages);
            BulletGroup("当前限制", report.BusinessBackground.CurrentLimitations);
            BulletGroup("未来增长空间", report.BusinessBackground.GrowthOpportunities);
            Heading("4. 当前痛点分析", 1);
            BulletGroup("表层痛点（事实）", report.PainAnalysis.SurfacePains);
            BulletGroup("深层商业问题（AI判断）", report.PainAnalysis.DeepBusinessProblems, XColor.FromArgb(255, 248, 232));
            Heading("5. 购买动机分析", 1);
            BulletGroup("产生兴趣的原因", report.PurchaseMotivation.InterestReasons);
            BulletGroup("当前触发事件", report.PurchaseMotivation.TriggerEvents);
            BulletGroup("决策关键因素", report.PurchaseMotivation.DecisionFactors);
            Heading("6. WhatsApp沟通分析", 1);
            LabelText("沟通积极度", report.WhatsAppAnalysis.EngagementLevel);
            BulletGroup("关注主题", report.WhatsAppAnalysis.FocusTopics);
            BulletGroup("需求与合作信号", report.WhatsAppAnalysis.PurchaseSignals);
            BulletGroup("主要顾虑", report.WhatsAppAnalysis.Concerns);
            foreach (var quote in report.WhatsAppAnalysis.Quotes.Take(8))
                Callout($"客户原话：“{quote.Original}”\n中文含义：{quote.ChineseMeaning}\nAI分析：{quote.AiAnalysis}", XColor.FromArgb(244, 246, 249), XColor.FromArgb(31, 77, 120));
            Heading("7. AI商机判断", 1);
            DimensionChart(report.OpportunityJudgment.DimensionScores);
            BulletGroup("正向因素", report.OpportunityJudgment.PositiveFactors, XColor.FromArgb(232, 245, 240));
            BulletGroup("负向因素", report.OpportunityJudgment.NegativeFactors, XColor.FromArgb(253, 236, 236));
            Heading("8. 产品匹配分析", 1);
            BulletGroup("高匹配点", report.ProductFit.HighMatchPoints);
            BulletGroup("低匹配点", report.ProductFit.LowMatchPoints);
            BulletGroup("需要验证的问题", report.ProductFit.QuestionsToValidate, XColor.FromArgb(255, 248, 232));
            Heading("9. 销售推进建议", 1);
            foreach (var action in report.SalesStrategy.Actions) LabelText(action.Timeframe, $"{action.Action}\n依据：{action.Rationale}\n成功标准：{action.SuccessCriterion}");
            Callout($"推荐话术\n{report.SalesStrategy.RecommendedTalkTrack}", XColor.FromArgb(232, 245, 240), XColor.FromArgb(15, 103, 81));
            BulletGroup("待解决问题", report.SalesStrategy.PendingQuestions);
            Heading("10. 风险分析", 1);
            BulletGroup("成交风险", report.RiskAnalysis.DealRisks, XColor.FromArgb(253, 236, 236));
            BulletGroup("使用风险", report.RiskAnalysis.AdoptionRisks, XColor.FromArgb(255, 248, 232));
            BulletGroup("流失风险", report.RiskAnalysis.ChurnRisks, XColor.FromArgb(253, 236, 236));
            Heading("11. AI总结", 1);
            Callout(report.ManagementSummary, XColor.FromArgb(232, 238, 245), XColor.FromArgb(11, 37, 69));
            Heading("证据说明", 2);
            LabelText("", "事实来自 CRM、Excel 导入字段、WhatsApp 原始消息、Lead Intelligence、自动化触达及系统审计轨迹；AI判断与销售建议基于上述事实推导，不会写回或覆盖原始客户数据。");
            Callout($"数据覆盖：WhatsApp {_source.SourceSnapshot.WhatsAppMessages.Count} 条 · 邮件 {_source.SourceSnapshot.EmailMessages.Count} 条 · 自动化触达 {_source.SourceSnapshot.CampaignTouches.Count} 次 · 客户轨迹 {_source.SourceSnapshot.Timeline.Count} 条 · 历史 AI 分析 {_source.SourceSnapshot.LeadAnalysisHistory.Count} 次\n快照时间：{_source.SourceSnapshot.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm}", XColor.FromArgb(244, 246, 249), XColor.FromArgb(31, 77, 120));
            Heading("证据账本（精选）", 2);
            foreach (var statement in report.EvidenceLedger.Where(item => !string.IsNullOrWhiteSpace(item.Statement)).Take(18))
                LabelText(string.IsNullOrWhiteSpace(statement.Nature) ? "事实" : statement.Nature, $"{statement.Statement}\n来源：{statement.Source} · 证据：{statement.Evidence}");
            Heading("业务知识引用", 2);
            if (report.KnowledgeReferences.Count == 0)
                LabelText("", "本版本未引用已批准业务知识。");
            else
                foreach (var reference in report.KnowledgeReferences.Take(20))
                    LabelText(reference.CitationLabel, $"作用域：{reference.Scope.Label} · 相关度：{reference.RelevanceScore:P0}\n{reference.Content}");
        }

        public void Finish() => FinalizePage();

        private void NewPage(bool reportPage = true)
        {
            FinalizePage();
            _page = _document.AddPage(); _page.Size = PdfSharp.PageSize.Letter;
            _gfx = XGraphics.FromPdfPage(_page); _pageNumber++; _y = Top;
            if (reportPage)
            {
                _gfx.DrawString("AI SALES OS · 客户智能分析中心", _small, XBrushes.DimGray, new XPoint(Left, 31));
                _gfx.DrawLine(new XPen(XColor.FromArgb(215, 222, 227), .7), Left, 42, _page.Width.Point - Right, 42);
            }
        }

        private void FinalizePage()
        {
            if (_gfx is null || _page is null) return;
            _gfx.DrawLine(new XPen(XColor.FromArgb(215, 222, 227), .7), Left, _page.Height.Point - 39, _page.Width.Point - Right, _page.Height.Point - 39);
            _gfx.DrawString($"{_source.CustomerName} · V{_source.Version}", _small, XBrushes.DimGray, new XPoint(Left, _page.Height.Point - 24));
            _gfx.DrawString($"第 {_pageNumber} 页", _small, XBrushes.DimGray, new XRect(Left, _page.Height.Point - 32, _page.Width.Point - Left - Right, 18), XStringFormats.TopRight);
            _gfx.Dispose(); _gfx = null;
        }

        private void Ensure(double height)
        {
            if (_page is null || _y + height > _page.Height.Point - Bottom) NewPage();
        }

        private void Heading(string text, int level)
        {
            var font = level == 1 ? _h1 : _h2; var before = level == 1 ? 18 : 11; var height = level == 1 ? 28 : 22;
            Ensure(before + height); _y += before;
            _gfx!.DrawString(text, font, new XSolidBrush(level == 1 ? XColor.FromArgb(46, 116, 181) : XColor.FromArgb(31, 77, 120)), new XPoint(Left, _y + font.Size));
            _y += height;
        }

        private void Section(string title, IEnumerable<string> lines) { Heading(title, 1); foreach (var line in lines) LabelText("", line); }

        private void LabelText(string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) text = "暂无充分信息。";
            var prefix = string.IsNullOrWhiteSpace(label) ? "" : label + "：";
            var height = MeasureWrapped(prefix + text, _body, _page!.Width.Point - Left - Right, 15) + 8;
            Ensure(height);
            DrawWrapped(prefix + text, _body, XBrushes.Black, Left, _y + 3, _page.Width.Point - Left - Right, 15);
            _y += height;
        }

        private void BulletGroup(string label, IEnumerable<string> items, XColor? fill = null)
        {
            Heading(label, 2);
            var values = items.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (values.Count == 0) values.Add("暂无充分信息。");
            var text = string.Join("\n", values.Select(value => "• " + value));
            if (fill is not null) Callout(text, fill.Value, XColor.FromArgb(31, 41, 51)); else LabelText("", text);
        }

        private void Callout(string text, XColor fill, XColor ink)
        {
            var width = _page!.Width.Point - Left - Right;
            var height = MeasureWrapped(text, _body, width - 24, 15) + 22;
            Ensure(height + 8);
            _gfx!.DrawRoundedRectangle(new XSolidBrush(fill), new XRect(Left, _y + 4, width, height), new XSize(8, 8));
            DrawWrapped(text, _body, new XSolidBrush(ink), Left + 12, _y + 14, width - 24, 15);
            _y += height + 12;
        }

        private void ScoreCards(CustomerOpportunityJudgment opportunity)
        {
            var width = _page!.Width.Point - Left - Right; var card = (width - 16) / 3;
            Ensure(68);
            var values = new[] { ("AI评分", $"{opportunity.AiScore}/100", XColor.FromArgb(232,245,240), XColor.FromArgb(15,143,106)), ("客户等级", opportunity.Grade + "级", XColor.FromArgb(232,238,245), XColor.FromArgb(11,37,69)), ("成交概率", opportunity.DealProbability + "%", XColor.FromArgb(255,248,232), XColor.FromArgb(122,90,0)) };
            for (var i = 0; i < values.Length; i++)
            {
                var x = Left + i * (card + 8);
                _gfx!.DrawRoundedRectangle(new XSolidBrush(values[i].Item3), new XRect(x, _y, card, 58), new XSize(8, 8));
                _gfx.DrawString(values[i].Item1, _small, XBrushes.DimGray, new XRect(x + 10, _y + 9, card - 20, 16), XStringFormats.TopLeft);
                _gfx.DrawString(values[i].Item2, new XFont("AISalesOS", 18, XFontStyleEx.Bold), new XSolidBrush(values[i].Item4), new XRect(x + 10, _y + 27, card - 20, 24), XStringFormats.TopLeft);
            }
            _y += 70;
        }

        private void DimensionChart(IEnumerable<LeadFactor> factors)
        {
            foreach (var factor in factors)
            {
                Ensure(30); var width = _page!.Width.Point - Left - Right; var labelWidth = 112d; var scoreWidth = 42d; var barWidth = width - labelWidth - scoreWidth;
                _gfx!.DrawString(DimensionName(factor.Key), _small, XBrushes.Black, new XRect(Left, _y + 3, labelWidth, 16), XStringFormats.TopLeft);
                _gfx.DrawRoundedRectangle(new XSolidBrush(XColor.FromArgb(231, 236, 239)), new XRect(Left + labelWidth, _y + 5, barWidth, 9), new XSize(4, 4));
                var ratio = factor.MaxScore <= 0 ? 0 : Math.Clamp((double)factor.Score / factor.MaxScore, 0, 1);
                _gfx.DrawRoundedRectangle(new XSolidBrush(XColor.FromArgb(15, 143, 106)), new XRect(Left + labelWidth, _y + 5, barWidth * ratio, 9), new XSize(4, 4));
                _gfx.DrawString($"{factor.Score}/{factor.MaxScore}", _small, XBrushes.DimGray, new XRect(Left + labelWidth + barWidth + 6, _y + 1, scoreWidth, 16), XStringFormats.TopRight);
                _y += 25;
            }
            _y += 4;
        }

        private double MeasureWrapped(string text, XFont font, double width, double lineHeight) => Wrap(text, font, width).Count * lineHeight;

        private void DrawWrapped(string text, XFont font, XBrush brush, double x, double y, double width, double lineHeight, XStringFormat? format = null)
        {
            var lines = Wrap(text, font, width); var alignment = format ?? XStringFormats.TopLeft;
            foreach (var line in lines) { _gfx!.DrawString(line, font, brush, new XRect(x, y, width, lineHeight), alignment); y += lineHeight; }
        }

        private List<string> Wrap(string text, XFont font, double width)
        {
            var result = new List<string>();
            foreach (var paragraph in text.Replace("\r", "").Split('\n'))
            {
                if (paragraph.Length == 0) { result.Add(""); continue; }
                var line = "";
                foreach (var character in paragraph)
                {
                    var candidate = line + character;
                    if (line.Length > 0 && _gfx!.MeasureString(candidate, font).Width > width) { result.Add(line); line = character.ToString(); }
                    else line = candidate;
                }
                if (line.Length > 0) result.Add(line);
            }
            return result;
        }

        private static string Join(IEnumerable<string> values) => string.Join("；", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        private static string DimensionName(string key) => key switch
        {
            "paid_marketing_willingness" => "增长投入意愿", "supply_stability" => "运营与交付稳定性", "ecommerce_foundation" => "相关业务基础",
            "private_traffic" => "客户触达能力", "existing_sales" => "商业验证程度", "materials_readiness" => "合作准备度", _ => key
        };
    }
}
