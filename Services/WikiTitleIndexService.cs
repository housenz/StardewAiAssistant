using System.Text.Json;
using StardewModdingAPI;

namespace StardewAiAssistant.Services;

public sealed class WikiTitleIndexService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<IndexedTitle> _titles = new();
    private readonly Dictionary<string, string> _exactTitles = new(StringComparer.OrdinalIgnoreCase);

    public WikiTitleIndexService(string modDirectoryPath, IMonitor monitor)
    {
        var path = Path.Combine(modDirectoryPath, "Data", "wiki-titles.json");
        try
        {
            if (!File.Exists(path))
            {
                monitor.Log($"Wiki title index not found: {path}", LogLevel.Warn);
                return;
            }

            var titles = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), JsonOptions) ?? new List<string>();
            foreach (var title in titles.Where(title => !string.IsNullOrWhiteSpace(title)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var normalized = Normalize(title);
                if (normalized.Length == 0)
                    continue;

                _titles.Add(new IndexedTitle(title, normalized));
                _exactTitles.TryAdd(normalized, title);
            }

            _titles.Sort((left, right) =>
            {
                var lengthCompare = right.Normalized.Length.CompareTo(left.Normalized.Length);
                return lengthCompare != 0 ? lengthCompare : string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
            });

            monitor.Log($"Loaded {_titles.Count} wiki titles from local keyword book.", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            monitor.Log($"Failed to load wiki title index: {ex.Message}", LogLevel.Warn);
        }
    }

    public WikiTitleMatch? Match(string candidate)
    {
        var normalizedCandidate = Normalize(candidate);
        if (normalizedCandidate.Length == 0)
            return null;

        if (_exactTitles.TryGetValue(normalizedCandidate, out var exactTitle))
            return new WikiTitleMatch(candidate, exactTitle, "normalized-exact");

        var titleInsideCandidate = _titles.FirstOrDefault(title =>
            title.Normalized.Length >= 2 &&
            normalizedCandidate.Contains(title.Normalized, StringComparison.OrdinalIgnoreCase));
        if (titleInsideCandidate is not null)
            return new WikiTitleMatch(candidate, titleInsideCandidate.Title, "query-contains-title");

        if (normalizedCandidate.Length >= 2)
        {
            var candidateInsideTitle = _titles.FirstOrDefault(title =>
                title.Normalized.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase));
            if (candidateInsideTitle is not null)
                return new WikiTitleMatch(candidate, candidateInsideTitle.Title, "title-contains-query");
        }

        return null;
    }

    public IReadOnlyList<WikiTitleCandidate> Search(string query, int limit)
    {
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
            return Array.Empty<WikiTitleCandidate>();

        var queryTokens = Tokenize(query)
            .Select(Normalize)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var candidates = new List<WikiTitleCandidate>();
        if (_exactTitles.TryGetValue(normalizedQuery, out var exactTitle))
            candidates.Add(new WikiTitleCandidate(exactTitle, "normalized-exact", 1000));

        foreach (var title in _titles)
        {
            if (title.Normalized.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                continue;

            if (title.Normalized.Length >= 2 && normalizedQuery.Contains(title.Normalized, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(new WikiTitleCandidate(title.Title, "query-contains-title", 900 + title.Normalized.Length));
                continue;
            }

            if (title.Normalized.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(new WikiTitleCandidate(title.Title, "title-contains-query", 700 + normalizedQuery.Length));
                continue;
            }

            var tokenHits = queryTokens.Count(token => title.Normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (tokenHits > 0)
            {
                var coverage = CalculateCoverage(normalizedQuery, title.Normalized, queryTokens);
                var lengthPenalty = Math.Min(Math.Abs(title.Normalized.Length - normalizedQuery.Length), 30);
                candidates.Add(new WikiTitleCandidate(title.Title, "title-contains-query-token", 300 + tokenHits * 150 + coverage * 4 - lengthPenalty));
            }
        }

        return candidates
            .GroupBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Title.Length)
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 30))
            .ToList();
    }

    public bool ContainsTitle(string title)
    {
        return _exactTitles.ContainsKey(Normalize(title));
    }

    private static string Normalize(string value)
    {
        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character) && !char.IsSymbol(character))
            .ToArray());
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        foreach (var token in value.Split(new[] { ' ', '\t', '\r', '\n', '，', '。', '！', '？', '、', ';', '；', ':', '：', '(', ')', '（', '）', '[', ']', '【', '】' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim();
            if (trimmed.Length > 0)
                yield return trimmed;
        }

        foreach (var match in System.Text.RegularExpressions.Regex.Matches(value, "[A-Za-z0-9]+").Cast<System.Text.RegularExpressions.Match>())
        {
            if (match.Value.Length >= 2)
                yield return match.Value;
        }

        var normalized = Normalize(value);
        for (var length = 2; length <= Math.Min(4, normalized.Length); length++)
        {
            for (var start = 0; start <= normalized.Length - length; start++)
                yield return normalized.Substring(start, length);
        }
    }

    private static int CalculateCoverage(string query, string title, IReadOnlyList<string> queryTokens)
    {
        var matchedChars = new HashSet<int>();
        foreach (var token in queryTokens)
        {
            if (token.Length == 0)
                continue;

            var index = query.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index < 0 || !title.Contains(token, StringComparison.OrdinalIgnoreCase))
                continue;

            for (var i = index; i < Math.Min(query.Length, index + token.Length); i++)
                matchedChars.Add(i);
        }

        return query.Length == 0 ? 0 : matchedChars.Count * 100 / query.Length;
    }

    private sealed record IndexedTitle(string Title, string Normalized);
}

public sealed record WikiTitleMatch(string Candidate, string Title, string MatchType);
public sealed record WikiTitleCandidate(string Title, string MatchType, int Score);
