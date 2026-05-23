using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewAiAssistant.Services;
using StardewValley;
using StardewValley.Menus;
using AssistantChatMessage = StardewAiAssistant.Models.ChatMessage;

namespace StardewAiAssistant.UI;

public sealed class AiChatMenu : IClickableMenu
{
    private readonly AnswerService _answerService;
    private readonly GameContextService _gameContextService;
    private readonly ChatHistoryService _history;
    private readonly TextBox _input;
    private readonly ClickableComponent _clearButton;
    private Task<string>? _pendingAnswer;
    private CancellationTokenSource? _pendingCancellation;
    private int _scrollOffset;

    public AiChatMenu(AnswerService answerService, GameContextService gameContextService, ChatHistoryService history)
        : base(
            Math.Max(16, Game1.uiViewport.Width / 2 - Math.Min(1040, Game1.uiViewport.Width - 64) / 2),
            Math.Max(16, Game1.uiViewport.Height / 2 - Math.Min(680, Game1.uiViewport.Height - 64) / 2),
            Math.Min(1040, Game1.uiViewport.Width - 64),
            Math.Min(680, Game1.uiViewport.Height - 64),
            true
        )
    {
        _answerService = answerService;
        _gameContextService = gameContextService;
        _history = history;
        _input = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
        {
            X = xPositionOnScreen + 32,
            Y = yPositionOnScreen + height - 82,
            Width = width - 64,
            Height = 48,
            Selected = true
        };

        _clearButton = new ClickableComponent(new Rectangle(xPositionOnScreen + width - 132, yPositionOnScreen + 28, 96, 36), "clear");

        Game1.keyboardDispatcher.Subscriber = _input;
    }

    public override void update(GameTime time)
    {
        base.update(time);
        _input.Update();

        if (_pendingAnswer is not { IsCompleted: true })
            return;

        var answer = _pendingAnswer.IsFaulted
            ? $"回答失败：{_pendingAnswer.Exception?.GetBaseException().Message}"
            : _pendingAnswer.Result;

        _history.Add("assistant", answer);
        _scrollOffset = 0;
        _pendingAnswer = null;
        _pendingCancellation?.Dispose();
        _pendingCancellation = null;
    }

    public override void receiveKeyPress(Keys key)
    {
        base.receiveKeyPress(key);

        switch (key)
        {
            case Keys.Escape:
                _pendingCancellation?.Cancel();
                exitThisMenu();
                return;
            case Keys.Enter:
                SubmitQuestion();
                return;
            case Keys.PageUp:
                Scroll(8);
                return;
            case Keys.PageDown:
                Scroll(-8);
                return;
            case Keys.Home:
                _scrollOffset = 100000;
                return;
            case Keys.End:
                _scrollOffset = 0;
                return;
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        Scroll(direction > 0 ? 3 : -3);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (_clearButton.containsPoint(x, y))
        {
            _history.Clear();
            _scrollOffset = 0;
            Game1.playSound("trashcan");
            return;
        }

        base.receiveLeftClick(x, y, playSound);
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        SubmitQuestion();
    }

    public override void draw(SpriteBatch b)
    {
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);
        b.DrawString(Game1.dialogueFont, "星露谷 AI 助手", new Vector2(xPositionOnScreen + 32, yPositionOnScreen + 24), Game1.textColor);

        DrawClearButton(b);
        DrawMessages(b);

        if (_pendingAnswer is not null)
            b.DrawString(Game1.smallFont, "AI 正在回答...", new Vector2(xPositionOnScreen + 36, yPositionOnScreen + height - 106), Color.Gray);

        _input.Draw(b);
        drawMouse(b);
    }

    protected override void cleanupBeforeExit()
    {
        _pendingCancellation?.Cancel();
        if (Game1.keyboardDispatcher.Subscriber == _input)
            Game1.keyboardDispatcher.Subscriber = null;
        base.cleanupBeforeExit();
    }

    private void SubmitQuestion()
    {
        if (_pendingAnswer is not null)
            return;

        var question = _input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(question))
            return;

        _history.Add("user", question);
        _input.Text = "";
        _scrollOffset = 0;

        var snapshot = _gameContextService.Capture();
        _pendingCancellation = new CancellationTokenSource();
        _pendingAnswer = _answerService.AnswerAsync(question, snapshot, _history.Messages, _pendingCancellation.Token);
        Game1.playSound("smallSelect");
    }

    private void Scroll(int lines)
    {
        var max = Math.Max(0, BuildRenderedLines().Count - GetMaxVisibleLines());
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, max);
    }

    private void DrawClearButton(SpriteBatch b)
    {
        var bounds = _clearButton.bounds;
        IClickableMenu.drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White);
        var text = "清空";
        var size = Game1.smallFont.MeasureString(text);
        b.DrawString(Game1.smallFont, text, new Vector2(bounds.Center.X - size.X / 2, bounds.Center.Y - size.Y / 2), Game1.textColor);
    }

    private void DrawMessages(SpriteBatch b)
    {
        var top = yPositionOnScreen + 86;
        var lineHeight = GetLineHeight();
        var maxLines = GetMaxVisibleLines();
        var lines = BuildRenderedLines();
        if (lines.Count == 0)
            return;

        var maxStart = Math.Max(0, lines.Count - maxLines);
        var start = Math.Clamp(lines.Count - maxLines - _scrollOffset, 0, maxStart);
        var y = top;
        foreach (var line in lines.Skip(start).Take(maxLines))
        {
            if (!string.IsNullOrEmpty(line.Text))
                b.DrawString(Game1.smallFont, line.Text, new Vector2(xPositionOnScreen + 36, y), line.Color);
            y += lineHeight;
        }

        if (_scrollOffset > 0)
            b.DrawString(Game1.smallFont, "↑ 滚轮/PageUp 查看更早记录，End 回到底部", new Vector2(xPositionOnScreen + 36, yPositionOnScreen + height - 108), Color.DimGray);
    }

    private List<RenderedLine> BuildRenderedLines()
    {
        var lines = new List<RenderedLine>();
        var visibleWidth = width - 72;

        foreach (var message in _history.Messages)
        {
            var label = message.Role == "user" ? "你" : "AI";
            var color = message.Role == "user" ? Color.DarkSlateBlue : Game1.textColor;
            foreach (var line in Wrap($"{label}: {message.Text}", Game1.smallFont, visibleWidth))
                lines.Add(new RenderedLine(line, color));

            lines.Add(new RenderedLine("", color));
        }

        return lines;
    }

    private int GetLineHeight()
    {
        return (int)Game1.smallFont.MeasureString("星露谷").Y + 4;
    }

    private int GetMaxVisibleLines()
    {
        var top = yPositionOnScreen + 86;
        var bottom = yPositionOnScreen + height - 116;
        return Math.Max(1, (bottom - top) / GetLineHeight());
    }

    private static List<string> Wrap(string text, SpriteFont font, int maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
            WrapParagraph(paragraph, font, maxWidth, lines);

        return lines;
    }

    private static void WrapParagraph(string text, SpriteFont font, int maxWidth, List<string> lines)
    {
        if (string.IsNullOrEmpty(text))
        {
            lines.Add("");
            return;
        }

        var current = "";
        for (var i = 0; i < text.Length; i++)
        {
            var candidate = current + text[i];
            if (font.MeasureString(candidate).X <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(current))
                lines.Add(current.TrimEnd());

            current = "";
            i--;
        }

        if (!string.IsNullOrWhiteSpace(current))
            lines.Add(current.TrimEnd());
    }

    private sealed record RenderedLine(string Text, Color Color);
}
