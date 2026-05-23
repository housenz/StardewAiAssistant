using System.Reflection;
using StardewValley;

namespace StardewAiAssistant.Services;

public sealed class NpcLocationService
{
    public string TryGetCurrentLocation(string npcName)
    {
        try
        {
            var npc = typeof(Game1).GetMethod("getCharacterFromName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(bool) }, null)
                ?.Invoke(null, new object[] { npcName, false });

            npc ??= typeof(Game1).GetMethod("getCharacterFromName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
                ?.Invoke(null, new object[] { npcName });

            if (npc is null)
                return "";

            var currentLocation = ReadValue(npc, "currentLocation");
            if (currentLocation is null)
                return "";

            var display = ReadString(currentLocation, "DisplayName", "");
            var name = ReadString(currentLocation, "Name", "");
            var unique = ReadString(currentLocation, "NameOrUniqueName", "");

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(display))
                parts.Add(display);
            if (!string.IsNullOrWhiteSpace(name) && !parts.Contains(name))
                parts.Add(name);
            if (!string.IsNullOrWhiteSpace(unique) && !parts.Contains(unique))
                parts.Add(unique);

            return parts.Count == 0 ? currentLocation.GetType().Name : string.Join(" / ", parts);
        }
        catch
        {
            return "";
        }
    }

    private static object? ReadValue(object? target, string name)
    {
        if (target is null)
            return null;

        var type = target.GetType();
        return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
    }

    private static string ReadString(object? target, string name, string fallback)
    {
        return ReadValue(target, name)?.ToString() ?? fallback;
    }
}
