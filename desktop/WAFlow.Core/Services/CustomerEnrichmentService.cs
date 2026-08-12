using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using WAFlow.Core.Domain;
using WAFlow.Core.Imports;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

/// <summary>
/// Coordinates public-web enrichment as a durable, local-first background job.
/// Search credentials remain in Windows Credential Manager; only query hashes,
/// public sources, evidence-bound facts and redacted audit metadata are stored.
/// </summary>
public sealed partial class CustomerEnrichmentService : IAsyncDisposable
{
    private static readonly string[] SupportedProviderIds = ["tavily", "brave", "searxng"];
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private readonly LocalRepository _repository;
    private readonly AiProviderService _aiProvider;
    private readonly CustomerEnrichmentAnalyzer _analyzer;
    private readonly CustomerBrainService _customerBrain;
    private readonly WhatsAppSyncService? _whatsAppSync;
    private readonly LeadIntelligenceAutomationService? _leadAutomation;
    private readonly ImportService? _imports;
    private readonly PublicWebReader _webReader;
    private readonly bool _ownsWebReader;
    private readonly IReadOnlyDictionary<string, ICustomerSearchProvider>? _injectedProviders;
    private readonly ICustomerSearchProvider? _tavilyProvider;
    private readonly ICustomerSearchProvider? _braveProvider;
    private readonly object _providerGate = new();
    private ICustomerSearchProvider? _searXngProvider;
    private string _searXngProviderUrl = "";
    private readonly TimeProvider _timeProvider;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private readonly SemaphoreSlim _usageGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCancellations = new(StringComparer.OrdinalIgnoreCase);
    private Task? _worker;
    private int _started;
    private int _disposed;

    public event EventHandler<CustomerEnrichmentChangedEventArgs>? Changed;

    public CustomerEnrichmentService(
        LocalRepository repository,
        AiProviderService aiProvider,
        CustomerBrainService customerBrain,
        WhatsAppSyncService? whatsAppSync = null,
        LeadIntelligenceAutomationService? leadAutomation = null,
        ImportService? imports = null,
        PublicWebReader? webReader = null,
        IEnumerable<ICustomerSearchProvider>? providers = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _aiProvider = aiProvider;
        _analyzer = new CustomerEnrichmentAnalyzer(aiProvider);
        _customerBrain = customerBrain;
        _whatsAppSync = whatsAppSync;
        _leadAutomation = leadAutomation;
        _imports = imports;
        _webReader = webReader ?? new PublicWebReader();
        _ownsWebReader = webReader is null;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _injectedProviders = providers?.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        if (_injectedProviders is null)
        {
            _tavilyProvider = new TavilySearchProvider(ProviderCredentialStore("tavily"));
            _braveProvider = new BraveSearchProvider(ProviderCredentialStore("brave"));
        }
        if (_whatsAppSync is not null) _whatsAppSync.MessageSynchronized += WhatsAppSync_MessageSynchronized;
        if (_leadAutomation is not null) _leadAutomation.AnalysisChanged += LeadAutomation_AnalysisChanged;
        if (_imports is not null) _imports.LeadsImported += Imports_LeadsImported;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _worker = Task.Run(() => ProcessQueueAsync(_lifetime.Token), CancellationToken.None);

        var startupSettings = await GetSettingsAsync(cancellationToken);
        var removedJobs = await _repository.PruneCustomerEnrichmentDataAsync(startupSettings.DataRetentionDays, cancellationToken);
        if (removedJobs > 0)
            await _repository.LogEventAsync(
                "customer_enrichment_retention_applied",
                null,
                null,
                $"removed_jobs={removedJobs};retention_days={startupSettings.DataRetentionDays}",
                cancellationToken);

        foreach (var recoverable in await _repository.GetRecoverableCustomerEnrichmentJobsAsync(cancellationToken))
        {
            if (recoverable.Status == CustomerEnrichmentJobStatus.Running)
            {
                var ledger = await _repository.GetCustomerEnrichmentUsageForJobAsync(recoverable.Id, cancellationToken);
                if (ledger.Any(item => item.Requests > 0 || item.RequestState.Equals("reserved", StringComparison.OrdinalIgnoreCase)))
                {
                    // A durable pre-request reservation means an external call may
                    // already have reached the provider. Never replay it silently.
                    recoverable.Status = CustomerEnrichmentJobStatus.Failed;
                    recoverable.FailedAt = _timeProvider.GetUtcNow();
                    recoverable.CostUsd = ledger.Sum(item => item.EstimatedCostUsd);
                    recoverable.ErrorCode = CustomerEnrichmentErrorCodes.RecoveryReviewRequired;
                    recoverable.ErrorMessage = "程序上次退出时外部请求可能已经执行。为避免重复调用或重复计费，任务未自动重试；请核对用量后手动强制刷新。";
                    await _repository.SaveCustomerEnrichmentJobAsync(recoverable, cancellationToken);
                    continue;
                }
                await _repository.ResetCustomerEnrichmentJobWorkAsync(recoverable.Id, cancellationToken);
                recoverable.Status = CustomerEnrichmentJobStatus.Queued;
                recoverable.StartedAt = null;
                recoverable.QueriesCount = 0;
                recoverable.SourcesCount = 0;
                recoverable.FactsCount = 0;
                recoverable.CostUsd = ledger.Sum(item => item.EstimatedCostUsd);
                recoverable.ErrorCode = "";
                recoverable.ErrorMessage = "程序上次退出时任务尚未完成，已安全恢复到队列。";
                await _repository.SaveCustomerEnrichmentJobAsync(recoverable, cancellationToken);
            }
            _queue.Writer.TryWrite(recoverable.Id);
        }

        await QueueStartupCandidatesAsync(cancellationToken);
    }

    public async Task<CustomerEnrichmentSnapshot> GetSnapshotAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _repository.GetCustomerEnrichmentSnapshotAsync(customerId, cancellationToken);
        var currentIdentityHash = await CustomerExternalFactPolicy.GetCurrentIdentityHashAsync(
            _repository,
            customerId,
            cancellationToken);
        snapshot.LatestJob = snapshot.Jobs.FirstOrDefault(job =>
            job.IdentityHash.Equals(currentIdentityHash, StringComparison.Ordinal));
        snapshot.Sources = snapshot.LatestJob is null
            ? []
            : (await _repository.GetCustomerEnrichmentSourcesAsync(
                customerId,
                snapshot.LatestJob.Id,
                cancellationToken)).ToList();
        snapshot.Facts = await CustomerExternalFactPolicy.GetFactsForCurrentIdentityAsync(
            _repository,
            customerId,
            cancellationToken);
        snapshot.ActiveFacts = await CustomerExternalFactPolicy.GetCurrentFactsAsync(
            _repository,
            customerId,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        var settings = await GetSettingsAsync(cancellationToken);
        ApplyFreeRemaining(snapshot.Usage, settings);
        return snapshot;
    }

    public Task<CustomerEnrichmentSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        _repository.GetCustomerEnrichmentSettingsAsync(cancellationToken);

    public async Task SaveSettingsAsync(CustomerEnrichmentSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ProviderOrder = settings.ProviderOrder
            .Where(item => SupportedProviderIds.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Select(item => item.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Concat(SupportedProviderIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.MonthlyBudgetUsd = Math.Max(0, settings.MonthlyBudgetUsd);
        settings.AiAnalysisReservationUsd = Math.Clamp(settings.AiAnalysisReservationUsd, 0, 1_000m);
        if (settings.MonthlyBudgetUsd <= 0)
        {
            settings.AllowPaidRequests = false;
            settings.AllowAiAnalysisRequests = false;
        }
        else if (settings.AllowAiAnalysisRequests)
        {
            if (settings.AiAnalysisReservationUsd <= 0)
                settings.AiAnalysisReservationUsd = CustomerEnrichmentSettings.DefaultAiAnalysisReservationUsd;
            settings.AiAnalysisReservationUsd = Math.Min(
                settings.AiAnalysisReservationUsd,
                settings.MonthlyBudgetUsd);
        }
        settings.TavilyMonthlyFreeRequests = Math.Clamp(settings.TavilyMonthlyFreeRequests, 0, 1_000_000);
        settings.BraveMonthlyFreeRequests = Math.Clamp(settings.BraveMonthlyFreeRequests, 0, 1_000_000);
        settings.MaxQueriesPerCustomer = Math.Clamp(settings.MaxQueriesPerCustomer, 1, 6);
        settings.MaxResultsPerQuery = Math.Clamp(settings.MaxResultsPerQuery, 1, 8);
        settings.MaxPagesPerCustomer = Math.Clamp(settings.MaxPagesPerCustomer, 1, 12);
        settings.CacheDays = Math.Clamp(settings.CacheDays, 1, 365);
        settings.StandardRefreshDays = Math.Clamp(settings.StandardRefreshDays, 7, 365);
        settings.HighValueRefreshDays = Math.Clamp(settings.HighValueRefreshDays, 3, settings.StandardRefreshDays);
        settings.MajorOpportunityRefreshDays = Math.Clamp(settings.MajorOpportunityRefreshDays, 1, settings.HighValueRefreshDays);
        settings.DataRetentionDays = Math.Clamp(settings.DataRetentionDays, 30, 3650);
        settings.MaxAutomaticJobsPerStartup = Math.Clamp(settings.MaxAutomaticJobsPerStartup, 0, 50);
        settings.AutoEnrichmentGrades = settings.AutoEnrichmentGrades
            .Select(item => item.Trim().ToUpperInvariant())
            .Where(item => item is "A" or "B" or "C" or "D")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ValidateSearXngUrl(settings.SearXngBaseUrl);
        await _repository.SaveCustomerEnrichmentSettingsAsync(settings, cancellationToken);
        await _repository.LogEventAsync(
            "customer_enrichment_settings_saved",
            null,
            null,
            $"providers={string.Join(',', settings.ProviderOrder)};searxng={settings.SearXngEnabled};local_estimate_notice_usd={settings.MonthlyBudgetUsd:0.####};paid_search={settings.AllowPaidRequests};ai_analysis={settings.AllowAiAnalysisRequests};limits={settings.MaxQueriesPerCustomer}/{settings.MaxResultsPerQuery}/{settings.MaxPagesPerCustomer}",
            cancellationToken);
    }

    public void SaveProviderKey(string providerId, string apiKey)
    {
        var normalized = NormalizeCredentialProvider(providerId);
        if (normalized == "searxng") throw new InvalidOperationException("本地 SearXNG 不需要 API Key。");
        if (string.IsNullOrWhiteSpace(apiKey)) return;
        ProviderCredentialStore(normalized).Save(apiKey.Trim());
    }

    public void DeleteProviderKey(string providerId)
    {
        var normalized = NormalizeCredentialProvider(providerId);
        if (normalized == "searxng") return;
        ProviderCredentialStore(normalized).Delete();
    }

    public bool HasProviderKey(string providerId)
    {
        var normalized = NormalizeCredentialProvider(providerId);
        if (normalized == "searxng") return false;
        return ProviderCredentialStore(normalized).Exists();
    }

    public async Task<IReadOnlyList<CustomerSearchProviderHealth>> GetAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var checkedAt = _timeProvider.GetUtcNow();
        var results = new List<CustomerSearchProviderHealth>();
        foreach (var providerId in settings.ProviderOrder)
        {
            var configured = IsConfigured(providerId, settings);
            var message = providerId switch
            {
                "tavily" when configured => "Tavily 已安全配置；点击测试后才会发起联网请求，并可能计入 Provider 账号用量。",
                "brave" when configured => "Brave Search 已安全配置；点击测试后才会发起联网请求，并可能计入 Provider 账号用量。",
                "searxng" when configured => $"SearXNG 已启用：{settings.SearXngBaseUrl}",
                "searxng" => "本地 SearXNG 未启用。",
                _ => $"{ProviderDisplayName(providerId)} 尚未配置 API Key。"
            };
            results.Add(new CustomerSearchProviderHealth(providerId, configured, message, checkedAt));
        }
        return results;
    }

    public async Task<CustomerSearchProviderHealth> TestProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        await TestProviderConfigurationAsync(providerId, null, null, cancellationToken);

    public async Task<CustomerSearchProviderHealth> TestProviderConfigurationAsync(
        string providerId,
        string? apiKeyOverride,
        string? searXngBaseUrlOverride,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var normalized = NormalizeProvider(providerId);
        ICustomerSearchProvider provider;
        var hasOverride = !string.IsNullOrWhiteSpace(apiKeyOverride);
        if (normalized == "searxng" && !string.IsNullOrWhiteSpace(searXngBaseUrlOverride))
        {
            ValidateSearXngUrl(searXngBaseUrlOverride);
            provider = new SearXngSearchProvider(searXngBaseUrlOverride.TrimEnd('/'));
        }
        else if (normalized == "tavily" && hasOverride)
            provider = new TavilySearchProvider(new FixedSecretStore(apiKeyOverride!));
        else if (normalized == "brave" && hasOverride)
            provider = new BraveSearchProvider(new FixedSecretStore(apiKeyOverride!));
        else
            provider = CreateProviders(settings).GetValueOrDefault(normalized)
                ?? throw new InvalidOperationException("未知搜索 Provider。");
        var configured = hasOverride
            || (normalized == "searxng" && !string.IsNullOrWhiteSpace(searXngBaseUrlOverride))
            || IsConfigured(normalized, settings);
        if (!configured)
            return new CustomerSearchProviderHealth(normalized, false, "该搜索 Provider 尚未配置。", _timeProvider.GetUtcNow());
        await _usageGate.WaitAsync(cancellationToken);
        try
        {
            var currentUsage = await _repository.GetCustomerEnrichmentUsageSummaryAsync(cancellationToken);
            var desiredAttempts = GetMaximumAttempts(provider);
            if (!TryResolveRequestReservation(
                    normalized,
                    settings,
                    currentUsage,
                    desiredAttempts,
                    allowPartialReservation: false,
                    out var reservedAttempts,
                    out var reservedCost,
                    out var blocked))
                return new CustomerSearchProviderHealth(normalized, false, blocked!.Message, _timeProvider.GetUtcNow());

            var usage = new CustomerEnrichmentProviderUsage
            {
                Provider = normalized,
                JobId = "settings-test",
                Requests = reservedAttempts,
                EstimatedCostUsd = reservedCost,
                Succeeded = false,
                ErrorCode = "REQUEST_RESERVED",
                ErrorMessage = "搜索连接测试已在联网前记录本地用量与费用估算预留。",
                RequestState = "reserved",
                CreatedAt = _timeProvider.GetLocalNow()
            };
            await _repository.SaveCustomerEnrichmentUsageAsync(usage, cancellationToken);
            var health = await provider.CheckHealthAsync(cancellationToken);
            var actualAttempts = GetActualAttempts(provider, reservedAttempts);
            usage.Requests = actualAttempts;
            usage.EstimatedCostUsd = EstimateSearchCost(normalized, settings, currentUsage, actualAttempts);
            usage.Succeeded = health.Available;
            usage.ErrorCode = health.Available ? "" : CustomerEnrichmentErrorCodes.SearchProviderUnavailable;
            usage.ErrorMessage = health.Available ? "" : health.Message;
            usage.RequestState = "completed";
            await _repository.SaveCustomerEnrichmentUsageAsync(usage, cancellationToken);
            await _repository.LogEventAsync(
                "customer_enrichment_provider_tested",
                null,
                null,
                $"provider={normalized};available={health.Available};attempts={actualAttempts}",
                cancellationToken);
            return health;
        }
        finally { _usageGate.Release(); }
    }

    public async Task<CustomerEnrichmentJob> QueueAsync(
        string customerId,
        CustomerEnrichmentTriggerType trigger = CustomerEnrichmentTriggerType.Manual,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(customerId)) throw new ArgumentException("客户 ID 不能为空。", nameof(customerId));
        await _queueGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await GetSettingsAsync(cancellationToken);
            if (trigger == CustomerEnrichmentTriggerType.Manual && !settings.ManualEnrichmentEnabled)
                throw new InvalidOperationException("客户外部调查的手动触发已在设置中关闭。");
            if (!HasAnyConfiguredProvider(settings))
                throw new InvalidOperationException("当前未配置可用的搜索服务。请在设置中配置 Tavily、Brave，或主动启用本地 SearXNG；主程序其他功能不受影响。");
            var lead = await _repository.GetLeadAsync(customerId, cancellationToken)
                ?? throw new InvalidOperationException("客户不存在或已经删除。");
            var identity = CustomerEnrichmentIdentityService.Build(lead);
            var configurationHash = BuildConfigurationHash(settings);
            var jobs = await _repository.GetCustomerEnrichmentJobsAsync(customerId, cancellationToken);
            var active = jobs.FirstOrDefault(item =>
                (item.Status is CustomerEnrichmentJobStatus.Queued or CustomerEnrichmentJobStatus.Running)
                && item.IdentityHash.Equals(identity.IdentityHash, StringComparison.Ordinal));
            if (active is not null) return active;

            if (!force)
            {
                var cacheCutoff = _timeProvider.GetUtcNow().AddDays(-settings.CacheDays);
                var cached = jobs.FirstOrDefault(item =>
                    item.IdentityHash.Equals(identity.IdentityHash, StringComparison.Ordinal)
                    && item.ConfigurationHash.Equals(configurationHash, StringComparison.Ordinal)
                    && item.CreatedAt >= cacheCutoff
                    && item.Status is CustomerEnrichmentJobStatus.Succeeded
                        or CustomerEnrichmentJobStatus.NeedsReview
                        or CustomerEnrichmentJobStatus.NoResults);
                if (cached is not null)
                {
                    cached.ReusedCache = true;
                    Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(
                        customerId,
                        cached.Id,
                        cached.Status,
                        $"已复用 {settings.CacheDays} 天缓存，未发起新的联网请求。"));
                    return cached;
                }
            }

            var job = new CustomerEnrichmentJob
            {
                CustomerId = customerId,
                TriggerType = trigger,
                Status = CustomerEnrichmentJobStatus.Queued,
                IdentityHash = identity.IdentityHash,
                ConfigurationHash = configurationHash,
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow()
            };
            await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
            await _repository.LogEventAsync(
                "customer_enrichment_queued",
                customerId,
                null,
                $"job_id={job.Id};trigger={trigger};identity_hash={ShortHash(identity.IdentityHash)}",
                cancellationToken);
            _queue.Writer.TryWrite(job.Id);
            Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(customerId, job.Id, job.Status, "客户公开商业信息调查已进入后台队列。"));
            return job;
        }
        finally { _queueGate.Release(); }
    }

    public async Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = await _repository.GetCustomerEnrichmentJobAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException("调查任务不存在。");
        if (job.Status is CustomerEnrichmentJobStatus.Succeeded
            or CustomerEnrichmentJobStatus.NoResults
            or CustomerEnrichmentJobStatus.Cancelled) return;
        if (_jobCancellations.TryGetValue(jobId, out var running)) running.Cancel();
        job.Status = CustomerEnrichmentJobStatus.Cancelled;
        job.CompletedAt = _timeProvider.GetUtcNow();
        job.ErrorCode = CustomerEnrichmentErrorCodes.JobCancelled;
        job.ErrorMessage = "用户已取消调查任务。";
        await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
        Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, job.ErrorMessage));
    }

    public async Task ReviewAsync(
        string factId,
        CustomerEnrichmentReviewAction action,
        string? editedValue = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var fact = await _repository.GetCustomerEnrichmentFactAsync(factId, cancellationToken)
            ?? throw new InvalidOperationException("待审核的客户调查事实不存在。");
        if (action is CustomerEnrichmentReviewAction.Confirm or CustomerEnrichmentReviewAction.EditAndConfirm)
        {
            var factJob = await _repository.GetCustomerEnrichmentJobAsync(fact.JobId, cancellationToken)
                ?? throw new InvalidOperationException("待审核事实对应的调查任务不存在。");
            var currentIdentityHash = await CustomerExternalFactPolicy.GetCurrentIdentityHashAsync(
                _repository,
                fact.CustomerId,
                cancellationToken);
            if (currentIdentityHash.Length == 0
                || !factJob.IdentityHash.Equals(currentIdentityHash, StringComparison.Ordinal))
                throw new InvalidOperationException("客户身份资料已在本次调查后发生变化。旧调查只能保留为历史，不能确认成当前事实；请重新发起调查。");
        }
        var previousValue = fact.FieldValue;
        switch (action)
        {
            case CustomerEnrichmentReviewAction.Confirm:
                fact.VerificationStatus = CustomerEnrichmentVerificationStatus.HumanConfirmed;
                break;
            case CustomerEnrichmentReviewAction.EditAndConfirm:
                if (string.IsNullOrWhiteSpace(editedValue)) throw new InvalidOperationException("编辑确认时必须填写新值。");
                fact.FieldValue = editedValue.Trim();
                fact.NormalizedValue = NormalizeFactValue(fact.FieldType, fact.FieldValue);
                fact.VerificationStatus = CustomerEnrichmentVerificationStatus.HumanConfirmed;
                // The old public quote supported the old value, not the human
                // edit. Keep the source records in the job audit, but do not
                // misrepresent them as direct evidence for the replacement.
                fact.SourceIds = [];
                fact.EvidenceQuote = "";
                break;
            case CustomerEnrichmentReviewAction.Reject:
                fact.VerificationStatus = CustomerEnrichmentVerificationStatus.Rejected;
                break;
            case CustomerEnrichmentReviewAction.MarkOutdated:
                fact.VerificationStatus = CustomerEnrichmentVerificationStatus.Outdated;
                fact.ExpiresAt = _timeProvider.GetUtcNow();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
        if (fact.VerificationStatus == CustomerEnrichmentVerificationStatus.HumanConfirmed)
        {
            var settings = await GetSettingsAsync(cancellationToken);
            fact.LastVerifiedAt = _timeProvider.GetUtcNow();
            fact.ExpiresAt = _timeProvider.GetUtcNow().AddDays(settings.StandardRefreshDays);
        }
        fact.ReviewNote = (reason ?? "").Trim();
        if (action == CustomerEnrichmentReviewAction.EditAndConfirm && fact.ReviewNote.Length == 0)
            fact.ReviewNote = "人工编辑确认；原公开来源不作为编辑后值的直接证据。";
        var review = new CustomerEnrichmentReview
        {
            CustomerId = fact.CustomerId,
            FactId = fact.Id,
            JobId = fact.JobId,
            Action = action,
            PreviousValue = previousValue,
            NewValue = fact.FieldValue,
            Reason = fact.ReviewNote,
            CreatedAt = _timeProvider.GetUtcNow()
        };
        if (fact.VerificationStatus == CustomerEnrichmentVerificationStatus.HumanConfirmed)
            fact.HumanReviewId = review.Id;
        await _repository.ApplyCustomerEnrichmentReviewAsync(fact, review, cancellationToken);
        await _repository.LogEventAsync(
            "customer_enrichment_fact_reviewed",
            fact.CustomerId,
            null,
            $"job_id={fact.JobId};fact_id={fact.Id};action={action};field={SanitizeAuditToken(fact.FieldType)}",
            cancellationToken);
        var reviewedFacts = (await _repository.GetCustomerEnrichmentFactsAsync(
                fact.CustomerId,
                latestPerValue: false,
                cancellationToken))
            .Where(item => item.JobId.Equals(fact.JobId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var job = await _repository.GetCustomerEnrichmentJobAsync(fact.JobId, cancellationToken);
        var reviewedStatus = ResolveReviewedJobStatus(reviewedFacts);
        if (job is not null)
        {
            job.Status = reviewedStatus;
            job.ErrorCode = "";
            job.ErrorMessage = reviewedStatus == CustomerEnrichmentJobStatus.Succeeded
                ? "人工审核已完成；只有已核验或人工确认事实会进入 Customer Brain。"
                : "人工审核已保存，仍有候选或冲突事实待处理。";
            await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
        }
        // The fact/review transaction is already durable. Do not let a UI
        // cancellation suppress the corresponding Brain invalidation/refresh.
        await CustomerAnalysisFreshness.SynchronizeAsync(
            _repository,
            fact.CustomerId,
            CancellationToken.None);
        await RefreshBrainSafelyAsync(fact.CustomerId, CancellationToken.None);
        Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(
            fact.CustomerId,
            fact.JobId,
            reviewedStatus,
            "人工审核结果已保存，Customer Brain 证据门禁已同步刷新。"));
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var jobId in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (!_jobCancellations.TryAdd(jobId, jobCancellation))
                {
                    jobCancellation.Dispose();
                    continue;
                }
                try { await ProcessJobAsync(jobId, jobCancellation.Token); }
                catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested) { await MarkCancelledAsync(jobId); }
                catch (Exception error) { await MarkFailedAsync(jobId, error); }
                finally
                {
                    _jobCancellations.TryRemove(jobId, out _);
                    jobCancellation.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ProcessJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = await _repository.GetCustomerEnrichmentJobAsync(jobId, cancellationToken);
        if (job is null || job.Status == CustomerEnrichmentJobStatus.Cancelled) return;
        var lead = await _repository.GetLeadAsync(job.CustomerId, cancellationToken)
            ?? throw new CustomerEnrichmentException(CustomerEnrichmentErrorCodes.CustomerIdentityMissing, "客户不存在或已经删除。");
        var settings = await GetSettingsAsync(cancellationToken);
        var identity = CustomerEnrichmentIdentityService.Build(lead);
        if (!string.IsNullOrWhiteSpace(job.IdentityHash)
            && !job.IdentityHash.Equals(identity.IdentityHash, StringComparison.Ordinal))
        {
            await MarkIdentityChangedAsync(job, cancellationToken);
            return;
        }
        var queryTexts = CustomerEnrichmentQueryGenerator.Generate(identity, settings.MaxQueriesPerCustomer);
        if (queryTexts.Count == 0)
            throw new CustomerEnrichmentException(CustomerEnrichmentErrorCodes.CustomerIdentityMissing, "客户缺少可用于公开商业调查的邮箱、电话、姓名或公司信息。");

        var providers = CreateProviders(settings);
        var orderedProviders = settings.ProviderOrder
            .Where(providerId => IsConfigured(providerId, settings) && providers.ContainsKey(providerId))
            .Select(providerId => providers[providerId])
            .ToList();
        if (orderedProviders.Count == 0)
            throw new CustomerEnrichmentException(
                CustomerEnrichmentErrorCodes.SearchProviderUnavailable,
                "尚未配置搜索 Provider。主程序其他功能可继续使用；请在设置中配置 Tavily、Brave 或启用本地 SearXNG。");

        job.Status = CustomerEnrichmentJobStatus.Running;
        job.StartedAt = _timeProvider.GetUtcNow();
        job.ErrorCode = "";
        job.ErrorMessage = "";
        job.IdentityHash = identity.IdentityHash;
        await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
        Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, "正在生成最小化公开检索查询。"));

        var collected = new List<(CustomerEnrichmentQuery Query, CustomerEnrichmentSearchResult Result)>();
        var usedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CustomerEnrichmentException? lastProviderError = null;
        var anySearchSucceeded = false;
        foreach (var queryText in queryTexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = new CustomerEnrichmentQuery
            {
                JobId = job.Id,
                CustomerId = job.CustomerId,
                QueryText = queryText,
                QueryHash = CustomerEnrichmentQueryGenerator.HashQuery(queryText),
                CreatedAt = _timeProvider.GetUtcNow()
            };
            await _repository.SaveCustomerEnrichmentQueryAsync(query, cancellationToken);

            IReadOnlyList<CustomerEnrichmentSearchResult>? results = null;
            foreach (var provider in orderedProviders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                query.Provider = provider.Id;
                await _usageGate.WaitAsync(cancellationToken);
                try
                {
                    var currentUsage = await _repository.GetCustomerEnrichmentUsageSummaryAsync(cancellationToken);
                    if (!TryResolveRequestReservation(
                            provider.Id,
                            settings,
                            currentUsage,
                            GetMaximumAttempts(provider),
                            allowPartialReservation: true,
                            out var reservedAttempts,
                            out var reservedCost,
                            out var blocked))
                    {
                        lastProviderError = blocked;
                        query.ErrorCode = blocked!.Code;
                        query.ErrorMessage = blocked.Message;
                        query.Status = "budget_blocked";
                        await _repository.SaveCustomerEnrichmentUsageAsync(new CustomerEnrichmentProviderUsage
                        {
                            Provider = provider.Id,
                            JobId = job.Id,
                            Requests = 0,
                            EstimatedCostUsd = 0,
                            Succeeded = false,
                            ErrorCode = blocked.Code,
                            ErrorMessage = blocked.Message,
                            RequestState = "blocked",
                            CreatedAt = _timeProvider.GetLocalNow()
                        }, cancellationToken);
                        continue;
                    }

                    var usage = new CustomerEnrichmentProviderUsage
                    {
                        Provider = provider.Id,
                        JobId = job.Id,
                        Requests = reservedAttempts,
                        EstimatedCostUsd = reservedCost,
                        Succeeded = false,
                        ErrorCode = "REQUEST_RESERVED",
                        ErrorMessage = "外部搜索请求已在联网前记录本地用量与费用估算预留。",
                        RequestState = "reserved",
                        CreatedAt = _timeProvider.GetLocalNow()
                    };
                    await _repository.SaveCustomerEnrichmentUsageAsync(usage, cancellationToken);
                    try
                    {
                        // The durable reservation is committed before the network
                        // call. A restart therefore cannot silently replay a request
                        // that may already have consumed provider quota.
                        results = await provider.SearchAsync(new CustomerSearchRequest(
                            queryText,
                            settings.MaxResultsPerQuery,
                            identity.Language,
                            identity.Country,
                            reservedAttempts), cancellationToken);
                        usage.Succeeded = true;
                        anySearchSucceeded = true;
                        query.Status = "succeeded";
                        query.ResultsCount = results.Count;
                        query.RetrievedAt = _timeProvider.GetUtcNow();
                        usedProviders.Add(provider.Id);
                    }
                    catch (OperationCanceledException)
                    {
                        usage.ErrorCode = CustomerEnrichmentErrorCodes.JobCancelled;
                        usage.ErrorMessage = "搜索任务已取消。";
                        throw;
                    }
                    catch (CustomerEnrichmentException error)
                    {
                        lastProviderError = error;
                        usage.ErrorCode = error.Code;
                        usage.ErrorMessage = error.Message;
                        query.ErrorCode = error.Code;
                        query.ErrorMessage = error.Message;
                        query.Status = "provider_failed";
                    }
                    catch (Exception error)
                    {
                        var safe = new CustomerEnrichmentException(
                            CustomerEnrichmentErrorCodes.SearchProviderUnavailable,
                            $"{ProviderDisplayName(provider.Id)} 暂时不可用，系统可切换到下一 Provider。",
                            true,
                            error);
                        lastProviderError = safe;
                        usage.ErrorCode = safe.Code;
                        usage.ErrorMessage = safe.Message;
                        query.ErrorCode = safe.Code;
                        query.ErrorMessage = safe.Message;
                        query.Status = "provider_failed";
                    }
                    finally
                    {
                        var actualAttempts = GetActualAttempts(provider, reservedAttempts);
                        usage.Requests = actualAttempts;
                        usage.EstimatedCostUsd = EstimateSearchCost(provider.Id, settings, currentUsage, actualAttempts);
                        usage.RequestState = "completed";
                        if (usage.Succeeded)
                        {
                            usage.ErrorCode = "";
                            usage.ErrorMessage = "";
                        }
                        await _repository.SaveCustomerEnrichmentUsageAsync(usage, CancellationToken.None);
                        job.CostUsd += usage.EstimatedCostUsd;
                    }
                }
                finally { _usageGate.Release(); }
                if (results is not null) break;
            }
            if (results is null)
            {
                query.Status = "failed";
                query.RetrievedAt = _timeProvider.GetUtcNow();
            }
            await _repository.SaveCustomerEnrichmentQueryAsync(query, cancellationToken);
            await _repository.LogEventAsync(
                "customer_enrichment_query_completed",
                job.CustomerId,
                null,
                $"job_id={job.Id};query_hash={ShortHash(query.QueryHash)};provider={query.Provider};status={query.Status};results={query.ResultsCount}",
                cancellationToken);
            if (results is not null)
                collected.AddRange(results.Select(result => (query, result)));
            Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(
                job.CustomerId,
                job.Id,
                CustomerEnrichmentJobStatus.Running,
                $"已完成 {Math.Min(job.QueriesCount + 1, queryTexts.Count)}/{queryTexts.Count} 条公开检索。"));
            job.QueriesCount++;
            await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
        }

        job.Provider = string.Join(" → ", usedProviders);
        if (collected.Count == 0)
        {
            if (!anySearchSucceeded && lastProviderError is not null && job.QueriesCount > 0)
                throw lastProviderError;
            job.Status = CustomerEnrichmentJobStatus.NoResults;
            job.CompletedAt = _timeProvider.GetUtcNow();
            job.ErrorCode = CustomerEnrichmentErrorCodes.NoPublicResults;
            job.ErrorMessage = "未找到与该客户可靠关联的公开商业信息；本程序未记录付费估算不代表 Provider 未计费，实际账单以 Provider 为准。";
            await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
            Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, job.ErrorMessage));
            return;
        }

        Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, "正在安全读取公开网页并核对客户身份。"));
        var sources = await BuildSourcesAsync(job, identity, collected, settings, cancellationToken);
        await _repository.SaveCustomerEnrichmentSourcesAsync(sources, cancellationToken);
        job.SourcesCount = sources.Count;
        if (sources.Count == 0)
        {
            job.Status = CustomerEnrichmentJobStatus.NoResults;
            job.CompletedAt = _timeProvider.GetUtcNow();
            job.ErrorCode = CustomerEnrichmentErrorCodes.NoPublicResults;
            job.ErrorMessage = "搜索结果未通过公开网页与主体匹配安全检查；本程序未记录付费估算不代表 Provider 未计费，实际账单以 Provider 为准。";
            await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
            Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, job.ErrorMessage));
            return;
        }

        if (!_aiProvider.HasApiKey(AiModuleKeys.CustomerEnrichment))
        {
            job.Status = CustomerEnrichmentJobStatus.NeedsReview;
            job.CompletedAt = _timeProvider.GetUtcNow();
            job.ErrorCode = CustomerEnrichmentErrorCodes.AnalysisProviderUnavailable;
            job.ErrorMessage = "公开来源已保存；尚未配置该板块 AI 模型，未生成任何推断事实，请人工查看来源或在设置中配置模型。";
            await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
            Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, job.ErrorMessage));
            return;
        }

        if (!settings.AllowAiAnalysisRequests
            || settings.MonthlyBudgetUsd <= 0 || settings.AiAnalysisReservationUsd <= 0)
        {
            job.Status = CustomerEnrichmentJobStatus.NeedsReview;
            job.CompletedAt = _timeProvider.GetUtcNow();
            job.ErrorCode = CustomerEnrichmentErrorCodes.AiAnalysisPaymentNotAuthorized;
            job.ErrorMessage = "公开来源已保存；AI 事实整理可能产生 Provider 费用。当前未启用 AI 事实整理或未设置正数本地月度估算提醒额度，因此未发起 AI 请求、未生成推断事实。";
            await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
            Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, job.ErrorMessage));
            return;
        }

        Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, "正在用严格证据 Schema 提取商业事实。"));
        CustomerEnrichmentAnalysisResult analysis;
        await _usageGate.WaitAsync(cancellationToken);
        try
        {
            var usageBefore = await _repository.GetCustomerEnrichmentUsageSummaryAsync(cancellationToken);
            var remainingBudget = Math.Max(0, settings.MonthlyBudgetUsd - usageBefore.MonthEstimatedCostUsd);
            if (settings.AiAnalysisReservationUsd > remainingBudget)
            {
                job.Status = CustomerEnrichmentJobStatus.NeedsReview;
                job.CompletedAt = _timeProvider.GetUtcNow();
                job.ErrorCode = CustomerEnrichmentErrorCodes.PaidRequestBlocked;
                job.ErrorMessage = $"公开来源已保存；本次 AI 本地估算预留 ${settings.AiAnalysisReservationUsd:0.####} 超过本程序本月剩余提醒额度 ${remainingBudget:0.####}，已停止新的 AI 调用。实际账单以 Provider 为准。";
                await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
                Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, job.ErrorMessage));
                return;
            }

            var aiUsage = new CustomerEnrichmentProviderUsage
            {
                Provider = "ai-analysis",
                JobId = job.Id,
                Requests = 1,
                EstimatedCostUsd = settings.AiAnalysisReservationUsd,
                Succeeded = false,
                ErrorCode = "REQUEST_RESERVED",
                ErrorMessage = "AI 分析已在联网前记录本地月度估算预留；实际账单以 Provider 为准。",
                RequestState = "reserved",
                CreatedAt = _timeProvider.GetLocalNow()
            };
            await _repository.SaveCustomerEnrichmentUsageAsync(aiUsage, cancellationToken);
            job.CostUsd += settings.AiAnalysisReservationUsd;
            try
            {
                analysis = await _analyzer.AnalyzeAsync(identity, sources, cancellationToken);
                aiUsage.Succeeded = true;
                aiUsage.ErrorCode = "";
                aiUsage.ErrorMessage = "";
                aiUsage.RequestState = "completed";
                await _repository.SaveCustomerEnrichmentUsageAsync(aiUsage, CancellationToken.None);
            }
            catch (AiProviderException error)
            {
                aiUsage.ErrorCode = error.Code;
                aiUsage.ErrorMessage = error.Message;
                aiUsage.RequestState = "completed";
                await _repository.SaveCustomerEnrichmentUsageAsync(aiUsage, CancellationToken.None);
                throw new CustomerEnrichmentException(
                    error.Code == "invalid_structured_output"
                        ? CustomerEnrichmentErrorCodes.InvalidModelResponse
                        : CustomerEnrichmentErrorCodes.AnalysisProviderUnavailable,
                    error.Message,
                    error.Retryable,
                    error);
            }
            catch (OperationCanceledException)
            {
                aiUsage.ErrorCode = CustomerEnrichmentErrorCodes.JobCancelled;
                aiUsage.ErrorMessage = "AI 分析任务已取消。";
                aiUsage.RequestState = "completed";
                await _repository.SaveCustomerEnrichmentUsageAsync(aiUsage, CancellationToken.None);
                throw;
            }
        }
        finally { _usageGate.Release(); }
        var latestLead = await _repository.GetLeadAsync(job.CustomerId, cancellationToken);
        var latestIdentityHash = latestLead is null
            ? ""
            : CustomerEnrichmentIdentityService.Build(latestLead).IdentityHash;
        if (!job.IdentityHash.Equals(latestIdentityHash, StringComparison.Ordinal))
        {
            await MarkIdentityChangedAsync(job, cancellationToken);
            return;
        }

        var facts = BuildFacts(job, lead, analysis, sources, settings);
        await _repository.SaveCustomerEnrichmentFactsAsync(facts, cancellationToken);
        job.FactsCount = facts.Count;
        job.CompletedAt = _timeProvider.GetUtcNow();
        job.ErrorCode = "";
        job.ErrorMessage = facts.Count == 0
            ? "公开来源已保存，但没有证据充分的商业事实；请查看候选来源。"
            : "调查完成；只有已核验或人工确认事实会进入 Customer Brain。";
        job.Status = ResolveReviewedJobStatus(facts);
        await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
        if (facts.Any(fact => fact.VerificationStatus == CustomerEnrichmentVerificationStatus.Verified))
            await RefreshBrainSafelyAsync(job.CustomerId, cancellationToken);
        await _repository.LogEventAsync(
            "customer_enrichment_completed",
            job.CustomerId,
            null,
            $"job_id={job.Id};status={job.Status};providers={string.Join(',', usedProviders)};queries={job.QueriesCount};sources={job.SourcesCount};facts={job.FactsCount};cost_usd={job.CostUsd:0.####}",
            cancellationToken);
        Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, job.ErrorMessage));
    }

    private async Task<List<CustomerEnrichmentSource>> BuildSourcesAsync(
        CustomerEnrichmentJob job,
        CustomerEnrichmentIdentity identity,
        IReadOnlyList<(CustomerEnrichmentQuery Query, CustomerEnrichmentSearchResult Result)> collected,
        CustomerEnrichmentSettings settings,
        CancellationToken cancellationToken)
    {
        var output = new List<CustomerEnrichmentSource>();
        var canonicalUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageReads = 0;
        foreach (var item in collected.OrderBy(item => item.Result.Rank))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(item.Result.Url, UriKind.Absolute, out var resultUri)
                || (!resultUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !resultUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))) continue;
            var canonicalUrl = Canonicalize(resultUri);
            var source = new CustomerEnrichmentSource
            {
                JobId = job.Id,
                QueryId = item.Query.Id,
                CustomerId = job.CustomerId,
                Url = resultUri.AbsoluteUri,
                CanonicalUrl = canonicalUrl,
                Title = item.Result.Title,
                Domain = resultUri.IdnHost.ToLowerInvariant(),
                Snippet = item.Result.Snippet,
                PublishedAt = item.Result.PublishedAt,
                RetrievedAt = item.Result.RetrievedAt,
                Provider = item.Result.Provider,
                Rank = item.Result.Rank,
                FetchStatus = "snippet_only"
            };
            if (pageReads < settings.MaxPagesPerCustomer)
            {
                pageReads++;
                try
                {
                    var read = await _webReader.ReadAsync(resultUri, cancellationToken);
                    source.Url = read.FinalUrl.AbsoluteUri;
                    source.CanonicalUrl = read.CanonicalUrl.AbsoluteUri;
                    source.Domain = read.FinalUrl.IdnHost.ToLowerInvariant();
                    source.Title = string.IsNullOrWhiteSpace(read.Title) ? source.Title : read.Title;
                    source.ContentText = read.ContentText;
                    source.ContentHash = read.ContentHash;
                    source.PublishedAt ??= read.PublishedAt;
                    source.RetrievedAt = read.RetrievedAt;
                    source.FetchStatus = "fetched";
                }
                catch (CustomerEnrichmentException error)
                {
                    source.FetchStatus = "fetch_failed";
                    source.FetchErrorCode = error.Code;
                }
            }
            if (string.IsNullOrWhiteSpace(source.ContentHash))
                source.ContentHash = StableHash($"{source.Title}\n{source.Snippet}");
            if (!canonicalUrls.Add(source.CanonicalUrl)) continue;
            if (source.ContentHash.Length > 0 && !contentHashes.Add(source.ContentHash)) continue;
            var score = CustomerEnrichmentEntityMatcher.Score(identity, source);
            source.IdentityMatchScore = score.Score;
            source.IdentityMatchStatus = score.Status;
            source.IdentityMatchReasons = score.Reasons;
            source.IdentityConflicts = score.Conflicts;
            output.Add(source);
        }
        return output;
    }

    private List<CustomerEnrichmentFact> BuildFacts(
        CustomerEnrichmentJob job,
        Lead lead,
        CustomerEnrichmentAnalysisResult analysis,
        IReadOnlyList<CustomerEnrichmentSource> sources,
        CustomerEnrichmentSettings settings)
    {
        var byId = sources.ToDictionary(source => source.Id, StringComparer.OrdinalIgnoreCase);
        var now = _timeProvider.GetUtcNow();
        var expiryDays = lead.Grade.Equals("A", StringComparison.OrdinalIgnoreCase)
            ? settings.HighValueRefreshDays
            : settings.StandardRefreshDays;
        var output = new List<CustomerEnrichmentFact>();
        void Add(IEnumerable<CustomerEnrichmentExtractedFact> candidates, CustomerEnrichmentVerificationStatus forcedStatus)
        {
            foreach (var candidate in candidates)
            {
                var referenced = candidate.SourceIds
                    .Where(byId.ContainsKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(sourceId => byId[sourceId])
                    .ToList();
                if (referenced.Count == 0 || string.IsNullOrWhiteSpace(candidate.FieldType)
                    || string.IsNullOrWhiteSpace(candidate.Value) || string.IsNullOrWhiteSpace(candidate.EvidenceQuote)) continue;
                var status = forcedStatus;
                if (forcedStatus == CustomerEnrichmentVerificationStatus.LikelyMatch)
                {
                    status = candidate.Confidence >= 80
                        && analysis.EntityMatch.Score >= 90
                        && analysis.EntityMatch.Status.Equals("verified", StringComparison.OrdinalIgnoreCase)
                        && analysis.EntityMatch.Conflicts.Count == 0
                        && referenced.All(source =>
                            source.IdentityMatchStatus == CustomerEnrichmentVerificationStatus.Verified
                            && source.IdentityConflicts.Count == 0)
                        ? CustomerEnrichmentVerificationStatus.Verified
                        : referenced.Max(source => source.IdentityMatchScore) >= 70
                            ? CustomerEnrichmentVerificationStatus.LikelyMatch
                            : CustomerEnrichmentVerificationStatus.PossibleMatch;
                }
                output.Add(new CustomerEnrichmentFact
                {
                    CustomerId = job.CustomerId,
                    JobId = job.Id,
                    FieldType = candidate.FieldType.Trim(),
                    FieldValue = candidate.Value.Trim(),
                    NormalizedValue = NormalizeFactValue(candidate.FieldType, candidate.Value),
                    Category = candidate.Category.Trim(),
                    FactType = candidate.FactType.Trim(),
                    ConfidenceScore = Math.Clamp(candidate.Confidence, 0, 100),
                    VerificationStatus = status,
                    SourceIds = referenced.Select(source => source.Id).ToList(),
                    EvidenceQuote = candidate.EvidenceQuote.Trim(),
                    FirstDiscoveredAt = now,
                    LastVerifiedAt = status == CustomerEnrichmentVerificationStatus.Verified ? now : null,
                    ExpiresAt = now.AddDays(expiryDays),
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }
        Add(analysis.Facts, CustomerEnrichmentVerificationStatus.LikelyMatch);
        Add(analysis.PossibleContext, CustomerEnrichmentVerificationStatus.PossibleMatch);
        Add(analysis.ConflictingInformation, CustomerEnrichmentVerificationStatus.Conflicting);
        return output
            .Where(fact => fact.NormalizedValue.Length > 0)
            .GroupBy(fact => $"{fact.FieldType}|{fact.NormalizedValue}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(fact => fact.ConfidenceScore).First())
            .ToList();
    }

    private async Task MarkFailedAsync(string jobId, Exception error)
    {
        try
        {
            var job = await _repository.GetCustomerEnrichmentJobAsync(jobId, CancellationToken.None);
            if (job is null || job.Status == CustomerEnrichmentJobStatus.Cancelled) return;
            var enrichmentError = error as CustomerEnrichmentException;
            job.Status = CustomerEnrichmentJobStatus.Failed;
            job.FailedAt = _timeProvider.GetUtcNow();
            job.CompletedAt = _timeProvider.GetUtcNow();
            job.ErrorCode = enrichmentError?.Code ?? CustomerEnrichmentErrorCodes.SearchProviderUnavailable;
            job.ErrorMessage = SafeErrorMessage(error);
            await _repository.SaveCustomerEnrichmentJobAsync(job, CancellationToken.None);
            await _repository.LogEventAsync(
                "customer_enrichment_failed",
                job.CustomerId,
                null,
                $"job_id={job.Id};code={SanitizeAuditToken(job.ErrorCode)};cost_usd={job.CostUsd:0.####}",
                CancellationToken.None);
            Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, job.ErrorMessage));
        }
        catch { /* The enrichment worker must never take down the host application. */ }
    }

    private async Task MarkIdentityChangedAsync(
        CustomerEnrichmentJob job,
        CancellationToken cancellationToken)
    {
        job.Status = CustomerEnrichmentJobStatus.NeedsReview;
        job.CompletedAt = _timeProvider.GetUtcNow();
        job.ErrorCode = CustomerEnrichmentErrorCodes.CustomerIdentityChanged;
        job.ErrorMessage = "客户身份资料已在调查期间发生变化。公开来源已保留为历史审计，但不会生成或启用当前事实；系统下次将按最新资料重新调查。";
        job.FactsCount = 0;
        await _repository.SaveCustomerEnrichmentJobAsync(job, cancellationToken);
        await CustomerAnalysisFreshness.SynchronizeAsync(
            _repository,
            job.CustomerId,
            CancellationToken.None);
        await RefreshBrainSafelyAsync(job.CustomerId, CancellationToken.None);
        await _repository.LogEventAsync(
            "customer_enrichment_identity_changed",
            job.CustomerId,
            null,
            $"job_id={job.Id};identity_hash={ShortHash(job.IdentityHash)}",
            CancellationToken.None);
        Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(
            job.CustomerId,
            job.Id,
            job.Status,
            job.ErrorMessage));
    }

    private async Task MarkCancelledAsync(string jobId)
    {
        try
        {
            var job = await _repository.GetCustomerEnrichmentJobAsync(jobId, CancellationToken.None);
            if (job is null) return;
            job.Status = CustomerEnrichmentJobStatus.Cancelled;
            job.CompletedAt = _timeProvider.GetUtcNow();
            job.ErrorCode = CustomerEnrichmentErrorCodes.JobCancelled;
            job.ErrorMessage = "调查任务已取消。";
            await _repository.SaveCustomerEnrichmentJobAsync(job, CancellationToken.None);
            Changed?.Invoke(this, new CustomerEnrichmentChangedEventArgs(job.CustomerId, job.Id, job.Status, job.ErrorMessage));
        }
        catch { }
    }

    private async Task QueueStartupCandidatesAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        if (settings.MaxAutomaticJobsPerStartup <= 0 || settings.AutoEnrichmentGrades.Count == 0
            || !HasAnyConfiguredProvider(settings)) return;
        var leads = (await _repository.GetLeadsAsync(cancellationToken: cancellationToken))
            .Where(lead => settings.AutoEnrichmentGrades.Contains(lead.Grade, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(lead => lead.Score)
            .Take(settings.MaxAutomaticJobsPerStartup)
            .ToList();
        foreach (var lead in leads)
        {
            try { await QueueAsync(lead.Id, CustomerEnrichmentTriggerType.HighValueLead, cancellationToken: cancellationToken); }
            catch { /* Optional automation must not block application startup. */ }
        }
    }

    private void WhatsAppSync_MessageSynchronized(object? sender, WhatsAppMessageSyncedEvent e)
    {
        // Enrichment only reads; withholding it for backlog would lose data the
        // user already has locally.
        var message = e.Message;
        if (Volatile.Read(ref _started) == 0 || message.Direction != WhatsAppMessageDirection.Incoming
            || message.IsGroup || message.IsStatusUpdate || string.IsNullOrWhiteSpace(message.LeadId)) return;
        _ = QueueAutomaticallyAsync(message.LeadId, CustomerEnrichmentTriggerType.NewWhatsAppConversation);
    }

    private void LeadAutomation_AnalysisChanged(object? sender, LeadAnalysisAutomationEventArgs args)
    {
        if (Volatile.Read(ref _started) == 0 || args.Status != AnalysisStatus.Succeeded) return;
        _ = QueueHighValueAfterAnalysisAsync(args.LeadId);
    }

    private void Imports_LeadsImported(object? sender, LeadsImportedEventArgs args)
    {
        if (Volatile.Read(ref _started) == 0 || args.LeadIds.Count == 0) return;
        _ = QueueImportedLeadsAsync(args.LeadIds);
    }

    private async Task QueueImportedLeadsAsync(IReadOnlyList<string> leadIds)
    {
        try
        {
            var settings = await GetSettingsAsync(_lifetime.Token);
            if (!HasAnyConfiguredProvider(settings)) return;
            var queued = 0;
            foreach (var leadId in leadIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (queued >= settings.MaxAutomaticJobsPerStartup) break;
                var lead = await _repository.GetLeadAsync(leadId, _lifetime.Token);
                if (lead is null || !settings.AutoEnrichmentGrades.Contains(lead.Grade, StringComparer.OrdinalIgnoreCase)) continue;
                await QueueAsync(lead.Id, CustomerEnrichmentTriggerType.CustomerImport, cancellationToken: _lifetime.Token);
                queued++;
            }
        }
        catch { }
    }

    private async Task QueueHighValueAfterAnalysisAsync(string leadId)
    {
        try
        {
            var settings = await GetSettingsAsync(_lifetime.Token);
            if (!HasAnyConfiguredProvider(settings)) return;
            var lead = await _repository.GetLeadAsync(leadId, _lifetime.Token);
            if (lead is not null && settings.AutoEnrichmentGrades.Contains(lead.Grade, StringComparer.OrdinalIgnoreCase))
                await QueueAsync(lead.Id, CustomerEnrichmentTriggerType.HighValueLead, cancellationToken: _lifetime.Token);
        }
        catch { }
    }

    private async Task QueueAutomaticallyAsync(string customerId, CustomerEnrichmentTriggerType trigger)
    {
        try
        {
            var settings = await GetSettingsAsync(_lifetime.Token);
            if (!HasAnyConfiguredProvider(settings)) return;
            var lead = await _repository.GetLeadAsync(customerId, _lifetime.Token);
            if (lead is null || !settings.AutoEnrichmentGrades.Contains(lead.Grade, StringComparer.OrdinalIgnoreCase)) return;
            await QueueAsync(customerId, trigger, cancellationToken: _lifetime.Token);
        }
        catch { }
    }

    private IReadOnlyDictionary<string, ICustomerSearchProvider> CreateProviders(CustomerEnrichmentSettings settings)
    {
        if (_injectedProviders is not null) return _injectedProviders;
        lock (_providerGate)
        {
            var searXngUrl = settings.SearXngBaseUrl.TrimEnd('/');
            if (_searXngProvider is null || !_searXngProviderUrl.Equals(searXngUrl, StringComparison.OrdinalIgnoreCase))
            {
                _searXngProvider = new SearXngSearchProvider(searXngUrl);
                _searXngProviderUrl = searXngUrl;
            }
            return new Dictionary<string, ICustomerSearchProvider>(StringComparer.OrdinalIgnoreCase)
            {
                ["tavily"] = _tavilyProvider!,
                ["brave"] = _braveProvider!,
                ["searxng"] = _searXngProvider
            };
        }
    }

    private bool IsConfigured(string providerId, CustomerEnrichmentSettings settings)
    {
        if (_injectedProviders?.ContainsKey(providerId) == true) return true;
        return providerId.ToLowerInvariant() switch
        {
            "tavily" => ProviderCredentialStore("tavily").Exists(),
            "brave" => ProviderCredentialStore("brave").Exists(),
            "searxng" => settings.SearXngEnabled,
            _ => false
        };
    }

    private bool HasAnyConfiguredProvider(CustomerEnrichmentSettings settings) =>
        settings.ProviderOrder.Any(providerId => IsConfigured(providerId, settings));

    private static bool TryResolveRequestReservation(
        string providerId,
        CustomerEnrichmentSettings settings,
        CustomerEnrichmentUsageSummary usage,
        int desiredAttempts,
        bool allowPartialReservation,
        out int reservedAttempts,
        out decimal estimatedCost,
        out CustomerEnrichmentException? blocked)
    {
        reservedAttempts = 0;
        estimatedCost = 0;
        blocked = null;
        var normalized = NormalizeProvider(providerId);
        desiredAttempts = Math.Clamp(desiredAttempts, 1, 10);
        if (normalized == "searxng")
        {
            reservedAttempts = desiredAttempts;
            return true;
        }
        var freeLimit = normalized switch
        {
            "tavily" => settings.TavilyMonthlyFreeRequests,
            "brave" => settings.BraveMonthlyFreeRequests,
            _ => 0
        };
        var used = usage.ProviderRequests.GetValueOrDefault(normalized);
        var freeRemaining = Math.Max(0, freeLimit - used);
        if (!settings.AllowPaidRequests)
        {
            reservedAttempts = allowPartialReservation
                ? Math.Min(desiredAttempts, freeRemaining)
                : freeRemaining >= desiredAttempts ? desiredAttempts : 0;
            if (reservedAttempts > 0) return true;
            blocked = new CustomerEnrichmentException(
                CustomerEnrichmentErrorCodes.ProviderQuotaExhausted,
                $"{ProviderDisplayName(normalized)} 的本地账号额度估算不足，本程序未继续发起该次请求；账号实际额度和账单以 Provider 为准。");
            return false;
        }

        // Public list prices captured in August 2026 are only a local estimate.
        // Provider plans, account-side usage and billing can differ at any time.
        var unitCost = normalized switch
        {
            "tavily" => 0.008m,
            "brave" => 0.005m,
            _ => 0m
        };
        var remaining = Math.Max(0, settings.MonthlyBudgetUsd - usage.MonthEstimatedCostUsd);
        var paidCapacityDecimal = unitCost <= 0 ? desiredAttempts : Math.Floor(remaining / unitCost);
        var paidCapacity = paidCapacityDecimal >= 10 ? 10 : Math.Max(0, (int)paidCapacityDecimal);
        var totalCapacity = Math.Min(10, Math.Min(10, freeRemaining) + paidCapacity);
        reservedAttempts = allowPartialReservation
            ? Math.Min(desiredAttempts, totalCapacity)
            : totalCapacity >= desiredAttempts ? desiredAttempts : 0;
        if (reservedAttempts > 0)
        {
            estimatedCost = Math.Max(0, reservedAttempts - freeRemaining) * unitCost;
            return true;
        }
        blocked = new CustomerEnrichmentException(
            CustomerEnrichmentErrorCodes.PaidRequestBlocked,
            $"{ProviderDisplayName(normalized)} 的下一次本地费用估算超过本程序本月剩余提醒额度 ${remaining:0.####}，已停止新请求；实际账单以 Provider 为准。");
        return false;
    }

    private static decimal EstimateSearchCost(
        string providerId,
        CustomerEnrichmentSettings settings,
        CustomerEnrichmentUsageSummary usageBefore,
        int actualAttempts)
    {
        if (actualAttempts <= 0) return 0;
        var normalized = NormalizeProvider(providerId);
        if (normalized == "searxng") return 0;
        var freeLimit = normalized == "tavily"
            ? settings.TavilyMonthlyFreeRequests
            : normalized == "brave" ? settings.BraveMonthlyFreeRequests : 0;
        var unitCost = normalized == "tavily" ? 0.008m : normalized == "brave" ? 0.005m : 0m;
        var used = usageBefore.ProviderRequests.GetValueOrDefault(normalized);
        var paidBefore = Math.Max(0, used - freeLimit);
        var paidAfter = Math.Max(0, used + actualAttempts - freeLimit);
        return (paidAfter - paidBefore) * unitCost;
    }

    private static int GetMaximumAttempts(ICustomerSearchProvider provider) =>
        provider is IMeteredCustomerSearchProvider metered
            ? Math.Clamp(metered.MaximumAttempts, 1, 10)
            : 1;

    private static int GetActualAttempts(ICustomerSearchProvider provider, int reservedAttempts) =>
        provider is IMeteredCustomerSearchProvider metered
            ? Math.Clamp(metered.LastAttemptCount, 0, reservedAttempts)
            : Math.Min(1, reservedAttempts);

    private static CustomerEnrichmentJobStatus ResolveReviewedJobStatus(
        IReadOnlyCollection<CustomerEnrichmentFact> facts)
    {
        if (facts.Count == 0) return CustomerEnrichmentJobStatus.NeedsReview;
        return facts.Any(fact => fact.VerificationStatus is
                CustomerEnrichmentVerificationStatus.LikelyMatch or
                CustomerEnrichmentVerificationStatus.PossibleMatch or
                CustomerEnrichmentVerificationStatus.Conflicting)
            ? CustomerEnrichmentJobStatus.NeedsReview
            : CustomerEnrichmentJobStatus.Succeeded;
    }

    private static void ApplyFreeRemaining(
        CustomerEnrichmentUsageSummary usage,
        CustomerEnrichmentSettings settings)
    {
        usage.ProviderFreeRemaining["tavily"] = Math.Max(
            0,
            settings.TavilyMonthlyFreeRequests - usage.ProviderRequests.GetValueOrDefault("tavily"));
        usage.ProviderFreeRemaining["brave"] = Math.Max(
            0,
            settings.BraveMonthlyFreeRequests - usage.ProviderRequests.GetValueOrDefault("brave"));
    }

    private async Task RefreshBrainSafelyAsync(string customerId, CancellationToken cancellationToken)
    {
        try { await _customerBrain.RefreshAsync(customerId, cancellationToken); }
        catch { /* Facts are already durable; Brain will refresh on its next normal read. */ }
    }

    private static WindowsCredentialStore ProviderCredentialStore(string providerId) =>
        new($"WAFlow/SearchProvider/{providerId.ToLowerInvariant()}");

    private static string NormalizeCredentialProvider(string providerId)
    {
        var normalized = NormalizeProvider(providerId);
        if (!SupportedProviderIds.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(providerId), "未知搜索 Provider。");
        return normalized;
    }

    private static string NormalizeProvider(string providerId) => (providerId ?? "").Trim().ToLowerInvariant();

    private static string ProviderDisplayName(string providerId) => providerId.ToLowerInvariant() switch
    {
        "tavily" => "Tavily",
        "brave" => "Brave Search",
        "searxng" => "SearXNG",
        _ => providerId
    };

    private static void ValidateSearXngUrl(string value)
    {
        if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("SearXNG 地址必须是有效的 HTTP/HTTPS URL，且不能包含用户名或密码。");
        if (uri.Scheme == Uri.UriSchemeHttp
            && uri.Host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1"))
            throw new InvalidOperationException("非本机 SearXNG 必须使用 HTTPS；本地服务可使用 127.0.0.1 HTTP 地址。");
    }

    private static string Canonicalize(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = "",
            Host = uri.IdnHost.ToLowerInvariant(),
            Scheme = uri.Scheme.ToLowerInvariant()
        };
        if ((builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443)
            || (builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80)) builder.Port = -1;
        return builder.Uri.AbsoluteUri;
    }

    private static string NormalizeFactValue(string fieldType, string value)
    {
        var normalizedType = (fieldType ?? "").Trim().ToLowerInvariant();
        if (normalizedType.Contains("email", StringComparison.Ordinal))
            return CustomerEnrichmentIdentityService.NormalizeEmail(value);
        if (normalizedType.Contains("phone", StringComparison.Ordinal)
            || normalizedType.Contains("whatsapp", StringComparison.Ordinal))
        {
            var digits = PhoneIdentity.Digits(value);
            return digits.Length == 0 ? "" : "+" + digits;
        }
        return Whitespace.Replace((value ?? "").Trim().ToLowerInvariant(), " ");
    }

    private static string StableHash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""))).ToLowerInvariant();

    private string BuildConfigurationHash(CustomerEnrichmentSettings settings) => StableHash(string.Join('|',
        string.Join(',', settings.ProviderOrder.Select(NormalizeProvider)),
        $"tavily={IsConfigured("tavily", settings)}",
        $"brave={IsConfigured("brave", settings)}",
        $"searxng={settings.SearXngEnabled}:{settings.SearXngBaseUrl.TrimEnd('/').ToLowerInvariant()}",
        $"limits={settings.MaxQueriesPerCustomer}/{settings.MaxResultsPerQuery}/{settings.MaxPagesPerCustomer}",
        $"ai={settings.AllowAiAnalysisRequests}:{settings.AiAnalysisReservationUsd:0.####}"));

    private static string ShortHash(string value) => value.Length <= 16 ? value : value[..16];

    private static string SanitizeAuditToken(string value) => new(
        (value ?? "").Where(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.').Take(64).ToArray());

    private static string SafeErrorMessage(Exception error)
    {
        var message = Whitespace.Replace(error.Message ?? "客户外部调查失败。", " ").Trim();
        return message.Length <= 400 ? message : message[..400];
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CustomerEnrichmentService));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_whatsAppSync is not null) _whatsAppSync.MessageSynchronized -= WhatsAppSync_MessageSynchronized;
        if (_leadAutomation is not null) _leadAutomation.AnalysisChanged -= LeadAutomation_AnalysisChanged;
        if (_imports is not null) _imports.LeadsImported -= Imports_LeadsImported;
        _queue.Writer.TryComplete();
        _lifetime.Cancel();
        foreach (var cancellation in _jobCancellations.Values) cancellation.Cancel();
        if (_worker is not null)
        {
            try { await _worker; }
            catch (OperationCanceledException) { }
        }
        foreach (var cancellation in _jobCancellations.Values) cancellation.Dispose();
        if (_ownsWebReader) _webReader.Dispose();
        _queueGate.Dispose();
        _usageGate.Dispose();
        _lifetime.Dispose();
    }

    private sealed class FixedSecretStore(string secret) : ISecretStore
    {
        public void Save(string value) => throw new NotSupportedException();
        public string Read() => secret;
    }
}
