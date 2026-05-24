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

    private sealed record IndexedTitle(string Title, string Normalized);
}

public sealed record WikiTitleMatch(string Candidate, string Title, string MatchType);
