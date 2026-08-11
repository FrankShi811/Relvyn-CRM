using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed class TodayBriefService
{
    private readonly LocalRepository _repository;
    private readonly PersonalSalesLearningService _learning;
    private readonly CustomerBrainService? _customerBrain;

    public TodayBriefService(
        LocalRepository repository,
        PersonalSalesLearningService? learning = null,
        CustomerBrainService? customerBrain = null)
    {
        _repository = repository;
        _learning = learning ?? new PersonalSalesLearningService(repository);
        _customerBrain = customerBrain;
    }

    public async Task<TodayBriefSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var leads = await _repository.GetLeadsAsync(cancellationToken: cancellationToken);
        var leadsById = leads.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var tasks = await _repository.GetFollowUpTasksAsync(null, cancellationToken);
        var activeTasks = tasks
            .Where(item => item.Status is FollowUpTaskStatus.Proposed or FollowUpTaskStatus.Open or FollowUpTaskStatus.InProgress)
            .OrderByDescending(item => PriorityRank(item.Priority))
            .ThenBy(item => item.DueAt)
            .ToList();
        var handoffs = await _repository.GetOpenHumanHandoffsAsync(cancellationToken);
        var sourcingRequests = await _repository.GetLatestSourcingRequestsAsync(cancellationToken);
        var knowledgeDocuments = await _repository.GetKnowledgeDocumentsAsync(false, cancellationToken);
        var knowledgeCandidates = await _repository.GetKnowledgeCandidatesAsync(KnowledgeCandidateStatus.Proposed, cancellationToken);
        var sourceAccountIds = handoffs.Select(item => item.AccountId)
            .Concat(sourcingRequests.SelectMany(item => item.Fields.Values.Select(field => field.SourceAccountId)))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var conversationsById = new Dictionary<string, WhatsAppConversation>(StringComparer.OrdinalIgnoreCase);
        foreach (var accountId in sourceAccountIds)
        {
            foreach (var conversation in await _repository.GetWhatsAppConversationsAsync(accountId, cancellationToken))
                conversationsById.TryAdd(conversation.Id, conversation);
        }
        var profileCache = new Dictionary<string, CustomerIntelligenceProfile?>(StringComparer.Ordinal);
        var identityCache = new Dictionary<string, GlobalCustomerIdentity?>(StringComparer.Ordinal);

        async Task<CustomerIntelligenceProfile?> GetProfileAsync(string customerId)
        {
            if (string.IsNullOrWhiteSpace(customerId)) return null;
            if (profileCache.TryGetValue(customerId, out var cached)) return cached;
            var candidate = _customerBrain is null
                ? await _repository.GetCustomerIntelligenceProfileAsync(customerId, cancellationToken)
                : await _customerBrain.GetAsync(customerId, cancellationToken);
            var profile = candidate?.HasCurrentDecision == true ? candidate : null;
            profileCache[customerId] = profile;
            return profile;
        }

        async Task<string> ResolveCustomerNameAsync(string customerId, string conversationId = "")
        {
            if (leadsById.TryGetValue(customerId, out var lead) && ResolveLeadCustomerName(lead, customerId) is { } leadName)
                return leadName;

            var profile = await GetProfileAsync(customerId);
            if (IsReadableCustomerName(profile?.CustomerName, customerId)) return profile!.CustomerName.Trim();

            if (!string.IsNullOrWhiteSpace(customerId))
            {
                if (!identityCache.TryGetValue(customerId, out var identity))
                {
                    identity = await _repository.GetGlobalCustomerIdentityAsync(customerId, cancellationToken);
                    identityCache[customerId] = identity;
                }
                if (IsReadableCustomerName(identity?.CanonicalName, customerId)) return identity!.CanonicalName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(conversationId) &&
                conversationsById.TryGetValue(conversationId, out var conversation))
            {
                if (IsReadableCustomerName(conversation.DisplayName, customerId)) return conversation.DisplayName.Trim();
                var digits = new string(conversation.Phone.Where(char.IsDigit).ToArray());
                if (digits.Length >= 4) return $"WhatsApp 客户 · 尾号 {digits[^4..]}";
            }

            return "未命名客户";
        }

        var items = new List<TodayBriefItem>();
        foreach (var task in activeTasks.Take(20))
        {
            leadsById.TryGetValue(task.CustomerId, out var lead);
            var profile = await GetProfileAsync(task.CustomerId);
            if (!string.IsNullOrWhiteSpace(task.RecommendationId)
                && task.Status != FollowUpTaskStatus.InProgress)
            {
                var recommendation = (await _repository.GetAiRecommendationHistoryAsync(task.CustomerId, cancellationToken))
                    .FirstOrDefault(item => item.Id.Equals(task.RecommendationId, StringComparison.Ordinal));
                if (recommendation is not null
                    && !string.IsNullOrWhiteSpace(recommendation.SourceProfileId)
                    && (profile?.HasCurrentDecision != true
                        || !profile.Id.Equals(recommendation.SourceProfileId, StringComparison.Ordinal)
                        || profile.Version != recommendation.SourceProfileVersion))
                    continue;
            }
            items.Add(new TodayBriefItem
            {
                CustomerId = task.CustomerId,
                CustomerName = await ResolveCustomerNameAsync(task.CustomerId),
                RecommendationId = task.RecommendationId,
                Action = task.Title,
                Reason = task.Reason,
                Priority = task.Priority,
                Status = task.Status,
                DueAt = task.DueAt,
                PurchaseProbability = profile?.PurchaseProbability ?? lead?.PurchaseProbability ?? 0,
                Confidence = profile?.Confidence ?? lead?.AnalysisConfidence ?? 0,
                SuggestedStage = profile?.SuggestedStage ?? lead?.Stage ?? LeadStage.New
            });
        }

        var sourcingReady = sourcingRequests
            .Where(item => item.Readiness.CanUseAgent)
            .Where(item => item.Version > item.LastSourcingRequirementVersion)
            .ToList();
        foreach (var handoff in handoffs.Take(8))
            items.Insert(0, BuildSpecialItem(handoff.CustomerId, await ResolveCustomerNameAsync(handoff.CustomerId, handoff.ConversationId),
                "handoff", "打开对应 WhatsApp 会话，完成人工处理并记录结果",
                handoff.Reason, FollowUpPriority.Urgent, handoff.AccountId, handoff.ConversationId, now));
        foreach (var sourcing in sourcingReady.Take(8))
        {
            var source = sourcing.Fields.Values.OrderByDescending(item => item.ObservedAt).FirstOrDefault();
            items.Add(BuildSpecialItem(sourcing.CustomerId, await ResolveCustomerNameAsync(sourcing.CustomerId, source?.SourceConversationId ?? ""),
                "sourcing_ready", "客户已提供足够信息，可以人工选择外部 Agent 开始搜品",
                sourcing.Readiness.Readiness == SourcingReadinessLevel.HighConfidence
                    ? "采购需求 5/5，信息完整。仍需人工选择 Agent 并确认发送内容。"
                    : $"采购需求 {sourcing.CollectedCount}/5，部分完整但可执行；仍缺 {string.Join("、", sourcing.Readiness.MissingElements)}。",
                FollowUpPriority.High,
                source?.SourceAccountId ?? "", source?.SourceConversationId ?? "", now));
        }
        foreach (var document in knowledgeDocuments
                     .Where(item => item.Status is KnowledgeDocumentStatus.ReadyForReview
                         or KnowledgeDocumentStatus.Outdated
                         or KnowledgeDocumentStatus.Conflicted)
                     .OrderByDescending(item => item.RiskLevel)
                     .ThenByDescending(item => item.UpdatedAt)
                     .Take(6))
        {
            var conflict = document.Status == KnowledgeDocumentStatus.Conflicted;
            items.Add(new TodayBriefItem
            {
                CustomerId = document.Scope.CustomerId,
                CustomerName = $"知识库 · {document.Title}",
                Category = conflict ? "knowledge_conflict" : "knowledge_review",
                Action = conflict
                    ? "打开知识库核对冲突来源，解决前不要恢复自动检索"
                    : "打开知识库复核原文、作用域、风险与时效，再决定是否启用",
                Reason = string.IsNullOrWhiteSpace(document.ProcessingError)
                    ? $"{document.CategoryLabel} · {document.ScopeLabel} · {document.VersionLabel}"
                    : document.ProcessingError,
                Priority = conflict ? FollowUpPriority.Urgent : FollowUpPriority.Normal,
                Status = FollowUpTaskStatus.Open,
                DueAt = now
            });
        }
        foreach (var candidate in knowledgeCandidates.Take(4))
            items.Add(new TodayBriefItem
            {
                CustomerName = $"候选知识 · {candidate.Title}",
                Category = "knowledge_candidate",
                Action = "查看真实发送样本、回复与阶段结果，人工批准或拒绝候选",
                Reason = $"{candidate.EvidenceLabel} · 样本 {candidate.SampleSize} · 回复 {candidate.Replies} · 阶段推进 {candidate.StageProgressions} · 成交 {candidate.Conversions}",
                Priority = candidate.EvidenceLevel == KnowledgeEvidenceLevel.OutcomeValidated
                    ? FollowUpPriority.High
                    : FollowUpPriority.Low,
                Status = FollowUpTaskStatus.Open,
                DueAt = now
            });

        var learning = await _learning.RefreshAsync(cancellationToken);

        return new TodayBriefSnapshot
        {
            GeneratedAt = now,
            OverdueCount = activeTasks.Count(item => item.DueAt < now),
            DueTodayCount = activeTasks.Count(item => item.DueAt.Date == now.Date),
            InProgressCount = activeTasks.Count(item => item.Status == FollowUpTaskStatus.InProgress),
            HumanHandoffCount = handoffs.Count,
            SourcingCompleteCount = sourcingReady.Count,
            CrossAccountFollowUpCount = 0,
            KnowledgeReviewCount = knowledgeDocuments.Count(item => item.Status is KnowledgeDocumentStatus.ReadyForReview or KnowledgeDocumentStatus.Outdated),
            KnowledgeConflictCount = knowledgeDocuments.Count(item => item.Status == KnowledgeDocumentStatus.Conflicted),
            KnowledgeCandidateCount = knowledgeCandidates.Count,
            Items = items
                .OrderByDescending(item => PriorityRank(item.Priority))
                .ThenBy(item => item.DueAt)
                .Take(30).ToList(),
            Learning = learning
        };
    }

    private TodayBriefItem BuildSpecialItem(
        string customerId, string customerName, string category, string action, string reason, FollowUpPriority priority,
        string accountId, string conversationId, DateTimeOffset dueAt)
    {
        return new TodayBriefItem
        {
            CustomerId = customerId,
            CustomerName = customerName,
            Category = category,
            Action = action,
            Reason = reason,
            Priority = priority,
            Status = FollowUpTaskStatus.Open,
            DueAt = dueAt,
            SourceAccountId = accountId,
            SourceConversationId = conversationId
        };
    }

    private static bool IsReadableCustomerName(string? value, string customerId)
    {
        var candidate = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Equals(customerId, StringComparison.OrdinalIgnoreCase)) return false;
        return candidate.Length < 24 || !candidate.All(Uri.IsHexDigit);
    }

    private static string? ResolveLeadCustomerName(Lead lead, string customerId)
    {
        var candidates = new List<string?> { lead.Name };
        candidates.AddRange(lead.CustomFields
            .Where(item =>
            {
                var key = item.Key.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
                return key.Contains("nickname", StringComparison.Ordinal)
                       || key.Contains("buyername", StringComparison.Ordinal)
                       || key.Contains("customername", StringComparison.Ordinal)
                       || key.Contains("买家昵称", StringComparison.Ordinal)
                       || key.Contains("买家姓名", StringComparison.Ordinal)
                       || key.Contains("客户姓名", StringComparison.Ordinal);
            })
            .Select(item => item.Value));
        candidates.Add(lead.Company);
        return candidates.Select(item => item?.Trim())
            .FirstOrDefault(item => IsReadableCustomerName(item, customerId));
    }

    private static int PriorityRank(FollowUpPriority priority) => priority switch
    {
        FollowUpPriority.Urgent => 4,
        FollowUpPriority.High => 3,
        FollowUpPriority.Normal => 2,
        _ => 1
    };
}
