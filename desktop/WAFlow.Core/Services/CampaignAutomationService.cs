using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record CampaignAudienceItem(Lead Lead, bool Eligible, string Reason, string PreviewMessage)
{
    public string DisplayName => Lead.DisplayName;
    public string Phone => Lead.PhoneE164;
    public string Grade => Lead.Grade;
    public string Stage => Labels.Stage(Lead.Stage);
    public string EligibilityLabel => Eligible ? "可发送" : "已排除";
}

public sealed record CampaignTemplateField(string Key, string Label, string Source)
{
    public string Token => $"{{{Key}}}";
    public string DisplayLabel => $"{Source} · {Label}  {Token}";
}

public sealed record CampaignExecutionSummary(
    WhatsAppCampaign Campaign,
    int Total,
    int Sent,
    int Failed,
    int Skipped,
    int Cancelled,
    int Queued,
    string NextPosition)
{
    public int DeliveryAcknowledged => Math.Max(0, Total - Sent - Failed - Skipped - Cancelled - Queued);
    public string Name => Campaign.Name;
    public string Channel => Campaign.ChannelLabel;
    public string AccountId => Campaign.AccountId;
    public string Status => Campaign.StatusLabel;
    public string Trigger => Campaign.ScheduleLabel;
    public string Progress => $"{Sent + DeliveryAcknowledged + Failed + Skipped + Cancelled} / {Total}";
    public string SuccessRate
    {
        get
        {
            var confirmed = Sent + DeliveryAcknowledged;
            var attempted = confirmed + Failed;
            return attempted == 0 ? "—" : $"{confirmed * 100d / attempted:0.#}%";
        }
    }
    public string StopOrNext => !string.IsNullOrWhiteSpace(Campaign.SafetyStopPosition)
        ? Campaign.SafetyStopPosition
        : NextPosition;
    public string Updated => Campaign.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string Detail => string.IsNullOrWhiteSpace(Campaign.PauseReason) ? "—" : Campaign.PauseReason;
}

public sealed class CampaignSafetyStoppedEventArgs : EventArgs
{
    public required string PreviousIp { get; init; }
    public required string CurrentIp { get; init; }
    public required IReadOnlyList<CampaignExecutionSummary> Campaigns { get; init; }
}

public sealed class CampaignAutomationService : IAsyncDisposable
{
    private readonly LocalRepository _repository;
    private readonly WhatsAppConnectionManager _bridge;
    private readonly PublicIpMonitor _publicIp;
    private readonly EmailService _email;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private Task? _safetyWorker;
    private readonly Dictionary<string, DateTimeOffset> _lastConnectAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WhatsAppBridgeEvent> _deliveryReceipts = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastDeliverySweep = DateTimeOffset.MinValue;

    public event EventHandler? CampaignChanged;
    public event EventHandler<CampaignSafetyStoppedEventArgs>? SafetyStopped;

    public CampaignAutomationService(LocalRepository repository, WhatsAppConnectionManager bridge, PublicIpMonitor publicIp, EmailService email)
    {
        _repository = repository;
        _bridge = bridge;
        _publicIp = publicIp;
        _email = email;
        _bridge.EventReceived += Bridge_EventReceived;
    }

    public async Task<List<CampaignAudienceItem>> ListAudienceAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        ValidateDraft(campaign);
        var leads = await _repository.GetLeadsAsync(cancellationToken: cancellationToken);
        return leads.Where(x => MatchesFilter(campaign, x))
            .Select(x => CreateAudienceItem(campaign, x))
            .OrderByDescending(x => x.Eligible).ThenByDescending(x => x.Lead.Score).ThenBy(x => x.DisplayName)
            .ToList();
    }

    public async Task<List<CampaignAudienceItem>> PreviewAudienceAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        var selected = campaign.SelectedLeadIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) return [];
        return (await ListAudienceAsync(campaign, cancellationToken)).Where(item => selected.Contains(item.Lead.Id)).ToList();
    }

    public async Task<List<CampaignTemplateField>> GetTemplateFieldsAsync(CancellationToken cancellationToken = default)
    {
        var fields = CoreTemplateFields().ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in (await _repository.GetLeadsAsync(cancellationToken: cancellationToken))
                     .SelectMany(lead => lead.CustomFields.Keys)
                     .Where(key => !string.IsNullOrWhiteSpace(key)))
            fields.TryAdd(key.Trim(), new CampaignTemplateField(key.Trim(), key.Trim(), "导入表格 / 客户列表"));
        return fields.Values.OrderBy(field => field.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<List<CampaignExecutionSummary>> GetExecutionHistoryAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CampaignExecutionSummary>();
        foreach (var campaign in (await _repository.GetCampaignsAsync(null, cancellationToken)).Where(item => item.ApprovedAt is not null || item.Status != CampaignStatus.Draft))
            result.Add(await BuildExecutionSummaryAsync(campaign, cancellationToken));
        return result.OrderByDescending(item => item.Campaign.UpdatedAt).ToList();
    }

    public async Task<CampaignMessageTemplate> SaveMessageTemplateAsync(CampaignMessageTemplate template, CancellationToken cancellationToken = default)
    {
        template.Name = template.Name.Trim(); template.Body = template.Body.Trim();
        if (string.IsNullOrWhiteSpace(template.Name)) throw new InvalidOperationException("请填写话术模板名称。");
        if (string.IsNullOrWhiteSpace(template.Body)) throw new InvalidOperationException("请填写话术模板内容。");
        if (template.Body.Length > 4096) throw new InvalidOperationException("WhatsApp 话术不能超过 4096 字符。");
        await ValidateTemplateFieldsAsync(template.Body, cancellationToken);
        await _repository.SaveCampaignMessageTemplateAsync(template, cancellationToken);
        await _repository.LogEventAsync("campaign_template_saved", null, null, $"template_id={template.Id};name={template.Name}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
        return template;
    }

    public async Task DeleteMessageTemplateAsync(CampaignMessageTemplate template, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteCampaignMessageTemplateAsync(template.Id, cancellationToken);
        await _repository.LogEventAsync("campaign_template_deleted", null, null, $"template_id={template.Id};name={template.Name}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    private static CampaignAudienceItem CreateAudienceItem(WhatsAppCampaign campaign, Lead lead)
    {
        var eligible = IsEligible(campaign, lead, out var reason);
        return new CampaignAudienceItem(lead, eligible, reason, RenderTemplate(campaign.MessageTemplate, lead));
    }

    public async Task SaveDraftAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        ValidateDraft(campaign);
        await ValidateTemplateFieldsAsync(campaign.MessageTemplate, cancellationToken);
        if (campaign.Channel == CampaignChannel.Email)
            await ValidateTemplateFieldsAsync(campaign.EmailSubjectTemplate, cancellationToken);
        var existing = await _repository.GetCampaignAsync(campaign.Id, cancellationToken);
        if (existing is not null && existing.Status != CampaignStatus.Draft)
            throw new InvalidOperationException("已排期的 Campaign 不能直接修改；请暂停或取消后新建。");
        campaign.SelectedLeadIds = campaign.SelectedLeadIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        campaign.Status = CampaignStatus.Draft;
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        await _repository.LogEventAsync("campaign_draft_saved", null, null, $"campaign_id={campaign.Id};name={campaign.Name}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<int> ApproveAndScheduleAsync(WhatsAppCampaign campaign, string actor = "当前用户", CancellationToken cancellationToken = default)
    {
        ValidateDraft(campaign);
        await ValidateTemplateFieldsAsync(campaign.MessageTemplate, cancellationToken);
        if (campaign.Channel == CampaignChannel.Email)
            await ValidateTemplateFieldsAsync(campaign.EmailSubjectTemplate, cancellationToken);
        if (campaign.SelectedLeadIds.Count == 0) throw new InvalidOperationException("请至少勾选 1 位客户后再建立发送任务。");
        var audience = await PreviewAudienceAsync(campaign, cancellationToken);
        var eligible = audience.Where(x => x.Eligible).ToList();
        if (eligible.Count == 0) throw new InvalidOperationException(campaign.Channel == CampaignChannel.Email
            ? "当前筛选没有可发送客户。请检查邮箱是否有效，以及客户是否已经退订。"
            : "当前筛选没有可发送客户。请检查号码是否有效，以及客户是否已经退订。");

        string baselineIp = "";
        if (campaign.Channel == CampaignChannel.WhatsApp)
        {
            var ip = await _publicIp.CheckAsync(campaign.AccountId, true, cancellationToken);
            if (!string.IsNullOrWhiteSpace(ip.Error) || string.IsNullOrWhiteSpace(ip.State.CurrentIp))
                throw new InvalidOperationException("无法取得当前公网 IP，安全阀门未能建立基线，因此没有创建发送任务。请检查网络后重试。");
            baselineIp = ip.State.CurrentIp;
        }
        else
        {
            var emailAccount = await _repository.GetEmailAccountAsync(campaign.AccountId, cancellationToken);
            if (emailAccount is null || emailAccount.Status != EmailConnectionStatus.Connected)
                throw new InvalidOperationException("邮件账号尚未连接，请先在邮件箱中完成 IMAP / SMTP 连接测试。");
        }

        var now = DateTimeOffset.Now;
        var firstSendAt = campaign.ScheduleMode == CampaignScheduleMode.Immediate
            ? now.AddSeconds(2)
            : campaign.StartsAt > now ? campaign.StartsAt : now.AddSeconds(2);
        var interval = campaign.IntervalDelay;
        var recipients = eligible.Select((item, index) => new CampaignRecipient
        {
            Id = $"{campaign.Id}:{item.Lead.Id}", CampaignId = campaign.Id, LeadId = item.Lead.Id,
            AccountId = campaign.AccountId, Phone = item.Lead.PhoneE164, Email = item.Lead.Email, DisplayName = item.DisplayName,
            RenderedSubject = RenderTemplate(campaign.EmailSubjectTemplate, item.Lead), RenderedMessage = item.PreviewMessage, Status = CampaignRecipientStatus.Queued,
            ScheduledAt = firstSendAt.AddTicks(interval.Ticks * index),
            NextAttemptAt = firstSendAt.AddTicks(interval.Ticks * index)
        }).ToList();

        campaign.StartsAt = firstSendAt;
        campaign.Status = CampaignStatus.Scheduled;
        campaign.ApprovedAt = DateTimeOffset.Now;
        campaign.ApprovedBy = actor;
        campaign.PauseReason = "";
        campaign.BaselinePublicIp = baselineIp;
        campaign.SafetyStopFromIp = "";
        campaign.SafetyStopToIp = "";
        campaign.SafetyStopPosition = "";
        campaign.SafetyStoppedAt = null;
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        await _repository.ReplaceCampaignRecipientsAsync(campaign.Id, recipients, cancellationToken);
        await _repository.LogEventAsync("campaign_approved", null, null, $"campaign_id={campaign.Id};channel={campaign.Channel};mode={campaign.ScheduleMode};recipients={recipients.Count};interval={campaign.EffectiveIntervalValue};unit={campaign.IntervalUnit};daily_limit={campaign.DailyLimit}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
        return recipients.Count;
    }

    public async Task PauseAsync(WhatsAppCampaign campaign, string reason = "用户手动暂停", CancellationToken cancellationToken = default)
    {
        if (campaign.Status is CampaignStatus.Completed or CampaignStatus.Cancelled) return;
        campaign.Status = CampaignStatus.Paused; campaign.PauseReason = reason;
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        await _repository.LogEventAsync("campaign_paused", null, null, $"campaign_id={campaign.Id};reason={reason}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResumeAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        if (campaign.Status is not (CampaignStatus.Paused or CampaignStatus.SafetyStopped)) throw new InvalidOperationException("只有已暂停或被安全阀门停止的 Campaign 可以继续。");
        if (campaign.Channel == CampaignChannel.WhatsApp)
        {
            var ip = await _publicIp.CheckAsync(campaign.AccountId, true, cancellationToken);
            if (!string.IsNullOrWhiteSpace(ip.Error) || string.IsNullOrWhiteSpace(ip.State.CurrentIp))
                throw new InvalidOperationException("无法验证当前公网 IP，任务仍保持停止。请检查网络后重试。");
            campaign.BaselinePublicIp = ip.State.CurrentIp;
        }
        campaign.Status = CampaignStatus.Scheduled; campaign.PauseReason = "";
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        await _repository.LogEventAsync("campaign_resumed", null, null, $"campaign_id={campaign.Id}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CancelAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken = default)
    {
        if (campaign.Status == CampaignStatus.Completed) return;
        campaign.Status = CampaignStatus.Cancelled; campaign.PauseReason = "用户已取消";
        await _repository.SaveCampaignAsync(campaign, cancellationToken);
        foreach (var recipient in await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken))
        {
            if (recipient.Status is CampaignRecipientStatus.Queued or CampaignRecipientStatus.Sending)
            {
                recipient.Status = CampaignRecipientStatus.Cancelled;
                await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
            }
        }
        await _repository.LogEventAsync("campaign_cancelled", null, null, $"campaign_id={campaign.Id}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task PauseAccountAsync(string accountId, string reason, CancellationToken cancellationToken = default)
    {
        await _repository.PauseActiveCampaignsAsync(accountId, reason, cancellationToken);
        await _repository.LogEventAsync("campaign_account_paused", null, null, $"account_id={accountId};reason={reason}", cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_worker is not null) return;
        await ReconcileStoredDeliveryStatusesAsync(cancellationToken);
        await _repository.RecoverInterruptedCampaignRecipientsAsync(cancellationToken);
        await CompleteRecoveredCampaignsAsync(cancellationToken);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
        _safetyWorker = Task.Run(() => RunSafetyMonitorAsync(_lifetime.Token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) await ProcessNextAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSafetyMonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) await CheckSafetyValveAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    public async Task<bool> CheckSafetyValveAsync(CancellationToken cancellationToken = default)
    {
        var active = await _repository.GetActiveCampaignsAsync(cancellationToken);
        foreach (var accountGroup in active.Where(item => item.Channel == CampaignChannel.WhatsApp).GroupBy(item => item.AccountId, StringComparer.OrdinalIgnoreCase))
        {
            var result = await _publicIp.CheckAsync(accountGroup.Key, true, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Error) || string.IsNullOrWhiteSpace(result.State.CurrentIp)) continue;
            foreach (var campaign in accountGroup)
            {
                if (string.IsNullOrWhiteSpace(campaign.BaselinePublicIp))
                {
                    campaign.BaselinePublicIp = result.State.CurrentIp;
                    await _repository.SaveCampaignAsync(campaign, cancellationToken);
                    continue;
                }
                if (!campaign.BaselinePublicIp.Equals(result.State.CurrentIp, StringComparison.OrdinalIgnoreCase))
                {
                    await StopAllForIpChangeAsync(campaign.BaselinePublicIp, result.State.CurrentIp, false, cancellationToken);
                    return false;
                }
            }
        }
        return true;
    }

    private async Task ProcessNextAsync(CancellationToken cancellationToken)
    {
        if (!await _sendLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            await SweepStaleDeliveryConfirmationsAsync(cancellationToken);
            var recipient = await _repository.GetNextDueCampaignRecipientAsync(DateTimeOffset.Now, cancellationToken);
            if (recipient is null) return;
            var campaign = await _repository.GetCampaignAsync(recipient.CampaignId, cancellationToken);
            if (campaign is null || campaign.Status is not (CampaignStatus.Scheduled or CampaignStatus.Running)) return;

            var lead = await _repository.GetLeadAsync(recipient.LeadId, cancellationToken);
            var reason = "客户记录不存在";
            var eligible = lead is not null && IsEligible(campaign, lead, out reason);
            if (!eligible)
            {
                recipient.Status = CampaignRecipientStatus.Skipped; recipient.SkipReason = reason;
                await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
                await CompleteCampaignIfFinishedAsync(campaign, cancellationToken);
                CampaignChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            var eligibleLead = lead!;

            if (campaign.Channel == CampaignChannel.WhatsApp && !await EnsureCampaignIpSafeAsync(campaign, cancellationToken)) return;

            var sentToday = await CountCampaignMessagesConsumingLimitAsync(
                campaign.AccountId,
                BeijingDayStart(DateTimeOffset.Now),
                cancellationToken);
            if (sentToday >= campaign.DailyLimit)
            {
                recipient.NextAttemptAt = NextBeijingDay(DateTimeOffset.Now, campaign.StartsAt);
                recipient.LastError = $"已达到当日上限 {campaign.DailyLimit}，自动顺延至次日。";
                await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
                CampaignChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (campaign.Channel == CampaignChannel.WhatsApp && !await EnsureConnectedAsync(campaign.AccountId, cancellationToken)) return;
            if (campaign.Status == CampaignStatus.Scheduled)
            {
                campaign.Status = CampaignStatus.Running; campaign.PauseReason = "";
                await _repository.SaveCampaignAsync(campaign, cancellationToken);
            }

            PrepareRecipientForNetworkSend(campaign, recipient);
            await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
            try
            {
                if (campaign.Channel == CampaignChannel.Email)
                {
                    var sent = await _email.SendAsync(campaign.AccountId, recipient.Email, recipient.RenderedSubject, recipient.RenderedMessage, eligibleLead.Id, cancellationToken: cancellationToken);
                    await ApplyEmailSendResultAsync(campaign, recipient, eligibleLead, sent, cancellationToken);
                }
                else
                {
                    await ConfirmNumberRegistrationAsync(campaign, recipient, eligibleLead, cancellationToken);
                    // Keyed on the recipient alone. The 45 second RPC timeout can
                    // fire on a message WhatsApp already accepted, and this
                    // scheduler retries two minutes later — inside the bridge's ten
                    // minute replay window, so that retry returns the original
                    // result instead of touching the customer twice.
                    var result = await _bridge.SendTextAsync(
                        campaign.AccountId,
                        recipient.Phone,
                        recipient.RenderedMessage,
                        OutboundSendOptions.ForCampaign(campaign.Id, recipient.Id),
                        cancellationToken);
                    recipient.ProviderMessageId = result.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(recipient.ProviderMessageId))
                        throw new WhatsAppBridgeException("message_id_missing", "WhatsApp 未返回消息编号，发送状态无法确认。为避免重复触达，系统不会自动重发。");
                    if (!result.TryGetProperty("targetVerified", out var targetVerified) ||
                        targetVerified.ValueKind != JsonValueKind.True)
                        throw new WhatsAppBridgeException("target_not_verified", "WhatsApp 未确认目标联系人，消息未发送。");
                    var numericStatus = result.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var parsedStatus) ? parsedStatus : 1;
                    if (numericStatus <= 0)
                        throw new WhatsAppBridgeException("message_send_failed", "WhatsApp 返回发送错误，消息未发送。");
                    if (numericStatus >= 2)
                    {
                        recipient.Status = CampaignRecipientStatus.Sent;
                        recipient.SentAt = DateTimeOffset.Now;
                        recipient.LastError = "";
                        await ApplyConfirmedSendToLeadAsync(campaign, recipient, eligibleLead, cancellationToken);
                    }
                    else
                    {
                        recipient.Status = CampaignRecipientStatus.Sending;
                        recipient.SentAt = null;
                        recipient.LastError = "等待 WhatsApp 服务器发送确认；未确认前不计入成功。";
                    }
                }
            }
            catch (EmailDeliveryAcknowledgedException error) when (campaign.Channel == CampaignChannel.Email)
            {
                await ApplyEmailDeliveryAcknowledgedExceptionAsync(campaign, recipient, error, cancellationToken);
            }
            catch (Exception error)
            {
                recipient.LastError = Safe(error.Message);
                if (campaign.Channel == CampaignChannel.Email || error is WhatsAppBridgeException { Code: "message_send_failed" or "message_id_missing" or "target_not_verified" or "number_not_registered" })
                {
                    recipient.Status = CampaignRecipientStatus.Failed;
                    recipient.SentAt = null;
                    await _repository.LogEventAsync("campaign_message_failed", eligibleLead.Id, null, $"campaign_id={campaign.Id};channel={campaign.Channel};recipient_id={recipient.Id};error={recipient.LastError}", cancellationToken);
                }
                else if (error is WhatsAppBridgeException { IsOutboundBlocked: true } blocked)
                {
                    // Nothing was sent, so this is a scheduling problem, not a
                    // delivery failure — it must not burn an attempt or mark the
                    // recipient failed. The governor already told us how long to
                    // wait; a hard cap means "not today".
                    recipient.AttemptCount = Math.Max(0, recipient.AttemptCount - 1);
                    recipient.Status = CampaignRecipientStatus.Queued;
                    if (OutboundBlockCodes.IsHardStop(blocked.Code))
                    {
                        // The governor's daily counters roll at local midnight, so
                        // anything short of "tomorrow" just re-blocks; this matches
                        // the campaign's own daily-limit deferral above.
                        recipient.NextAttemptAt = NextBeijingDay(DateTimeOffset.Now, campaign.StartsAt);
                        await PauseAsync(campaign, blocked.Message, cancellationToken);
                    }
                    else
                    {
                        recipient.NextAttemptAt = DateTimeOffset.Now.Add(
                            blocked.RetryAfter is { } wait && wait > TimeSpan.Zero
                                ? wait + TimeSpan.FromSeconds(5)
                                : TimeSpan.FromMinutes(2));
                    }
                }
                else if (campaign.Channel == CampaignChannel.WhatsApp && !_bridge.IsConnectedFor(campaign.AccountId))
                {
                    recipient.Status = CampaignRecipientStatus.Queued;
                    recipient.NextAttemptAt = DateTimeOffset.Now.AddMinutes(5);
                    await PauseAsync(campaign, "WhatsApp 已断开；重新连接后请继续 Campaign。", cancellationToken);
                }
                else if (recipient.AttemptCount >= 3) recipient.Status = CampaignRecipientStatus.Failed;
                else
                {
                    recipient.Status = CampaignRecipientStatus.Queued;
                    recipient.NextAttemptAt = DateTimeOffset.Now.AddMinutes(recipient.AttemptCount == 1 ? 2 : 10);
                }
            }
            var finalizationToken = recipient.Status == CampaignRecipientStatus.DeliveryAcknowledged
                ? CancellationToken.None
                : cancellationToken;
            await _repository.SaveCampaignRecipientAsync(recipient, finalizationToken);
            if (campaign.Channel == CampaignChannel.WhatsApp && recipient.Status == CampaignRecipientStatus.Sending)
                await ReconcileRecipientAfterSendAsync(campaign.AccountId, recipient.ProviderMessageId, cancellationToken);
            await CompleteCampaignIfFinishedAsync(campaign, finalizationToken);
            CampaignChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _sendLock.Release(); }
    }

    private static void PrepareRecipientForNetworkSend(
        WhatsAppCampaign campaign,
        CampaignRecipient recipient)
    {
        recipient.Status = CampaignRecipientStatus.Sending;
        recipient.AttemptCount++;
        if (campaign.Channel != CampaignChannel.Email) return;

        recipient.CustomerAttributionIsolated = true;
        recipient.CustomerAttributionNote =
            "邮件正在发送，客户归属将在 SMTP 结果与当前会话身份复核后确认；确认前不会作为原客户的 AI 分析来源。";
    }

    private async Task ApplyEmailSendResultAsync(
        WhatsAppCampaign campaign,
        CampaignRecipient recipient,
        Lead eligibleLead,
        EmailMessage sent,
        CancellationToken cancellationToken)
    {
        recipient.ProviderMessageId = sent.ProviderMessageId;
        recipient.SentAt = sent.Timestamp;
        if (sent.ContextChangedAfterSend)
        {
            MarkEmailDeliveryAcknowledged(
                recipient,
                "SMTP 已确认接收，但发送期间客户归属发生变化；本次发送已与原客户隔离，请勿重复发送。",
                sent.ContextChangeReason);
            await TryLogEventAsync(
                "campaign_email_delivery_acknowledged_unattributed",
                null,
                $"campaign_id={campaign.Id};recipient_id={recipient.Id};message_id={recipient.ProviderMessageId};reason={Safe(recipient.CustomerAttributionNote)}",
                cancellationToken);
            return;
        }

        recipient.Status = CampaignRecipientStatus.Sent;
        recipient.LastError = "";
        recipient.CustomerAttributionIsolated = false;
        recipient.CustomerAttributionNote = "";
        await TryLogEventAsync(
            "campaign_message_sent",
            eligibleLead.Id,
            $"campaign_id={campaign.Id};channel=Email;recipient_id={recipient.Id};message_id={recipient.ProviderMessageId}",
            cancellationToken);
    }

    private async Task ApplyEmailDeliveryAcknowledgedExceptionAsync(
        WhatsAppCampaign campaign,
        CampaignRecipient recipient,
        EmailDeliveryAcknowledgedException error,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error.ProviderMessageId))
            recipient.ProviderMessageId = error.ProviderMessageId.Trim();
        recipient.SentAt = DateTimeOffset.Now;
        MarkEmailDeliveryAcknowledged(
            recipient,
            "SMTP 已确认接收，但本地发送记录尚未保存完成；已作为不可重试终态计入发送限额，请勿重复发送。",
            error.InnerException?.Message);
        await TryLogEventAsync(
            "campaign_email_delivery_acknowledged_persistence_pending",
            null,
            $"campaign_id={campaign.Id};recipient_id={recipient.Id};message_id={recipient.ProviderMessageId};reason={Safe(recipient.CustomerAttributionNote)}",
            cancellationToken);
    }

    private static void MarkEmailDeliveryAcknowledged(
        CampaignRecipient recipient,
        string message,
        string? detail)
    {
        recipient.Status = CampaignRecipientStatus.DeliveryAcknowledged;
        recipient.CustomerAttributionIsolated = true;
        recipient.CustomerAttributionNote = string.IsNullOrWhiteSpace(detail)
            ? message
            : $"{message} 原因：{Safe(detail)}";
        recipient.LastError = recipient.CustomerAttributionNote;
    }

    private async Task TryLogEventAsync(
        string eventType,
        string? leadId,
        string detail,
        CancellationToken cancellationToken)
    {
        try { await _repository.LogEventAsync(eventType, leadId, null, detail, cancellationToken); }
        catch { /* An audit write failure must never downgrade an SMTP-acknowledged delivery into a retryable send. */ }
    }

    private async Task<int> CountCampaignMessagesConsumingLimitAsync(
        string accountId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var count = await _repository.CountCampaignMessagesSentAsync(accountId, since, cancellationToken);
        foreach (var campaign in await _repository.GetCampaignsAsync(accountId, cancellationToken))
        {
            count += (await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken))
                .Count(recipient => recipient.Status == CampaignRecipientStatus.DeliveryAcknowledged
                    && recipient.SentAt is not null
                    && recipient.SentAt >= since);
        }
        return count;
    }

    private async Task ReconcileRecipientAfterSendAsync(string accountId, string providerMessageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId)) return;
        var key = DeliveryKey(accountId, providerMessageId);
        if (_deliveryReceipts.TryRemove(key, out var pendingReceipt))
        {
            await HandleDeliveryReceiptAsync(pendingReceipt, cancellationToken);
            return;
        }

        var storedMessage = await _repository.GetWhatsAppMessageByProviderIdAsync(accountId, providerMessageId, cancellationToken);
        if (storedMessage is null || storedMessage.Status == WhatsAppMessageStatus.Pending) return;
        await ApplyStoredMessageStatusAsync(storedMessage, cancellationToken);
    }

    private async Task ConfirmNumberRegistrationAsync(WhatsAppCampaign campaign, CampaignRecipient recipient, Lead lead, CancellationToken cancellationToken)
    {
        try
        {
            var registration = await _bridge.ValidateNumberAsync(campaign.AccountId, recipient.Phone, cancellationToken);
            var confirmed = registration.TryGetProperty("exists", out var existsElement) && existsElement.ValueKind == System.Text.Json.JsonValueKind.True;
            lead.WhatsAppRegistrationStatus = confirmed
                ? WhatsAppRegistrationStatus.Registered
                : WhatsAppRegistrationStatus.NotRegistered;
            lead.WhatsAppRegistrationPhone = recipient.Phone;
            lead.WhatsAppRegistrationCheckedAt = DateTimeOffset.Now;
            lead.WhatsAppRegistrationLastAttemptAt = DateTimeOffset.Now;
            lead.WhatsAppRegistrationNextRetryAt = null;
            lead.WhatsAppRegistrationError = "";
            await _repository.UpsertLeadAsync(lead, cancellationToken);
            if (!confirmed)
            {
                await _repository.LogEventAsync("campaign_whatsapp_registration_unconfirmed", lead.Id, null,
                    $"campaign_id={campaign.Id};recipient_id={recipient.Id};send_blocked=true", cancellationToken);
                throw new WhatsAppBridgeException("number_not_registered", "WhatsApp 明确返回该号码未注册，消息未发送。");
            }
        }
        catch (WhatsAppBridgeException error) when (error.Code == "number_not_registered")
        {
            throw;
        }
        catch (Exception error)
        {
            lead.WhatsAppRegistrationStatus = WhatsAppRegistrationStatus.RetryableFailed;
            lead.WhatsAppRegistrationPhone = "";
            lead.WhatsAppRegistrationCheckedAt = null;
            lead.WhatsAppRegistrationError = error is WhatsAppBridgeException bridge ? bridge.Code : "whatsapp_check_unavailable";
            lead.WhatsAppRegistrationNextRetryAt = DateTimeOffset.Now.AddMinutes(2);
            await _repository.UpsertLeadAsync(lead, cancellationToken);
            await _repository.LogEventAsync("campaign_whatsapp_registration_check_unavailable", lead.Id, null,
                $"campaign_id={campaign.Id};recipient_id={recipient.Id};diagnostic_only=true;send_continued=true;error={Safe(error.Message)}", cancellationToken);
        }
    }

    private async Task HandleDeliveryReceiptAsync(WhatsAppBridgeEvent e, CancellationToken cancellationToken)
    {
        if (e.Name != "message_status") return;
        var providerMessageId = e.Data.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(providerMessageId)) return;
        var accountId = string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId;
        if (!e.Data.TryGetProperty("status", out var statusElement) || !statusElement.TryGetInt32(out var numericStatus)) return;
        if (numericStatus == 1) return;

        var recipient = await _repository.GetCampaignRecipientByProviderMessageIdAsync(accountId, providerMessageId, cancellationToken);
        if (recipient is null)
        {
            _deliveryReceipts[DeliveryKey(accountId, providerMessageId)] = e;
            return;
        }

        var campaign = await _repository.GetCampaignAsync(recipient.CampaignId, cancellationToken);
        if (campaign is null || campaign.Channel != CampaignChannel.WhatsApp) return;
        if (numericStatus <= 0)
        {
            var failureReason = e.Data.TryGetProperty("failureReason", out var failureElement) ? failureElement.GetString() ?? "" : "";
            await MarkDeliveryFailedAsync(campaign, recipient,
                string.IsNullOrWhiteSpace(failureReason) ? "WhatsApp 返回发送错误，消息未发送。" : failureReason,
                cancellationToken);
        }
        else if (numericStatus >= 2)
        {
            await MarkDeliverySentAsync(campaign, recipient, cancellationToken);
        }
    }

    private async Task ApplyStoredMessageStatusAsync(WhatsAppMessage message, CancellationToken cancellationToken)
    {
        var recipient = await _repository.GetCampaignRecipientByProviderMessageIdAsync(message.AccountId, message.ProviderMessageId, cancellationToken);
        if (recipient is null) return;
        var campaign = await _repository.GetCampaignAsync(recipient.CampaignId, cancellationToken);
        if (campaign is null || campaign.Channel != CampaignChannel.WhatsApp) return;
        if (message.Status == WhatsAppMessageStatus.Failed)
            await MarkDeliveryFailedAsync(campaign, recipient,
                string.IsNullOrWhiteSpace(message.FailureReason) ? "WhatsApp 返回发送错误，消息未发送。" : message.FailureReason,
                cancellationToken);
        else if (message.Status is WhatsAppMessageStatus.Sent or WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read)
            await MarkDeliverySentAsync(campaign, recipient, cancellationToken);
    }

    private async Task MarkDeliverySentAsync(WhatsAppCampaign campaign, CampaignRecipient recipient, CancellationToken cancellationToken)
    {
        var changed = recipient.Status != CampaignRecipientStatus.Sent;
        recipient.Status = CampaignRecipientStatus.Sent;
        recipient.SentAt ??= DateTimeOffset.Now;
        recipient.LastError = "";
        var lead = await _repository.GetLeadAsync(recipient.LeadId, cancellationToken);
        if (changed && lead is not null) await ApplyConfirmedSendToLeadAsync(campaign, recipient, lead, cancellationToken);
        await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
        await CompleteCampaignIfFinishedAsync(campaign, cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplyConfirmedSendToLeadAsync(WhatsAppCampaign campaign, CampaignRecipient recipient, Lead lead, CancellationToken cancellationToken)
    {
        lead.LastContactAt = recipient.SentAt ?? DateTimeOffset.Now;
        lead.LatestMessage = recipient.RenderedMessage;
        if (lead.Stage == LeadStage.New) lead.Stage = LeadStage.Contacted;
        await _repository.UpsertLeadAsync(lead, cancellationToken);
        await _repository.LogEventAsync("campaign_message_sent", lead.Id, null, $"campaign_id={campaign.Id};channel={campaign.Channel};recipient_id={recipient.Id};message_id={recipient.ProviderMessageId}", cancellationToken);
    }

    private async Task MarkDeliveryFailedAsync(WhatsAppCampaign campaign, CampaignRecipient recipient, string error, CancellationToken cancellationToken)
    {
        var changed = recipient.Status != CampaignRecipientStatus.Failed || !string.Equals(recipient.LastError, error, StringComparison.Ordinal);
        recipient.Status = CampaignRecipientStatus.Failed;
        recipient.SentAt = null;
        recipient.LastError = Safe(error);
        await _repository.SaveCampaignRecipientAsync(recipient, cancellationToken);
        if (changed)
            await _repository.LogEventAsync("campaign_message_failed", recipient.LeadId, null, $"campaign_id={campaign.Id};recipient_id={recipient.Id};message_id={recipient.ProviderMessageId};error={recipient.LastError}", cancellationToken);
        await CompleteCampaignIfFinishedAsync(campaign, cancellationToken);
        CampaignChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReconcileStoredDeliveryStatusesAsync(CancellationToken cancellationToken)
    {
        foreach (var campaign in await _repository.GetCampaignsAsync(null, cancellationToken))
        {
            foreach (var recipient in await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(recipient.ProviderMessageId) || recipient.Status is not (CampaignRecipientStatus.Sending or CampaignRecipientStatus.Sent)) continue;
                var message = await _repository.GetWhatsAppMessageByProviderIdAsync(recipient.AccountId, recipient.ProviderMessageId, cancellationToken);
                if (message is not null) await ApplyStoredMessageStatusAsync(message, cancellationToken);
            }
        }
    }

    private async Task SweepStaleDeliveryConfirmationsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (now - _lastDeliverySweep < TimeSpan.FromSeconds(15)) return;
        _lastDeliverySweep = now;
        foreach (var recipient in await _repository.GetCampaignRecipientsAwaitingConfirmationAsync(now.AddSeconds(-90), cancellationToken))
        {
            var campaign = await _repository.GetCampaignAsync(recipient.CampaignId, cancellationToken);
            if (campaign is null) continue;
            var message = await _repository.GetWhatsAppMessageByProviderIdAsync(recipient.AccountId, recipient.ProviderMessageId, cancellationToken);
            if (message is not null && message.Status != WhatsAppMessageStatus.Pending)
                await ApplyStoredMessageStatusAsync(message, cancellationToken);
            else
                await MarkDeliveryFailedAsync(campaign, recipient, "90 秒内未收到 WhatsApp 服务器发送确认；未计为成功，且为避免重复触达不会自动重发。", cancellationToken);
        }
    }

    private async Task CompleteRecoveredCampaignsAsync(CancellationToken cancellationToken)
    {
        foreach (var campaign in await _repository.GetCampaignsAsync(null, cancellationToken))
            await CompleteCampaignIfFinishedAsync(campaign, cancellationToken);
    }

    private static string DeliveryKey(string accountId, string providerMessageId) => $"{(string.IsNullOrWhiteSpace(accountId) ? "primary" : accountId)}:{providerMessageId}";

    private async Task<bool> EnsureConnectedAsync(string accountId, CancellationToken cancellationToken)
    {
        if (_bridge.IsConnectedFor(accountId)) return true;
        var lastAttempt = _lastConnectAttempts.GetValueOrDefault(accountId);
        if (DateTimeOffset.Now - lastAttempt < TimeSpan.FromSeconds(15)) return false;
        _lastConnectAttempts[accountId] = DateTimeOffset.Now;
        try
        {
            await _bridge.ConnectAsync(accountId, cancellationToken);
        }
        catch { return false; }
        return _bridge.IsConnectedFor(accountId);
    }

    private async Task<bool> EnsureCampaignIpSafeAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken)
    {
        var result = await _publicIp.CheckAsync(campaign.AccountId, true, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error) || string.IsNullOrWhiteSpace(result.State.CurrentIp)) return true;
        if (string.IsNullOrWhiteSpace(campaign.BaselinePublicIp))
        {
            campaign.BaselinePublicIp = result.State.CurrentIp;
            await _repository.SaveCampaignAsync(campaign, cancellationToken);
            return true;
        }
        if (campaign.BaselinePublicIp.Equals(result.State.CurrentIp, StringComparison.OrdinalIgnoreCase)) return true;
        await StopAllForIpChangeAsync(campaign.BaselinePublicIp, result.State.CurrentIp, true, cancellationToken);
        return false;
    }

    private async Task StopAllForIpChangeAsync(string previousIp, string currentIp, bool sendLockAlreadyHeld, CancellationToken cancellationToken)
    {
        if (!sendLockAlreadyHeld) await _sendLock.WaitAsync(cancellationToken);
        List<CampaignExecutionSummary> summaries;
        try
        {
            var active = await _repository.GetActiveCampaignsAsync(cancellationToken);
            if (active.Count == 0) return;
            foreach (var campaign in active)
            {
                var recipients = await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken);
                var terminal = recipients.Count(item => IsTerminal(item.Status));
                var next = recipients.FirstOrDefault(item => item.Status is CampaignRecipientStatus.Queued or CampaignRecipientStatus.Sending);
                campaign.Status = CampaignStatus.SafetyStopped;
                campaign.PauseReason = $"公网 IP 从 {previousIp} 变化为 {currentIp}，安全阀门已停止所有自动触达。";
                campaign.SafetyStopFromIp = previousIp;
                campaign.SafetyStopToIp = currentIp;
                campaign.SafetyStoppedAt = DateTimeOffset.Now;
                campaign.SafetyStopPosition = next is null
                    ? $"已处理 {terminal}/{recipients.Count}"
                    : $"第 {terminal + 1}/{recipients.Count} 位前停止 · {next.DisplayName}";
                await _repository.SaveCampaignAsync(campaign, cancellationToken);
            }
            summaries = [];
            foreach (var campaign in active) summaries.Add(await BuildExecutionSummaryAsync(campaign, cancellationToken));
            await _repository.LogEventAsync("campaign_ip_safety_stop", null, null, $"from={previousIp};to={currentIp};campaigns={summaries.Count};sent={summaries.Sum(item => item.Sent)};failed={summaries.Sum(item => item.Failed)}", cancellationToken);
        }
        finally { if (!sendLockAlreadyHeld) _sendLock.Release(); }

        CampaignChanged?.Invoke(this, EventArgs.Empty);
        SafetyStopped?.Invoke(this, new CampaignSafetyStoppedEventArgs { PreviousIp = previousIp, CurrentIp = currentIp, Campaigns = summaries });
    }

    private async Task<CampaignExecutionSummary> BuildExecutionSummaryAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken)
    {
        var recipients = await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken);
        var next = recipients.FirstOrDefault(item => item.Status is CampaignRecipientStatus.Queued or CampaignRecipientStatus.Sending);
        var terminal = recipients.Count(item => IsTerminal(item.Status));
        var nextPosition = next is null ? "—" : $"第 {terminal + 1}/{recipients.Count} 位 · {next.DisplayName}";
        return new CampaignExecutionSummary(
            campaign,
            recipients.Count,
            recipients.Count(item => item.Status == CampaignRecipientStatus.Sent),
            recipients.Count(item => item.Status == CampaignRecipientStatus.Failed),
            recipients.Count(item => item.Status == CampaignRecipientStatus.Skipped),
            recipients.Count(item => item.Status == CampaignRecipientStatus.Cancelled),
            recipients.Count(item => item.Status is CampaignRecipientStatus.Queued or CampaignRecipientStatus.Sending),
            nextPosition);
    }

    private static bool IsTerminal(CampaignRecipientStatus status) => status is CampaignRecipientStatus.Sent or CampaignRecipientStatus.DeliveryAcknowledged or CampaignRecipientStatus.Failed or CampaignRecipientStatus.Skipped or CampaignRecipientStatus.Cancelled;

    private async Task CompleteCampaignIfFinishedAsync(WhatsAppCampaign campaign, CancellationToken cancellationToken)
    {
        var recipients = await _repository.GetCampaignRecipientsAsync(campaign.Id, cancellationToken);
        if (recipients.Count > 0 && recipients.All(x => x.Status is CampaignRecipientStatus.Sent or CampaignRecipientStatus.DeliveryAcknowledged or CampaignRecipientStatus.Skipped or CampaignRecipientStatus.Failed or CampaignRecipientStatus.Cancelled))
        {
            if (campaign.Status == CampaignStatus.Completed) return;
            campaign.Status = CampaignStatus.Completed; campaign.PauseReason = "";
            await _repository.SaveCampaignAsync(campaign, cancellationToken);
            await _repository.LogEventAsync("campaign_completed", null, null, $"campaign_id={campaign.Id};channel={campaign.Channel};sent={recipients.Count(x => x.Status == CampaignRecipientStatus.Sent)};delivery_acknowledged={recipients.Count(x => x.Status == CampaignRecipientStatus.DeliveryAcknowledged)}", cancellationToken);
        }
    }

    private async void Bridge_EventReceived(object? sender, WhatsAppBridgeEvent e)
    {
        try
        {
            if (e.Name == "message_status")
            {
                await HandleDeliveryReceiptAsync(e, _lifetime?.Token ?? CancellationToken.None);
                return;
            }
            if (e.Name != "connection" || !e.Data.TryGetProperty("state", out var state) || state.GetString() != "logged_out") return;
            await _repository.PauseActiveCampaignsAsync(string.IsNullOrWhiteSpace(e.AccountId) ? "primary" : e.AccountId, "WhatsApp 登录已失效，Campaign 已自动暂停。");
            CampaignChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    public static string RenderTemplate(string template, Lead lead)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = lead.Name, ["company"] = lead.Company, ["country"] = lead.Country,
            ["product"] = lead.ProductInterest, ["owner"] = lead.Owner, ["grade"] = lead.Grade,
            ["stage"] = Labels.Stage(lead.Stage), ["phone"] = lead.PhoneE164, ["email"] = lead.Email,
            ["language"] = lead.PreferredLanguage, ["order_value"] = lead.EstimatedOrderValue.ToString("0.##"),
            ["currency"] = lead.Currency, ["score"] = lead.Score.ToString(), ["tags"] = string.Join(", ", lead.Tags),
            ["profile_summary"] = lead.ProfileSummary, ["customer_segment"] = lead.CustomerSegment,
            ["next_action"] = lead.NextAction, ["latest_message"] = lead.LatestMessage, ["source"] = lead.Source
        };
        foreach (var pair in lead.CustomFields) fields.TryAdd(pair.Key, pair.Value);
        return Regex.Replace(template, "\\{([^{}]+)\\}", match => fields.TryGetValue(match.Groups[1].Value.Trim(), out var value) ? value : "", RegexOptions.CultureInvariant);
    }

    public static IReadOnlyList<CampaignTemplateField> CoreTemplateFields() =>
    [
        new("name", "姓名", "客户列表"), new("company", "公司", "客户列表"), new("country", "国家", "客户列表"), new("phone", "WhatsApp 号码", "客户列表"),
        new("email", "邮箱", "客户列表"), new("language", "客户语言", "客户列表"), new("product", "产品或服务兴趣", "客户列表"), new("order_value", "预计机会金额", "客户列表"),
        new("currency", "币种", "客户列表"), new("owner", "负责人", "客户列表"), new("tags", "标签", "客户列表"), new("source", "客户来源", "客户列表"),
        new("grade", "商机等级", "商机智能"), new("stage", "跟进阶段", "商机智能"), new("score", "商机评分", "商机智能"), new("profile_summary", "客户画像", "商机智能"),
        new("customer_segment", "客户分组", "商机智能"), new("next_action", "下一步建议", "商机智能"), new("latest_message", "最近消息", "商机智能")
    ];

    private async Task ValidateTemplateFieldsAsync(string template, CancellationToken cancellationToken)
    {
        var available = (await GetTemplateFieldsAsync(cancellationToken)).Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = Regex.Matches(template, "\\{([^{}]+)\\}")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(key => !available.Contains(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"话术包含不存在的客户字段：{string.Join("、", unknown)}。请从“插入客户字段”列表选择。");
    }

    private static bool MatchesFilter(WhatsAppCampaign campaign, Lead lead) =>
        (campaign.GradeFilter is "" or "全部" || lead.Grade.Equals(campaign.GradeFilter, StringComparison.OrdinalIgnoreCase)) &&
        (campaign.StageFilter is null || lead.Stage == campaign.StageFilter) &&
        (string.IsNullOrWhiteSpace(campaign.TagFilter) || lead.Tags.Any(x => x.Contains(campaign.TagFilter.Trim(), StringComparison.CurrentCultureIgnoreCase))) &&
        (string.IsNullOrWhiteSpace(campaign.OwnerFilter) || lead.Owner.Contains(campaign.OwnerFilter.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private static bool IsEligible(WhatsAppCampaign campaign, Lead lead, out string reason)
    {
        if (lead.OptedOut) { reason = "客户已退订"; return false; }
        if (campaign.Channel == CampaignChannel.Email)
        {
            if (lead.EmailOptedOut) { reason = "客户已退订邮件"; return false; }
            if (!System.Net.Mail.MailAddress.TryCreate(lead.Email, out _)) { reason = "邮箱无效或为空"; return false; }
            reason = "邮箱有效";
            return true;
        }
        if (!lead.PhoneValid) { reason = "号码格式无效"; return false; }
        if (lead.WhatsAppRegistrationStatus == WhatsAppRegistrationStatus.NotRegistered
            && lead.WhatsAppRegistrationMatchesCurrentPhone)
        {
            reason = "号码未注册 WhatsApp";
            return false;
        }
        var numberState = lead.IsWhatsAppRegistered ? "WhatsApp 已确认有效" : "发送前将由 WhatsApp 实时检测";
        reason = lead.WhatsAppOptIn ? $"{numberState} · 已记录营销同意" : $"{numberState} · 未记录营销同意";
        return true;
    }

    private static void ValidateDraft(WhatsAppCampaign campaign)
    {
        if (string.IsNullOrWhiteSpace(campaign.Name)) throw new InvalidOperationException("请填写 Campaign 名称。");
        if (string.IsNullOrWhiteSpace(campaign.MessageTemplate)) throw new InvalidOperationException("请填写发送话术。");
        if (campaign.Channel == CampaignChannel.WhatsApp && campaign.MessageTemplate.Length > 4096) throw new InvalidOperationException("WhatsApp 话术不能超过 4096 字符。");
        if (campaign.Channel == CampaignChannel.Email)
        {
            if (string.IsNullOrWhiteSpace(campaign.EmailSubjectTemplate)) throw new InvalidOperationException("请填写邮件主题。");
            if (campaign.EmailSubjectTemplate.Length > 998) throw new InvalidOperationException("邮件主题过长。");
            if (campaign.MessageTemplate.Length > 200_000) throw new InvalidOperationException("邮件正文不能超过 200,000 字符。");
        }
        var interval = campaign.EffectiveIntervalValue;
        if (campaign.IntervalUnit == CampaignIntervalUnit.Seconds && interval is < 10 or > 3600)
            throw new InvalidOperationException("按秒发送时，间隔必须在 10–3600 秒之间。过密发送不会让个人账号更安全。");
        if (campaign.IntervalUnit == CampaignIntervalUnit.Minutes && interval is < 1 or > 1440)
            throw new InvalidOperationException("按分钟发送时，间隔必须在 1–1440 分钟之间。");
        if (campaign.DailyLimit is < 1 or > 1000) throw new InvalidOperationException("每日上限必须在 1–1000 之间。");
    }

    private static TimeZoneInfo BeijingTimeZone()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Local;
    }

    private static DateTimeOffset BeijingDayStart(DateTimeOffset now)
    {
        var zone = BeijingTimeZone(); var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(start, zone.GetUtcOffset(start));
    }

    private static DateTimeOffset NextBeijingDay(DateTimeOffset now, DateTimeOffset campaignStart)
    {
        var zone = BeijingTimeZone(); var localNow = TimeZoneInfo.ConvertTime(now, zone); var localStart = TimeZoneInfo.ConvertTime(campaignStart, zone);
        var next = DateTime.SpecifyKind(localNow.Date.AddDays(1).Add(localStart.TimeOfDay), DateTimeKind.Unspecified);
        return new DateTimeOffset(next, zone.GetUtcOffset(next));
    }

    private static string Safe(string value)
    {
        var clean = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean[..Math.Min(clean.Length, 500)];
    }

    public async ValueTask DisposeAsync()
    {
        _bridge.EventReceived -= Bridge_EventReceived;
        _lifetime?.Cancel();
        if (_worker is not null) try { await _worker; } catch (OperationCanceledException) { }
        if (_safetyWorker is not null) try { await _safetyWorker; } catch (OperationCanceledException) { }
        _lifetime?.Dispose(); _sendLock.Dispose();
    }
}
