using System.Text.Json;
using System.Text.RegularExpressions;
using StardewAiAssistant.Models;

namespace StardewAiAssistant.Services;

public sealed class AnswerService
{
    private const int MaxWikiPagesPerRound = 6;
    private const int MaxSupervisorReviewEvidenceChars = 10000;
    private const int MaxExecuteWikiInputChars = 60000;
    private const int MaxCandidatePreviewChars = 1400;
    private const int MaxCandidatePreviewPages = 10;
    private const int MaxReplanRounds = 1;

    private readonly ModConfig _config;
    private readonly AiClient _aiClient;
    private readonly WikiKnowledgeService _knowledgeService;
    private readonly WikiTitleIndexService _wikiTitleIndexService;
    private readonly NpcLocationService _npcLocationService;
    private readonly AgentDebugLogger _debugLogger;

    public AnswerService(
        ModConfig config,
        AiClient aiClient,
        WikiKnowledgeService knowledgeService,
        WikiTitleIndexService wikiTitleIndexService,
        NpcLocationService npcLocationService,
        AgentDebugLogger debugLogger)
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
        _debugLogger.Log("snapshot", TrimForLog(snapshot.ToPromptContext(), 4000));

        var historyContext = BuildHistoryContext(history);
        var conversationMessages = BuildConversationMessages(history, question);
        var priorExecution = ExecuteAgentResult.Empty;
        SupervisorPlan? plan = null;

        for (var round = 0; round <= MaxReplanRounds; round++)
        {
            plan = await SupervisorAgentPlanAsync(question, snapshot, historyContext, priorExecution, round, cancellationToken);
            _debugLogger.Log(round == 0 ? "supervisor-plan" : "supervisor-replan", FormatSupervisorPlan(plan));

            var titleCandidates = plan.NeedsWiki
                ? await SearchWikiTitlesAsync(plan.Queries, cancellationToken)
                : Array.Empty<WikiTitleSearchResult>();

            var candidatePreviews = plan.NeedsWiki
                ? await FetchCandidatePreviewsAsync(titleCandidates, cancellationToken)
                : Array.Empty<WikiPagePreview>();

            IReadOnlyList<string> selectedTitles = Array.Empty<string>();
            if (plan.NeedsWiki && titleCandidates.Count > 0)
                selectedTitles = await ExecuteAgentSelectTitlesAsync(question, snapshot, plan, titleCandidates, candidatePreviews, cancellationToken);

            var pages = plan.NeedsWiki
                ? await FetchWikiPagesAsync(selectedTitles, cancellationToken)
                : Array.Empty<KnowledgeEntry>();

            var execution = await ExecuteAgentAnswerAsync(question, snapshot, plan, titleCandidates, pages, conversationMessages, cancellationToken);
            _debugLogger.Log("execute-result", FormatExecuteResult(execution));
            _debugLogger.Log("execute-evidence", FormatEvidence(execution.Evidence, 12000));

            var review = await SupervisorAgentReviewAsync(question, snapshot, plan, execution, round, cancellationToken);
            _debugLogger.Log("supervisor-review", FormatSupervisorReview(review));

            if (review.IsApproved)
            {
                var final = AddWikiSummarySuffix(string.IsNullOrWhiteSpace(review.FinalAnswer) ? execution.Answer : review.FinalAnswer, execution);
                _debugLogger.Log("final-answer", final);
                return final;
            }

            if (round < MaxReplanRounds)
            {
                priorExecution = execution with
                {
                    NeedsReplan = true,
                    MissingInfo = execution.MissingInfo.Concat(review.Issues).Concat(review.ReplanQueries).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                };
                _debugLogger.Log("replan-trigger", "supervisor-agent rejected execute-agent answer; running one replan round.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(review.FinalAnswer))
            {
                var final = AddWikiSummarySuffix(review.FinalAnswer, execution);
                _debugLogger.Log("final-answer-corrected", final);
                return final;
            }

            if (!string.IsNullOrWhiteSpace(execution.Answer))
            {
                var final = AddWikiSummarySuffix(execution.Answer, execution);
                _debugLogger.Log("final-answer-fallback", final);
                return final;
            }

            return "我还不能确定答案：缺少足够的游戏状态或 wiki 证据。";
        }

        return "我还不能确定答案：任务规划没有生成可用结果。";
    }

    private async Task<SupervisorPlan> SupervisorAgentPlanAsync(
        string question,
        GameSnapshot snapshot,
        string historyContext,
        ExecuteAgentResult priorExecution,
        int round,
        CancellationToken cancellationToken)
    {
        var isReplan = round > 0;
        var systemPrompt =
            "你是《星露谷物语》游戏内问答系统的 supervisor-agent。\n" +
            "你负责 plan 和 replan，判断是否需要查询 wiki，并给 execute-agent 制定工具计划。\n" +
            "不要依赖模型记忆回答游戏百科事实。凡是费用、材料、配方、建筑、商店、NPC 日程、物品来源、任务、解锁条件、价格、鱼、作物等静态知识，通常需要 wiki 证据。\n" +
            "如果问题只询问当前存档状态（例如当前金钱、背包、位置、天气、运气），可以不查 wiki。\n" +
            "wiki 只支持标题/关键词搜索，不支持语义搜索。查询词应短、像页面标题；如果用户词没有精确标题，execute-agent 会根据标题候选多跳选择页面。\n" +
            "只返回 JSON，不要 Markdown，不要解释。\n" +
            "JSON 格式：{\"needsWiki\":true,\"canAnswerFromContext\":false,\"reason\":\"...\",\"queries\":[\"关键词\"],\"factsToVerify\":[\"需要验证的事实\"]}";

        var userPrompt =
            snapshot.ToPromptContext() + "\n\n" +
            "最近对话：\n" + historyContext + "\n\n" +
            "可用工具：\n" +
            "- SearchWikiTitles(query): 查询本地 wiki 标题关键词本，只返回候选标题。\n" +
            "- FetchWikiPage(title): 读取 biligame wiki 页面清洗后的全文。\n" +
            "- execute-agent 会基于全文生成答案，并抽取本次答案使用到的证据片段。\n\n" +
            (isReplan ? "上轮 execute-agent 结果：\n" + FormatExecuteResult(priorExecution) + "\n\n" : "") +
            "用户问题：\n" + question;

        _debugLogger.Log(isReplan ? "supervisor-replan-agent" : "supervisor-agent", "Calling supervisor-agent for plan.");
        _debugLogger.Log(isReplan ? "supervisor-replan-input" : "supervisor-plan-input", TrimForLog(systemPrompt + "\n\n" + userPrompt, 8000));
        var response = await _aiClient.CompleteAsync(systemPrompt, SingleUserMessage(userPrompt), cancellationToken);
        _debugLogger.Log(isReplan ? "supervisor-replan-response" : "supervisor-plan-response", TrimForLog(response, 4000));

        var plan = ParseSupervisorPlan(response);
        if (plan is not null)
            return SanitizePlan(plan);

        _debugLogger.Log("supervisor-plan-parse", "AI response was not valid JSON. Falling back to conservative wiki search using the user question.");
        return new SupervisorPlan(
            NeedsWiki: true,
            CanAnswerFromContext: false,
            Reason: "supervisor-agent 未返回可解析 JSON，保守地查询 wiki。",
            Queries: SplitGenericSearchTerms(question).DefaultIfEmpty(question).Take(4).ToList(),
            FactsToVerify: new[] { "用户问题涉及的可验证事实" }
        );
    }

    private async Task<IReadOnlyList<WikiTitleSearchResult>> SearchWikiTitlesAsync(IReadOnlyList<string> queries, CancellationToken cancellationToken)
    {
        var results = new List<WikiTitleSearchResult>();
        foreach (var query in queries.Where(query => !string.IsNullOrWhiteSpace(query)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
        {
            var localCandidates = _wikiTitleIndexService.Search(query, limit: 16).ToList();
            var onlineTitles = await _knowledgeService.SearchTitlesOnlyAsync(query, limit: 8, cancellationToken);
            var candidates = localCandidates
                .Concat(onlineTitles.Select((title, index) => new WikiTitleCandidate(title, "online-search", 850 - index)))
                .GroupBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Title.Length)
                .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .ToList();

            _debugLogger.Log(
                "wiki-title-search",
                candidates.Count == 0
                    ? $"query='{query}' candidates=0"
                    : $"query='{query}' candidates={candidates.Count}\n" + string.Join("\n", candidates.Select(candidate => $"- {candidate.Title} [{candidate.MatchType}] score={candidate.Score}"))
            );
            results.Add(new WikiTitleSearchResult(query, candidates));
        }

        return results;
    }

    private async Task<IReadOnlyList<WikiPagePreview>> FetchCandidatePreviewsAsync(IReadOnlyList<WikiTitleSearchResult> titleCandidates, CancellationToken cancellationToken)
    {
        var previews = new List<WikiPagePreview>();
        var titles = titleCandidates
            .SelectMany(result => result.Candidates)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidatePreviewPages)
            .ToList();

        foreach (var title in titles)
        {
            var preview = await _knowledgeService.GetPagePreviewAsync(title, MaxCandidatePreviewChars, cancellationToken);
            if (preview is null)
            {
                _debugLogger.Log("wiki-candidate-preview", $"title='{title}' result=missing");
                continue;
            }

            previews.Add(preview);
            _debugLogger.Log("wiki-candidate-preview", $"title='{title}' chars={preview.Preview.Length} cache={(preview.FromCache ? "hit" : "miss")}");
        }

        return previews;
    }

    private async Task<IReadOnlyList<string>> ExecuteAgentSelectTitlesAsync(
        string question,
        GameSnapshot snapshot,
        SupervisorPlan plan,
        IReadOnlyList<WikiTitleSearchResult> titleCandidates,
        IReadOnlyList<WikiPagePreview> candidatePreviews,
        CancellationToken cancellationToken)
    {
        var systemPrompt =
            "你是《星露谷物语》游戏内问答系统的 execute-agent。\n" +
            "你现在只负责从 wiki 标题候选和页面摘要中选择应该读取全文的页面。\n" +
            "不要回答问题。不要选择看起来无关的页面。用户关键词没有精确标题时，必须根据候选标题和摘要多跳选择最可能包含答案的页面。\n" +
            "如果没有合适页面，selectedTitles 返回空数组并说明 missingInfo。\n" +
            "只返回 JSON，不要 Markdown，不要解释。\n" +
            "JSON 格式：{\"selectedTitles\":[\"页面标题\"],\"missingInfo\":[\"...\"]}";

        var userPrompt =
            snapshot.ToPromptContext() + "\n\n" +
            "用户问题：\n" + question + "\n\n" +
            "supervisor-agent 计划：\n" + FormatSupervisorPlan(plan) + "\n\n" +
            "标题候选：\n" + FormatTitleSearchResults(titleCandidates) + "\n\n" +
            "候选页面摘要：\n" + FormatCandidatePreviews(candidatePreviews);

        _debugLogger.Log("execute-title-select-agent", "Calling execute-agent to select wiki titles.");
        _debugLogger.Log("execute-title-select-input", TrimForLog(systemPrompt + "\n\n" + userPrompt, 12000));
        var response = await _aiClient.CompleteAsync(systemPrompt, SingleUserMessage(userPrompt), cancellationToken);
        _debugLogger.Log("execute-title-select-response", TrimForLog(response, 4000));

        var selected = ParseSelectedTitles(response)
            .Where(title => titleCandidates.SelectMany(result => result.Candidates).Any(candidate => candidate.Title.Equals(title, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(_config.MaxKnowledgeEntries, 1, MaxWikiPagesPerRound))
            .ToList();

        _debugLogger.Log("execute-title-select", selected.Count == 0 ? "No wiki titles selected." : string.Join("\n", selected.Select(title => "- " + title)));
        return selected;
    }

    private async Task<IReadOnlyList<KnowledgeEntry>> FetchWikiPagesAsync(IReadOnlyList<string> titles, CancellationToken cancellationToken)
    {
        var pages = new List<KnowledgeEntry>();
        foreach (var title in titles.Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Clamp(_config.MaxKnowledgeEntries, 1, MaxWikiPagesPerRound)))
        {
            var result = await _knowledgeService.GetPageWithMetadataAsync(title, cancellationToken);
            if (result?.Entry is null)
            {
                _debugLogger.Log("wiki-page-fetch", $"title='{title}' result=missing");
                continue;
            }

            pages.Add(result.Entry);
            _debugLogger.Log("wiki-page-fetch", $"title='{title}' fetched='{result.Entry.Title}' chars={result.Entry.Content.Length} cache={(result.FromCache ? "hit" : "miss")}");
        }

        return DeduplicateKnowledge(pages);
    }

    private async Task<ExecuteAgentResult> ExecuteAgentAnswerAsync(
        string question,
        GameSnapshot snapshot,
        SupervisorPlan plan,
        IReadOnlyList<WikiTitleSearchResult> titleCandidates,
        IReadOnlyList<KnowledgeEntry> pages,
        IReadOnlyList<ChatMessage> conversationMessages,
        CancellationToken cancellationToken)
    {
        var wikiInput = FormatWikiPagesForExecute(pages, MaxExecuteWikiInputChars);
        _debugLogger.Log("execute-full-wiki-input", $"pages={pages.Count}; chars={wikiInput.Length}\n" + TrimForLog(wikiInput, 30000));

        var systemPrompt =
            "你是《星露谷物语》游戏内问答系统的 execute-agent。\n" +
            "你负责执行 supervisor-agent 的计划、基于当前游戏状态和 wiki 全文生成候选答案，并抽取答案实际使用到的 wiki 原文证据片段。\n" +
            "如果 supervisor 说不查 wiki，但你判断问题需要 wiki 证据，请不要编造，返回 needsReplan=true 并说明缺少哪些查询。\n" +
            "如果提供了 wiki 全文，答案中的百科事实必须由 wiki 全文或当前游戏状态支持。\n" +
            "evidenceSnippets 必须是 wiki 全文中的原文短句或表格行，不能改写；答案中的每个数字、材料、日期、地点都必须能在 evidenceSnippets 或当前游戏状态中找到。\n" +
            "只返回 JSON，不要 Markdown，不要解释。\n" +
            "JSON 格式：{\"answer\":\"候选答案\",\"evidenceSnippets\":[\"片段\"],\"queriedPages\":[\"页面\"],\"missingInfo\":[\"...\"],\"needsReplan\":false}";

        var userPrompt =
            snapshot.ToPromptContext() + "\n\n" +
            "用户问题：\n" + question + "\n\n" +
            "supervisor-agent 计划：\n" + FormatSupervisorPlan(plan) + "\n\n" +
            "wiki 标题候选：\n" + FormatTitleSearchResults(titleCandidates) + "\n\n" +
            "已读取的 wiki 全文：\n" + wikiInput;

        _debugLogger.Log("execute-answer-agent", "Calling execute-agent to produce candidate answer and evidence.");
        _debugLogger.Log("execute-answer-input", TrimForLog(systemPrompt + "\n\n" + userPrompt, 35000));
        var response = await _aiClient.CompleteAsync(systemPrompt, conversationMessages.Count == 0 ? SingleUserMessage(userPrompt) : AppendUserContext(conversationMessages, userPrompt), cancellationToken);
        _debugLogger.Log("execute-answer-response", TrimForLog(response, 8000));

        var parsed = ParseExecuteAgentResult(response);
        if (parsed is not null)
            return ValidateExecutionEvidence(snapshot, pages, parsed);

        _debugLogger.Log("execute-answer-parse", "AI response was not valid JSON. Returning replan request.");
        return new ExecuteAgentResult(
            Answer: "",
            Evidence: Array.Empty<string>(),
            QueriedPages: pages.Select(page => page.Title).ToList(),
            MissingInfo: new[] { "execute-agent 没有返回可解析 JSON" },
            NeedsReplan: true
        );
    }

    private async Task<SupervisorReview> SupervisorAgentReviewAsync(
        string question,
        GameSnapshot snapshot,
        SupervisorPlan plan,
        ExecuteAgentResult execution,
        int round,
        CancellationToken cancellationToken)
    {
        var evidence = FormatEvidence(execution.Evidence, MaxSupervisorReviewEvidenceChars);
        var systemPrompt =
            "你是《星露谷物语》游戏内问答系统的 supervisor-agent，现在负责最终检查 execute-agent 的候选答案。\n" +
            "你只能根据当前游戏状态、execute-agent 的候选答案、已查询页面、证据片段和缺失信息判断答案是否可靠。\n" +
            "注意：这里不会提供 wiki 全文，只有 execute-agent 抽取的答案证据片段，以减少 token 消耗。\n" +
            "候选答案中的数字、材料、日期、地点等关键事实必须能被证据片段或当前游戏状态直接支持，否则必须驳回。\n" +
            "如果证据不足以支持候选答案，isApproved=false，并给出 replanQueries。最多只会 replan 一次。\n" +
            "重要：TomorrowWeather/明天天气是天气字段，sunny/Sun 表示晴天，不是 Sunday/星期日。星期只以 TomorrowDayOfWeek/明天星期字段为准。\n" +
            "只返回 JSON，不要 Markdown，不要解释。\n" +
            "JSON 格式：{\"isApproved\":true,\"finalAnswer\":\"最终答案\",\"issues\":[\"...\"],\"replanQueries\":[\"...\"]}";

        var userPrompt =
            snapshot.ToPromptContext() + "\n\n" +
            "用户问题：\n" + question + "\n\n" +
            "supervisor-agent 计划：\n" + FormatSupervisorPlan(plan) + "\n\n" +
            "execute-agent 候选答案：\n" + (string.IsNullOrWhiteSpace(execution.Answer) ? "无" : execution.Answer) + "\n\n" +
            "已查询页面：\n" + (execution.QueriedPages.Count == 0 ? "无" : string.Join("，", execution.QueriedPages)) + "\n\n" +
            "execute-agent 证据片段：\n" + evidence + "\n\n" +
            "execute-agent 缺失信息：\n" + (execution.MissingInfo.Count == 0 ? "无" : string.Join("；", execution.MissingInfo)) + "\n\n" +
            $"当前轮次：{round}，最多 replan 次数：{MaxReplanRounds}";

        _debugLogger.Log("supervisor-review-agent", "Calling supervisor-agent to review execute answer.");
        _debugLogger.Log("supervisor-review-input", TrimForLog(systemPrompt + "\n\n" + userPrompt, 16000));
        var response = await _aiClient.CompleteAsync(systemPrompt, SingleUserMessage(userPrompt), cancellationToken);
        _debugLogger.Log("supervisor-review-response", TrimForLog(response, 6000));

        var parsed = ParseSupervisorReview(response);
        if (parsed is not null)
            return ApplyDeterministicAnswerChecks(snapshot, execution.Answer, parsed);

        _debugLogger.Log("supervisor-review-parse", "AI response was not valid JSON. Using deterministic checks.");
        return ApplyDeterministicAnswerChecks(snapshot, execution.Answer, new SupervisorReview(false, "", new[] { "supervisor-agent 没有返回可解析 JSON" }, execution.MissingInfo));
    }

    private static SupervisorPlan SanitizePlan(SupervisorPlan plan)
    {
        var queries = plan.Queries
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => query.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return plan with
        {
            Queries = plan.NeedsWiki && queries.Count == 0 ? new[] { "星露谷物语" } : queries,
            FactsToVerify = plan.FactsToVerify.Where(fact => !string.IsNullOrWhiteSpace(fact)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList()
        };
    }

    private static SupervisorPlan? ParseSupervisorPlan(string response)
    {
        using var document = TryParseJsonObject(response);
        if (document is null)
            return null;

        var root = document.RootElement;
        return new SupervisorPlan(
            GetBool(root, "needsWiki") ?? true,
            GetBool(root, "canAnswerFromContext") ?? false,
            GetString(root, "reason") ?? "supervisor-agent 未说明原因。",
            GetStringArray(root, "queries"),
            GetStringArray(root, "factsToVerify")
        );
    }

    private static IReadOnlyList<string> ParseSelectedTitles(string response)
    {
        using var document = TryParseJsonObject(response);
        if (document is null)
            return Array.Empty<string>();

        return GetStringArray(document.RootElement, "selectedTitles");
    }

    private static ExecuteAgentResult? ParseExecuteAgentResult(string response)
    {
        using var document = TryParseJsonObject(response);
        if (document is null)
            return null;

        var root = document.RootElement;
        return new ExecuteAgentResult(
            GetString(root, "answer") ?? "",
            GetStringArray(root, "evidenceSnippets"),
            GetStringArray(root, "queriedPages"),
            GetStringArray(root, "missingInfo"),
            GetBool(root, "needsReplan") ?? false
        );
    }

    private static SupervisorReview? ParseSupervisorReview(string response)
    {
        using var document = TryParseJsonObject(response);
        if (document is null)
            return null;

        var root = document.RootElement;
        return new SupervisorReview(
            GetBool(root, "isApproved") ?? false,
            GetString(root, "finalAnswer") ?? "",
            GetStringArray(root, "issues"),
            GetStringArray(root, "replanQueries")
        );
    }

    private static SupervisorReview ApplyDeterministicAnswerChecks(GameSnapshot snapshot, string answer, SupervisorReview review)
    {
        var issues = review.Issues.ToList();
        var finalAnswer = review.FinalAnswer;

        if (MentionsWrongTomorrowWeekday(answer, snapshot.TomorrowDayOfWeek, out var wrongWeekday))
        {
            issues.Add($"答案把明天说成{wrongWeekday}，但游戏状态显示明天星期={snapshot.TomorrowDayOfWeek}。");
            finalAnswer = BuildWeekdayCorrection(snapshot);
        }

        return issues.Count == review.Issues.Count
            ? review
            : new SupervisorReview(false, finalAnswer, issues.Distinct().ToList(), review.ReplanQueries);
    }

    private static ExecuteAgentResult ValidateExecutionEvidence(GameSnapshot snapshot, IReadOnlyList<KnowledgeEntry> pages, ExecuteAgentResult result)
    {
        if (pages.Count == 0)
        {
            return result with
            {
                Evidence = Array.Empty<string>(),
                QueriedPages = Array.Empty<string>()
            };
        }

        var missing = result.MissingInfo.ToList();
        var evidence = result.Evidence.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pageText = NormalizeForEvidence(string.Join("\n", pages.Select(page => page.Content)));
        var evidenceText = NormalizeForEvidence(string.Join("\n", evidence));
        var snapshotText = NormalizeForEvidence(snapshot.ToPromptContext());

        if (evidence.Count == 0 && !string.IsNullOrWhiteSpace(result.Answer))
            missing.Add("答案使用了 wiki 页面，但 execute-agent 没有提供原文证据片段。");

        foreach (var item in evidence.Where(item => item.Trim().Length >= 8))
        {
            if (!pageText.Contains(NormalizeForEvidence(item), StringComparison.OrdinalIgnoreCase))
                missing.Add($"证据片段不像 wiki 原文：{TrimForLog(item, 80)}");
        }

        foreach (var number in ExtractNumericFacts(result.Answer))
        {
            var compact = number.Replace(",", "");
            if (!evidenceText.Replace(",", "").Contains(compact, StringComparison.OrdinalIgnoreCase) &&
                !snapshotText.Replace(",", "").Contains(compact, StringComparison.OrdinalIgnoreCase))
                missing.Add($"答案中的数字 {number} 没有在证据片段或游戏状态中找到。");
        }

        var queriedPages = result.QueriedPages.Count == 0
            ? pages.Select(page => page.Title).ToList()
            : result.QueriedPages;

        return missing.Count == result.MissingInfo.Count && queriedPages.Count == result.QueriedPages.Count
            ? result
            : result with
            {
                QueriedPages = queriedPages,
                MissingInfo = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                NeedsReplan = result.NeedsReplan || missing.Count > result.MissingInfo.Count
            };
    }

    private static IEnumerable<string> ExtractNumericFacts(string text)
    {
        foreach (Match match in Regex.Matches(text, @"\d[\d,]*(?:\.\d+)?"))
            yield return match.Value;
    }

    private static string NormalizeForEvidence(string value)
    {
        return Regex.Replace(value, @"\s+", "");
    }

    private static string AddWikiSummarySuffix(string answer, ExecuteAgentResult execution)
    {
        const string suffix = "（本答案通过wiki总结得出）";
        if (string.IsNullOrWhiteSpace(answer))
            return answer;

        if (execution.QueriedPages.Count == 0 && execution.Evidence.Count == 0)
            return answer;

        var trimmed = answer.TrimEnd();
        return trimmed.Contains(suffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + suffix;
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

    private static IReadOnlyList<KnowledgeEntry> DeduplicateKnowledge(IEnumerable<KnowledgeEntry> knowledge)
    {
        return knowledge
            .GroupBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(MaxWikiPagesPerRound)
            .ToList();
    }

    private static IEnumerable<string> SplitGenericSearchTerms(string text)
    {
        return Regex.Split(text, @"[\s，。！？、；：,.!?;:（）()\[\]【】""“”「」]+")
            .Select(term => term.Trim())
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase);
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

    private static IReadOnlyList<ChatMessage> AppendUserContext(IReadOnlyList<ChatMessage> messages, string context)
    {
        var result = messages.ToList();
        result.Add(new ChatMessage("user", context));
        return result;
    }

    private static string BuildHistoryContext(IReadOnlyList<ChatMessage> history)
    {
        var value = string.Join(
            "\n",
            history
                .Where(message => message.Role is "user" or "assistant")
                .TakeLast(8)
                .Select(message => $"{message.Role}: {message.Text}")
        );
        return string.IsNullOrWhiteSpace(value) ? "无" : value;
    }

    private static IReadOnlyList<ChatMessage> SingleUserMessage(string text)
    {
        return new[] { new ChatMessage("user", text) };
    }

    private static string FormatSupervisorPlan(SupervisorPlan plan)
    {
        return
            $"- needsWiki={plan.NeedsWiki}\n" +
            $"- canAnswerFromContext={plan.CanAnswerFromContext}\n" +
            $"- reason={plan.Reason}\n" +
            "- queries:\n" + (plan.Queries.Count == 0 ? "无" : string.Join("\n", plan.Queries.Select(query => "- " + query))) + "\n" +
            "- factsToVerify:\n" + (plan.FactsToVerify.Count == 0 ? "无" : string.Join("\n", plan.FactsToVerify.Select(fact => "- " + fact)));
    }

    private static string FormatTitleSearchResults(IReadOnlyList<WikiTitleSearchResult> results)
    {
        if (results.Count == 0)
            return "无标题候选。";

        return string.Join(
            "\n\n",
            results.Select(result =>
                $"查询词：{result.Query}\n" +
                (result.Candidates.Count == 0
                    ? "候选：无"
                    : "候选：\n" + string.Join("\n", result.Candidates.Select(candidate => $"- {candidate.Title} [{candidate.MatchType}] score={candidate.Score}")))
            )
        );
    }

    private static string FormatCandidatePreviews(IReadOnlyList<WikiPagePreview> previews)
    {
        if (previews.Count == 0)
            return "无候选页面摘要。";

        return string.Join("\n\n", previews.Select(preview => $"# {preview.Title}\n{preview.Preview}"));
    }

    private static string FormatWikiPagesForExecute(IReadOnlyList<KnowledgeEntry> pages, int maxCharsPerPage)
    {
        if (pages.Count == 0)
            return "无 wiki 全文。";

        return string.Join(
            "\n\n",
            pages.Select(page =>
            {
                var content = page.Content.Length <= maxCharsPerPage
                    ? page.Content
                    : page.Content[..maxCharsPerPage] + $"\n...[truncated execute wiki page {maxCharsPerPage}/{page.Content.Length} chars]";
                return $"# {page.Title}\n{content}";
            })
        );
    }

    private static string FormatExecuteResult(ExecuteAgentResult result)
    {
        return
            $"- needsReplan={result.NeedsReplan}\n" +
            "- answer:\n" + (string.IsNullOrWhiteSpace(result.Answer) ? "无" : result.Answer) + "\n" +
            "- queriedPages:\n" + (result.QueriedPages.Count == 0 ? "无" : string.Join("，", result.QueriedPages)) + "\n" +
            "- evidence:\n" + FormatEvidence(result.Evidence, 6000) + "\n" +
            "- missingInfo:\n" + (result.MissingInfo.Count == 0 ? "无" : string.Join("；", result.MissingInfo));
    }

    private static string FormatEvidence(IReadOnlyList<string> evidence, int maxChars)
    {
        if (evidence.Count == 0)
            return "无证据片段。";

        var text = string.Join("\n", evidence.Select(item => "- " + item));
        return text.Length <= maxChars ? text : text[..maxChars] + $"\n...[truncated evidence {maxChars}/{text.Length} chars]";
    }

    private static string FormatSupervisorReview(SupervisorReview review)
    {
        return
            $"- isApproved={review.IsApproved}\n" +
            "- finalAnswer:\n" + (string.IsNullOrWhiteSpace(review.FinalAnswer) ? "无" : review.FinalAnswer) + "\n" +
            "- issues:\n" + (review.Issues.Count == 0 ? "无" : string.Join("；", review.Issues)) + "\n" +
            "- replanQueries:\n" + (review.ReplanQueries.Count == 0 ? "无" : string.Join("；", review.ReplanQueries));
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
        if (!TryGetPropertyIgnoreCase(element, name, out var value))
            return Array.Empty<string>();

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : new[] { text };
        }

        if (value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();
    }

    private sealed record SupervisorPlan(bool NeedsWiki, bool CanAnswerFromContext, string Reason, IReadOnlyList<string> Queries, IReadOnlyList<string> FactsToVerify);
    private sealed record WikiTitleSearchResult(string Query, IReadOnlyList<WikiTitleCandidate> Candidates);
    private sealed record ExecuteAgentResult(string Answer, IReadOnlyList<string> Evidence, IReadOnlyList<string> QueriedPages, IReadOnlyList<string> MissingInfo, bool NeedsReplan)
    {
        public static ExecuteAgentResult Empty { get; } = new("", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false);
    }
    private sealed record SupervisorReview(bool IsApproved, string FinalAnswer, IReadOnlyList<string> Issues, IReadOnlyList<string> ReplanQueries);
}
