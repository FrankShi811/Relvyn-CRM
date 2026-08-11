using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed partial class SourcingRequestService
{
    private readonly LocalRepository _repository;

    public SourcingRequestService(LocalRepository repository) => _repository = repository;

    public async Task<SourcingRequest> MergeAsync(
        string customerId,
        string accountId,
        string conversationId,
        string messageId,
        IEnumerable<CustomerSuccessSourcingProposal> proposals,
        CancellationToken cancellationToken = default)
    {
        var existingRequest = await _repository.GetLatestSourcingRequestAsync(customerId, cancellationToken);
        var request = existingRequest ?? new SourcingRequest
        {
            CustomerId = customerId,
            Status = SourcingRequestStatus.Draft
        };
        request.Fields ??= [];
        request.Conflicts ??= [];
        var beforeFingerprint = Fingerprint(request);
        foreach (var proposal in proposals.GroupBy(item => item.Field).Select(group => group.First()).Take(5))
        {
            var candidate = new SourcingFieldValue
            {
                Field = proposal.Field,
                Value = proposal.Value.Trim(),
                NormalizedValue = NormalizeValue(proposal.Field, proposal.Value),
                HumanConfirmed = proposal.HumanConfirmed,
                SourceAccountId = accountId,
                SourceConversationId = conversationId,
                SourceMessageId = messageId,
                EvidenceQuote = proposal.EvidenceQuote.Trim(),
                ObservedAt = DateTimeOffset.Now
            };
            candidate.IsStructurallyValid = Validate(candidate);
            if (!candidate.IsStructurallyValid) continue;
            if (request.Fields.TryGetValue(candidate.Field, out var existing) &&
                !string.Equals(existing.NormalizedValue, candidate.NormalizedValue, StringComparison.OrdinalIgnoreCase))
            {
                var conflict = request.Conflicts.FirstOrDefault(item => item.Field == candidate.Field && !item.IsResolved);
                if (conflict is null)
                {
                    conflict = new SourcingFieldConflict { Field = candidate.Field, Values = [existing] };
                    request.Conflicts.Add(conflict);
                }
                if (!conflict.Values.Any(item => item.NormalizedValue.Equals(candidate.NormalizedValue, StringComparison.OrdinalIgnoreCase)))
                    conflict.Values.Add(candidate);
                continue;
            }
            request.Fields[candidate.Field] = candidate;
        }
        if (existingRequest is not null && !beforeFingerprint.Equals(Fingerprint(request), StringComparison.Ordinal))
            request.Version++;
        request.Status = request.Conflicts.Any(item => !item.IsResolved)
            ? SourcingRequestStatus.FieldConflict
            : request.Completeness == 100 ? SourcingRequestStatus.Complete
            : request.Completeness > 0 ? SourcingRequestStatus.Collecting : SourcingRequestStatus.Draft;
        request.Summary = BuildSummary(request);
        await _repository.UpsertSourcingRequestAsync(request, cancellationToken);
        await SyncCustomerBrainStateAsync(request, cancellationToken);
        var readiness = request.Readiness;
        await _repository.LogEventAsync("sourcing_request_updated", customerId, null, Json.Serialize(new
        {
            request.Id,
            request.Version,
            status = request.Status.ToString(),
            request.Completeness,
            readiness.CollectedCount,
            readiness = readiness.Readiness.ToString(),
            readiness.ProductIdentifiable,
            readiness.Confidence,
            missing = request.MissingFields.Select(item => item.ToString()),
            conflicts = request.Conflicts.Count(item => !item.IsResolved)
        }), cancellationToken);
        return request;
    }

    public async Task<SourcingRequest> ResolveConflictAsync(
        string customerId, SourcingFieldKey field, string selectedValue, string actor = "user",
        CancellationToken cancellationToken = default)
    {
        var request = await _repository.GetLatestSourcingRequestAsync(customerId, cancellationToken)
                      ?? throw new InvalidOperationException("没有待处理的客户需求。");
        var conflict = request.Conflicts.FirstOrDefault(item => item.Field == field && !item.IsResolved)
                       ?? throw new InvalidOperationException("该字段没有待处理冲突。");
        var selected = conflict.Values.FirstOrDefault(item =>
            item.Value.Equals(selectedValue, StringComparison.OrdinalIgnoreCase) ||
            item.NormalizedValue.Equals(NormalizeValue(field, selectedValue), StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException("所选值不在冲突候选中。");
        selected.HumanConfirmed = true;
        request.Fields[field] = selected;
        conflict.IsResolved = true;
        conflict.Resolution = $"{actor} 于 {DateTimeOffset.Now:O} 选择：{selected.Value}";
        request.Version++;
        request.Status = request.Conflicts.Any(item => !item.IsResolved)
            ? SourcingRequestStatus.FieldConflict
            : request.Completeness == 100 ? SourcingRequestStatus.Complete : SourcingRequestStatus.Collecting;
        request.Summary = BuildSummary(request);
        await _repository.UpsertSourcingRequestAsync(request, cancellationToken);
        await SyncCustomerBrainStateAsync(request, cancellationToken);
        return request;
    }

    public async Task<SourcingRequest> MarkRequirementVersionUsedAsync(
        string customerId,
        int requirementVersion,
        CancellationToken cancellationToken = default)
    {
        var request = await _repository.GetLatestSourcingRequestAsync(customerId, cancellationToken)
                      ?? throw new InvalidOperationException("没有可提交的客户需求。");
        request.LastSourcingRequirementVersion = Math.Max(request.LastSourcingRequirementVersion, requirementVersion);
        await _repository.UpsertSourcingRequestAsync(request, cancellationToken);
        return request;
    }

    public static bool Validate(SourcingFieldValue value)
    {
        if (string.IsNullOrWhiteSpace(value.Value) || string.IsNullOrWhiteSpace(value.EvidenceQuote)) return false;
        return value.Field switch
        {
            SourcingFieldKey.ProductImage =>
                value.HumanConfirmed || LinkRegex().IsMatch(value.Value) ||
                value.Value.Contains("[image]", StringComparison.OrdinalIgnoreCase) ||
                value.Value.Contains("图片", StringComparison.OrdinalIgnoreCase) ||
                SourcingReadinessPolicy.IsProductIdentifiable(value.Value),
            SourcingFieldKey.Quantity => NumberRegex().IsMatch(value.Value),
            SourcingFieldKey.TargetPrice =>
                NumberRegex().IsMatch(value.Value) && CurrencyRegex().IsMatch(value.Value),
            SourcingFieldKey.Destination => value.Value.Trim().Length >= 3 &&
                (value.Value.Any(char.IsLetter) || PostcodeRegex().IsMatch(value.Value)),
            SourcingFieldKey.ShippingPreference => ShippingRegex().IsMatch(value.Value),
            _ => false
        };
    }

    private static string NormalizeValue(SourcingFieldKey field, string value)
    {
        var trimmed = string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (field is SourcingFieldKey.Quantity or SourcingFieldKey.TargetPrice)
        {
            var numbers = NumberRegex().Matches(trimmed).Select(match => match.Value).ToList();
            return numbers.Count == 0 ? trimmed : string.Join("|", numbers) + "|" + trimmed;
        }
        return trimmed;
    }

    private static string BuildSummary(SourcingRequest request)
    {
        var fields = string.Join("；", Enum.GetValues<SourcingFieldKey>().Select(field =>
            request.Fields.TryGetValue(field, out var value) && value.IsStructurallyValid
                ? $"{field}={value.Value}" : $"{field}=待补充"));
        var readiness = request.Readiness;
        return $"{fields}；readiness={readiness.Readiness}；collected={readiness.CollectedCount}/5";
    }

    private async Task SyncCustomerBrainStateAsync(SourcingRequest request, CancellationToken cancellationToken)
    {
        var profile = await _repository.GetCustomerIntelligenceProfileAsync(request.CustomerId, cancellationToken);
        if (profile is null) return;
        var readiness = request.Readiness;
        profile.ProductRequirementState = new ProductRequirementState
        {
            Completeness = readiness.CollectedCount,
            Readiness = readiness.Readiness,
            MissingElements = [.. readiness.MissingElements],
            ProductIdentifiable = readiness.ProductIdentifiable,
            RequirementVersion = request.Version,
            LastUpdatedAt = request.UpdatedAt
        };
        await _repository.SaveCustomerIntelligenceProfileAsync(profile, cancellationToken);
    }

    private static string Fingerprint(SourcingRequest request)
    {
        var value = Json.Serialize(new
        {
            fields = request.Fields.OrderBy(item => item.Key).Select(item => new
            {
                item.Key,
                item.Value.NormalizedValue,
                item.Value.IsStructurallyValid,
                item.Value.HumanConfirmed
            }),
            conflicts = request.Conflicts.Select(item => new
            {
                item.Field,
                item.IsResolved,
                values = item.Values.Select(value => value.NormalizedValue)
            })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    [GeneratedRegex(@"https?://|www\.|\.(jpg|jpeg|png|webp)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();
    [GeneratedRegex(@"\d+(?:[.,]\d+)?")]
    private static partial Regex NumberRegex();
    [GeneratedRegex(@"(?:\$|usd|us\$|eur|€|gbp|£|rmb|cny|￥|人民币|美元|欧元|英镑)", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyRegex();
    [GeneratedRegex(@"\b\d{4,10}\b")]
    private static partial Regex PostcodeRegex();
    [GeneratedRegex(@"(?:air|sea|ocean|express|courier|rail|train|truck|road|空运|海运|快递|铁路|卡车|陆运|不确定|uncertain|other)", RegexOptions.IgnoreCase)]
    private static partial Regex ShippingRegex();
}
