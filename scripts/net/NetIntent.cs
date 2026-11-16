using System.Text.Json;
using System.Text.Json.Serialization;
using SevenDay.Core;

namespace SevenDay.Net;

/// <summary>联机动作类型（主机权威 + 动作同步）。</summary>
public enum NetIntentType
{
    Deploy = 0,
    Move = 1,
    Attack = 2,
    Spawn = 3,
    Build = 4,
    Upgrade = 5,
    Repair = 6,
    Demolish = 7,
    EndTurn = 8,
    Surrender = 9,
}

/// <summary>
/// 一条联机动作。字段刻意保持扁平、可 JSON 序列化；
/// 不带实例 id（两端 id 分配顺序不同），只带坐标与手牌序号。
/// </summary>
public sealed class NetIntent
{
    /// <summary>动作类型（<see cref="NetIntentType"/>）。</summary>
    public int T { get; set; }

    /// <summary>发起方：0 = 主机，1 = 客户端。</summary>
    public int A { get; set; }

    /// <summary>手牌序号（Deploy）。</summary>
    public int Hand { get; set; }

    /// <summary>目标坐标（Deploy / Move / Attack / Spawn / Build / 建筑操作）。</summary>
    public int Q { get; set; }
    public int R { get; set; }

    /// <summary>起始坐标（Move / Attack 的来源）。</summary>
    public int Q2 { get; set; }
    public int R2 { get; set; }

    /// <summary>卡 id（Spawn）。</summary>
    public string Card { get; set; } = "";

    /// <summary>建筑定义 id（Build）。</summary>
    public string Id { get; set; } = "";

    /// <summary>主机分配的序号（用于去重）。</summary>
    public int Seq { get; set; }

    /// <summary>发送方自己的动作序号（客户端确认/去重用；主机广播时原样带回）。</summary>
    public int LocalSeq { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static NetIntent FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NetIntent>(json, Options);
        }
        catch (System.Exception e)
        {
            Godot.GD.PushWarning($"[Net] Intent 解析失败：{e.Message}");
            return null;
        }
    }

    public NetIntentType Type => (NetIntentType)T;

    // ── 构造便捷方法 ──
    public static NetIntent Deploy(int actor, int hand, HexCoord coord) => new()
    {
        T = (int)NetIntentType.Deploy, A = actor, Hand = hand, Q = coord.Q, R = coord.R,
    };

    public static NetIntent Move(int actor, HexCoord from, HexCoord to) => new()
    {
        T = (int)NetIntentType.Move, A = actor, Q2 = from.Q, R2 = from.R, Q = to.Q, R = to.R,
    };

    public static NetIntent Attack(int actor, HexCoord from, HexCoord target) => new()
    {
        T = (int)NetIntentType.Attack, A = actor, Q2 = from.Q, R2 = from.R, Q = target.Q, R = target.R,
    };

    public static NetIntent Spawn(int actor, HexCoord building, string cardId) => new()
    {
        T = (int)NetIntentType.Spawn, A = actor, Q = building.Q, R = building.R, Card = cardId,
    };

    public static NetIntent Build(int actor, HexCoord coord, string defId) => new()
    {
        T = (int)NetIntentType.Build, A = actor, Q = coord.Q, R = coord.R, Id = defId,
    };

    public static NetIntent BuildingOp(int actor, NetIntentType type, HexCoord coord) => new()
    {
        T = (int)type, A = actor, Q = coord.Q, R = coord.R,
    };

    public static NetIntent EndTurn(int actor) => new() { T = (int)NetIntentType.EndTurn, A = actor };

    public static NetIntent Surrender(int actor) => new() { T = (int)NetIntentType.Surrender, A = actor };
}
