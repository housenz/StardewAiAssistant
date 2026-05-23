using StardewAiAssistant.Models;

namespace StardewAiAssistant.Services;

public sealed class ChatHistoryService
{
    private readonly List<ChatMessage> _messages = new();

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public ChatHistoryService()
    {
        _messages.Add(new ChatMessage("assistant", "输入问题后按 Enter。例：现在海莉在哪里？"));
    }

    public void Add(string role, string text)
    {
        _messages.Add(new ChatMessage(role, text));
    }

    public void Clear()
    {
        _messages.Clear();
        _messages.Add(new ChatMessage("assistant", "历史记录已清空。"));
    }
}
