using System.Collections;
using System.Reflection;
using Microsoft.Xna.Framework;
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
            TomorrowDayOfWeek = GetTomorrowDayOfWeek(),
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
            FarmCrops = BuildFarmCrops(),
            Wallet = BuildWallet(farmer),
            WorldProgress = BuildWorldProgress(farmer),
            FarmBuildings = BuildFarmBuildings(),
            FarmAnimals = BuildFarmAnimals(),
            Recipes = BuildRecipes(farmer),
            Collections = BuildCollections(farmer),
            KeyFlags = BuildKeyFlags(farmer),
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

        var rawWeather = candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "unknown";
        return NormalizeWeather(rawWeather);
    }

    private static string GetDayOfWeek()
    {
        return GetDayOfWeek(Game1.dayOfMonth);
    }

    private static string GetTomorrowDayOfWeek()
    {
        return GetDayOfWeek(Game1.dayOfMonth + 1);
    }

    private static string GetDayOfWeek(int dayOfMonth)
    {
        return (dayOfMonth % 7) switch
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

    private static string NormalizeWeather(string weather)
    {
        return weather.Trim().ToLowerInvariant() switch
        {
            "sun" or "sunny" => "sunny",
            "rain" or "rainy" => "rain",
            "storm" or "lightning" => "storm",
            "snow" or "snowy" => "snow",
            "wind" or "windy" => "wind",
            "festival" => "festival",
            "greenrain" or "green_rain" => "green_rain",
            "" => "unknown",
            var value => value
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

        var levels = string.Join("，", skills.Select(skill => $"{skill.Item1}={ReadInt(farmer, skill.Item2)?.ToString() ?? "unknown"}"));
        return $"{levels}；职业={BuildProfessions(farmer)}";
    }

    private static string BuildInventory(object? farmer)
    {
        var items = ReadValue(farmer, "Items") as IEnumerable;
        if (items is null)
            return "unknown";

        var names = new List<string>();
        var usedSlots = 0;
        var enumeratedSlots = 0;
        foreach (var item in items)
        {
            enumeratedSlots++;
            if (item is null)
                continue;

            usedSlots++;
            var name = ReadString(item, "DisplayName", ReadString(item, "Name", "unknown"));
            var stack = ReadInt(item, "Stack") ?? 1;
            var quality = FormatQuality(ReadInt(item, "Quality"));
            names.Add(stack > 1 ? $"{name}{quality} x{stack}" : $"{name}{quality}");
        }

        var preview = names.Count == 0 ? "空" : string.Join("，", names.Take(18));
        if (names.Count > 18)
            preview += $"，另有 {names.Count - 18} 项";

        var capacity = ReadInventoryCapacity(farmer) ?? enumeratedSlots;
        return $"已用 {usedSlots} 格，容量 {capacity} 格；{preview}";
    }

    private static string BuildFarmCrops()
    {
        var farm = ReadStaticValue(typeof(Game1), "farm")
            ?? typeof(Game1).GetMethod("getFarm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, Array.Empty<object>());
        if (farm is null)
            return "无法读取农场对象";

        var terrainFeatures = ReadValue(farm, "terrainFeatures") as IEnumerable;
        if (terrainFeatures is null)
            terrainFeatures = ReadValue(ReadValue(farm, "terrainFeatures"), "Pairs") as IEnumerable;
        if (terrainFeatures is null)
            return "无法枚举农场 terrainFeatures";

        var crops = new List<CropInfo>();
        var hoeDirtCount = 0;
        foreach (var entry in terrainFeatures)
        {
            var feature = ReadProperty(entry, "Value");
            feature ??= entry;
            if (feature is null || !feature.GetType().Name.Contains("HoeDirt", StringComparison.OrdinalIgnoreCase))
                continue;

            hoeDirtCount++;
            var crop = ReadValue(feature, "crop");
            if (crop is null)
                continue;

            var key = ReadProperty(entry, "Key");
            var tile = FormatTile(key);
            var cropName = GetCropName(crop);
            var watered = ReadInt(feature, "state") > 0 || ReadBool(feature, "isWatered") is true;
            var daysLeft = EstimateCropDaysLeft(crop);
            crops.Add(new CropInfo(cropName, watered, daysLeft, tile));
        }

        if (crops.Count == 0)
            return hoeDirtCount == 0
                ? "已读取农场 terrainFeatures，但未发现耕地或已种植作物"
                : $"已读取到 {hoeDirtCount} 块耕地，但没有检测到 crop 数据";

        var grouped = crops
            .GroupBy(crop => new { crop.Name, crop.Watered, crop.DaysLeft })
            .OrderByDescending(group => group.Count())
            .Take(12)
            .Select(group =>
            {
                var watered = group.Key.Watered ? "已浇水" : "未浇水";
                var days = group.Key.DaysLeft is null ? "成熟时间未知" : group.Key.DaysLeft <= 0 ? "可收获或已成熟" : $"约 {group.Key.DaysLeft} 天成熟";
                var sampleTiles = string.Join("、", group.Take(4).Select(crop => crop.Tile));
                return $"{group.Key.Name} x{group.Count()}，{watered}，{days}，示例位置={sampleTiles}";
            });

        return string.Join("；", grouped);
    }

    private static int? ReadInventoryCapacity(object? farmer)
    {
        var candidates = new[]
        {
            ReadInt(farmer, "MaxItems"),
            ReadInt(farmer, "maxItems"),
            ReadInt(farmer, "MaxInventoryItems"),
            ReadInt(farmer, "maxInventoryItems")
        };

        var value = candidates.FirstOrDefault(candidate => candidate is > 0);
        if (value is not null)
            return value;

        var items = ReadValue(farmer, "Items");
        return ReadInt(items, "Capacity") ?? ReadInt(items, "Count");
    }

    private static string BuildProfessions(object? farmer)
    {
        var professions = ReadValue(farmer, "professions") as IEnumerable;
        if (professions is null)
            return "unknown";

        var names = new List<string>();
        foreach (var profession in professions)
        {
            var id = ConvertValue<int>(profession);
            if (id is not null)
                names.Add(ProfessionName(id.Value));
        }

        return names.Count == 0 ? "无或无法读取" : string.Join("，", names);
    }

    private static string ProfessionName(int id)
    {
        return id switch
        {
            0 => "牧场主",
            1 => "农耕人",
            2 => "鸡舍大师",
            3 => "牧羊人",
            4 => "工匠",
            5 => "农业学家",
            6 => "渔夫",
            7 => "捕猎者",
            8 => "垂钓者",
            9 => "海盗",
            10 => "水手",
            11 => "诱饵大师",
            12 => "护林人",
            13 => "收集者",
            14 => "伐木工",
            15 => "萃取者",
            16 => "植物学家",
            17 => "追踪者",
            18 => "矿工",
            19 => "地质学家",
            20 => "铁匠",
            21 => "勘探者",
            22 => "挖掘者",
            23 => "宝石专家",
            24 => "战士",
            25 => "侦查员",
            26 => "野兽猎人",
            27 => "防御者",
            28 => "杂技演员",
            29 => "亡命徒",
            _ => $"未知职业({id})"
        };
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
        var jojaMember = ToYesNo(HasAnyMail(farmer, "JojaMember"));
        var desert = ToYesNo(HasAnyMail(farmer, "ccVault", "jojaVault"));
        var greenhouse = ToYesNo(HasAnyMail(farmer, "ccPantry", "jojaPantry"));
        var communityCenter = ToYesNo(HasAnyMail(farmer, "ccComplete"));
        var gingerIsland = ToYesNo(HasAnyMail(farmer, "willyBoatFixed", "Visited_Island", "islandNorthCaveOpened"));

        return $"矿洞已解锁={mineUnlocked}，矿洞最深层={deepestMineLevel}，骷髅洞钥匙={skullKey}，Joja会员={jojaMember}，沙漠={desert}，下水道={sewer}，温室={greenhouse}，社区中心完成={communityCenter}，姜岛={gingerIsland}";
    }

    private static string BuildFarmBuildings()
    {
        var farm = ReadStaticValue(typeof(Game1), "farm")
            ?? typeof(Game1).GetMethod("getFarm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, Array.Empty<object>());
        if (farm is null)
            return "无法读取农场对象";

        var buildings = ReadValue(farm, "buildings") as IEnumerable;
        if (buildings is null)
            return "无法读取建筑列表";

        var entries = new List<string>();
        foreach (var building in buildings)
        {
            var type = ReadString(building, "buildingType", ReadString(building, "BuildingType", building?.GetType().Name ?? "未知建筑"));
            var tileX = ReadInt(building, "tileX") ?? ReadInt(building, "tileXOverride");
            var tileY = ReadInt(building, "tileY") ?? ReadInt(building, "tileYOverride");
            var days = ReadInt(building, "daysUntilUpgrade") ?? ReadInt(building, "daysOfConstructionLeft");
            var indoors = ReadValue(building, "indoors");
            var animalCount = CountEnumerable(ReadValue(indoors, "animals"));
            var detail = $"{type}";
            if (tileX is not null && tileY is not null)
                detail += $"@({tileX},{tileY})";
            if (days is > 0)
                detail += $"，建设/升级剩余{days}天";
            if (animalCount > 0)
                detail += $"，动物{animalCount}只";
            entries.Add(detail);
        }

        return entries.Count == 0 ? "无建筑或无法检测到建筑" : string.Join("；", entries.Take(20));
    }

    private static string BuildFarmAnimals()
    {
        var farm = ReadStaticValue(typeof(Game1), "farm")
            ?? typeof(Game1).GetMethod("getFarm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, Array.Empty<object>());
        if (farm is null)
            return "无法读取农场对象";

        var animals = new List<string>();
        CollectAnimals(ReadValue(farm, "animals"), animals);

        var buildings = ReadValue(farm, "buildings") as IEnumerable;
        if (buildings is not null)
        {
            foreach (var building in buildings)
            {
                var indoors = ReadValue(building, "indoors");
                CollectAnimals(ReadValue(indoors, "animals"), animals);
            }
        }

        if (animals.Count == 0)
            return "无动物或无法检测到动物";

        return string.Join("；", animals.GroupBy(value => value).OrderByDescending(group => group.Count()).Select(group => $"{group.Key} x{group.Count()}").Take(20));
    }

    private static void CollectAnimals(object? source, List<string> output)
    {
        if (source is not IEnumerable enumerable)
            return;

        foreach (var item in enumerable)
        {
            var animal = ReadProperty(item, "Value") ?? item;
            var type = ReadString(animal, "type", ReadString(animal, "displayType", ReadString(animal, "DisplayType", animal?.GetType().Name ?? "未知动物")));
            var name = ReadString(animal, "displayName", ReadString(animal, "Name", ""));
            output.Add(string.IsNullOrWhiteSpace(name) || name == type ? type : $"{name}({type})");
        }
    }

    private static string BuildRecipes(object? farmer)
    {
        var cooking = CountEnumerable(ReadValue(farmer, "cookingRecipes"));
        var crafting = CountEnumerable(ReadValue(farmer, "craftingRecipes"));
        return $"烹饪配方已学={FormatCount(cooking)}，制作配方已学={FormatCount(crafting)}";
    }

    private static string BuildCollections(object? farmer)
    {
        var shipped = CountEnumerable(ReadValue(farmer, "basicShipped"));
        var fish = CountEnumerable(ReadValue(farmer, "fishCaught"));
        var minerals = CountEnumerable(ReadValue(farmer, "mineralsFound"));
        var artifacts = CountEnumerable(ReadValue(farmer, "archaeologyFound"));
        var cooked = CountEnumerable(ReadValue(farmer, "recipesCooked"));
        var crafted = CountEnumerable(ReadValue(farmer, "craftingRecipes"));
        return $"出货条目={FormatCount(shipped)}，钓鱼图鉴={FormatCount(fish)}，矿物={FormatCount(minerals)}，古物={FormatCount(artifacts)}，做过料理={FormatCount(cooked)}，已学制作配方={FormatCount(crafted)}";
    }

    private static string BuildKeyFlags(object? farmer)
    {
        var checks = new[]
        {
            ("Joja会员", HasAnyMail(farmer, "JojaMember")),
            ("社区中心开启", HasAnyMail(farmer, "ccDoorUnlock", "seenJunimoNote")),
            ("社区中心完成", HasAnyMail(farmer, "ccComplete")),
            ("温室修复", HasAnyMail(farmer, "ccPantry", "jojaPantry")),
            ("矿车修复", HasAnyMail(farmer, "ccBoilerRoom", "jojaBoilerRoom")),
            ("巴士修复", HasAnyMail(farmer, "ccVault", "jojaVault")),
            ("桥梁修复", HasAnyMail(farmer, "ccCraftsRoom", "jojaCraftsRoom")),
            ("淘金盘", HasAnyMail(farmer, "ccFishTank", "jojaFishTank")),
            ("威利的船", HasAnyMail(farmer, "willyBoatFixed")),
            ("姜岛到达", HasAnyMail(farmer, "Visited_Island")),
            ("矮人语", ReadBool(farmer, "canUnderstandDwarves") is true),
            ("下水道钥匙", ReadBool(farmer, "hasRustyKey") is true),
            ("骷髅钥匙", ReadBool(farmer, "hasSkullKey") is true),
            ("赌场会员卡", ReadBool(farmer, "hasClubCard") is true)
        };

        return string.Join("，", checks.Select(check => $"{check.Item1}={(check.Item2 ? "是" : "未确认")}"));
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
            {
                var giftsToday = ReadInt(value, "GiftsToday");
                var giftsThisWeek = ReadInt(value, "GiftsThisWeek");
                entries.Add($"{key}:{points / 250}心({points}好感，今日送礼={giftsToday?.ToString() ?? "未知"}，本周送礼={giftsThisWeek?.ToString() ?? "未知"})");
            }
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

    private static int CountEnumerable(object? collection)
    {
        if (collection is not IEnumerable enumerable)
            return -1;

        var count = 0;
        foreach (var _ in enumerable)
            count++;
        return count;
    }

    private static string FormatCount(int count)
    {
        return count < 0 ? "unknown" : count.ToString();
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
        target = UnwrapNetValue(target);
        if (target is null)
            return null;

        var type = target.GetType();
        var value = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
        return UnwrapNetValue(value);
    }

    private static object? ReadProperty(object? target, string name)
    {
        target = UnwrapNetValue(target);
        return UnwrapNetValue(target?.GetType().GetProperty(name)?.GetValue(target));
    }

    private static string ReadString(object? target, string name, string fallback) => ReadValue(target, name)?.ToString() ?? fallback;

    private static int? ReadInt(object? target, string name) => ConvertValue<int>(ReadValue(target, name));

    private static float? ReadFloat(object? target, string name) => ConvertValue<float>(ReadValue(target, name));

    private static double? ReadDouble(object? target, string name) => ConvertValue<double>(ReadValue(target, name));

    private static bool? ReadBool(object? target, string name) => ConvertValue<bool>(ReadValue(target, name));

    private static T? ConvertValue<T>(object? value) where T : struct
    {
        value = UnwrapNetValue(value);
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

    private static object? UnwrapNetValue(object? value)
    {
        var visited = 0;
        while (value is not null && visited++ < 4)
        {
            var type = value.GetType();
            if (!type.FullName?.StartsWith("Netcode.", StringComparison.OrdinalIgnoreCase) ?? true)
                return value;

            var property = type.GetProperty("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property is null)
                return value;

            value = property.GetValue(value);
        }

        return value;
    }

    private static string FormatQuality(int? quality)
    {
        return quality switch
        {
            1 => "[银星]",
            2 => "[金星]",
            4 => "[铱星]",
            _ => ""
        };
    }

    private static string FormatTile(object? key)
    {
        key = UnwrapNetValue(key);
        if (key is Vector2 vector)
            return $"({(int)vector.X},{(int)vector.Y})";

        var x = ReadProperty(key, "X")?.ToString() ?? "?";
        var y = ReadProperty(key, "Y")?.ToString() ?? "?";
        return $"({x},{y})";
    }

    private static string GetCropName(object crop)
    {
        var harvestId = ReadInt(crop, "indexOfHarvest")?.ToString()
            ?? ReadValue(crop, "netFruitIndex")?.ToString()
            ?? ReadValue(crop, "netSeedIndex")?.ToString();

        if (string.IsNullOrWhiteSpace(harvestId))
            return ReadString(crop, "Name", "未知作物");

        var objectData = ReadStaticValue(typeof(Game1), "objectData") as IEnumerable;
        if (objectData is not null)
        {
            foreach (var entry in objectData)
            {
                if (!string.Equals(ReadProperty(entry, "Key")?.ToString(), harvestId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var data = ReadProperty(entry, "Value");
                var displayName = ReadString(data, "DisplayName", "");
                var name = ReadString(data, "Name", "");
                return !string.IsNullOrWhiteSpace(displayName) ? displayName : !string.IsNullOrWhiteSpace(name) ? name : $"物品ID {harvestId}";
            }
        }

        return $"作物/收获物ID {harvestId}";
    }

    private static int? EstimateCropDaysLeft(object crop)
    {
        if (ReadBool(crop, "fullyGrown") is true)
            return 0;

        var phaseDays = ReadValue(crop, "phaseDays") as IEnumerable;
        if (phaseDays is null)
            return null;

        var currentPhase = ReadInt(crop, "currentPhase") ?? ReadInt(crop, "phaseToShow") ?? 0;
        var dayOfCurrentPhase = ReadInt(crop, "dayOfCurrentPhase") ?? 0;
        var days = new List<int>();
        foreach (var item in phaseDays)
        {
            var day = ConvertValue<int>(item);
            if (day is not null)
                days.Add(day.Value);
        }
        if (days.Count == 0 || currentPhase >= days.Count)
            return null;

        var remaining = Math.Max(0, days[currentPhase] - dayOfCurrentPhase);
        for (var i = currentPhase + 1; i < days.Count; i++)
            remaining += days[i];

        return remaining;
    }

    private sealed record CropInfo(string Name, bool Watered, int? DaysLeft, string Tile);
}
