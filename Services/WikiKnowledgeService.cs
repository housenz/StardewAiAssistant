using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using StardewAiAssistant.Models;
using StardewModdingAPI;

namespace StardewAiAssistant.Services;

public sealed class WikiKnowledgeService
{
    private const string ApiUrl = "https://wiki.biligame.com/stardewvalley/api.php";
    private const int MaxStoredPageChars = 60000;
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex RedirectRegex = new(@"^重定向到：\s*(?<title>.+)$", RegexOptions.Compiled);
    private readonly IMonitor _monitor;
    private readonly Dictionary<string, KnowledgeEntry> _pageCache = new(StringComparer.OrdinalIgnoreCase);

    public WikiKnowledgeService(IMonitor monitor)
    {
        _monitor = monitor;
    }

    public async Task<IReadOnlyList<KnowledgeEntry>> SearchAsync(string question, GameSnapshot snapshot, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question))
            return Array.Empty<KnowledgeEntry>();

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StardewAiAssistant/0.1");

            var titles = await SearchTitlesAsync(client, BuildSearchQuery(question, snapshot), Math.Clamp(limit, 1, 8), cancellationToken);
            var entries = new List<KnowledgeEntry>();
            foreach (var title in titles)
            {
                if (_pageCache.TryGetValue(title, out var cached))
                {
                    entries.Add(cached);
                    continue;
                }

                var entry = await FetchPageAsync(client, title, cancellationToken);
                if (entry is null)
                    continue;

                _pageCache[title] = entry;
                entries.Add(entry);
            }

            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _monitor.Log($"Online wiki search failed: {ex.Message}", LogLevel.Warn);
            return Array.Empty<KnowledgeEntry>();
        }
    }

    public async Task<KnowledgeEntry?> GetPageAsync(string title, CancellationToken cancellationToken)
    {
        var result = await GetPageWithMetadataAsync(title, cancellationToken);
        return result?.Entry;
    }

    public async Task<WikiPageFetchResult?> GetPageWithMetadataAsync(string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        if (_pageCache.TryGetValue(title, out var cached))
            return new WikiPageFetchResult(cached, true);

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StardewAiAssistant/0.1");

            var entry = await FetchPageAsync(client, title, cancellationToken);
            if (entry is not null)
                _pageCache[title] = entry;

            return entry is null ? null : new WikiPageFetchResult(entry, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _monitor.Log($"Online wiki page fetch failed for '{title}': {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> SearchTitlesOnlyAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StardewAiAssistant/0.1");
            return await SearchTitlesAsync(client, query, Math.Clamp(limit, 1, 20), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _monitor.Log($"Online wiki title search failed for '{query}': {ex.Message}", LogLevel.Warn);
            return Array.Empty<string>();
        }
    }

    public async Task<WikiPagePreview?> GetPagePreviewAsync(string title, int maxChars, CancellationToken cancellationToken)
    {
        var result = await GetPageWithMetadataAsync(title, cancellationToken);
        if (result?.Entry is null)
            return null;

        var content = result.Entry.Content.Length <= maxChars
            ? result.Entry.Content
            : result.Entry.Content[..maxChars] + $"...[preview truncated {maxChars}/{result.Entry.Content.Length} chars]";

        return new WikiPagePreview(result.Entry.Title, content, result.FromCache);
    }

    private static string BuildSearchQuery(string question, GameSnapshot snapshot)
    {
        var query = question;
        if (question.Contains("哪里") || question.Contains("在哪") || question.Contains("位置") || question.Contains("where", StringComparison.OrdinalIgnoreCase))
            query += $" 日程 {snapshot.Season} {snapshot.Weather}";

        return query;
    }

    private static async Task<IReadOnlyList<string>> SearchTitlesAsync(HttpClient client, string query, int limit, CancellationToken cancellationToken)
    {
        var url = $"{ApiUrl}?action=query&format=json&list=search&srlimit={limit}&srsearch={Uri.EscapeDataString(query)}";
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("query", out var queryElement) ||
            !queryElement.TryGetProperty("search", out var searchElement))
            return Array.Empty<string>();

        return searchElement
            .EnumerateArray()
            .Select(item => item.TryGetProperty("title", out var title) ? title.GetString() : null)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<KnowledgeEntry?> FetchPageAsync(HttpClient client, string title, CancellationToken cancellationToken, int redirectDepth = 0)
    {
        var url = $"{ApiUrl}?action=parse&format=json&prop=text&disabletoc=1&disableeditsection=1&page={Uri.EscapeDataString(title)}";
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("parse", out var parseElement) ||
            !parseElement.TryGetProperty("text", out var textElement) ||
            !textElement.TryGetProperty("*", out var htmlElement))
            return null;

        var strippedText = StripHtml(htmlElement.GetString() ?? "");
        var redirectTarget = TryGetRedirectTarget(strippedText);
        if (!string.IsNullOrWhiteSpace(redirectTarget) && redirectDepth < 3)
        {
            _monitor.Log($"Following wiki redirect: {title} -> {redirectTarget}", LogLevel.Trace);
            var redirected = await FetchPageAsync(client, redirectTarget, cancellationToken, redirectDepth + 1);
            if (redirected is not null)
            {
                _pageCache[title] = redirected;
                return redirected;
            }
        }

        var text = ExtractUsefulText(title, strippedText);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return new KnowledgeEntry
        {
            Id = "online_" + title,
            Title = title,
            Keywords = BuildKeywords(title),
            Conditions = new KnowledgeConditions(),
            Content = Truncate(text, MaxStoredPageChars)
        };
    }

    private static string TryGetRedirectTarget(string text)
    {
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? text.Trim();
        var match = RedirectRegex.Match(firstLine);
        return match.Success ? match.Groups["title"].Value.Trim() : "";
    }

    private static string ExtractUsefulText(string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        return $"{title} 页面全文：{Truncate(text, MaxStoredPageChars)}";
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + $"\n...[truncated {maxLength}/{value.Length} chars]";
    }

    private static string StripHtml(string html)
    {
        var noScript = Regex.Replace(html, "<script.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var noStyle = Regex.Replace(noScript, "<style.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        noStyle = Regex.Replace(noStyle, "<templatestyles.*?</templatestyles>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        noStyle = Regex.Replace(noStyle, "<h[1-6][^>]*>", "\n== ", RegexOptions.IgnoreCase);
        noStyle = Regex.Replace(noStyle, "</h[1-6]>", " ==\n", RegexOptions.IgnoreCase);
        noStyle = Regex.Replace(noStyle, "</tr>", "\n", RegexOptions.IgnoreCase);
        noStyle = Regex.Replace(noStyle, "</t[dh]>\\s*<t[dh][^>]*>", "：", RegexOptions.IgnoreCase);
        noStyle = Regex.Replace(noStyle, "<br\\s*/?>|</p>|</li>|</div>", "\n", RegexOptions.IgnoreCase);
        noStyle = Regex.Replace(noStyle, "<li[^>]*>", "- ", RegexOptions.IgnoreCase);

        var text = HtmlTagRegex.Replace(noStyle, " ");
        text = WebUtility.HtmlDecode(text);
        text = RemoveCssNoise(text);
        text = Regex.Replace(text, "[ \\t\\x0B\\f\\r]+", " ");
        text = Regex.Replace(text, "\\n\\s+", "\n");
        text = Regex.Replace(text, "\\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string RemoveCssNoise(string text)
    {
        var cleaned = Regex.Replace(text, @"(?s)(?:\.[A-Za-z0-9_-]+|#[A-Za-z0-9_-]+|[A-Za-z][A-Za-z0-9_-]*)\s*\{[^{}]*\}", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\b(?:display|margin|padding|border|background|color|font-size|line-height|width|height|overflow|cursor|user-select|text-decoration|transition|align-items|justify-content|flex|gap|scrollbar-width)\s*:\s*[^;。；\n]+[;；]?", " ");
        return Regex.Replace(cleaned, "[ \\t\\x0B\\f\\r]+", " ");
    }

    private static string[] BuildKeywords(string title)
    {
        return title
            .Split(new[] { ' ', '/', '\\', '（', '）', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Append(title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed record WikiPageFetchResult(KnowledgeEntry Entry, bool FromCache);
public sealed record WikiPagePreview(string Title, string Preview, bool FromCache);
