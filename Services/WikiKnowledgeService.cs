using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using StardewAiAssistant.Models;
using StardewModdingAPI;

namespace StardewAiAssistant.Services;

public sealed class WikiKnowledgeService
{
    private const string ApiUrl = "https://wiki.biligame.com/stardewvalley/api.php";
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);
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
        if (string.IsNullOrWhiteSpace(title))
            return null;

        if (_pageCache.TryGetValue(title, out var cached))
            return cached;

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

            return entry;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _monitor.Log($"Online wiki page fetch failed for '{title}': {ex.Message}", LogLevel.Warn);
            return null;
        }
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

    private static async Task<KnowledgeEntry?> FetchPageAsync(HttpClient client, string title, CancellationToken cancellationToken)
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

        var text = ExtractUsefulText(title, StripHtml(htmlElement.GetString() ?? ""));
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return new KnowledgeEntry
        {
            Id = "online_" + title,
            Title = title,
            Keywords = BuildKeywords(title),
            Conditions = new KnowledgeConditions(),
            Content = text.Length > 12000 ? text[..12000] : text
        };
    }

    private static string ExtractUsefulText(string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var scheduleStart = text.IndexOf("日程", StringComparison.Ordinal);
        if (scheduleStart < 0)
            return text.Length > 5000 ? text[..5000] : text;

        var sectionEndCandidates = new[]
        {
            text.IndexOf("人际关系", scheduleStart, StringComparison.Ordinal),
            text.IndexOf("送礼", scheduleStart, StringComparison.Ordinal),
            text.IndexOf("观影", scheduleStart, StringComparison.Ordinal),
            text.IndexOf("好感度事件", scheduleStart, StringComparison.Ordinal)
        }.Where(index => index > scheduleStart).ToList();

        var sectionEnd = sectionEndCandidates.Count == 0 ? Math.Min(text.Length, scheduleStart + 12000) : sectionEndCandidates.Min();
        var intro = text[..Math.Min(scheduleStart, 800)].Trim();
        var schedule = text[scheduleStart..Math.Min(text.Length, sectionEnd)].Trim();

        return $"{title} 页面摘要：{intro}\n\n{title} 日程章节：{schedule}";
    }

    private static string StripHtml(string html)
    {
        var noScript = Regex.Replace(html, "<script.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var noStyle = Regex.Replace(noScript, "<style.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var text = HtmlTagRegex.Replace(noStyle, " ");
        text = WebUtility.HtmlDecode(text);
        return WhitespaceRegex.Replace(text, " ").Trim();
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
