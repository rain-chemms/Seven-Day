using System.Collections.Generic;
using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay;

/// <summary>跨场景传递的小状态：这一局选了什么地图、什么难度，以及玩家自定义的出战卡组。</summary>
public static class Session
{
    /// <summary>本局是否来自局域网大厅（决定是否走联机战斗逻辑）。</summary>
    public static bool LanBattle;

    /// <summary>本局是否为明确的单机对局；用于阻止上一局联机会话/延迟 RPC 污染。</summary>
    public static bool SingleBattle;

    /// <summary>本次进入战斗是否要载入“未完成联机对局”的存档（房主恢复用）。</summary>
    public static bool RestoreLanSave;

    /// <summary>主菜单是否需要从全黑淡入（启动动画结束后做柔和过渡）。</summary>
    public static bool FadeInFromBlack;

    public static string MapId = "map_01";
    public static AIDifficulty Difficulty = AIDifficulty.Normal;

    /// <summary>玩家起始基地等级（1~3，来自地图选择界面）。</summary>
    public static int StartHqLevel
    {
        get => Mathf.Clamp(Settings.GetInt("battle", "hq_level", 1), 1, 3);
        set => Settings.SetInt("battle", "hq_level", Mathf.Clamp(value, 1, 3));
    }

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

    /// <summary>调试用：命令行 --lan-deck 指定的卡组（只在本进程生效，不写设置）。</summary>
    public static List<string> DebugDeckOverride;

    /// <summary>现在这一局实际要用的卡组：自定义优先，否则回落到地图默认。</summary>
    public static List<string> EffectiveDeck(MapDef map)
    {
        if (DebugDeckOverride != null && DebugDeckOverride.Count >= DeckMin)
            return new List<string>(DebugDeckOverride);

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
