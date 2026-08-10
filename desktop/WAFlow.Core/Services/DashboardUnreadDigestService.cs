using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class DashboardUnreadDigestService
{
    private const string Instructions = """
        你是 AI Sales OS 的每日销售收件箱分析员。请只根据 suppliedThreads 中尚未阅读的 WhatsApp 和邮件原文，
        生成一组供销售人员今天立即处理的中文要点。不得把销售人员的外发内容、旧 CRM 推断或常识当成客户事实，
        不得虚构客户身份、交易、金额、期限、承诺、风险或紧迫性。

        要求：
        1. 每个要点必须对应一个 suppliedThreads.sourceKey；同一 sourceKey 最多出现一次。
        2. 优先覆盖明确询价、合同或交易、付款、交付或实施、投诉、拒绝、需要人工判断或有明确下一步的来信；其余按时间新旧排序。
        3. headline 用 4–16 个中文字符概括客户来意。
        4. summary 用一到两句中文说明客户说了什么；没有证据时明确写“需查看原文确认”。
        5. suggestedAction 必须是销售人员可执行的下一步，不能代表销售人员自动承诺、自动回复或自动修改 CRM。
        6. priority 只能是 urgent、high、normal。只有原文存在明确时间压力、交易阻塞、投诉或高风险时才能使用 urgent。
        7. 最多返回 8 个要点。只返回严格 JSON，不要 Markdown、解释或思考过程。

        固定 JSON 结构：
        {
          "items": [
            {
              "sourceKey": "suppliedThreads 中的 sourceKey",
              "headline": "中文短标题",
              "summary": "中文摘要",
              "suggestedAction": "中文下一步",
              "priority": "urgent|high|normal"
            }
          ]
        }
        """;

    private readonly LocalRepository _repository;
    private readonly IStructuredAiProvider _provider;
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private int _backgroundRefreshVersion;
    private int _backgroundRefreshRunning;

    public DashboardUnreadDigestService(LocalRepository repository, IStructuredAiProvider provider)
    {
        _repository = repository;
        _provider = provider;
    }

    public void QueueBackgroundRefresh()
    {
        Interlocked.Increment(ref _backgroundRefreshVersion);
        TryStartBackgroundRefresh();
    }

    private void TryStartBackgroundRefresh()
    {
        if (Interlocked.CompareExchange(ref _backgroundRefreshRunning, 1, 0) != 0) return;
        _ = Task.Run(RunBackgroundRefreshLoopAsync);
    }

    private async Task RunBackgroundRefreshLoopAsync()
    {
        var processedVersion = 0;
        try
        {
            while (true)
            {
                var requestedVersion = Volatile.Read(ref _backgroundRefreshVersion);
                await Task.Delay(900);
                if (requestedVersion != Volatile.Read(ref _backgroundRefreshVersion)) continue;
                try
                {
                    await GetAsync();
                }
                catch
                {
                    // Inbox synchronization remains authoritative. A later message event
                    // or Dashboard visit retries transient provider and network failures.
                }
                processedVersion = requestedVersion;
                if (processedVersion == Volatile.Read(ref _backgroundRefreshVersion)) break;
            }
        }
        finally
        {
            Volatile.Write(ref _backgroundRefreshRunning, 0);
            if (processedVersion != Volatile.Read(ref _backgroundRefreshVersion))
                TryStartBackgroundRefresh();
        }
    }

    public async Task<DashboardUnreadDigest> GetAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await _generationGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await _repository.GetDashboardUnreadSnapshotAsync(
                maxThreads: 12,
                maxMessagesPerThread: 6,
                cancellationToken);
            if (snapshot.WhatsAppUnreadCount + snapshot.EmailUnreadCount == 0)
                return Empty(snapshot);

            var model = "";
            try { model = await _provider.GetSelectedModelAsync(AiModuleKeys.Dashboard, cancellationToken); }
            catch (DeepSeekException) { model = ""; }

            var cached = await _repository.GetDashboardUnreadDigestCacheAsync(cancellationToken);
            if (!forceRefresh
                && cached is not null
                && cached.Fingerprint.Equals(snapshot.Fingerprint, StringComparison.Ordinal)
                && cached.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
                return cached;

            if (string.IsNullOrWhiteSpace(model))
            {
                var fallback = Fallback(
                    snapshot,
                    "",
                    "当前先展示未读原文预览。");
                await _repository.SaveDashboardUnreadDigestCacheAsync(fallback, cancellationToken);
                return fallback;
            }

            try
            {
                var allowed = snapshot.Threads
                    .ToDictionary(thread => thread.SourceKey, StringComparer.OrdinalIgnoreCase);
                var response = await _provider.CompleteStructuredAsync<DashboardUnreadDigestAiResponse>(
                    AiModuleKeys.Dashboard,
                    Instructions,
                    new
                    {
                        generatedAt = DateTimeOffset.Now,
                        unreadTotals = new
                        {
                            whatsapp = snapshot.WhatsAppUnreadCount,
                            email = snapshot.EmailUnreadCount,
                            threads = snapshot.TotalUnreadThreadCount
                        },
                        suppliedThreads = snapshot.Threads.Select(thread => new
                        {
                            thread.SourceKey,
                            channel = thread.Channel,
                            sender = thread.SenderLabel,
                            contact = thread.ContactLabel,
                            thread.Subject,
                            thread.UnreadCount,
                            thread.LastMessageAt,
                            messages = thread.Messages
                        })
                    },
                    candidate => Validate(candidate, allowed),
                    cancellationToken);

                var items = response.Items
                    .Where(item => allowed.ContainsKey(item.SourceKey))
                    .DistinctBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .Select(item =>
                    {
                        var source = allowed[item.SourceKey];
                        return new DashboardUnreadDigestItem
                        {
                            SourceKey = source.SourceKey,
                            Channel = source.Channel,
                            SenderLabel = source.SenderLabel,
                            Headline = Clean(item.Headline, 60),
                            Summary = Clean(item.Summary, 320),
                            SuggestedAction = Clean(item.SuggestedAction, 220),
                            Priority = NormalizePriority(item.Priority),
                            LastMessageAt = source.LastMessageAt,
                            UnreadCount = source.UnreadCount
                        };
                    })
                    .ToList();

                var digest = new DashboardUnreadDigest
                {
                    Fingerprint = snapshot.Fingerprint,
                    Model = model,
                    GeneratedAt = DateTimeOffset.Now,
                    WhatsAppUnreadCount = snapshot.WhatsAppUnreadCount,
                    EmailUnreadCount = snapshot.EmailUnreadCount,
                    TotalUnreadThreadCount = snapshot.TotalUnreadThreadCount,
                    SummarizedThreadCount = items.Count,
                    IsAiGenerated = true,
                    StatusMessage = items.Count == 0
                        ? "AI 未找到可可靠概括的未读内容，请进入 Inbox 查看原文。"
                        : "AI 已按未读原文整理；摘要不会自动回复或标记已读。",
                    Items = items
                };
                await _repository.SaveDashboardUnreadDigestCacheAsync(digest, cancellationToken);
                return digest;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                var fallback = Fallback(
                    snapshot,
                    model,
                    $"本次 AI 摘要暂不可用（{FriendlyError(error)}）；已保留未读原文预览。");
                await _repository.SaveDashboardUnreadDigestCacheAsync(fallback, cancellationToken);
                return fallback;
            }
        }
        finally
        {
            _generationGate.Release();
        }
    }

    public static string? Validate(
        DashboardUnreadDigestAiResponse response,
        IReadOnlyDictionary<string, DashboardUnreadThread> allowed)
    {
        response.Items ??= [];
        if (response.Items.Count is < 1 or > 8) return "items 必须包含 1–8 个未读会话要点。";
        if (response.Items.Any(item => string.IsNullOrWhiteSpace(item.SourceKey) || !allowed.ContainsKey(item.SourceKey)))
            return "sourceKey 必须来自 suppliedThreads。";
        if (response.Items.Select(item => item.SourceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != response.Items.Count)
            return "同一 sourceKey 只能出现一次。";
        if (response.Items.Any(item =>
                string.IsNullOrWhiteSpace(item.Headline)
                || string.IsNullOrWhiteSpace(item.Summary)
                || string.IsNullOrWhiteSpace(item.SuggestedAction)))
            return "每个要点必须包含 headline、summary 和 suggestedAction。";
        if (response.Items.Any(item => item.Headline.Trim().Length > 60
                                       || item.Summary.Trim().Length > 320
                                       || item.SuggestedAction.Trim().Length > 220))
            return "摘要字段超出允许长度。";
        if (response.Items.Any(item =>
                NormalizePriority(item.Priority) != (item.Priority ?? "").Trim().ToLowerInvariant()))
            return "priority 只能是 urgent、high 或 normal。";
        return null;
    }

    private static DashboardUnreadDigest Empty(DashboardUnreadSnapshot snapshot) => new()
    {
        Fingerprint = snapshot.Fingerprint,
        GeneratedAt = DateTimeOffset.Now,
        WhatsAppUnreadCount = 0,
        EmailUnreadCount = 0,
        TotalUnreadThreadCount = 0,
        SummarizedThreadCount = 0,
        IsAiGenerated = false,
        StatusMessage = "WhatsApp 和邮件箱暂无未读消息。",
        Items = []
    };

    private static DashboardUnreadDigest Fallback(
        DashboardUnreadSnapshot snapshot,
        string model,
        string status)
    {
        var items = snapshot.Threads
            .Take(6)
            .Select(thread =>
            {
                var preview = thread.Messages.LastOrDefault() ?? "请进入 Inbox 查看原文。";
                return new DashboardUnreadDigestItem
                {
                    SourceKey = thread.SourceKey,
                    Channel = thread.Channel,
                    SenderLabel = thread.SenderLabel,
                    Headline = string.IsNullOrWhiteSpace(thread.Subject)
                        ? "待查看新消息"
                        : Clean(thread.Subject, 60),
                    Summary = Clean(preview, 320),
                    SuggestedAction = $"进入 {(thread.Channel == "email" ? "邮件箱" : "WhatsApp")} 查看原文并决定是否回复。",
                    Priority = "normal",
                    LastMessageAt = thread.LastMessageAt,
                    UnreadCount = thread.UnreadCount
                };
            })
            .ToList();
        return new DashboardUnreadDigest
        {
            Fingerprint = snapshot.Fingerprint,
            Model = model,
            GeneratedAt = DateTimeOffset.Now,
            WhatsAppUnreadCount = snapshot.WhatsAppUnreadCount,
            EmailUnreadCount = snapshot.EmailUnreadCount,
            TotalUnreadThreadCount = snapshot.TotalUnreadThreadCount,
            SummarizedThreadCount = items.Count,
            IsAiGenerated = false,
            StatusMessage = status,
            Items = items
        };
    }

    private static string Clean(string? value, int maxLength)
    {
        var normalized = string.Join(
            " ",
            (value ?? "").Replace('\u00a0', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }

    private static string NormalizePriority(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "urgent" => "urgent",
        "high" => "high",
        _ => "normal"
    };

    private static string FriendlyError(Exception error) => error switch
    {
        DeepSeekException deepSeek when !string.IsNullOrWhiteSpace(deepSeek.Message) => deepSeek.Message,
        TimeoutException => "请求超时",
        _ => "模型或网络异常"
    };
}

public sealed class DashboardUnreadDigestAiResponse
{
    public List<DashboardUnreadDigestAiItem> Items { get; set; } = [];
}

public sealed class DashboardUnreadDigestAiItem
{
    public string SourceKey { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SuggestedAction { get; set; } = "";
    public string Priority { get; set; } = "normal";
}
