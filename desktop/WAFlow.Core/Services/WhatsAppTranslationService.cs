using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed partial class WhatsAppTranslationService
{
    private const int DetectionSampleLimit = 30;
    private const int TranslationHistoryLimit = 160;
    private const int RecentTranslationLimit = 30;
    private const int TranslationBatchSize = 10;

    private const string DetectionInstructions = """
        你是 AI Sales OS 的会话语言识别器。只分析 suppliedIncomingMessages 中客户发送的原文，
        判断该会话对方长期占主导的沟通语言。忽略孤立短词、网址、数字、表情和销售人员发出的消息。
        mixed 不是语言；存在多种语言时选择反复出现、承载主要业务内容的一种。语言标签必须使用简洁
        BCP-47 标签（例如 en、es、fr、de、ar、pt-BR、zh-Hans、zh-Hant）。languageName 用本机语言书写。
        不确定时仍返回最可能的主导语言，但 confidence 必须如实降低。只返回严格 JSON：
        {"languageCode":"string","languageName":"string","confidence":0.0}
        """;

    private const string TranslationInstructions = """
        你是 AI Sales OS 的忠实商务翻译器。逐条翻译 suppliedMessages，不能总结、补充、回答或改写事实。
        保留人名、公司名、网址、邮箱、电话号码、SKU、订单号、货币、金额、数量、日期和换行；不要翻译代码。
        每个输入 id 必须原样返回一次，顺序保持一致。sourceLanguageCode 使用简洁 BCP-47 标签。
        只返回严格 JSON：
        {"items":[{"id":"string","sourceLanguageCode":"string","translatedText":"string"}]}
        """;

    private readonly LocalRepository _repository;
    private readonly IStructuredAiProvider _provider;
    private readonly Func<CultureInfo> _cultureProvider;

    public WhatsAppTranslationService(
        LocalRepository repository,
        IStructuredAiProvider provider,
        Func<CultureInfo>? cultureProvider = null)
    {
        _repository = repository;
        _provider = provider;
        _cultureProvider = cultureProvider ?? (() => CultureInfo.CurrentUICulture);
    }

    public static (string Code, string Name) ResolveLocalLanguage(CultureInfo culture)
    {
        var code = culture.Name;
        if (string.IsNullOrWhiteSpace(code)) code = "en";
        code = code.ToLowerInvariant() switch
        {
            "zh-cn" or "zh-sg" or "zh-hans" => "zh-Hans",
            "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" => "zh-Hant",
            _ => code
        };
        var name = code switch
        {
            "zh-Hans" => "简体中文",
            "zh-Hant" => "繁体中文",
            _ => culture.NativeName
        };
        return (code, string.IsNullOrWhiteSpace(name) ? code : name);
    }

    public async Task<WhatsAppTranslationContext> GetContextAsync(
        string conversationId,
        bool forceDetection = false,
        CancellationToken cancellationToken = default)
    {
        EnsureProvider();
        var local = ResolveLocalLanguage(_cultureProvider());
        var model = await _provider.GetSelectedModelAsync(AiModuleKeys.WhatsAppInbox, cancellationToken);
        var messages = (await _repository.GetWhatsAppMessagesAsync(
                conversationId,
                TranslationHistoryLimit,
                cancellationToken))
            .Where(IsTranslatableMessage)
            .OrderBy(message => message.Timestamp)
            .ToList();
        var samples = messages
            .Where(message => message.Direction == WhatsAppMessageDirection.Incoming)
            .TakeLast(DetectionSampleLimit)
            .Select(message => message.Body.Trim())
            .ToList();
        var fingerprint = Hash(string.Join('\n', samples) + $"|{local.Code}|{model}");
        var state = await _repository.GetWhatsAppTranslationStateAsync(conversationId, cancellationToken);
        var profile = state.Profile;

        if (samples.Count == 0)
        {
            profile = new WhatsAppConversationLanguageProfile
            {
                ConversationId = conversationId,
                LocalLanguageCode = local.Code,
                LocalLanguageName = local.Name,
                CustomerLanguageCode = "",
                CustomerLanguageName = "待客户发来文本后识别",
                Confidence = 0,
                SampleCount = 0,
                SourceFingerprint = fingerprint,
                Model = model,
                UpdatedAt = DateTimeOffset.Now
            };
        }
        else if (forceDetection ||
                 profile is null ||
                 !profile.SourceFingerprint.Equals(fingerprint, StringComparison.Ordinal) ||
                 !profile.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var response = await _provider.CompleteStructuredAsync<WhatsAppLanguageDetectionResponse>(
                    AiModuleKeys.WhatsAppInbox,
                    DetectionInstructions,
                    new
                    {
                        localLanguage = new { code = local.Code, name = local.Name },
                        suppliedIncomingMessages = samples
                    },
                    ValidateDetection,
                    cancellationToken);
                profile = new WhatsAppConversationLanguageProfile
                {
                    ConversationId = conversationId,
                    LocalLanguageCode = local.Code,
                    LocalLanguageName = local.Name,
                    CustomerLanguageCode = NormalizeLanguageCode(response.LanguageCode),
                    CustomerLanguageName = response.LanguageName.Trim(),
                    Confidence = Math.Clamp(response.Confidence, 0, 1),
                    SampleCount = samples.Count,
                    SourceFingerprint = fingerprint,
                    Model = model,
                    UpdatedAt = DateTimeOffset.Now
                };
            }
            catch (AiProviderException error) when (
                error.Code.Equals("invalid_structured_output", StringComparison.OrdinalIgnoreCase) ||
                error.Retryable)
            {
                profile = BuildFallbackProfile(
                    conversationId,
                    samples,
                    local,
                    model,
                    fingerprint,
                    profile);
            }
        }

        state.Profile = profile;
        state.Translations = state.Translations
            .Where(item => item.UpdatedAt > DateTimeOffset.Now.AddMonths(-6))
            .OrderByDescending(item => item.UpdatedAt)
            .Take(600)
            .ToList();
        await _repository.SaveWhatsAppTranslationStateAsync(conversationId, state, cancellationToken);
        return new WhatsAppTranslationContext
        {
            Profile = profile!,
            CachedTranslations = state.Translations.Where(translation =>
            {
                var message = messages.FirstOrDefault(item =>
                    TranslationMessageId(item).Equals(translation.MessageId, StringComparison.OrdinalIgnoreCase));
                if (message is null || !Hash(message.Body).Equals(translation.SourceTextHash, StringComparison.Ordinal))
                    return false;
                var target = message.Direction == WhatsAppMessageDirection.Incoming
                    ? profile!.LocalLanguageCode
                    : profile!.CustomerLanguageCode;
                return translation.TargetLanguageCode.Equals(target, StringComparison.OrdinalIgnoreCase);
            }).ToList()
        };
    }

    public async Task<List<WhatsAppMessageTranslation>> TranslateRecentMessagesAsync(
        string conversationId,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var context = await GetContextAsync(conversationId, cancellationToken: cancellationToken);
        var profile = context.Profile;
        if (string.IsNullOrWhiteSpace(profile.CustomerLanguageCode))
            throw new InvalidOperationException("当前会话还没有足够的客户文本用于识别语言。");

        var messages = (await _repository.GetWhatsAppMessagesAsync(
                conversationId,
                TranslationHistoryLimit,
                cancellationToken))
            .Where(IsTranslatableMessage)
            .OrderByDescending(message => message.Timestamp)
            .ThenByDescending(TranslationMessageId, StringComparer.OrdinalIgnoreCase)
            .Take(RecentTranslationLimit)
            .OrderBy(message => message.Timestamp)
            .ThenBy(TranslationMessageId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var state = await _repository.GetWhatsAppTranslationStateAsync(conversationId, cancellationToken);
        var output = new List<WhatsAppMessageTranslation>();
        var pending = new List<TranslationSource>();

        foreach (var message in messages)
        {
            var target = message.Direction == WhatsAppMessageDirection.Incoming
                ? profile.LocalLanguageCode
                : profile.CustomerLanguageCode;
            var targetName = message.Direction == WhatsAppMessageDirection.Incoming
                ? profile.LocalLanguageName
                : profile.CustomerLanguageName;
            var sourceHash = Hash(message.Body);
            var key = TranslationMessageId(message);
            var cached = state.Translations.FirstOrDefault(item =>
                item.MessageId.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                item.TargetLanguageCode.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                item.SourceTextHash.Equals(sourceHash, StringComparison.Ordinal));
            if (!force && cached is not null)
            {
                output.Add(cached);
                continue;
            }
            var assumedSource = message.Direction == WhatsAppMessageDirection.Incoming
                ? profile.CustomerLanguageCode
                : profile.LocalLanguageCode;
            if (LanguageMatches(assumedSource, target))
            {
                var identity = new WhatsAppMessageTranslation
                {
                    MessageId = key,
                    Direction = message.Direction == WhatsAppMessageDirection.Incoming ? "incoming" : "outgoing",
                    SourceLanguageCode = assumedSource,
                    TargetLanguageCode = target,
                    TargetLanguageName = targetName,
                    SourceTextHash = sourceHash,
                    OriginalText = message.Body,
                    TranslatedText = message.Body,
                    Model = profile.Model,
                    UpdatedAt = DateTimeOffset.Now
                };
                state.Translations.RemoveAll(item =>
                    item.MessageId.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    item.TargetLanguageCode.Equals(target, StringComparison.OrdinalIgnoreCase));
                state.Translations.Add(identity);
                output.Add(identity);
                continue;
            }
            pending.Add(new TranslationSource(
                key,
                message.Body,
                message.Direction == WhatsAppMessageDirection.Incoming ? "incoming" : "outgoing",
                target,
                targetName,
                sourceHash));
        }

        foreach (var batch in pending.Chunk(TranslationBatchSize))
        {
            var translated = await TranslateBatchWithRecoveryAsync(batch, profile, cancellationToken);
            foreach (var item in translated)
            {
                state.Translations.RemoveAll(existing =>
                    existing.MessageId.Equals(item.MessageId, StringComparison.OrdinalIgnoreCase) &&
                    existing.TargetLanguageCode.Equals(item.TargetLanguageCode, StringComparison.OrdinalIgnoreCase));
                state.Translations.Add(item);
                output.Add(item);
            }
        }

        state.Profile = profile;
        await _repository.SaveWhatsAppTranslationStateAsync(conversationId, state, cancellationToken);
        return output;
    }

    public async Task<WhatsAppMessageTranslation> TranslateOutgoingAsync(
        string conversationId,
        string sourceText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
            throw new InvalidOperationException("请先输入需要翻译的消息。");
        var context = await GetContextAsync(conversationId, cancellationToken: cancellationToken);
        var profile = context.Profile;
        if (string.IsNullOrWhiteSpace(profile.CustomerLanguageCode))
            throw new InvalidOperationException("当前会话还没有识别出客户主流语言。");
        var sourceHash = Hash(sourceText);
        var id = $"draft:{sourceHash}";
        var state = await _repository.GetWhatsAppTranslationStateAsync(conversationId, cancellationToken);
        var cached = state.Translations.FirstOrDefault(item =>
            item.MessageId.Equals(id, StringComparison.OrdinalIgnoreCase) &&
            item.TargetLanguageCode.Equals(profile.CustomerLanguageCode, StringComparison.OrdinalIgnoreCase) &&
            item.SourceTextHash.Equals(sourceHash, StringComparison.Ordinal));
        if (cached is not null) return cached;
        if (LanguageMatches(profile.LocalLanguageCode, profile.CustomerLanguageCode))
        {
            return new WhatsAppMessageTranslation
            {
                MessageId = id,
                Direction = "outgoing_draft",
                SourceLanguageCode = profile.LocalLanguageCode,
                TargetLanguageCode = profile.CustomerLanguageCode,
                TargetLanguageName = profile.CustomerLanguageName,
                SourceTextHash = sourceHash,
                OriginalText = sourceText.Trim(),
                TranslatedText = sourceText.Trim(),
                Model = profile.Model,
                UpdatedAt = DateTimeOffset.Now
            };
        }

        var result = (await TranslateBatchWithRecoveryAsync(
            [new TranslationSource(
                id,
                sourceText.Trim(),
                "outgoing_draft",
                profile.CustomerLanguageCode,
                profile.CustomerLanguageName,
                sourceHash)],
            profile,
            cancellationToken)).Single();
        state.Profile = profile;
        state.Translations.RemoveAll(item =>
            item.MessageId.Equals(id, StringComparison.OrdinalIgnoreCase) &&
            item.TargetLanguageCode.Equals(profile.CustomerLanguageCode, StringComparison.OrdinalIgnoreCase));
        state.Translations.Add(result);
        await _repository.SaveWhatsAppTranslationStateAsync(conversationId, state, cancellationToken);
        return result;
    }

    private async Task<List<WhatsAppMessageTranslation>> TranslateBatchWithRecoveryAsync(
        IReadOnlyCollection<TranslationSource> sources,
        WhatsAppConversationLanguageProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            return await TranslateBatchAsync(sources, profile, cancellationToken);
        }
        catch (AiProviderException error) when (
            error.Code.Equals("invalid_structured_output", StringComparison.OrdinalIgnoreCase) &&
            sources.Count > 1)
        {
            var ordered = sources.ToList();
            var split = ordered.Count / 2;
            var first = await TranslateBatchWithRecoveryAsync(ordered.Take(split).ToList(), profile, cancellationToken);
            var second = await TranslateBatchWithRecoveryAsync(ordered.Skip(split).ToList(), profile, cancellationToken);
            first.AddRange(second);
            return first;
        }
    }

    private async Task<List<WhatsAppMessageTranslation>> TranslateBatchAsync(
        IReadOnlyCollection<TranslationSource> sources,
        WhatsAppConversationLanguageProfile profile,
        CancellationToken cancellationToken)
    {
        EnsureProvider();
        var model = await _provider.GetSelectedModelAsync(AiModuleKeys.WhatsAppInbox, cancellationToken);
        var response = await _provider.CompleteStructuredAsync<WhatsAppTranslationBatchResponse>(
            AiModuleKeys.WhatsAppInbox,
            TranslationInstructions,
            new
            {
                localLanguage = new { code = profile.LocalLanguageCode, name = profile.LocalLanguageName },
                customerDominantLanguage = new { code = profile.CustomerLanguageCode, name = profile.CustomerLanguageName },
                suppliedMessages = sources.Select(item => new
                {
                    id = item.Id,
                    direction = item.Direction,
                    targetLanguageCode = item.TargetCode,
                    targetLanguageName = item.TargetName,
                    text = item.Text
                })
            },
            candidate => ValidateTranslation(candidate, sources),
            cancellationToken);

        var byId = response.Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        return sources.Select(source =>
        {
            var item = byId[source.Id];
            return new WhatsAppMessageTranslation
            {
                MessageId = source.Id,
                Direction = source.Direction,
                SourceLanguageCode = NormalizeLanguageCode(item.SourceLanguageCode),
                TargetLanguageCode = source.TargetCode,
                TargetLanguageName = source.TargetName,
                SourceTextHash = source.SourceHash,
                OriginalText = source.Text,
                TranslatedText = item.TranslatedText.Trim(),
                Model = model,
                UpdatedAt = DateTimeOffset.Now
            };
        }).ToList();
    }

    private void EnsureProvider()
    {
        if (!_provider.HasApiKey(AiModuleKeys.WhatsAppInbox))
            throw new AiProviderException("provider_not_configured", "请先在“设置”中为 WhatsApp 配置 AI 模型。", false);
    }

    private static bool IsTranslatableMessage(WhatsAppMessage message)
    {
        if (message.IsStatusUpdate || message.IsRevoked || string.IsNullOrWhiteSpace(message.Body))
            return false;
        if (!message.Kind.Equals("text", StringComparison.OrdinalIgnoreCase))
            return false;
        var text = message.Body.Trim();
        if (text.Length < 2 || (text.StartsWith('[') && text.EndsWith(']')))
            return false;
        return !Uri.TryCreate(text, UriKind.Absolute, out _);
    }

    private static string TranslationMessageId(WhatsAppMessage message) =>
        string.IsNullOrWhiteSpace(message.ProviderMessageId) ? message.Id : message.ProviderMessageId;

    private static string? ValidateDetection(WhatsAppLanguageDetectionResponse response)
    {
        if (!LanguageCodeRegex().IsMatch(response.LanguageCode?.Trim() ?? ""))
            return "languageCode 必须是 BCP-47 语言标签。";
        if (string.IsNullOrWhiteSpace(response.LanguageName))
            return "languageName 不能为空。";
        return response.Confidence is < 0 or > 1 ? "confidence 必须在 0 到 1 之间。" : null;
    }

    private static string? ValidateTranslation(
        WhatsAppTranslationBatchResponse response,
        IReadOnlyCollection<TranslationSource> sources)
    {
        if (response.Items is null || response.Items.Count != sources.Count)
            return "items 数量必须与输入一致。";
        var expected = sources.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = response.Items.Select(item => item.Id).ToList();
        if (actual.Distinct(StringComparer.OrdinalIgnoreCase).Count() != actual.Count ||
            actual.Any(id => !expected.Contains(id)))
            return "items 的 id 必须与输入逐条对应且不能重复。";
        if (response.Items.Any(item => string.IsNullOrWhiteSpace(item.TranslatedText)))
            return "translatedText 不能为空。";
        return response.Items.Any(item => !LanguageCodeRegex().IsMatch(item.SourceLanguageCode?.Trim() ?? ""))
            ? "sourceLanguageCode 必须是 BCP-47 语言标签。"
            : null;
    }

    private static WhatsAppConversationLanguageProfile BuildFallbackProfile(
        string conversationId,
        IReadOnlyCollection<string> samples,
        (string Code, string Name) local,
        string model,
        string fingerprint,
        WhatsAppConversationLanguageProfile? existing)
    {
        var detected = existing is not null && !string.IsNullOrWhiteSpace(existing.CustomerLanguageCode)
            ? (NormalizeLanguageCode(existing.CustomerLanguageCode), existing.CustomerLanguageName, existing.Confidence)
            : DetectLanguageLocally(samples, local.Code);
        return new WhatsAppConversationLanguageProfile
        {
            ConversationId = conversationId,
            LocalLanguageCode = local.Code,
            LocalLanguageName = local.Name,
            CustomerLanguageCode = detected.Item1,
            CustomerLanguageName = string.IsNullOrWhiteSpace(detected.Item2)
                ? LanguageName(detected.Item1, local.Code)
                : detected.Item2,
            Confidence = Math.Clamp(detected.Item3, 0, 1),
            SampleCount = samples.Count,
            SourceFingerprint = fingerprint,
            Model = model,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private static (string Code, string Name, double Confidence) DetectLanguageLocally(
        IEnumerable<string> samples,
        string localLanguageCode)
    {
        var text = string.Join(' ', samples).Trim();
        if (Regex.IsMatch(text, @"[\u3040-\u30ff]"))
            return ("ja", LanguageName("ja", localLanguageCode), .72);
        if (Regex.IsMatch(text, @"[\uac00-\ud7af]"))
            return ("ko", LanguageName("ko", localLanguageCode), .72);
        if (Regex.IsMatch(text, @"[\u4e00-\u9fff]"))
            return ("zh-Hans", LanguageName("zh-Hans", localLanguageCode), .68);
        if (Regex.IsMatch(text, @"[\u0400-\u04ff]"))
            return ("ru", LanguageName("ru", localLanguageCode), .68);
        if (Regex.IsMatch(text, @"[\u0600-\u06ff]"))
            return ("ar", LanguageName("ar", localLanguageCode), .68);
        if (Regex.IsMatch(text, @"[\u0900-\u097f]"))
            return ("hi", LanguageName("hi", localLanguageCode), .68);

        var normalized = $" {Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}]+", " ")} ";
        var candidates = new[]
        {
            ("es", new[] { " el ", " la ", " que ", " para ", " por ", " gracias ", " precio ", " pedido " }),
            ("fr", new[] { " le ", " la ", " que ", " pour ", " merci ", " prix ", " commande ", " avec " }),
            ("de", new[] { " der ", " die ", " das ", " und ", " für ", " danke ", " preis ", " bestellung " }),
            ("pt", new[] { " o ", " a ", " que ", " para ", " obrigado ", " preço ", " pedido ", " com " }),
            ("it", new[] { " il ", " la ", " che ", " per ", " grazie ", " prezzo ", " ordine ", " con " }),
            ("tr", new[] { " ve ", " için ", " teşekkür ", " fiyat ", " sipariş ", " ile ", " bir ", " bu " })
        };
        var scored = candidates
            .Select(candidate => (
                candidate.Item1,
                Score: candidate.Item2.Count(token => normalized.Contains(token, StringComparison.Ordinal))))
            .OrderByDescending(candidate => candidate.Score)
            .First();
        var code = scored.Score >= 2 ? scored.Item1 : "en";
        return (code, LanguageName(code, localLanguageCode), scored.Score >= 2 ? .58 : .42);
    }

    private static string LanguageName(string code, string localLanguageCode)
    {
        if (!localLanguageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return code;
        return NormalizeLanguageCode(code).ToLowerInvariant() switch
        {
            "zh-hans" => "简体中文",
            "zh-hant" => "繁体中文",
            "en" => "英语",
            "es" => "西班牙语",
            "fr" => "法语",
            "de" => "德语",
            "pt" or "pt-br" => "葡萄牙语",
            "it" => "意大利语",
            "ru" => "俄语",
            "ar" => "阿拉伯语",
            "hi" => "印地语",
            "ja" => "日语",
            "ko" => "韩语",
            "tr" => "土耳其语",
            _ => code
        };
    }

    private static string NormalizeLanguageCode(string value)
    {
        var trimmed = value.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "zh-cn" or "zh-sg" or "zh-hans" => "zh-Hans",
            "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" => "zh-Hant",
            _ => trimmed
        };
    }

    private static bool LanguageMatches(string left, string right)
    {
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) return true;
        var leftBase = left.Split('-', 2)[0];
        var rightBase = right.Split('-', 2)[0];
        return leftBase.Equals(rightBase, StringComparison.OrdinalIgnoreCase) &&
               !leftBase.Equals("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex("^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCodeRegex();

    private sealed record TranslationSource(
        string Id,
        string Text,
        string Direction,
        string TargetCode,
        string TargetName,
        string SourceHash);
}
