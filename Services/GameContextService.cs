using System.Collections;
using System.Reflection;
using StardewAiAssistant.Models;
using StardewValley;

namespace StardewAiAssistant.Services;

public sealed class GameContextService
{
    public GameSnapshot Capture()
    {
        var farmer = Game1.player;
        var location = Game1.currentLocation;

        return new GameSnapshot
        {
            Season = NormalizeSeason(Game1.currentSeason),
            Weather = GetWeather(),
            TomorrowWeather = GetTomorrowWeather(),
            TimeOfDay = Game1.timeOfDay,
            DayOfMonth = Game1.dayOfMonth,
            Year = Game1.year,
            DayOfWeek = GetDayOfWeek(),
            CurrentLocation = BuildLocationDescription(location),
            PlayerName = ReadString(farmer, "Name", "Player"),
            FarmName = ReadString(farmer, "farmName", "unknown"),
            X = farmer?.TilePoint.X ?? 0,
            Y = farmer?.TilePoint.Y ?? 0,
            DailyLuck = FormatDouble(ReadDouble(Game1.player, "DailyLuck")),
            Money = ReadInt(farmer, "Money")?.ToString() ?? "unknown",
            TotalMoneyEarned = ReadInt(farmer, "totalMoneyEarned")?.ToString() ?? "unknown",
            Health = FormatPair(ReadInt(farmer, "health"), ReadInt(farmer, "maxHealth")),
            Stamina = FormatPair(ReadFloat(farmer, "stamina"), ReadInt(farmer, "MaxStamina")),
            Skills = BuildSkills(farmer),
            Inventory = BuildInventory(farmer),
            Wallet = BuildWallet(farmer),
            WorldProgress = BuildWorldProgress(farmer),
            Stardrops = BuildStardrops(farmer),
            Social = BuildSocial(farmer),
            ActiveQuests = BuildActiveQuests(farmer)
        };
    }

    private static string NormalizeSeason(string season)
    {
        return season.ToLowerInvariant() switch
        {
            "spring" => "spring",
            "summer" => "summer",
            "fall" => "fall",
            "winter" => "winter",
            var value => value
        };
    }

    private static string GetWeather()
    {
        if (ReadStaticBool(typeof(Game1), "isGreenRain"))
            return "green_rain";
        if (Game1.isLightning)
            return "storm";
        if (Game1.isRaining)
            return "rain";
        if (Game1.isSnowing)
            return "snow";
        return "sunny";
    }

    private static string GetTomorrowWeather()
    {
        var candidates = new[]
        {
            ReadStaticString(typeof(Game1), "weatherForTomorrow"),
            ReadNestedString(ReadStaticValue(typeof(Game1), "netWorldState"), "Value", "WeatherForTomorrow"),
            ReadNestedString(ReadStaticValue(typeof(Game1), "worldState"), "WeatherForTomorrow")
        };

        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "unknown";
    }

    private static string GetDayOfWeek()
    {
        return (Game1.dayOfMonth % 7) switch
        {
            1 => "Monday",
            2 => "Tuesday",
            3 => "Wednesday",
            4 => "Thursday",
            5 => "Friday",
            6 => "Saturday",
            _ => "Sunday"
        };
    }

    private static string BuildLocationDescription(object? location)
    {
        if (location is null)
            return "unknown";

        var displayName = ReadString(location, "DisplayName", "");
        var name = ReadString(location, "Name", "");
        var uniqueName = ReadString(location, "NameOrUniqueName", "");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(displayName))
            parts.Add($"display={displayName}");
        if (!string.IsNullOrWhiteSpace(name))
            parts.Add($"internal={name}");
        if (!string.IsNullOrWhiteSpace(uniqueName) && uniqueName != name)
            parts.Add($"unique={uniqueName}");

        return parts.Count == 0 ? location.GetType().Name : string.Join(", ", parts);
    }

    private static string BuildSkills(object? farmer)
    {
        var skills = new[]
        {
            ("耕种", "farmingLevel"),
            ("采矿", "miningLevel"),
            ("采集", "foragingLevel"),
            ("钓鱼", "fishingLevel"),
            ("战斗", "combatLevel")
        };

        return string.Join("，", skills.Select(skill => $"{skill.Item1}={ReadInt(farmer, skill.Item2)?.ToString() ?? "unknown"}"));
    }

    private static string BuildInventory(object? farmer)
    {
        var items = ReadValue(farmer, "Items") as IEnumerable;
        if (items is null)
            return "unknown";

        var names = new List<string>();
        var usedSlots = 0;
        var totalSlots = 0;
        foreach (var item in items)
        {
            totalSlots++;
            if (item is null)
                continue;

            usedSlots++;
            var name = ReadString(item, "DisplayName", ReadString(item, "Name", "unknown"));
            var stack = ReadInt(item, "Stack") ?? 1;
            names.Add(stack > 1 ? $"{name} x{stack}" : name);
        }

        var preview = names.Count == 0 ? "空" : string.Join("，", names.Take(18));
        if (names.Count > 18)
            preview += $"，另有 {names.Count - 18} 项";

        return $"{usedSlots}/{totalSlots} 格；{preview}";
    }

    private static string BuildWallet(object? farmer)
    {
        var flags = new[]
        {
            ("矮人语", "canUnderstandDwarves"),
            ("骷髅钥匙", "hasSkullKey"),
            ("下水道钥匙", "hasRustyKey"),
            ("放大镜", "hasMagnifyingGlass"),
            ("赌场会员卡", "hasClubCard"),
            ("特别符咒", "hasSpecialCharm"),
            ("黑暗护身符", "hasDarkTalisman"),
            ("魔法墨水", "hasMagicInk")
        };

        var values = flags.Select(flag => $"{flag.Item1}={(ReadBool(farmer, flag.Item2) is true ? "已获得" : "未确认")}");
        return string.Join("，", values);
    }

    private static string BuildWorldProgress(object? farmer)
    {
        var deepestMineLevel = ReadInt(farmer, "deepestMineLevel")?.ToString() ?? "unknown";
        var mineUnlocked = ToYesNo(ReadInt(farmer, "deepestMineLevel") > 0);
        var skullKey = ToYesNo(ReadBool(farmer, "hasSkullKey") is true);
        var sewer = ToYesNo(ReadBool(farmer, "hasRustyKey") is true);
        var desert = ToYesNo(HasAnyMail(farmer, "ccVault", "jojaVault"));
        var greenhouse = ToYesNo(HasAnyMail(farmer, "ccPantry", "jojaPantry"));
        var communityCenter = ToYesNo(HasAnyMail(farmer, "ccComplete"));
        var gingerIsland = ToYesNo(HasAnyMail(farmer, "willyBoatFixed", "Visited_Island", "islandNorthCaveOpened"));

        return $"矿洞已解锁={mineUnlocked}，矿洞最深层={deepestMineLevel}，骷髅洞钥匙={skullKey}，沙漠={desert}，下水道={sewer}，温室={greenhouse}，社区中心完成={communityCenter}，姜岛={gingerIsland}";
    }

    private static string BuildStardrops(object? farmer)
    {
        var maxStamina = ReadInt(farmer, "MaxStamina");
        var found = maxStamina is null ? "unknown" : Math.Max(0, (maxStamina.Value - 270) / 34).ToString();

        var checks = new[]
        {
            ("星露谷展览会购买", HasAnyMail(farmer, "CF_Fair", "stardropFair")),
            ("矿洞100层宝箱", HasAnyMail(farmer, "CF_Mines", "stardropMine")),
            ("配偶/室友高好感", HasAnyMail(farmer, "CF_Spouse", "CF_Krobus")),
            ("下水道科罗布斯购买", HasAnyMail(farmer, "CF_Sewer", "stardropKrobus")),
            ("博物馆全收集", HasAnyMail(farmer, "museumComplete", "CF_Museum")),
            ("钓鱼全收集", HasAnyMail(farmer, "CF_Fish", "MasterAngler")),
            ("全出货成就", HasAnyMail(farmer, "CF_FullShipment", "FullShipment"))
        };

        var known = string.Join("，", checks.Select(check => $"{check.Item1}={(check.Item2 ? "可能已获得" : "未确认")}"));
        return $"根据最大体力推断已获得数量={found}；来源状态：{known}";
    }

    private static string BuildSocial(object? farmer)
    {
        var spouse = ReadString(farmer, "spouse", "");
        var friendshipData = ReadValue(farmer, "friendshipData") as IEnumerable;
        if (friendshipData is null)
            return string.IsNullOrWhiteSpace(spouse) ? "unknown" : $"配偶/室友={spouse}";

        var entries = new List<string>();
        foreach (var entry in friendshipData)
        {
            var key = ReadProperty(entry, "Key")?.ToString();
            var value = ReadProperty(entry, "Value");
            var points = ReadInt(value, "Points");
            if (!string.IsNullOrWhiteSpace(key) && points is not null)
                entries.Add($"{key}:{points / 250}心");
        }

        var preview = entries.Count == 0 ? "unknown" : string.Join("，", entries.OrderBy(value => value).Take(20));
        if (entries.Count > 20)
            preview += $"，另有 {entries.Count - 20} 人";

        return string.IsNullOrWhiteSpace(spouse) ? preview : $"配偶/室友={spouse}；{preview}";
    }

    private static string BuildActiveQuests(object? farmer)
    {
        var quests = ReadValue(farmer, "questLog") as IEnumerable;
        if (quests is null)
            return "unknown";

        var names = new List<string>();
        foreach (var quest in quests)
        {
            var title = ReadString(quest, "questTitle", ReadString(quest, "Title", ""));
            if (!string.IsNullOrWhiteSpace(title))
                names.Add(title);
        }

        return names.Count == 0 ? "无当前任务或无法读取" : string.Join("，", names.Take(12));
    }

    private static bool HasAnyMail(object? farmer, params string[] flags)
    {
        return ContainsAny(ReadValue(farmer, "mailReceived"), flags)
            || ContainsAny(ReadValue(farmer, "eventsSeen"), flags);
    }

    private static bool ContainsAny(object? collection, params string[] values)
    {
        if (collection is not IEnumerable enumerable)
            return false;

        var set = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        foreach (var item in enumerable)
        {
            if (item is not null && set.Contains(item.ToString() ?? ""))
                return true;
        }

        return false;
    }

    private static string FormatPair(int? current, int? max)
    {
        return current is null && max is null ? "unknown" : $"{current?.ToString() ?? "?"}/{max?.ToString() ?? "?"}";
    }

    private static string FormatPair(float? current, int? max)
    {
        return current is null && max is null ? "unknown" : $"{(current is null ? "?" : ((int)current.Value).ToString())}/{max?.ToString() ?? "?"}";
    }

    private static string FormatDouble(double? value)
    {
        return value is null ? "unknown" : value.Value.ToString("0.000");
    }

    private static string ToYesNo(bool value)
    {
        return value ? "是" : "未确认";
    }

    private static object? ReadStaticValue(Type type, string name)
    {
        return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private static bool ReadStaticBool(Type type, string name) => ReadStaticValue(type, name) is true;

    private static string? ReadStaticString(Type type, string name) => ReadStaticValue(type, name)?.ToString();

    private static string? ReadNestedString(object? root, params string[] path)
    {
        var current = root;
        foreach (var part in path)
            current = ReadValue(current, part);
        return current?.ToString();
    }

    private static object? ReadValue(object? target, string name)
    {
        if (target is null)
            return null;

        var type = target.GetType();
        return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
    }

    private static object? ReadProperty(object? target, string name) => target?.GetType().GetProperty(name)?.GetValue(target);

    private static string ReadString(object? target, string name, string fallback) => ReadValue(target, name)?.ToString() ?? fallback;

    private static int? ReadInt(object? target, string name) => ConvertValue<int>(ReadValue(target, name));

    private static float? ReadFloat(object? target, string name) => ConvertValue<float>(ReadValue(target, name));

    private static double? ReadDouble(object? target, string name) => ConvertValue<double>(ReadValue(target, name));

    private static bool? ReadBool(object? target, string name) => ConvertValue<bool>(ReadValue(target, name));

    private static T? ConvertValue<T>(object? value) where T : struct
    {
        if (value is T typed)
            return typed;

        if (value is not IConvertible convertible)
            return null;

        try
        {
            return (T)Convert.ChangeType(convertible, typeof(T));
        }
        catch
        {
            return null;
        }
    }
}
