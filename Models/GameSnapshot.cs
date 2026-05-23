namespace StardewAiAssistant.Models;

public sealed class GameSnapshot
{
    public string Season { get; set; } = "unknown";
    public string Weather { get; set; } = "unknown";
    public string TomorrowWeather { get; set; } = "unknown";
    public int TimeOfDay { get; set; }
    public int DayOfMonth { get; set; }
    public int Year { get; set; }
    public string DayOfWeek { get; set; } = "unknown";
    public string CurrentLocation { get; set; } = "unknown";
    public string PlayerName { get; set; } = "Player";
    public string FarmName { get; set; } = "unknown";
    public int X { get; set; }
    public int Y { get; set; }
    public string DailyLuck { get; set; } = "unknown";
    public string Money { get; set; } = "unknown";
    public string TotalMoneyEarned { get; set; } = "unknown";
    public string Health { get; set; } = "unknown";
    public string Stamina { get; set; } = "unknown";
    public string Skills { get; set; } = "unknown";
    public string Inventory { get; set; } = "unknown";
    public string FarmCrops { get; set; } = "unknown";
    public string Wallet { get; set; } = "unknown";
    public string WorldProgress { get; set; } = "unknown";
    public string Stardrops { get; set; } = "unknown";
    public string Social { get; set; } = "unknown";
    public string ActiveQuests { get; set; } = "unknown";

    public string ToPromptContext()
    {
        return string.Join(
            "\n",
            "当前游戏状态：",
            $"- 时间：第 {Year} 年，{Season}，第 {DayOfMonth} 天，星期={DayOfWeek}，时刻={TimeOfDay}",
            $"- 天气：今天={Weather}，明天={TomorrowWeather}",
            $"- 玩家：{PlayerName}，农场={FarmName}，当前位置={CurrentLocation}，坐标=({X},{Y})",
            $"- 运气：{DailyLuck}",
            $"- 金钱：当前={Money}，历史总收入={TotalMoneyEarned}",
            $"- 生命/体力：生命={Health}，体力={Stamina}",
            $"- 技能：{Skills}",
            $"- 背包：{Inventory}",
            $"- 农场作物：{FarmCrops}",
            $"- 钱包/能力：{Wallet}",
            $"- 世界进度：{WorldProgress}",
            $"- 星之果实：{Stardrops}",
            $"- 社交：{Social}",
            $"- 当前任务：{ActiveQuests}"
        );
    }
}
