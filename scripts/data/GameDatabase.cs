using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using SevenDay.Core;

namespace SevenDay.Data;

public sealed class UnitDef
{
    public string Id { get; set; } = "";
    public string NameZh { get; set; } = "";
    public string NameEn { get; set; } = "";
    public int Hp { get; set; } = 100;
    public int Damage { get; set; } = 50;
    public int Move { get; set; } = 1;
    public int Range { get; set; } = 1;
    public bool CanHitAir { get; set; }
    public bool CanHitGround { get; set; } = true;
    public bool IsAir { get; set; }

    /// <summary>
    /// 自杀式单位（FPV 无人机）：攻击时冲到目标格自爆，造成伤害后自己也死亡。
    /// </summary>
    public bool Suicide { get; set; }

    public string Model { get; set; } = "";
}

public sealed class BuildingDef
{
    public string Id { get; set; } = "";
    public string NameZh { get; set; } = "";
    public string NameEn { get; set; } = "";
    public int[] Hp { get; set; } = { 100, 100, 100 };
    public int[] Income { get; set; } = { 0, 0, 0 };
    /// <summary>升到该等级需要的金币；索引 0 是建造价。</summary>
    public int[] UpgradeCost { get; set; } = { 0, 0, 0 };
    public int[] ControlRadius { get; set; } = { 1, 1, 1 };
    public int[] AttackDamage { get; set; } = { 0, 0, 0 };
    /// <summary>攻击距离（格），0 表示不能攻击。</summary>
    public int AttackRange { get; set; }
    public bool CanHitAir { get; set; }
    public bool CanHitGround { get; set; } = true;
    public string Model { get; set; } = "";
    public string Preview { get; set; } = "";

    /// <summary>花多少钱可以让这座防御建筑在当回合多开一次炮；0 = 不支持。</summary>
    public int ExtraFireCost { get; set; }

    /// <summary>
    /// 需要隐藏的部件名关键字（例如导弹发射井里的 Missile / Flame）：
    /// 这些是「弹药」而不是建筑本体，摆在格子上会穿到平台下面去。
    /// </summary>
    public string[] HiddenParts { get; set; } = System.Array.Empty<string>();

    public int MaxLevel => Hp.Length;

    public int HpAt(int level) => Hp[Mathf.Clamp(level - 1, 0, Hp.Length - 1)];
    public int IncomeAt(int level) => Income[Mathf.Clamp(level - 1, 0, Income.Length - 1)];
    public int ControlAt(int level) => ControlRadius[Mathf.Clamp(level - 1, 0, ControlRadius.Length - 1)];
    public int DamageAt(int level) => AttackDamage[Mathf.Clamp(level - 1, 0, AttackDamage.Length - 1)];
    public int UpgradeCostAt(int level) => UpgradeCost[Mathf.Clamp(level, 0, UpgradeCost.Length - 1)];
}

public sealed class CardDef
{
    public string Id { get; set; } = "";
    public string NameZh { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string DescriptionZh { get; set; } = "";
    public string DescriptionEn { get; set; } = "";
    public string Kind { get; set; } = "unit";
    public string Unit { get; set; } = "";
    public string Building { get; set; } = "";
    public int Cost { get; set; }
    /// <summary>魔法卡的效果 id：airstrike / repair / boost / missile。</summary>
    public string Effect { get; set; } = "";
    public int Power { get; set; }

    /// <summary>
    /// 关键字：retain = 保留（回合结束不被弃掉）；exhaust = 消耗（打出后进消耗堆，不再洗回牌库）。
    /// </summary>
    public string[] Keywords { get; set; } = System.Array.Empty<string>();

    public bool Retain => HasKeyword("retain");
    public bool Exhaust => HasKeyword("exhaust");

    public bool HasKeyword(string key) =>
        Keywords != null && System.Array.IndexOf(Keywords, key) >= 0;

    public CardKind KindEnum => Kind switch
    {
        "global" => CardKind.Global,
        "strike" => CardKind.Strike,
        _ => CardKind.Unit,
    };
}

public sealed class MapDef
{
    public string Id { get; set; } = "";
    public string NameZh { get; set; } = "";
    public string NameEn { get; set; } = "";
    public int Width { get; set; } = 11;
    public int Height { get; set; } = 9;
    public string[] TerrainRows { get; set; } = Array.Empty<string>();
    public int[] PlayerHq { get; set; } = { 0, 0 };
    public int[] EnemyHq { get; set; } = { 0, 0 };
    public int StartGold { get; set; } = 300;
    public string[] PlayerDeck { get; set; } = Array.Empty<string>();
    public string[] EnemyDeck { get; set; } = Array.Empty<string>();
}

/// <summary>读取 res://data 下的 JSON 表。所有数值都能在 JSON 里改，不需要动代码。</summary>
public static class GameDatabase
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly Dictionary<string, UnitDef> Units = new();
    public static readonly Dictionary<string, BuildingDef> Buildings = new();
    public static readonly Dictionary<string, CardDef> Cards = new();
    public static readonly Dictionary<string, MapDef> Maps = new();

    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;

        foreach (UnitDef d in LoadList<UnitDef>("res://data/units.json"))
            Units[d.Id] = d;
        foreach (BuildingDef d in LoadList<BuildingDef>("res://data/buildings.json"))
            Buildings[d.Id] = d;
        foreach (CardDef d in LoadList<CardDef>("res://data/cards.json"))
            Cards[d.Id] = d;

        foreach (string path in DirAccess.GetFilesAt("res://data/maps"))
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            var map = LoadOne<MapDef>("res://data/maps/" + path);
            if (map != null)
                Maps[map.Id] = map;
        }
    }

    public static UnitDef Unit(string id)
    {
        EnsureLoaded();
        return Units.TryGetValue(id, out UnitDef d) ? d : null;
    }

    public static BuildingDef Building(string id)
    {
        EnsureLoaded();
        return Buildings.TryGetValue(id, out BuildingDef d) ? d : null;
    }

    public static CardDef Card(string id)
    {
        EnsureLoaded();
        return Cards.TryGetValue(id, out CardDef d) ? d : null;
    }

    public static MapDef Map(string id)
    {
        EnsureLoaded();
        if (Maps.TryGetValue(id, out MapDef d))
            return d;
        foreach (MapDef m in Maps.Values)
            return m;
        return null;
    }

    private static List<T> LoadList<T>(string path)
    {
        string text = Godot.FileAccess.GetFileAsString(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            GD.PushError($"[GameDatabase] 读不到数据表: {path}");
            return new List<T>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(text, Options) ?? new List<T>();
        }
        catch (Exception e)
        {
            GD.PushError($"[GameDatabase] 解析失败 {path}: {e.Message}");
            return new List<T>();
        }
    }

    private static T LoadOne<T>(string path)
    {
        string text = Godot.FileAccess.GetFileAsString(path);
        if (string.IsNullOrWhiteSpace(text))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (Exception e)
        {
            GD.PushError($"[GameDatabase] 解析失败 {path}: {e.Message}");
            return default;
        }
    }
}
