using StardewAiAssistant.Models;
using StardewAiAssistant.Services;
using StardewAiAssistant.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewAiAssistant;

public sealed class ModEntry : Mod
{
    private ModConfig _config = new();
    private GameContextService _gameContextService = null!;
    private WikiKnowledgeService _knowledgeService = null!;
    private WikiTitleIndexService _wikiTitleIndexService = null!;
    private ChatHistoryService _chatHistoryService = null!;
    private AnswerService _answerService = null!;

    public override void Entry(IModHelper helper)
    {
        _config = helper.ReadConfig<ModConfig>();
        _gameContextService = new GameContextService();
        _chatHistoryService = new ChatHistoryService();
        _knowledgeService = new WikiKnowledgeService(Monitor);
        _wikiTitleIndexService = new WikiTitleIndexService(helper.DirectoryPath, Monitor);

        var aiClient = new AiClient(_config);
        var debugLogger = new AgentDebugLogger(_config, Monitor, helper.DirectoryPath);
        _answerService = new AnswerService(_config, aiClient, _knowledgeService, _wikiTitleIndexService, new NpcLocationService(), debugLogger);

        helper.Events.Input.ButtonPressed += OnButtonPressed;
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;

        if (e.Button != _config.OpenMenuButton)
            return;

        if (Game1.activeClickableMenu is AiChatMenu aiChatMenu && aiChatMenu.CanCloseWithHotkey)
        {
            aiChatMenu.exitThisMenu();
            Helper.Input.Suppress(e.Button);
            return;
        }

        if (Game1.activeClickableMenu is not null)
            return;

        Game1.activeClickableMenu = new AiChatMenu(_answerService, _gameContextService, _chatHistoryService);
        Helper.Input.Suppress(e.Button);
    }
}
