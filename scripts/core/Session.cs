using System.Collections.Generic;
using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay;

/// <summary>跨场景传递的小状态：这一局选了什么地图、什么难度，以及玩家自定义的出战卡组。</summary>
public static class Session
{
    public static string MapId = "map_01";
    public static AIDifficulty Difficulty = AIDifficulty.Normal;

    /// <summary>一局结束后回填，用于结算画面与历史记录。</summary>
    public static int LastTurns;
    public static bool LastWin;

    // ─────────────────────────── 出战卡组 ───────────────────────────

    public const int DeckMin = 7;
    public const int DeckMax = 50;

    /// <summary>可保存的卡组预设槽位数。</summary>
    public const int PresetSlots = 5;

    private const string DeckSection = "deck";
    private const string DeckKey = "player";

    private static List<string> _deck;

    /// <summary>玩家自定义卡组；没配过（或不足 7 张）时返回空列表，用地图默认卡组。</summary>
    public static List<string> PlayerDeck
    {
        get
        {
            _deck ??= LoadDeck();
            return _deck;
        }
    }

    /// <summary>现在这一局实际要用的卡组：自定义优先，否则回落到地图默认。</summary>
    public static List<string> EffectiveDeck(MapDef map)
    {
        List<string> custom = PlayerDeck;
        if (custom != null && custom.Count >= DeckMin)
            return new List<string>(custom);

        return map != null ? new List<string>(map.PlayerDeck) : new List<string>();
    }

    /// <summary>地图自带的默认卡组。</summary>
    public static List<string> DefaultDeck()
    {
        MapDef map = GameDatabase.Map(MapId);
        return map != null ? new List<string>(map.PlayerDeck) : new List<string>();
    }

    public static void SaveDeck(IEnumerable<string> ids)
    {
        _deck = new List<string>(ids);
        Settings.SetString(DeckSection, DeckKey, string.Join(",", _deck));
        Settings.Save();
    }

    public static void ClearDeck()
    {
        _deck = new List<string>();
        Settings.SetString(DeckSection, DeckKey, "");
        Settings.Save();
    }

    private static List<string> LoadDeck()
    {
        string raw = Settings.GetString(DeckSection, DeckKey, "");
        var list = new List<string>();

        if (string.IsNullOrWhiteSpace(raw))
            return list;

        foreach (string part in raw.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
        {
            string id = part.Trim();
            if (GameDatabase.Card(id) != null)
                list.Add(id);
        }

        return list;
    }
}
