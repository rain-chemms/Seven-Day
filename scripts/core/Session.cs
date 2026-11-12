using SevenDay.Core;

namespace SevenDay;

/// <summary>跨场景传递的小状态：这一局选了什么地图、什么难度。</summary>
public static class Session
{
    public static string MapId = "map_01";
    public static AIDifficulty Difficulty = AIDifficulty.Normal;

    /// <summary>一局结束后回填，用于结算画面与历史记录。</summary>
    public static int LastTurns;
    public static bool LastWin;
}
