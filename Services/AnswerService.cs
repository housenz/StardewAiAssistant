using System.Text.Json;
using System.Text.RegularExpressions;
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
        ["Emily"] = new[] { "艾米丽", "艾米莉", "Emily" },
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

    private static readonly string[] ContextOnlyKeywords =
    {
        "我在哪", "我的位置", "当前位置", "明天天气", "今天运气", "我的运气", "多少钱", "金币",
        "我的技能", "技能等级", "职业", "好感", "背包", "农场作物", "星之果实", "任务"
    };

    private static readonly string[] WikiIntentKeywords =
    {
        "哪里获得", "怎么获得", "如何获得", "怎么做", "配方", "喜欢什么", "讨厌什么",
        "在哪", "在哪里", "日程", "位置", "什么时候", "鱼", "作物", "料理", "任务", "事件"
    };

    private static readonly Dictionary<string, string[]> WikiEntityAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["旅行货车"] = new[] { "旅行货车", "猪车", "旅行商人" },
        ["星之果实"] = new[] { "星之果实", "星星果实" },
        ["社区中心"] = new[] { "社区中心", "收集包" },
        ["姜岛"] = new[] { "姜岛", "姜饼岛" },
        ["沙漠"] = new[] { "沙漠", "卡利科沙漠" },
        ["矿井"] = new[] { "矿井", "矿洞" },
        ["骷髅洞穴"] = new[] { "骷髅洞穴", "骷髅洞", "沙漠矿洞" },
        ["下水道"] = new[] { "下水道", "科罗布斯" },
        ["温室"] = new[] { "温室" }
    };

    private static readonly string[] InvalidStandaloneWikiTerms =
    {
        "时间", "到达时间", "什么时候", "几点", "何时", "哪天", "今天", "明天", "星期",
        "位置", "地点", "在哪里", "在哪", "哪里", "怎么", "如何", "方法", "条件", "出现", "开放时间"
    };

    private readonly ModConfig _config;
    private readonly AiClient _aiClient;
    private readonly WikiKnowledgeService _knowledgeService;
    private readonly WikiTitleIndexService _wikiTitleIndexService;
    private readonly NpcLocationService _npcLocationService;
    private readonly AgentDebugLogger _debugLogger;

    public AnswerService(ModConfig config, AiClient aiClient, WikiKnowledgeService knowledgeService, WikiTitleIndexService wikiTitleIndexService, NpcLocationService npcLocationService, AgentDebugLogger debugLogger)
    {
        _config = config;
        _aiClient = aiClient;
        _knowledgeService = knowledgeService;
        _wikiTitleIndexService = wikiTitleIndexService;
        _npcLocationService = npcLocationService;
        _debugLogger = debugLogger;
    }

    public async Task<string> AnswerAsync(string question, GameSnapshot snapshot, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        _debugLogger.Separator("New AI question");
        _debugLogger.Log("question", question);
        _debugLogger.Log("snapshot", TrimForLog(snapshot.ToPromptContext(), 3000));

        var conversationMessages = BuildConversationMessages(history, question);
        var historyContext = BuildHistoryContext(history);
        var npcLocation = TryFindLiveNpcLocation(question);
        _debugLogger.Log("npc-location", string.IsNullOrWhiteSpace(npcLocation.Context) ? "No NPC location context." : npcLocation.Context);

        var supervisorDecision = await SupervisorAgentDecideAsync(question, snapshot, historyContext, npcLocation.Context, cancellationToken);
        _debugLogger.Log("supervisor-result", FormatSupervisorDecision(supervisorDecision));

        var plan = await PlanAgentCreatePlanAsync(question, snapshot, historyContext, npcLocation.Context, supervisorDecision, cancellationToken);
        _debugLogger.Log("plan-result", FormatPlan(plan));

        var execution = await ExecuteAgentRunPlanAsync(question, snapshot, plan, cancellationToken);
        _debugLogger.Log("execute-result", FormatExecution(execution));

        if (plan.NeedsWiki && (!execution.IsSufficient || execution.Knowledge.Count == 0))
        {
            _debugLogger.Log("replan-trigger", "Execution result is insufficient or no wiki knowledge was found.");
            var replan = await PlanAgentReplanAsync(question, snapshot, plan, execution, cancellationToken);
            _debugLogger.Log("replan-result", FormatPlan(replan));
            if (replan.Queries.Count > 0)
            {
                var reexecution = await ExecuteAgentRunPlanAsync(question, snapshot, replan, cancellationToken);
                plan = MergePlans(plan, replan);
                execution = MergeExecutions(execution, reexecution);
                _debugLogger.Log("reexecute-result", FormatExecution(reexecution));
            }
        }

        var systemPrompt = BuildFinalSupervisorPrompt(snapshot, supervisorDecision, plan, execution, npcLocation.Context);
        _debugLogger.Log("prompt-copy", $"Copied {execution.Knowledge.Count} wiki entries into final-supervisor prompt.");
        _debugLogger.Log("final-supervisor-prompt", TrimForLog(systemPrompt, 30000));
        var finalAnswer = await _aiClient.CompleteAsync(systemPrompt, conversationMessages, cancellationToken);
        _debugLogger.Log("final-answer-draft", finalAnswer);

        var answerReview = await AnswerCheckerAgentReviewAsync(question, snapshot, finalAnswer, cancellationToken);
        _debugLogger.Log("answer-checker", $"isCorrect={answerReview.IsCorrect}; issues={string.Join("；", answerReview.Issues)}");
        if (!answerReview.IsCorrect && !string.IsNullOrWhiteSpace(answerReview.CorrectedAnswer))
        {
            _debugLogger.Log("final-answer-corrected", answerReview.CorrectedAnswer);
            return answerReview.CorrectedAnswer;
        }

        _debugLogger.Log("final-answer", finalAnswer);
        return finalAnswer;
    }

    private async Task<SupervisorDecision> SupervisorAgentDecideAsync(
        string question,
        GameSnapshot snapshot,
        string historyContext,
        string npcLocationContext,
        CancellationToken cancellationToken)
    {
        var deterministic = BuildDeterministicSupervisorDecision(question);
        var systemPrompt =
            "你是《星露谷物语》游戏内问答系统的 supervisor-agent。\n" +
            "你的任务是判断用户问题是否能直接由当前游戏状态回答，还是必须查询 wiki。\n" +
            "注意：如果要查 wiki，wiki 只支持关键词/页面名搜索，不支持语义搜索。\n" +
            "注意：明天天气字段里的 sunny/Sun 是天气，不是 Sunday/星期日；明天星期几只能看“明天星期”字段。\n" +
            "只返回 JSON，不要返回 Markdown，不要解释。\n" +
            "JSON 格式：{\"needsWiki\":true,\"canAnswerFromContext\":false,\"reason\":\"...\",\"informationNeeded\":[\"...\"]}";

        var userPrompt =
            snapshot.ToPromptContext() + "\n\n" +
            "最近对话：\n" + historyContext + "\n\n" +
            "NPC 实时位置上下文：\n" + (string.IsNullOrWhiteSpace(npcLocationContext) ? "无" : npcLocationContext) + "\n\n" +
            "规则兜底判断：\n" +
            $"- needsWiki={deterministic.NeedsWiki}\n" +
            $"- reason={deterministic.Reason}\n\n" +
            "用户问题：\n" + question;

        _debugLogger.Log("supervisor-agent", "Calling AI supervisor-agent.");
        _debugLogger.Log("supervisor-prompt", TrimForLog(systemPrompt + "\n\n" + userPrompt, 6000));
        var response = await _aiClient.CompleteAsync(systemPrompt, SingleUserMessage(userPrompt), cancellationToken);
        _debugLogger.Log("supervisor-response", TrimForLog(response, 4000));
        var parsed = ParseSupervisorDecision(response);
        if (parsed is null)
        {
            _debugLogger.Log("supervisor-parse", "AI response was not valid JSON. Using deterministic fallback.");
            return deterministic;
        }

        if (!deterministic.NeedsWiki)
            return parsed;

        return parsed with
        {
            NeedsWiki = true,
            CanAnswerFromContext = parsed.CanAnswerFromContext && !deterministic.NeedsWiki,
            Reason = parsed.Reason + "；规则校验认为该问题仍需要 wiki/工具查询：" + deterministic.Reason,
            InformationNeeded = parsed.InformationNeeded.Concat(deterministic.InformationNeeded).Distinct().ToList()
        };
    }

    private async Task<AgentPlan> PlanAgentCreatePlanAsync(
        string question,
        GameSnapshot snapshot,
        string historyContext,
        string npcLocationContext,
        SupervisorDecision decision,
        CancellationToken cancellationToken)
    {
        var fallbackQueries = BuildFallbackQueries(question, decision.NeedsWiki).ToList();
        var systemPrompt =
            "你是《星露谷物语》游戏内问答系统的 plan-agent。\n" +
            "你必须为 execute-agent 制定执行计划。\n" +
            "如果 supervisor 判断需要查 wiki，你必须把自然语言问题拆成 biligame wiki 能搜索的短关键词或精确页面名。\n" +
            "wiki 不支持语义搜索，所以不要把整句问题当成查询词。\n" +
            "优先使用：NPC 中文名、物品名、任务名、地点名、日程、送礼、配方、鱼、作物等页面关键词。\n" +
            "不要使用“时间”“到达时间”“什么时候”“几点”“在哪里”这类意图词作为查询词；例如用户问“猪车什么时候来”，应查询“旅行货车”，不要查询“旅行货车 时间”。\n" +
            "如果用户没说出有效关键词，你需要根据意图推理相关关键词。\n" +
            "只返回 JSON，不要返回 Markdown，不要解释。\n" +
            "JSON 格式：{\"needsWiki\":true,\"steps\":[\"...\"],\"queries\":[{\"text\":\"海莉\",\"exactPage\":true,\"reason\":\"...\"},{\"text\":\"海莉 日程\",\"exactPage\":false,\"reason\":\"...\"}]}";

        var userPrompt =
            snapshot.ToPromptContext() + "\n\n" +
            "最近对话：\n" + historyContext + "\n\n" +
            "NPC 实时位置上下文：\n" + (string.IsNullOrWhiteSpace(npcLocationContext) ? "无" : npcLocationContext) + "\n\n" +
            "supervisor-agent 判断：\n" +
            $"- needsWiki={decision.NeedsWiki}\n" +
            $"- canAnswerFromContext={decision.CanAnswerFromContext}\n" +
            $"- reason={decision.Reason}\n" +
            $"- informationNeeded={string.Join("；", decision.InformationNeeded)}\n\n" +
            "代码兜底候选关键词：\n" +
            string.Join("\n", fallbackQueries.Select(query => $"- {query.Text} ({(query.ExactPage ? "页面" : "关键词")})：{query.Reason}")) + "\n\n" +
            "用户问题：\n" + question;

        _debugLogger.Log("plan-agent", "Calling AI plan-agent.");
        _debugLogger.Log("plan-fallback-queries", FormatQueries(fallbackQueries));
        _debugLogger.Log("plan-prompt", TrimForLog(systemPrompt + "\n\n" + userPrompt, 6000));
        var response = await _aiClient.CompleteAsync(systemPrompt, SingleUserMessage(userPrompt), cancellationToken);
        _debugLogger.Log("plan-response", TrimForLog(response, 4000));
        var plan = ParseAgentPlan(response, decision, fallbackQueries);
        var normalizedPlan = NormalizePlan(plan, decision.NeedsWiki, fallbackQueries);
        _debugLogger.Log("plan-normalized", FormatPlan(normalizedPlan));
        return normalizedPlan;
    }

    private async Task<AgentPlan> PlanAgentReplanAsync(
        string question,
        GameSnapshot snapshot,
        AgentPlan previousPlan,
        ExecutionResult execution,
        CancellationToken cancellationToken)
    {
        var fallbackQueries = BuildFallbackQueries(question, true)
            .Where(query => previousPlan.Queries.All(existing => !existing.Text.Equals(query.Text, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var systemPrompt =
            "你是《星露谷物语》游戏内问答系统的 plan-agent，现在执行 Replan。\n" +
            "上一次查询结果不足。你必须根据失败日志和已获得的 wiki 摘要，重新给出更好的 biligame wiki 关键词。\n" +
            "wiki 只支持关键词/页面名搜索，不支持语义搜索。请使用更短、更像页面标题的关键词。\n" +
            "不要重复已经查过的关键词。\n" +
            "不要使用“时间”“到达时间”“什么时候”“几点”“在哪里”这类意图词作为查询词；应该回退到物品、NPC、地点、商店或机制的页面名。\n" +
            "只返回 JSON，不要返回 Markdown，不要解释。\n" +
            "JSON 格式：{\"needsWiki\":true,\"steps\":[\"...\"],\"queries\":[{\"text\":\"关键词\",\"exactPage\":false,\"reason\":\"...\"}]}";

        var userPrompt =
            snapshot.ToPromptContext() + "\n\n" +
            "用户问题：\n" + question + "\n\n" +
            "上一次计划：\n" + FormatPlan(previousPlan) + "\n\n" +
            "execute-agent 结果：\n" + FormatExecution(execution) + "\n\n" +
            "代码兜底候选关键词：\n" +
            string.Join("\n", fallbackQueries.Select(query => $"- {query.Text} ({(query.ExactPage ? "页面" : "关键词")})：{query.Reason}"));

        _debugLogger.Log("replan-agent", "Calling AI plan-agent for replan.");
        _debugLogger.Log("replan-fallback-queries", FormatQueries(fallbackQueries));
        _debugLogger.Log("replan-prompt", TrimForLog(systemPrompt + "\n\n" + userPrompt, 6000));
        var response = await _aiClient.CompleteAsync(systemPrompt, SingleUserMessage(userPrompt), cancellationToken);
        _debugLogger.Log("replan-response", TrimForLog(response, 4000));
        var plan = ParseAgentPlan(response, previousPlan.Decision, fallbackQueries);
        var normalizedPlan = NormalizePlan(plan, true, fallbackQueries);
        _debugLogger.Log("replan-normalized", FormatPlan(normalizedPlan));
        return normalizedPlan;
    }

    private async Task<ExecutionResult> ExecuteAgentRunPlanAsync(string question, GameSnapshot snapshot, AgentPlan plan, CancellationToken cancellationToken)
    {
        _debugLogger.Log("execute-agent", "Executing wiki tool plan.");
        _debugLogger.Log("execute-plan", FormatPlan(plan));

        if (!plan.NeedsWiki || plan.Queries.Count == 0)
        {
            return new ExecutionResult(
                Array.Empty<KnowledgeEntry>(),
                new[] { "execute-agent：plan-agent 判断无需 wiki 查询。" },
                "未执行 wiki 工具调用。",
                IsSufficient: true
            );
        }

        var knowledge = new List<KnowledgeEntry>();
        var logs = new List<string>();
        foreach (var query in plan.Queries.Take(Math.Clamp(_config.MaxKnowledgeEntries + 2, 3, 10)))
        {
            _debugLogger.Log("wiki-query", $"{query.Text} | exactPage={query.ExactPage} | reason={query.Reason}");
            if (query.ExactPage)
            {
                var page = await _knowledgeService.GetPageAsync(query.Text, cancellationToken);
                logs.Add(page is null
                    ? $"execute-agent：精确页面「{query.Text}」无结果。"
                    : $"execute-agent：已获取精确页面「{page.Title}」。");
                _debugLogger.Log("wiki-result", logs[^1]);
                if (page is not null)
                    knowledge.Add(page);
                continue;
            }

            var entries = await _knowledgeService.SearchAsync(query.Text, snapshot, Math.Min(3, Math.Max(1, _config.MaxKnowledgeEntries)), cancellationToken);
            logs.Add(entries.Count == 0
                ? $"execute-agent：关键词「{query.Text}」无结果。"
                : $"execute-agent：关键词「{query.Text}」返回 {entries.Count} 条。");
            _debugLogger.Log("wiki-result", logs[^1] + (entries.Count == 0 ? "" : " Titles: " + string.Join(", ", entries.Select(entry => entry.Title))));
            knowledge.AddRange(entries);
        }

        var distinctKnowledge = DeduplicateKnowledge(knowledge);
        _debugLogger.Log("wiki-knowledge", FormatKnowledge(distinctKnowledge, 50000));
        var executionReview = await ExecuteAgentReviewAsync(question, snapshot, plan, distinctKnowledge, logs, cancellationToken);
        _debugLogger.Log("execute-review", $"isSufficient={executionReview.IsSufficient}; summary={executionReview.Summary}; missing={string.Join("；", executionReview.MissingInfo)}");
        return new ExecutionResult(distinctKnowledge, logs, executionReview.Summary, executionReview.IsSufficient);
    }

    private async Task<ExecutionReview> ExecuteAgentReviewAsync(
        string question,
        GameSnapshot snapshot,
        AgentPlan plan,
        IReadOnlyList<KnowledgeEntry> knowledge,
        IReadOnlyList<string> logs,
        CancellationToken cancellationToken)
    {
        var systemPrompt =
            "你是《星露谷物语》游戏内问答系统的 execute-agent。\n" +
            "你已经执行了 plan-agent 的 wiki 查询计划。现在请判断工具结果是否足够支持最终回答。\n" +
            "你不能编造，只能根据当前游戏状态、查询日志和 wiki 摘要判断是否足够。\n" +
            "重要：明天天气字段里的 sunny/Sun 是天气，不是 Sunday/星期日；明天星期几只能看“明天星期”字段。\n" +
            "只返回 JSON，不要返回 Markdown，不要解释。\n" +
            "JSON 格式：{\"isSufficient\":true,\"summary\":\"...\",\"missingInfo\":[\"...\"]}";

        var userPrompt =
            snapshot.ToPromptContext() + "\n\n" +
            "用户问题：\n" + question + "\n\n" +
            "plan-agent 计划：\n" + FormatPlan(plan) + "\n\n" +
            "查询日志：\n" + string.Join("\n", logs.Select(log => "- " + log)) + "\n\n" +
            "wiki 摘要：\n" + FormatKnowledge(knowledge, 20000);

        _debugLogger.Log("execute-review-agent", "Calling AI execute-agent to review tool results.");
        _debugLogger.Log("prompt-copy", $"Copied {knowledge.Count} wiki entries into execute-review prompt.");
        _debugLogger.Log("execute-review-prompt", TrimForLog(systemPrompt + "\n\n" + userPrompt, 25000));
        var response = await _aiClient.CompleteAsync(systemPrompt, SingleUserMessage(userPrompt), cancellationToken);
        _debugLogger.Log("execute-review-response", TrimForLog(response, 4000));
        var parsed = ParseExecutionReview(response);
        if (parsed is not null)
            return parsed;

        _debugLogger.Log("execute-review-parse", "AI response was not valid JSON. Using knowledge count fallback.");
        return new ExecutionReview(knowledge.Count > 0, "execute-agent 未返回可解析 JSON，使用工具结果数量作为兜底判断。", Array.Empty<string>());
    }

    private async Task<AnswerReview> AnswerCheckerAgentReviewAsync(
        string question,
        GameSnapshot snapshot,
        string answer,
        CancellationToken cancellationToken)
    {
        var systemPrompt =
            "你是《星露谷物语》游戏内问答系统的 answer-checker-agent。\n" +
            "你的任务是检查最终答案是否与确定的游戏状态矛盾，尤其是星期、日期、天气、金钱、背包、技能等低级事实。\n" +
            "重要：TomorrowWeather/明天天气是天气字段，sunny/Sun 表示晴天，不是 Sunday/星期日。星期只以 TomorrowDayOfWeek/明天星期字段为准。\n" +
            "如果答案有事实错误，给出修正后的完整答案；如果没有错误，correctedAnswer 留空。\n" +
            "只返回 JSON，不要返回 Markdown，不要解释。\n" +
            "JSON 格式：{\"isCorrect\":true,\"issues\":[\"...\"],\"correctedAnswer\":\"...\"}";

        var userPrompt =
            snapshot.ToPromptContext() + "\n\n" +
            "用户问题：\n" + question + "\n\n" +
            "待检查答案：\n" + answer;

        _debugLogger.Log("answer-checker-agent", "Calling AI answer-checker-agent.");
        _debugLogger.Log("answer-checker-prompt", TrimForLog(systemPrompt + "\n\n" + userPrompt, 5000));
        var response = await _aiClient.CompleteAsync(systemPrompt, SingleUserMessage(userPrompt), cancellationToken);
        _debugLogger.Log("answer-checker-response", TrimForLog(response, 4000));

        var parsed = ParseAnswerReview(response);
        if (parsed is not null)
            return ApplyDeterministicAnswerChecks(snapshot, answer, parsed);

        _debugLogger.Log("answer-checker-parse", "AI response was not valid JSON. Using deterministic checks only.");
        return ApplyDeterministicAnswerChecks(snapshot, answer, new AnswerReview(true, Array.Empty<string>(), ""));
    }

    private static string BuildFinalSupervisorPrompt(
        GameSnapshot snapshot,
        SupervisorDecision decision,
        AgentPlan plan,
        ExecutionResult execution,
        string npcLocationContext)
    {
        return
            "你是《星露谷物语》游戏内问答系统的 supervisor-agent，负责给玩家最终答案。\n" +
            "这是一次真正的多 Agent 编排：supervisor-agent 已判断问题，plan-agent 已规划，execute-agent 已执行工具和评估结果，必要时 plan-agent 已 replan。\n" +
            "你必须综合当前游戏状态、NPC 实时位置、wiki 结果、Agent 日志和最近对话回答。\n" +
            "回答要中文、简洁、直接，适合游戏内阅读。\n" +
            "如果信息不足，明确说无法确定，并说明缺少哪类信息；不要让玩家自己去查网页。\n" +
            "涉及计算时，先根据背包、职业、品质、数量等信息逐步核算，再给结论。\n\n" +
            "重要：明天天气字段里的 sunny/Sun 是天气，不是 Sunday/星期日；明天星期几只能看“明天星期”字段。\n\n" +
            snapshot.ToPromptContext() + "\n\n" +
            "NPC 实时位置上下文：\n" +
            (string.IsNullOrWhiteSpace(npcLocationContext) ? "无" : npcLocationContext) + "\n\n" +
            "supervisor-agent 判断：\n" +
            $"- needsWiki={decision.NeedsWiki}\n" +
            $"- canAnswerFromContext={decision.CanAnswerFromContext}\n" +
            $"- reason={decision.Reason}\n" +
            $"- informationNeeded={string.Join("；", decision.InformationNeeded)}\n\n" +
            "plan-agent 最终计划：\n" + FormatPlan(plan) + "\n\n" +
            "execute-agent 执行结果：\n" + FormatExecution(execution) + "\n\n" +
            "wiki 查询结果：\n" + FormatKnowledge(execution.Knowledge, 30000);
    }

    private SupervisorDecision BuildDeterministicSupervisorDecision(string question)
    {
        var asksNpcLocation = AsksLocation(question) && DetectNpc(question) is not null;
        var asksCurrentPlayerState = ContextOnlyKeywords.Any(keyword => question.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        var asksExternalKnowledge = WikiIntentKeywords.Any(keyword => question.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            || question.Contains("wiki", StringComparison.OrdinalIgnoreCase);

        if (asksNpcLocation)
            return new SupervisorDecision(true, false, "玩家询问 NPC 位置，需要结合当前状态、NPC 实时位置和 wiki 日程。", new[] { "NPC 日程", "当前季节/天气/星期/时间", "地图解锁状态" });

        if (asksCurrentPlayerState && !asksExternalKnowledge)
            return new SupervisorDecision(false, true, "问题主要询问当前存档状态，可先用游戏上下文回答。", Array.Empty<string>());

        if (asksExternalKnowledge)
            return new SupervisorDecision(true, false, "问题涉及外部游戏知识，需要查询 wiki。", new[] { "相关 wiki 页面" });

        return new SupervisorDecision(false, true, "问题可能可由当前上下文和对话历史回答；若不足，最终答案说明缺少信息。", Array.Empty<string>());
    }

    private static SupervisorDecision? ParseSupervisorDecision(string response)
    {
        using var document = TryParseJsonObject(response);
        if (document is null)
            return null;

        var root = document.RootElement;
        return new SupervisorDecision(
            GetBool(root, "needsWiki") ?? false,
            GetBool(root, "canAnswerFromContext") ?? false,
            GetString(root, "reason") ?? "supervisor-agent 未说明原因。",
            GetStringArray(root, "informationNeeded")
        );
    }

    private AgentPlan ParseAgentPlan(string response, SupervisorDecision decision, IReadOnlyList<WikiQuery> fallbackQueries)
    {
        using var document = TryParseJsonObject(response);
        if (document is null)
        {
            return new AgentPlan(
                decision.NeedsWiki,
                decision,
                new[] { "plan-agent 未返回可解析 JSON，使用代码兜底关键词。" },
                fallbackQueries
            );
        }

        var root = document.RootElement;
        var queries = new List<WikiQuery>();
        if (TryGetPropertyIgnoreCase(root, "queries", out var queryElement) && queryElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in queryElement.EnumerateArray())
            {
                var text = GetString(item, "text") ?? GetString(item, "keyword") ?? GetString(item, "query");
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                queries.Add(new WikiQuery(
                    text.Trim(),
                    GetBool(item, "exactPage") ?? GetBool(item, "exact_page") ?? false,
                    GetString(item, "reason") ?? "plan-agent 生成的查询词。"
                ));
            }
        }

        return new AgentPlan(
            GetBool(root, "needsWiki") ?? decision.NeedsWiki,
            decision,
            GetStringArray(root, "steps").DefaultIfEmpty("plan-agent 已生成执行计划。").ToList(),
            queries
        );
    }

    private static ExecutionReview? ParseExecutionReview(string response)
    {
        using var document = TryParseJsonObject(response);
        if (document is null)
            return null;

        var root = document.RootElement;
        return new ExecutionReview(
            GetBool(root, "isSufficient") ?? false,
            GetString(root, "summary") ?? "execute-agent 未提供摘要。",
            GetStringArray(root, "missingInfo")
        );
    }

    private static AnswerReview? ParseAnswerReview(string response)
    {
        using var document = TryParseJsonObject(response);
        if (document is null)
            return null;

        var root = document.RootElement;
        return new AnswerReview(
            GetBool(root, "isCorrect") ?? false,
            GetStringArray(root, "issues"),
            GetString(root, "correctedAnswer") ?? ""
        );
    }

    private static AnswerReview ApplyDeterministicAnswerChecks(GameSnapshot snapshot, string answer, AnswerReview review)
    {
        var issues = review.Issues.ToList();
        var correctedAnswer = review.CorrectedAnswer;

        if (MentionsWrongTomorrowWeekday(answer, snapshot.TomorrowDayOfWeek, out var wrongWeekday))
        {
            issues.Add($"答案把明天说成{wrongWeekday}，但游戏状态显示明天星期={snapshot.TomorrowDayOfWeek}。");
            if (string.IsNullOrWhiteSpace(correctedAnswer))
                correctedAnswer = BuildWeekdayCorrection(snapshot);
        }

        return issues.Count == 0
            ? review
            : new AnswerReview(false, issues.Distinct().ToList(), correctedAnswer);
    }

    private static bool MentionsWrongTomorrowWeekday(string answer, string tomorrowDayOfWeek, out string wrongWeekday)
    {
        wrongWeekday = "";
        var correctChinese = ToChineseWeekday(tomorrowDayOfWeek);
        if (string.IsNullOrWhiteSpace(correctChinese))
            return false;

        var weekdays = new[] { "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日", "星期天" };
        foreach (var weekday in weekdays)
        {
            if (weekday == correctChinese)
                continue;

            if (Regex.IsMatch(answer, $"明天[^。！？\\n]{{0,16}}{Regex.Escape(weekday)}|{Regex.Escape(weekday)}[^。！？\\n]{{0,16}}明天"))
            {
                wrongWeekday = weekday;
                return true;
            }
        }

        return false;
    }

    private static string BuildWeekdayCorrection(GameSnapshot snapshot)
    {
        return $"我刚才的回答里把天气和星期混淆了。当前游戏状态显示：今天是{ToChineseWeekday(snapshot.DayOfWeek)}，明天是{ToChineseWeekday(snapshot.TomorrowDayOfWeek)}；明天天气是 {snapshot.TomorrowWeather}。请以“明天星期={snapshot.TomorrowDayOfWeek}”为准，不要把明天天气当成星期。";
    }

    private static string ToChineseWeekday(string weekday)
    {
        return weekday.ToLowerInvariant() switch
        {
            "monday" => "星期一",
            "tuesday" => "星期二",
            "wednesday" => "星期三",
            "thursday" => "星期四",
            "friday" => "星期五",
            "saturday" => "星期六",
            "sunday" => "星期日",
            _ => weekday
        };
    }

    private AgentPlan NormalizePlan(AgentPlan plan, bool needsWiki, IReadOnlyList<WikiQuery> fallbackQueries)
    {
        var queries = NormalizeQueries(plan.Queries);
        if (needsWiki && queries.Count == 0)
            queries = NormalizeQueries(fallbackQueries);

        return plan with
        {
            NeedsWiki = needsWiki,
            Queries = queries.Take(Math.Clamp(_config.MaxKnowledgeEntries + 2, 3, 10)).ToList()
        };
    }

    private static AgentPlan MergePlans(AgentPlan first, AgentPlan second)
    {
        return first with
        {
            Steps = first.Steps.Concat(second.Steps.Select(step => "Replan: " + step)).ToList(),
            Queries = DeduplicateQueries(first.Queries.Concat(second.Queries))
        };
    }

    private static ExecutionResult MergeExecutions(ExecutionResult first, ExecutionResult second)
    {
        return new ExecutionResult(
            DeduplicateKnowledge(first.Knowledge.Concat(second.Knowledge)),
            first.Logs.Concat(second.Logs).ToList(),
            first.AgentSummary + "\n" + second.AgentSummary,
            first.IsSufficient || second.IsSufficient
        );
    }

    private static IEnumerable<WikiQuery> BuildFallbackQueries(string question, bool needsWiki)
    {
        if (!needsWiki)
            yield break;

        var npcName = DetectNpc(question);
        if (npcName is not null)
        {
            foreach (var alias in NpcAliases[npcName])
                yield return new WikiQuery(alias, ExactPage: true, "识别到 NPC，优先按页面名查询。");

            if (AsksLocation(question))
                yield return new WikiQuery($"{NpcAliases[npcName][0]} 日程", ExactPage: false, "询问 NPC 位置，需要查询日程。");

            if (question.Contains("喜欢", StringComparison.OrdinalIgnoreCase) || question.Contains("送礼", StringComparison.OrdinalIgnoreCase))
                yield return new WikiQuery($"{NpcAliases[npcName][0]} 送礼", ExactPage: false, "询问送礼偏好，需要查询送礼。");
        }

        foreach (var keyword in ExtractLikelyTerms(question))
            yield return new WikiQuery(keyword, ExactPage: false, "从用户问题中提取的候选关键词。");

        if (needsWiki && npcName is null)
        {
            foreach (var keyword in InferGenericFallbackKeywords(question))
                yield return new WikiQuery(keyword, ExactPage: false, "从用户意图推理出的通用候选关键词。");
        }
    }

    private NpcLocationResult TryFindLiveNpcLocation(string question)
    {
        if (!AsksLocation(question))
            return new NpcLocationResult("", "");

        var npcName = DetectNpc(question);
        if (npcName is null)
            return new NpcLocationResult("", "玩家在问人物位置，但没有识别出具体 NPC 名称。");

        var liveLocation = _npcLocationService.TryGetCurrentLocation(npcName);
        if (!string.IsNullOrWhiteSpace(liveLocation))
            return new NpcLocationResult($"{GetPreferredAlias(npcName)}现在在：{liveLocation}。", $"游戏当前 NPC 实例位置：{npcName}={liveLocation}");

        return new NpcLocationResult("", $"游戏当前没有直接读取到 {GetPreferredAlias(npcName)} 的实时位置，需要结合 wiki 日程推理。");
    }

    private static IEnumerable<string> ExtractLikelyTerms(string text)
    {
        foreach (var term in SplitTerms(text))
        {
            if (term.Length < 2)
                continue;

            if (IsStopWord(term))
                continue;

            yield return term;
        }
    }

    private static IEnumerable<string> InferGenericFallbackKeywords(string question)
    {
        if (question.Contains("卖", StringComparison.OrdinalIgnoreCase) || question.Contains("价格", StringComparison.OrdinalIgnoreCase))
        {
            yield return "售价";
            yield return "职业";
        }

        if (question.Contains("鱼", StringComparison.OrdinalIgnoreCase))
            yield return "鱼";

        if (question.Contains("作物", StringComparison.OrdinalIgnoreCase))
            yield return "作物";

        if (question.Contains("配方", StringComparison.OrdinalIgnoreCase) || question.Contains("料理", StringComparison.OrdinalIgnoreCase))
            yield return "食谱";
    }

    private static IEnumerable<string> SplitTerms(string text)
    {
        return Regex.Split(text, @"[\s，。！？、；：,.!?;:（）()\[\]【】""“”「」]+")
            .Select(term => term.Trim())
            .Where(term => term.Length > 0);
    }

    private static bool IsStopWord(string term)
    {
        var stopWords = new HashSet<string>
        {
            "现在", "目前", "应该", "怎么", "如何", "为什么", "多少", "一个", "一下", "我的", "这个", "那个",
            "哪里", "在哪", "在哪里", "请问", "能否", "可以", "知道", "告诉我"
        };

        return stopWords.Contains(term);
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

    private static string GetPreferredAlias(string npcName)
    {
        return NpcAliases.TryGetValue(npcName, out var aliases) ? aliases[0] : npcName;
    }

    private static List<WikiQuery> DeduplicateQueries(IEnumerable<WikiQuery> queries)
    {
        return queries
            .Where(query => !string.IsNullOrWhiteSpace(query.Text))
            .GroupBy(query => query.Text.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var selected = group.OrderByDescending(query => query.ExactPage).First();
                return selected with { Text = selected.Text.Trim() };
            })
            .ToList();
    }

    private List<WikiQuery> NormalizeQueries(IEnumerable<WikiQuery> queries)
    {
        return DeduplicateQueries(queries.SelectMany(NormalizeQuery));
    }

    private IEnumerable<WikiQuery> NormalizeQuery(WikiQuery query)
    {
        var originalText = query.Text.Trim();
        var text = NormalizeQueryText(originalText);
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        if (TryMapKnownEntity(originalText, out var aliasEntity) || TryMapKnownEntity(text, out aliasEntity))
        {
            var title = _wikiTitleIndexService.ContainsTitle(aliasEntity)
                ? aliasEntity
                : _wikiTitleIndexService.Match(aliasEntity)?.Title ?? aliasEntity;
            _debugLogger.Log("keyword-book-match", $"candidate='{originalText}' matched='{title}' method='alias-map'");
            yield return query with
            {
                Text = title,
                ExactPage = true,
                Reason = query.Reason + "；已规范化为 wiki 页面实体名，移除时间/位置等意图词。"
            };
            yield break;
        }

        var titleMatch = _wikiTitleIndexService.Match(originalText) ?? _wikiTitleIndexService.Match(text);
        if (titleMatch is not null)
        {
            _debugLogger.Log("keyword-book-match", $"candidate='{originalText}' matched='{titleMatch.Title}' method='{titleMatch.MatchType}'");
            yield return query with
            {
                Text = titleMatch.Title,
                ExactPage = true,
                Reason = query.Reason + "；命中本地 wiki 标题关键词本。"
            };
            yield break;
        }

        if (IsInvalidStandaloneWikiTerm(text))
        {
            _debugLogger.Log("keyword-book-match", $"candidate='{originalText}' dropped='true' reason='invalid standalone intent term'");
            yield break;
        }

        _debugLogger.Log("keyword-book-match", $"candidate='{originalText}' matched='' method='fallback-online-search'");
        yield return query with { Text = text };
    }

    private static string NormalizeQueryText(string text)
    {
        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        foreach (var term in InvalidStandaloneWikiTerms.OrderByDescending(term => term.Length))
            normalized = normalized.Replace(term, "", StringComparison.OrdinalIgnoreCase).Trim();

        normalized = Regex.Replace(normalized, @"\s+", " ").Trim(' ', '-', '_', '，', '。', '：', ':', '；', ';');
        return normalized;
    }

    private static bool TryMapKnownEntity(string text, out string entity)
    {
        foreach (var (canonical, aliases) in WikiEntityAliases)
        {
            if (aliases.Any(alias => text.Contains(alias, StringComparison.OrdinalIgnoreCase)))
            {
                entity = canonical;
                return true;
            }
        }

        foreach (var aliases in NpcAliases.Values)
        {
            if (aliases.Any(alias => text.Contains(alias, StringComparison.OrdinalIgnoreCase)))
            {
                entity = aliases[0];
                return true;
            }
        }

        entity = "";
        return false;
    }

    private static bool IsInvalidStandaloneWikiTerm(string text)
    {
        return InvalidStandaloneWikiTerms.Any(term => text.Equals(term, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<KnowledgeEntry> DeduplicateKnowledge(IEnumerable<KnowledgeEntry> knowledge)
    {
        return knowledge
            .GroupBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(12)
            .ToList();
    }

    private static List<ChatMessage> BuildConversationMessages(IReadOnlyList<ChatMessage> history, string question)
    {
        var messages = history
            .Where(message => message.Role is "user" or "assistant")
            .TakeLast(10)
            .ToList();

        if (messages.Count == 0 || messages[^1].Role != "user" || messages[^1].Text != question)
            messages.Add(new ChatMessage("user", question));

        return messages;
    }

    private static string BuildHistoryContext(IReadOnlyList<ChatMessage> history)
    {
        var lines = history
            .Where(message => message.Role is "user" or "assistant")
            .TakeLast(8)
            .Select(message => $"{message.Role}: {message.Text}");

        var value = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(value) ? "无" : value;
    }

    private static IReadOnlyList<ChatMessage> SingleUserMessage(string text)
    {
        return new[] { new ChatMessage("user", text) };
    }

    private static string FormatPlan(AgentPlan plan)
    {
        var steps = plan.Steps.Count == 0 ? "无" : string.Join("\n", plan.Steps.Select(step => "- " + step));
        var queries = plan.Queries.Count == 0
            ? "无"
            : string.Join("\n", plan.Queries.Select(query => $"- {query.Text} ({(query.ExactPage ? "精确页面" : "关键词")})：{query.Reason}"));

        return
            $"- needsWiki={plan.NeedsWiki}\n" +
            "- steps:\n" + steps + "\n" +
            "- queries:\n" + queries;
    }

    private static string FormatSupervisorDecision(SupervisorDecision decision)
    {
        return
            $"- needsWiki={decision.NeedsWiki}\n" +
            $"- canAnswerFromContext={decision.CanAnswerFromContext}\n" +
            $"- reason={decision.Reason}\n" +
            $"- informationNeeded={string.Join("；", decision.InformationNeeded)}";
    }

    private static string FormatQueries(IReadOnlyList<WikiQuery> queries)
    {
        if (queries.Count == 0)
            return "无";

        return string.Join("\n", queries.Select(query => $"- {query.Text} ({(query.ExactPage ? "精确页面" : "关键词")})：{query.Reason}"));
    }

    private static string FormatExecution(ExecutionResult execution)
    {
        return
            $"- isSufficient={execution.IsSufficient}\n" +
            "- logs:\n" + string.Join("\n", execution.Logs.Select(log => "- " + log)) + "\n" +
            "- execute-agent summary:\n" + execution.AgentSummary;
    }

    private static string FormatKnowledge(IReadOnlyList<KnowledgeEntry> knowledge, int maxChars)
    {
        if (knowledge.Count == 0)
            return "无 wiki 页面。";

        var text = string.Join("\n", knowledge.Select(entry => $"- {entry.Title} ({entry.Content.Length} chars): {entry.Content}"));
        return text.Length <= maxChars
            ? text
            : text[..maxChars] + $"\n...[truncated in log/prompt {maxChars}/{text.Length} chars]";
    }

    private static string TrimForLog(string value, int maxChars)
    {
        return value.Length <= maxChars ? value : value[..maxChars] + $"\n...[trimmed in log {maxChars}/{value.Length} chars]";
    }

    private static JsonDocument? TryParseJsonObject(string text)
    {
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return "";

        return text[start..(end + 1)];
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();
    }

    private sealed record SupervisorDecision(bool NeedsWiki, bool CanAnswerFromContext, string Reason, IReadOnlyList<string> InformationNeeded);
    private sealed record WikiQuery(string Text, bool ExactPage, string Reason);
    private sealed record AgentPlan(bool NeedsWiki, SupervisorDecision Decision, IReadOnlyList<string> Steps, IReadOnlyList<WikiQuery> Queries);
    private sealed record ExecutionReview(bool IsSufficient, string Summary, IReadOnlyList<string> MissingInfo);
    private sealed record AnswerReview(bool IsCorrect, IReadOnlyList<string> Issues, string CorrectedAnswer);
    private sealed record ExecutionResult(IReadOnlyList<KnowledgeEntry> Knowledge, IReadOnlyList<string> Logs, string AgentSummary, bool IsSufficient);
    private sealed record NpcLocationResult(string DirectAnswer, string Context);
}
