using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WAFlow.Core.Services;

/// <summary>
/// Builds the lightweight, readable conversation-body projection used by the
/// desktop Inbox. The original HTML stays untouched for the dedicated original
/// email viewer, where its links and complete layout remain available.
/// </summary>
public static partial class EmailMessagePresentationPolicy
{
    private const int MaximumConversationCharacters = 16_000;

    public static string FormatConversationBody(string? plainText, string? htmlBody)
    {
        var fromHtml = string.IsNullOrWhiteSpace(htmlBody)
            ? ""
            : NormalizeLines(HtmlToReadableText(htmlBody));
        var fromPlain = NormalizeLines(plainText ?? "");
        var body = fromHtml.Length > 0 ? fromHtml : fromPlain;

        if (body.Length == 0 && !string.IsNullOrWhiteSpace(htmlBody))
            return "此邮件主要包含图片或富格式内容，请查看原邮件。";
        if (body.Length <= MaximumConversationCharacters) return body;

        var truncated = body[..MaximumConversationCharacters];
        var boundary = truncated.LastIndexOfAny(['\n', '。', '.', '!', '?', '！', '？']);
        if (boundary >= MaximumConversationCharacters * 3 / 4)
            truncated = truncated[..(boundary + 1)];
        return truncated.TrimEnd() + "\n\n内容较长，完整内容请查看原邮件。";
    }

    private static string HtmlToReadableText(string html)
    {
        var text = CommentsRegex().Replace(html, " ");
        text = NonContentElementRegex().Replace(text, " ");
        text = HiddenElementRegex().Replace(text, " ");
        text = ImageRegex().Replace(text, " ");
        text = BreakRegex().Replace(text, "\n");
        text = ListItemStartRegex().Replace(text, "\n• ");
        text = TableCellEndRegex().Replace(text, "  ");
        text = BlockEndRegex().Replace(text, "\n");
        text = HtmlTagRegex().Replace(text, " ");
        return WebUtility.HtmlDecode(WebUtility.HtmlDecode(text));
    }

    private static string NormalizeLines(string value)
    {
        var normalized = (value ?? "")
            .Replace('\0', ' ')
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ')
            .Replace("\u200B", "")
            .Replace("\u200C", "")
            .Replace("\u200D", "")
            .Replace("\uFEFF", "");
        normalized = new string(normalized
            .Where(character => character == '\n' || character == '\t' || !char.IsControl(character))
            .ToArray());

        var output = new List<string>();
        var previousText = "";
        var blankPending = false;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = InlineWhitespaceRegex().Replace(rawLine, " ").Trim();
            if (line.Length == 0)
            {
                blankPending = output.Count > 0;
                continue;
            }
            if (IsLinkNoise(line)) continue;
            if (line.Equals(previousText, StringComparison.OrdinalIgnoreCase)) continue;
            if (blankPending) output.Add("");
            output.Add(line);
            previousText = line;
            blankPending = false;
        }

        return string.Join('\n', output).Trim();
    }

    private static bool IsLinkNoise(string line)
    {
        var candidate = line.Trim('(', ')', '[', ']', '<', '>', '{', '}', ' ', '\t');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return true;
        return TrackingParameterRegex().IsMatch(candidate)
               || UrlOnlyLineRegex().IsMatch(candidate);
    }

    [GeneratedRegex(@"<!--[\s\S]*?-->", RegexOptions.Compiled)]
    private static partial Regex CommentsRegex();

    [GeneratedRegex(@"<(script|style|head|svg|canvas|noscript)\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NonContentElementRegex();

    [GeneratedRegex(@"<(?<tag>[a-z][a-z0-9]*)\b[^>]*(?:display\s*:\s*none|visibility\s*:\s*hidden|mso-hide\s*:\s*all)[^>]*>[\s\S]*?</\k<tag>\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HiddenElementRegex();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ListItemStartRegex();

    [GeneratedRegex(@"</(?:td|th)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TableCellEndRegex();

    [GeneratedRegex(@"</(?:p|div|section|article|header|footer|h[1-6]|li|tr|table|blockquote)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.Compiled)]
    private static partial Regex InlineWhitespaceRegex();

    [GeneratedRegex(@"^(?:[?&]?)?(?:utm_[a-z0-9_]+|fbclid|gclid|mc_[a-z0-9_]+|ref|tracking(?:_id)?)\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TrackingParameterRegex();

    [GeneratedRegex(@"^[\p{P}\p{S}\s]*(?:https?://|www\.)\S+[\p{P}\p{S}\s]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlOnlyLineRegex();
}
