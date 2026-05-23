namespace StardewAiAssistant.Models;

public sealed class KnowledgeEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string[] Keywords { get; set; } = Array.Empty<string>();
    public KnowledgeConditions Conditions { get; set; } = new();
    public string Content { get; set; } = "";
}

public sealed class KnowledgeConditions
{
    public string? Season { get; set; }
    public string? Weather { get; set; }
    public int? MinTime { get; set; }
    public int? MaxTime { get; set; }
    public string? Location { get; set; }
}
