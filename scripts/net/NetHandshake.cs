using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace SevenDay.Net;

/// <summary>主机发给客户端的房间信息（还没定最终配置，客户端要据此回自己的卡组）。</summary>
public sealed class NetRoomOffer
{
    public string MapId { get; set; } = "map_01";
    public int Difficulty { get; set; } = 1;

    /// <summary>主机决定的初始基地等级（双方共用）。</summary>
    public int HqLevel { get; set; } = 1;

    /// <summary>客户端游戏版本（用于提示两端版本不一致）。</summary>
    public string Version { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static NetRoomOffer FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NetRoomOffer>(json, Options);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Net] 房间信息解析失败：{e.Message}");
            return null;
        }
    }
}

/// <summary>重连数据暂存：客户端收到状态包后重载战斗场景，_Ready 时从这里恢复。</summary>
public static class NetResumeStore
{
    public static NetResumePayload Pending;
    public static bool WasResumed;

    public static bool HasPending => Pending != null;

    public static void Clear()
    {
        Pending = null;
        WasResumed = false;
    }
}

/// <summary>重连时主机发回的状态包：配置 + 已执行动作序列。</summary>
public sealed class NetResumePayload
{
    public string Token { get; set; } = "";

    /// <summary>让重连方使用哪个 actor（-1 = 未指定）。</summary>
    public int AssignActor { get; set; } = -1;

    /// <summary>本局是否已锁定房主（接管过）。</summary>
    public bool HostLocked { get; set; }

    /// <summary>主机已处理过的客户端动作序号，重连方要接着往后编号。</summary>
    public int ClientSeqBase { get; set; }
    public NetBattleConfig Config { get; set; }
    public string[] History { get; set; } = System.Array.Empty<string>();

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static NetResumePayload FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NetResumePayload>(json, Options);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Net] 重连数据解析失败：{e.Message}");
            return null;
        }
    }
}

/// <summary>客户端回给主机的“我方配置”：卡组 + 基地等级。</summary>
public sealed class NetClientHello
{
    public int HqLevel { get; set; } = 1;

    /// <summary>客户端游戏版本（用于提示两端版本不一致）。</summary>
    public string Version { get; set; } = "";
    public string[] Deck { get; set; } = Array.Empty<string>();

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static NetClientHello FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NetClientHello>(json, Options);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Net] 客户端配置解析失败：{e.Message}");
            return null;
        }
    }
}
