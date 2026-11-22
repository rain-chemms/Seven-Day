using System;
using System.Collections.Generic;
using System.Linq;
using SevenDay.Core;

namespace SevenDay.Battle;

/// <summary>天气类型。None 表示无特殊天气。</summary>
public enum WeatherType
{
    None = 0,
    Thunderstorm = 1,
    Tornado = 2,
    Sandstorm = 3,
    Earthquake = 4,
    ClearSky = 5,
    Moonbow = 6,
    Aurora = 7,
    GiftRain = 8,
}

public sealed class WeatherDef
{
    public WeatherType Type;
    public string Id = "";
    public string NameZh = "";
    public string NameEn = "";
    public string DescZh = "";
    public string DescEn = "";
    public int Duration;
    public bool Positive;
    public string Icon = "";

    public string Name => Loc.Name(NameZh, NameEn);
    public string Desc => Loc.Name(DescZh, DescEn);
}

public static class WeatherCatalog
{
    public static readonly WeatherDef[] All =
    {
        new()
        {
            Type = WeatherType.Thunderstorm, Id = "thunderstorm",
            NameZh = "雷雨", NameEn = "Thunderstorm", Duration = 3, Positive = false,
            DescZh = "每回合随机 5 个格子落下闪电，对格内所有军队和建筑造成 175 点伤害。",
            DescEn = "Each round lightning strikes 5 random hexes, dealing 175 damage to all units and buildings there.",
            Icon = "res://gameAsset/textures/ui/weather/weatherIcon/weather_thunderstorm.png",
        },
        new()
        {
            Type = WeatherType.Tornado, Id = "tornado",
            NameZh = "龙卷风", NameEn = "Tornado", Duration = 5, Positive = false,
            DescZh = "龙卷风每回合先向随机方向移动 1 格，再对范围内单位造成其当前生命值 50% 的伤害（至少 1）。",
            DescEn = "Each round the tornado moves one hex in a random direction, then deals 50% of current HP (min 1) to units in range 1.",
            Icon = "res://gameAsset/textures/ui/weather/weatherIcon/weather_tornado.png",
        },
        new()
        {
            Type = WeatherType.Sandstorm, Id = "sandstorm",
            NameZh = "沙尘暴", NameEn = "Sandstorm", Duration = 4, Positive = false,
            DescZh = "所有单位移动力和攻击距离 -1；沙地上的军队每回合受到最大生命值 20% 的伤害。",
            DescEn = "All units lose 1 move and attack range. Units on sand take 20% max HP damage each round.",
            Icon = "res://gameAsset/textures/ui/weather/weatherIcon/weather_sandstorm.png",
        },
        new()
        {
            Type = WeatherType.Earthquake, Id = "earthquake",
            NameZh = "地震", NameEn = "Earthquake", Duration = 1, Positive = false,
            DescZh = "随机摧毁 4 栋非基地建筑，不返还金币。",
            DescEn = "Destroys 4 random non-HQ buildings without refunding gold.",
            Icon = "res://gameAsset/textures/ui/weather/weatherIcon/weather_earthquake.png",
        },
        new()
        {
            Type = WeatherType.ClearSky, Id = "clear_sky",
            NameZh = "快晴", NameEn = "Clear Sky", Duration = 4, Positive = true,
            DescZh = "所有军队移动力和攻击距离 +1。",
            DescEn = "All units gain 1 move and attack range.",
            Icon = "res://gameAsset/textures/ui/weather/weatherIcon/weather_clear_sky.png",
        },
        new()
        {
            Type = WeatherType.Moonbow, Id = "moonbow",
            NameZh = "月虹", NameEn = "Moonbow", Duration = 2, Positive = true,
            DescZh = "每打出一张牌，抽一张牌。",
            DescEn = "Whenever you play a card, draw a card.",
            Icon = "res://gameAsset/textures/ui/weather/weatherIcon/weather_moonbow.png",
        },
        new()
        {
            Type = WeatherType.Aurora, Id = "aurora",
            NameZh = "极光", NameEn = "Aurora", Duration = 6, Positive = true,
            DescZh = "所有建筑和军队每回合恢复最大生命值 30% 的血量。",
            DescEn = "All buildings and units heal 30% max HP each round.",
            Icon = "res://gameAsset/textures/ui/weather/weatherIcon/weather_aurora.png",
        },
        new()
        {
            Type = WeatherType.GiftRain, Id = "gift_rain",
            NameZh = "圣诞礼物雨", NameEn = "Gift Rain", Duration = 1, Positive = true,
            DescZh = "随机 5 个格子落下礼物，军队走上去即可拾取，每个价值 800 金币。",
            DescEn = "Gifts fall on 5 random hexes. A unit moving onto one collects it for 800 gold.",
            Icon = "res://gameAsset/textures/ui/weather/weatherIcon/weather_gift_rain.png",
        },
    };

    private static readonly Dictionary<WeatherType, WeatherDef> Map = new();

    static WeatherCatalog()
    {
        foreach (WeatherDef d in All)
            Map[d.Type] = d;
    }

    public static WeatherDef Get(WeatherType type) =>
        Map.TryGetValue(type, out WeatherDef def) ? def : null;

    public static WeatherType RandomType(Random rng, int mask = 0x1FE)
    {
        if (rng == null)
            return WeatherType.None;

        var candidates = All
            .Where(d => (mask & (1 << (int)d.Type)) != 0)
            .ToArray();
        if (candidates.Length == 0)
            return WeatherType.None;

        // 每次切换天气等概率抽取；持续时间差异由各天气自身设计决定。
        return candidates[rng.Next(candidates.Length)].Type;
    }
}
