using StardewAiAssistant.Models;

namespace StardewAiAssistant.Services;

public sealed class AnswerService
{
    private static readonly Dictionary<string, string[]> NpcAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Abigail"] = new[] { "阿比盖尔", "Abigail" },
        ["Alex"] = new[] { "亚历克斯", "Alex" },
        ["Caroline"] = new[] { "卡洛琳", "Caroline" },
        ["Clint"] = new[] { "克林特", "Clint" },
        ["Demetrius"] = new[] { "德米特里厄斯", "Demetrius" },
        ["Elliott"] = new[] { "艾利欧特", "Elliott" },
        ["Emily"] = new[] { "艾米丽", "Emily" },
        ["Evelyn"] = new[] { "艾芙琳", "Evelyn" },
        ["George"] = new[] { "乔治", "George" },
        ["Gus"] = new[] { "格斯", "Gus" },
        ["Haley"] = new[] { "海莉", "Haley" },
        ["Harvey"] = new[] { "哈维", "Harvey" },
        ["Jas"] = new[] { "贾斯", "Jas" },
        ["Jodi"] = new[] { "乔迪", "Jodi" },
        ["Kent"] = new[] { "肯特", "Kent" },
        ["Leah"] = new[] { "莉亚", "Leah" },
        ["Lewis"] = new[] { "刘易斯", "Lewis" },
        ["Linus"] = new[] { "莱纳斯", "Linus" },
        ["Marnie"] = new[] { "玛妮", "Marnie" },
        ["Maru"] = new[] { "玛鲁", "Maru" },
        ["Pam"] = new[] { "潘姆", "Pam" },
        ["Penny"] = new[] { "潘妮", "Penny" },
        ["Pierre"] = new[] { "皮埃尔", "Pierre" },
        ["Robin"] = new[] { "罗宾", "Robin" },
        ["Sam"] = new[] { "山姆", "Sam" },
        ["Sandy"] = new[] { "桑迪", "Sandy" },
        ["Sebastian"] = new[] { "塞巴斯蒂安", "Sebastian" },
        ["Shane"] = new[] { "谢恩", "Shane" },
        ["Vincent"] = new[] { "文森特", "Vincent" },
        ["Willy"] = new[] { "威利", "Willy" },
        ["Wizard"] = new[] { "法师", "巫师", "Wizard" }
    };

    private readonly ModConfig _config;
    private readonly AiClient _aiClient;
    private readonly WikiKnowledgeService _knowledgeService;
    private readonly NpcLocationService _npcLocationService;

    public AnswerService(ModConfig config, AiClient aiClient, WikiKnowledgeService knowledgeService, NpcLocationService npcLocationService)
    {
        _config = config;
        _aiClient = aiClient;
        _knowledgeService = knowledgeService;
        _npcLocationService = npcLocationService;
    }

    public async Task<string> AnswerAsync(string question, GameSnapshot snapshot, CancellationToken cancellationToken)
    {
        var expandedQuestion = ExpandQuestion(question);
        var knowledge = (await _knowledgeService.SearchAsync(expandedQuestion, snapshot, _config.MaxKnowledgeEntries, cancellationToken)).ToList();
        await AddExactNpcPageIfNeededAsync(question, knowledge, cancellationToken);
        var npcLocation = TryFindNpcLocation(question, snapshot, knowledge);

        if (_config.PreferLocalAnswer && !string.IsNullOrWhiteSpace(npcLocation.DirectAnswer))
            return npcLocation.DirectAnswer;

        var systemPrompt = BuildSystemPrompt(snapshot, knowledge, npcLocation.Context);
        var messages = new List<ChatMessage>
        {
            new("user", question)
        };

        return await _aiClient.CompleteAsync(systemPrompt, messages, cancellationToken);
    }

    private async Task AddExactNpcPageIfNeededAsync(string question, List<KnowledgeEntry> knowledge, CancellationToken cancellationToken)
    {
        var npcName = DetectNpc(question);
        if (npcName is null)
            return;

        foreach (var title in NpcAliases[npcName])
        {
            var page = await _knowledgeService.GetPageAsync(title, cancellationToken);
            if (page is null)
                continue;

            if (knowledge.All(entry => !entry.Title.Equals(page.Title, StringComparison.OrdinalIgnoreCase)))
                knowledge.Insert(0, page);

            return;
        }
    }

    private NpcLocationResult TryFindNpcLocation(string question, GameSnapshot snapshot, IReadOnlyList<KnowledgeEntry> knowledge)
    {
        if (!AsksLocation(question))
            return new NpcLocationResult("", "");

        var npcName = DetectNpc(question);
        if (npcName is null)
            return new NpcLocationResult("", "玩家在问人物位置，但没有识别出具体 NPC 名称。");

        var liveLocation = _npcLocationService.TryGetCurrentLocation(npcName);
        if (!string.IsNullOrWhiteSpace(liveLocation))
            return new NpcLocationResult($"{GetPreferredAlias(npcName)}现在在：{liveLocation}。", $"游戏当前 NPC 实例位置：{npcName}={liveLocation}");

        var relevantFacts = knowledge
            .Where(entry => ContainsAny(entry.Title, NpcAliases[npcName]) || ContainsAny(entry.Content, NpcAliases[npcName]))
            .Select(entry => $"{entry.Title}: {entry.Content}")
            .ToList();

        var wikiFacts = relevantFacts.Count == 0
            ? "在线 wiki 没有返回该 NPC 的明确日程页面。"
            : string.Join("\n", relevantFacts);

        return new NpcLocationResult(
            "",
            $"玩家在问 {GetPreferredAlias(npcName)} 的位置。请根据当前游戏状态和在线 wiki 日程判断。\n" +
            $"当前条件：季节={snapshot.Season}，天气={snapshot.Weather}，时间={snapshot.TimeOfDay}，星期={snapshot.DayOfWeek}，世界进度={snapshot.WorldProgress}。\n" +
            $"在线 wiki 相关内容：\n{wikiFacts}"
        );
    }

    private static bool AsksLocation(string question)
    {
        return question.Contains("哪里", StringComparison.OrdinalIgnoreCase)
            || question.Contains("在哪", StringComparison.OrdinalIgnoreCase)
            || question.Contains("位置", StringComparison.OrdinalIgnoreCase)
            || question.Contains("where", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DetectNpc(string question)
    {
        foreach (var (name, aliases) in NpcAliases)
        {
            if (aliases.Any(alias => question.Contains(alias, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return null;
    }

    private static string ExpandQuestion(string question)
    {
        var npcName = DetectNpc(question);
        if (npcName is null)
            return question;

        return question + " " + npcName + " " + string.Join(" ", NpcAliases[npcName]) + " 日程 位置";
    }

    private static string GetPreferredAlias(string npcName)
    {
        return NpcAliases.TryGetValue(npcName, out var aliases) ? aliases[0] : npcName;
    }

    private static bool ContainsAny(string value, IEnumerable<string> needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSystemPrompt(GameSnapshot snapshot, IReadOnlyList<KnowledgeEntry> knowledge, string npcLocationContext)
    {
        var facts = knowledge.Count == 0
            ? "在线 wiki 没有返回相关页面。"
            : string.Join("\n", knowledge.Select(entry => $"- {entry.Title}: {entry.Content}"));

        return
            "你是《星露谷物语》的游戏内中文问答助手。你必须优先使用当前游戏状态、NPC 位置上下文和在线 wiki 查询结果回答。\n" +
            "回答要短、明确、适合玩家在游戏中快速阅读。不要编造 wiki 或当前状态无法推出的信息。\n" +
            "当玩家问 NPC 在哪里时，必须结合季节、天气、星期、时间、地图解锁状态和在线 wiki 日程判断。\n" +
            "如果在线 wiki 没有返回足够日程数据，请明确说明缺少哪类信息，不要让玩家自己去查网页。\n\n" +
            snapshot.ToPromptContext() + "\n\n" +
            "NPC 位置上下文：\n" +
            (string.IsNullOrWhiteSpace(npcLocationContext) ? "无额外 NPC 位置上下文。" : npcLocationContext) + "\n\n" +
            "在线 wiki 查询结果：\n" +
            facts;
    }

    private sealed record NpcLocationResult(string DirectAnswer, string Context);
}
