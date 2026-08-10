using System.Threading.Channels;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record LeadAnalysisAutomationEventArgs(string LeadId, AnalysisStatus Status, string Message);
public sealed class LeadBulkAnalysisProgress(
    int total,
    int completed,
    int succeeded,
    int failed,
    string currentLeadId,
    string currentLeadName,
    string state,
    string message) : EventArgs
{
    public int Total { get; } = total;
    public int Completed { get; } = completed;
    public int Succeeded { get; } = succeeded;
    public int Failed { get; } = failed;
    public string CurrentLeadId { get; } = currentLeadId;
    public string CurrentLeadName { get; } = currentLeadName;
    public string State { get; } = state;
    public string Message { get; } = message;
}
public sealed record LeadBulkAnalysisResult(int Total, int Succeeded, int Failed);

public sealed class LeadIntelligenceAutomationService : IAsyncDisposable
{
    private readonly LocalRepository _repository;
    private readonly DeepSeekService _provider;
    private readonly WhatsAppSyncService _sync;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _bulkAnalysisGate = new(1, 1);
    private readonly object _bulkProgressLock = new();
    private Task? _worker;
    private LeadBulkAnalysisProgress? _currentBulkProgress;
    private string _currentBulkModel = "";
    private int _bulkAnalysisActive;

    public event EventHandler<LeadAnalysisAutomationEventArgs>? AnalysisChanged;
    public event EventHandler<LeadBulkAnalysisProgress>? BulkAnalysisProgressChanged;

    public bool IsBulkAnalysisRunning => Volatile.Read(ref _bulkAnalysisActive) == 1;

    public LeadBulkAnalysisProgress? CurrentBulkProgress
    {
        get
        {
            lock (_bulkProgressLock) return _currentBulkProgress;
        }
    }

    public string CurrentBulkModel
    {
        get
        {
            lock (_bulkProgressLock) return _currentBulkModel;
        }
    }

    public LeadIntelligenceAutomationService(LocalRepository repository, DeepSeekService provider, WhatsAppSyncService sync)
    {
        _repository = repository;
        _provider = provider;
        _sync = sync;
        _sync.MessageSynchronized += Sync_MessageSynchronized;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _worker ??= Task.Run(() => ProcessQueueAsync(_lifetime.Token), CancellationToken.None);
        var pending = (await _repository.GetLeadsAsync(cancellationToken: cancellationToken))
            .Where(lead => lead.AnalysisStatus == AnalysisStatus.Queued)
            .Select(lead => lead.Id);
        foreach (var leadId in pending) _queue.Writer.TryWrite(leadId);
    }

    public async Task QueueLeadForReplyAsync(WhatsAppMessage message, CancellationToken cancellationToken = default)
    {
        if (message.IsGroup || message.IsStatusUpdate || message.Direction != WhatsAppMessageDirection.Incoming || string.IsNullOrWhiteSpace(message.LeadId) || string.IsNullOrWhiteSpace(message.Body)) return;
        var lead = await _repository.GetLeadAsync(message.LeadId, cancellationToken);
        if (lead is null) return;

        LeadScoringService.ResetToAiBaseline(
            lead,
            "已收到 WhatsApp 新回复，等待 AI 结合完整上下文分析",
            _provider.HasApiKey() ? "AI 分析队列处理中。" : "配置 AI API 后将自动分析该客户回复。");
        lead.AnalysisStatus = AnalysisStatus.Queued;
        lead.AnalysisTrigger = "whatsapp_reply";
        lead.AnalysisRequestedAt = message.Timestamp;
        lead.AnalysisError = _provider.HasApiKey() ? "" : "等待配置 AI API 与模型后自动分析。";
        await _repository.UpsertLeadAsync(lead, cancellationToken);
        await _repository.LogEventAsync("lead_analysis_queued", lead.Id, null, $"trigger=whatsapp_reply; message_id={message.ProviderMessageId}; contract=v{LeadIntelligenceContract.Version}", cancellationToken);
        AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(lead.Id, AnalysisStatus.Queued, "WhatsApp 新回复已进入 AI 分析队列"));
        _queue.Writer.TryWrite(lead.Id);
    }

    public async Task NotifyProviderConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var pending = (await _repository.GetLeadsAsync(cancellationToken: cancellationToken))
            .Where(lead => lead.AnalysisStatus == AnalysisStatus.Queued)
            .Select(lead => lead.Id);
        foreach (var leadId in pending) _queue.Writer.TryWrite(leadId);
    }

    public void QueueOpportunitySupplementAnalysis(IEnumerable<string> leadIds)
    {
        foreach (var leadId in leadIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(
                leadId,
                AnalysisStatus.Queued,
                "新增交易证据已进入商机智能增量分析队列"));
            _queue.Writer.TryWrite(leadId);
        }
    }

    public async Task<LeadBulkAnalysisResult> AnalyzeAllLeadsAsync(IProgress<LeadBulkAnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _bulkAnalysisGate.WaitAsync(0, cancellationToken))
        {
            throw new DeepSeekException("bulk_analysis_running", "商机批量分析正在运行，请等待当前任务完成。", true);
        }

        lock (_bulkProgressLock)
        {
            _currentBulkProgress = null;
            _currentBulkModel = "";
        }
        Volatile.Write(ref _bulkAnalysisActive, 1);

        try
        {
            return await AnalyzeAllLeadsCoreAsync(progress, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _bulkAnalysisActive, 0);
            _bulkAnalysisGate.Release();
        }
    }

    private async Task<LeadBulkAnalysisResult> AnalyzeAllLeadsCoreAsync(IProgress<LeadBulkAnalysisProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_provider.HasApiKey()) throw new DeepSeekException("provider_not_configured", "请先在 API 对接中配置 API Key。", false);
        var execution = await _provider.ResolveExecutionProfileAsync(AiModuleKeys.LeadIntelligence, cancellationToken);
        lock (_bulkProgressLock) _currentBulkModel = execution.Model;

        var leads = await _repository.GetLeadsAsync(cancellationToken: cancellationToken);
        var currentIds = leads.Select(lead => lead.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = await _repository.GetLeadBulkAnalysisRunStateAsync(cancellationToken);
        var providerId = execution.ProviderId;
        var canResume = previous is { IsComplete: false, PendingLeadIds.Count: > 0 }
            && (string.IsNullOrWhiteSpace(previous.ProviderId)
                || previous.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            && previous.Model.Equals(execution.Model, StringComparison.OrdinalIgnoreCase);
        var retryableLeadIds = leads
            .Where(lead => lead.AnalysisStatus == AnalysisStatus.RetryableFailed)
            .Select(lead => lead.Id)
            .ToList();
        var retryFailedOnly = !canResume && retryableLeadIds.Count > 0;

        LeadBulkAnalysisRunState run;
        if (canResume)
        {
            run = previous!;
            run.AllLeadIds = run.AllLeadIds
                .Where(currentIds.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            run.PendingLeadIds = run.PendingLeadIds
                .Where(currentIds.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requeuedFailures = retryableLeadIds
                .Where(id => run.AllLeadIds.Contains(id, StringComparer.OrdinalIgnoreCase)
                    && !run.PendingLeadIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToList();
            run.PendingLeadIds = requeuedFailures
                .Concat(run.PendingLeadIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            run.Failed = Math.Max(0, run.Failed - requeuedFailures.Count);
        }
        else
        {
            var selectedLeadIds = retryFailedOnly
                ? retryableLeadIds
                : leads.Select(lead => lead.Id).ToList();
            run = new LeadBulkAnalysisRunState
            {
                ProviderId = providerId,
                Model = execution.Model,
                AllLeadIds = selectedLeadIds.ToList(),
                PendingLeadIds = selectedLeadIds.ToList()
            };
        }
        run.ProviderId = providerId;
        run.Model = execution.Model;

        // An interrupted run resumes from its first unfinished customer. Leads
        // imported afterwards are appended, while completed customers are not
        // replayed and do not consume another AI request.
        foreach (var lead in retryFailedOnly ? [] : leads)
        {
            if (run.AllLeadIds.Contains(lead.Id, StringComparer.OrdinalIgnoreCase)) continue;
            run.AllLeadIds.Add(lead.Id);
            run.PendingLeadIds.Add(lead.Id);
        }

        run.UpdatedAt = DateTimeOffset.Now;
        await _repository.SaveLeadBulkAnalysisRunStateAsync(run, cancellationToken);
        var total = run.AllLeadIds.Count;
        var completed = total - run.PendingLeadIds.Count;
        await _repository.LogEventAsync(
            canResume ? "lead_bulk_analysis_resumed" : "lead_bulk_analysis_started",
            null,
            null,
            $"run={run.RunId}; total={total}; completed={completed}; pending={run.PendingLeadIds.Count}; provider={providerId}; model={execution.Model}; retry_failed_only={retryFailedOnly}",
            cancellationToken);
        PublishBulkProgress(progress, new(total, completed, run.Succeeded, run.Failed, "", "", canResume ? "resuming" : "starting",
            canResume
                ? $"继续上次任务并优先重试失败客户：剩余 {run.PendingLeadIds.Count} 位"
                : retryFailedOnly
                    ? $"仅重试 {total} 位失败客户，不重复消耗已成功客户 Token"
                    : $"准备分析 {total} 位客户"));

        var consecutiveFailures = 0;
        while (run.PendingLeadIds.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leadId = run.PendingLeadIds[0];
            var lead = await _repository.GetLeadAsync(leadId, cancellationToken);
            if (lead is null)
            {
                run.Failed++;
                run.PendingLeadIds.RemoveAt(0);
                run.UpdatedAt = DateTimeOffset.Now;
                await _repository.SaveLeadBulkAnalysisRunStateAsync(run, cancellationToken);
                completed = total - run.PendingLeadIds.Count;
                PublishBulkProgress(progress, new(total, completed, run.Succeeded, run.Failed, leadId, "", "failed", "客户已不存在"));
                continue;
            }

            lead.AnalysisTrigger = "bulk";
            lead.AnalysisRequestedAt = DateTimeOffset.Now;
            completed = total - run.PendingLeadIds.Count;
            PublishBulkProgress(progress, new(total, completed, run.Succeeded, run.Failed, lead.Id, lead.DisplayName, "running", $"正在分析：{lead.DisplayName}"));
            try
            {
                await _provider.AnalyzeLeadAsync(lead, cancellationToken);
                consecutiveFailures = 0;
                run.Succeeded++;
                run.PendingLeadIds.RemoveAt(0);
                run.UpdatedAt = DateTimeOffset.Now;
                await _repository.SaveLeadBulkAnalysisRunStateAsync(run, cancellationToken);
                completed = total - run.PendingLeadIds.Count;
                PublishBulkProgress(progress, new(total, completed, run.Succeeded, run.Failed, lead.Id, lead.DisplayName, "succeeded", $"已完成：{lead.DisplayName}"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                run.UpdatedAt = DateTimeOffset.Now;
                await _repository.SaveLeadBulkAnalysisRunStateAsync(run, CancellationToken.None);
                PublishBulkProgress(progress, new(total, completed, run.Succeeded, run.Failed, lead.Id, lead.DisplayName, "cancelled", $"已在 {lead.DisplayName} 处停止，下次从此处继续"));
                throw;
            }
            catch (Exception error)
            {
                if (error is DeepSeekException { Retryable: false })
                {
                    run.UpdatedAt = DateTimeOffset.Now;
                    await _repository.SaveLeadBulkAnalysisRunStateAsync(run, cancellationToken);
                    throw;
                }
                consecutiveFailures++;
                if (consecutiveFailures >= 3)
                {
                    run.UpdatedAt = DateTimeOffset.Now;
                    await _repository.SaveLeadBulkAnalysisRunStateAsync(run, cancellationToken);
                    await _repository.LogEventAsync(
                        "lead_bulk_analysis_paused",
                        lead.Id,
                        null,
                        $"run={run.RunId}; consecutive_failures={consecutiveFailures}; pending={run.PendingLeadIds.Count}; error={error.GetType().Name}",
                        cancellationToken);
                    PublishBulkProgress(progress, new(total, completed, run.Succeeded, run.Failed, lead.Id, lead.DisplayName, "paused",
                        $"AI 连续 {consecutiveFailures} 位客户分析失败，已暂停并保留剩余队列"));
                    throw new DeepSeekException(
                        "bulk_analysis_paused",
                        $"AI 连续 {consecutiveFailures} 位客户分析失败，批量任务已自动暂停，剩余 {run.PendingLeadIds.Count} 位客户仍保留在队列中，未继续消耗 Token。请稍后重试或检查模型设置。",
                        true,
                        error);
                }
                run.Failed++;
                run.PendingLeadIds.RemoveAt(0);
                run.UpdatedAt = DateTimeOffset.Now;
                await _repository.SaveLeadBulkAnalysisRunStateAsync(run, cancellationToken);
                completed = total - run.PendingLeadIds.Count;
                PublishBulkProgress(progress, new(total, completed, run.Succeeded, run.Failed, lead.Id, lead.DisplayName, "failed", $"失败：{lead.DisplayName} · {error.Message}"));
            }
        }

        run.IsComplete = true;
        run.UpdatedAt = DateTimeOffset.Now;
        await _repository.SaveLeadBulkAnalysisRunStateAsync(run, cancellationToken);
        await _repository.LogEventAsync("lead_bulk_analysis_completed", null, null, $"run={run.RunId}; total={total}; succeeded={run.Succeeded}; failed={run.Failed}; provider={providerId}; model={execution.Model}", cancellationToken);
        PublishBulkProgress(progress, new(total, total, run.Succeeded, run.Failed, "", "", "completed", "分析完成"));
        return new LeadBulkAnalysisResult(total, run.Succeeded, run.Failed);
    }

    private void PublishBulkProgress(IProgress<LeadBulkAnalysisProgress>? progress, LeadBulkAnalysisProgress value)
    {
        lock (_bulkProgressLock) _currentBulkProgress = value;
        progress?.Report(value);
        BulkAnalysisProgressChanged?.Invoke(this, value);
    }

    private void Sync_MessageSynchronized(object? sender, WhatsAppMessageSyncedEvent e)
    {
        // Lead scoring reads messages, it does not answer them, so backlog and
        // live traffic are equally valid input here.
        var message = e.Message;
        if (message.IsGroup || message.IsStatusUpdate || message.Direction != WhatsAppMessageDirection.Incoming) return;
        _ = QueueReplySafelyAsync(message);
    }

    private async Task QueueReplySafelyAsync(WhatsAppMessage message)
    {
        try { await QueueLeadForReplyAsync(message, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(message.LeadId, AnalysisStatus.RetryableFailed, error.Message));
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var leadId in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            if (!_provider.HasApiKey()) continue;
            var lead = await _repository.GetLeadAsync(leadId, cancellationToken);
            if (lead is null || lead.AnalysisStatus != AnalysisStatus.Queued) continue;
            var requestMarker = lead.AnalysisRequestedAt;
            AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(
                lead.Id,
                AnalysisStatus.Running,
                lead.AnalysisTrigger == "opportunity_supplement_import"
                    ? "AI 正在分析新增交易证据"
                    : "AI 正在分析 WhatsApp 回复"));
            try
            {
                var analyzed = await _provider.AnalyzeLeadAsync(lead, cancellationToken);
                AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(analyzed.Id, AnalysisStatus.Succeeded, "AI 已更新商机画像与等级"));
                if (analyzed.AnalysisRequestedAt is not null && requestMarker is not null && analyzed.AnalysisRequestedAt > requestMarker)
                {
                    LeadScoringService.ResetToAiBaseline(analyzed, "分析期间收到新的 WhatsApp 回复，等待重新分析", "AI 已使用最新消息重新排队。");
                    analyzed.AnalysisStatus = AnalysisStatus.Queued;
                    analyzed.AnalysisError = "分析期间收到新回复，已再次排队。";
                    await _repository.UpsertLeadAsync(analyzed, cancellationToken);
                    _queue.Writer.TryWrite(analyzed.Id);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                AnalysisChanged?.Invoke(this, new LeadAnalysisAutomationEventArgs(lead.Id, AnalysisStatus.RetryableFailed, error.Message));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sync.MessageSynchronized -= Sync_MessageSynchronized;
        _queue.Writer.TryComplete();
        _lifetime.Cancel();
        if (_worker is not null)
        {
            try { await _worker; }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        _bulkAnalysisGate.Dispose();
    }
}
