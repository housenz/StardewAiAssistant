using System.Text;
using StardewAiAssistant.Models;
using StardewModdingAPI;

namespace StardewAiAssistant.Services;

public sealed class AgentDebugLogger
{
    private readonly ModConfig _config;
    private readonly IMonitor _monitor;
    private readonly string _logPath;
    private readonly object _lock = new();

    public AgentDebugLogger(ModConfig config, IMonitor monitor, string modDirectoryPath)
    {
        _config = config;
        _monitor = monitor;
        _logPath = Path.Combine(modDirectoryPath, "StardewAiAssistant-debug.txt");

        if (_config.EnableDebugLogging)
            Log("debug", $"Debug logging enabled. Log file: {_logPath}");
    }

    public void Log(string phase, string message)
    {
        if (!_config.EnableDebugLogging)
            return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{phase}] {message}";

        try
        {
            lock (_lock)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to write AI debug log: {ex.Message}", LogLevel.Warn);
        }

        _monitor.Log($"[AI debug:{phase}] {TrimForSmapi(message)}", LogLevel.Trace);
    }

    public void Separator(string title)
    {
        if (!_config.EnableDebugLogging)
            return;

        Log("session", "");
        Log("session", "============================================================");
        Log("session", title);
        Log("session", "============================================================");
    }

    private static string TrimForSmapi(string message)
    {
        message = message.Replace("\r", " ").Replace("\n", " ");
        return message.Length <= 300 ? message : message[..300] + "...";
    }
}
