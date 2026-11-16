using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SevenDay.Net;

/// <summary>局域网连接的持有者：主机 / 客户端各持有一个 ENetMultiplayerPeer。</summary>
public static class LanSession
{
    public const int DefaultPort = 7777;

    public static ENetMultiplayerPeer Peer { get; private set; }
    public static bool IsHost { get; private set; }

    /// <summary>本机在协议里的 actor：0 = 最初建房方，1 = 最初加入方。房主迁移后保持不变。</summary>
    public static int LocalActor { get; private set; }

    /// <summary>本局是否已经发生过“房主接管”：一旦接管，房主角色固定，不再迁回或二次迁移。</summary>
    public static bool HostLocked { get; private set; }

    /// <summary>客户端已发出但还没被主机确认的动作（断线重连后重发，防止控制信息丢失）。</summary>
    public static readonly List<NetIntent> PendingIntents = new();

    /// <summary>客户端动作序号（跨场景重载保持递增）。</summary>
    public static int ClientSeq;

    /// <summary>是否处于联机流程（大厅或战斗）。</summary>
    public static bool Active => Peer != null;

    /// <summary>是否真的有对端连上（主机要等客户端连上才算，由 NetBridge 维护）。</summary>
    public static bool LinkReady { get; set; }
    public static bool IsConnected => Peer != null && LinkReady;

    /// <summary>对端 peer id（2 人局：主机看客户端，客户端看主机）。</summary>
    public static int RemotePeerId => IsHost ? 2 : 1;

    /// <summary>主机分配的开局配置（客户端从 RPC 收到）。</summary>
    public static NetBattleConfig Config { get; set; }

    /// <summary>主机先发来的“房间信息”（地图/难度），客户端据此算自己的卡组。</summary>
    public static string OfferedMapId { get; set; } = "map_01";
    public static int OfferedDifficulty { get; set; } = 1;

    public static string LastError { get; private set; } = "";

    /// <summary>最近一次加入的地址（重连用）。</summary>
    public static string LastIp { get; private set; } = "";
    public static int LastPort { get; private set; }

    /// <summary>重连时继承“房主已锁定”状态。</summary>
    public static void SetHostLocked(bool locked) => HostLocked = locked;

    /// <summary>重连时由主机指定本机 actor。</summary>
    public static void AssignActor(int actor) => LocalActor = actor == 0 ? 0 : 1;

    /// <summary>本局联机令牌：重连时校验是否是同一局。</summary>
    public static string MatchToken { get; set; } = "";

    /// <summary>断线后把本机提升为房主（接管对局）：在原端口上开服务器。</summary>
    public static bool PromoteToHost(out string error)
    {
        error = "";

        // 已经接管过一次：不再二次迁移，避免反复换房主导致重连混乱
        if (HostLocked)
        {
            error = "本局已由另一方接管房主，不会再迁移";
            return false;
        }

        int port = LastPort > 0 ? LastPort : DefaultPort;

        try
        {
            Peer?.Close();
        }
        catch
        {
            // 忽略关闭异常
        }

        var peer = new ENetMultiplayerPeer();
        Error err = peer.CreateServer(port, 4);
        if (err != Error.Ok)
        {
            error = $"接管房主失败：端口 {port} 不可用（{err}）";
            return false;
        }

        Peer = peer;
        IsHost = true;
        LinkReady = false;
        LastPort = port;
        HostLocked = true;
        GD.Print($"[Net] 已提升为房主，监听 {port}（房主角色已锁定）");
        return true;
    }

    /// <summary>客户端重连：用上次的地址新建 ENet 客户端。</summary>
    public static bool ReconnectClient(out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(LastIp))
        {
            error = "没有可重连的地址";
            return false;
        }

        // 关掉上一次重连用的 peer，避免服务器连接槽被无效连接占满
        try
        {
            Peer?.Close();
        }
        catch
        {
            // 忽略关闭异常
        }

        var peer = new ENetMultiplayerPeer();
        Error err = peer.CreateClient(LastIp, LastPort);
        if (err != Error.Ok)
        {
            error = $"重连 {LastIp}:{LastPort} 失败：{err}";
            return false;
        }

        Peer = peer;
        IsHost = false;
        LinkReady = false;
        return true;
    }

    public static bool StartHost(int port, out string error)
    {
        error = "";
        Leave();
        var peer = new ENetMultiplayerPeer();
        // 允许多个连接；连续开关房间时端口可能还没释放，重试几次
        Error err = Error.CantCreate;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            err = peer.CreateServer(port, 4);
            if (err == Error.Ok)
                break;

            OS.DelayMsec(150);
        }

        if (err != Error.Ok)
        {
            error = $"监听端口 {port} 失败：{err}";
            LastError = error;
            return false;
        }

        Peer = peer;
        IsHost = true;
        LinkReady = false;
        LocalActor = 0;
        HostLocked = false;
        return true;
    }

    public static bool Join(string ip, int port, out string error)
    {
        error = "";
        Leave();
        var peer = new ENetMultiplayerPeer();
        Error err = peer.CreateClient(ip, port);
        if (err != Error.Ok)
        {
            error = $"连接 {ip}:{port} 失败：{err}";
            LastError = error;
            return false;
        }

        Peer = peer;
        IsHost = false;
        LinkReady = false;
        LocalActor = 1;
        HostLocked = false;
        LastIp = ip;
        LastPort = port;
        return true;
    }

    public static void Leave()
    {
        HostLocked = false;
        PendingIntents.Clear();
        try
        {
            Peer?.Close();
        }
        catch
        {
            // 忽略关闭异常
        }

        Peer = null;
        IsHost = false;
        LinkReady = false;
        MatchToken = "";
        Config = null;
        OfferedMapId = "map_01";
        OfferedDifficulty = 1;
    }

    /// <summary>把 peer 挂到场景树默认的 MultiplayerAPI 上（RPC 才会生效）。</summary>
    public static void AttachTo(Node node)
    {
        if (Peer != null)
            node.Multiplayer.MultiplayerPeer = Peer;
    }

    /// <summary>本机在局域网里的 IPv4 地址（取第一个非回环地址）。</summary>
    public static string LocalIpv4()
    {
        try
        {
            foreach (string addr in IP.GetLocalAddresses())
            {
                string s = addr.ToString();
                if (s.Contains('.') && !s.StartsWith("127.") && !s.StartsWith("169.254."))
                    return s;
            }
        }
        catch
        {
            // 拿不到就回落到回环
        }

        return "127.0.0.1";
    }
}
