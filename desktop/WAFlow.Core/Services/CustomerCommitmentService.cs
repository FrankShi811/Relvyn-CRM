using System.Security.Cryptography;
using System.Text;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

/// <summary>
/// Maintains explicit promises made by the salesperson to a customer. AI may
/// discover and refresh evidence, but only a human action may complete a promise.
/// </summary>
public sealed class CustomerCommitmentService
{
    private readonly LocalRepository _repository;

    public CustomerCommitmentService(LocalRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<CustomerCommitment>> SynchronizeDetectedAsync(
        string customerId,
        IEnumerable<CustomerCommitmentCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetCustomerCommitmentsAsync(customerId, cancellationToken: cancellationToken);
        var bySource = existing.ToDictionary(SourceKey, StringComparer.OrdinalIgnoreCase);
        var synchronized = new List<CustomerCommitment>();

        foreach (var candidate in candidates
                     .Where(IsUsableCandidate)
                     .GroupBy(SourceKey, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderByDescending(item => item.Confidence).First()))
        {
            var key = SourceKey(candidate);
            if (bySource.TryGetValue(key, out var current))
            {
                // A model refresh must never reopen or mutate a promise that a
                // person has already confirmed complete.
                if (current.Status == CustomerCommitmentStatus.Completed)
                {
                    synchronized.Add(current);
                    continue;
                }

                current.Title = candidate.Title.Trim();
                current.Detail = candidate.Detail.Trim();
                current.Evidence = candidate.Evidence.Trim();
                current.Confidence = candidate.Confidence;
                current.SourceOccurredAt = candidate.SourceOccurredAt;
                current.DueAt ??= candidate.DueAt;
                await _repository.UpsertCustomerCommitmentAsync(current, cancellationToken);
                synchronized.Add(current);
                continue;
            }

            var commitment = new CustomerCommitment
            {
                Id = StableId("customer_commitment", customerId, candidate.SourceChannel, candidate.SourceMessageId),
                CustomerId = customerId,
                Title = candidate.Title.Trim(),
                Detail = candidate.Detail.Trim(),
                Status = CustomerCommitmentStatus.Active,
                DueAt = candidate.DueAt,
                SourceChannel = NormalizeChannel(candidate.SourceChannel),
                SourceMessageId = candidate.SourceMessageId.Trim(),
                Evidence = candidate.Evidence.Trim(),
                Confidence = candidate.Confidence,
                SourceOccurredAt = candidate.SourceOccurredAt,
                DetectedAt = DateTimeOffset.Now
            };
            await _repository.UpsertCustomerCommitmentAsync(commitment, cancellationToken);
            await _repository.UpsertCustomerEventAsync(new CustomerEventLogEntry
            {
                Id = StableId("event", customerId, "commitment_detected", commitment.Id),
                CustomerId = customerId,
                EventType = "commitment_detected",
                Title = "发现待履约承诺",
                Detail = $"{commitment.Title}；来源 {commitment.SourceChannel} 消息 {commitment.SourceMessageId}。",
                SourceType = "customer_commitment",
                SourceId = commitment.Id,
                OccurredAt = commitment.DetectedAt
            }, cancellationToken);
            await _repository.LogEventAsync(
                "customer_commitment_detected",
                customerId,
                null,
                $"commitment_id={commitment.Id};source={commitment.SourceChannel}:{commitment.SourceMessageId};confidence={commitment.Confidence:F2}",
                cancellationToken);
            bySource[key] = commitment;
            synchronized.Add(commitment);
        }

        return synchronized;
    }

    public Task<List<CustomerCommitment>> GetAsync(
        string customerId,
        bool activeOnly = false,
        CancellationToken cancellationToken = default) =>
        _repository.GetCustomerCommitmentsAsync(customerId, activeOnly, cancellationToken);

    public Task<List<CustomerCommitment>> GetActiveAsync(
        string customerId,
        CancellationToken cancellationToken = default) =>
        _repository.GetCustomerCommitmentsAsync(customerId, activeOnly: true, cancellationToken: cancellationToken);

    public async Task<IReadOnlyDictionary<string, CustomerCommitmentSummary>> GetActiveSummariesAsync(
        IEnumerable<string> customerIds,
        CancellationToken cancellationToken = default)
    {
        var requested = customerIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
            return new Dictionary<string, CustomerCommitmentSummary>(StringComparer.OrdinalIgnoreCase);

        var commitments = await _repository.GetCustomerCommitmentsAsync(
            customerId: null,
            activeOnly: true,
            cancellationToken);
        return commitments
            .Where(item => requested.Contains(item.CustomerId))
            .GroupBy(item => item.CustomerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var ordered = group
                        .OrderBy(item => item.DueAt is null)
                        .ThenBy(item => item.DueAt)
                        .ThenBy(item => item.DetectedAt)
                        .ToList();
                    return new CustomerCommitmentSummary
                    {
                        CustomerId = group.Key,
                        ActiveCount = ordered.Count,
                        OverdueCount = ordered.Count(item => item.IsOverdue),
                        NextDueAt = ordered.Where(item => item.DueAt is not null).Min(item => item.DueAt),
                        FirstTitle = ordered.FirstOrDefault()?.Title ?? ""
                    };
                },
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task<CustomerCommitment> CompleteAsync(
        string customerId,
        string commitmentId,
        string completionNote,
        CancellationToken cancellationToken = default)
    {
        var commitment = (await _repository.GetCustomerCommitmentsAsync(customerId, cancellationToken: cancellationToken))
            .FirstOrDefault(item => item.Id.Equals(commitmentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("待履约承诺不存在或不属于当前客户。");
        if (commitment.Status == CustomerCommitmentStatus.Completed) return commitment;

        commitment.Status = CustomerCommitmentStatus.Completed;
        commitment.CompletedAt = DateTimeOffset.Now;
        commitment.CompletionNote = string.IsNullOrWhiteSpace(completionNote)
            ? "用户确认承诺已履约"
            : completionNote.Trim();
        await _repository.UpsertCustomerCommitmentAsync(commitment, cancellationToken);
        await _repository.UpsertCustomerEventAsync(new CustomerEventLogEntry
        {
            Id = StableId("event", customerId, "commitment_completed", commitment.Id),
            CustomerId = customerId,
            EventType = "commitment_completed",
            Title = "承诺已人工完成",
            Detail = $"{commitment.Title}；{commitment.CompletionNote}",
            SourceType = "customer_commitment",
            SourceId = commitment.Id,
            OccurredAt = commitment.CompletedAt.Value
        }, cancellationToken);
        await _repository.LogEventAsync(
            "customer_commitment_completed",
            customerId,
            null,
            $"commitment_id={commitment.Id};completed_by=human",
            cancellationToken);
        return commitment;
    }

    private static bool IsUsableCandidate(CustomerCommitmentCandidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.Title)
        && !string.IsNullOrWhiteSpace(candidate.SourceChannel)
        && !string.IsNullOrWhiteSpace(candidate.SourceMessageId)
        && !string.IsNullOrWhiteSpace(candidate.Evidence)
        && candidate.Confidence >= 0.72
        && candidate.Confidence <= 1;

    private static string SourceKey(CustomerCommitmentCandidate candidate) =>
        $"{NormalizeChannel(candidate.SourceChannel)}\u001f{candidate.SourceMessageId.Trim()}";

    private static string SourceKey(CustomerCommitment commitment) =>
        $"{NormalizeChannel(commitment.SourceChannel)}\u001f{commitment.SourceMessageId.Trim()}";

    private static string NormalizeChannel(string value) =>
        value.Trim().Equals("email", StringComparison.OrdinalIgnoreCase) ? "Email" : "WhatsApp";

    private static string StableId(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values)))).ToLowerInvariant()[..32];
}
