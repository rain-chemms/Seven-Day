using System;
using System.Linq;
using Godot;
using SevenDay;
using SevenDay.Data;
using SevenDay.Battle;
using SevenDay.Core;

namespace SevenDay.Net;

/// <summary>
/// 常驻节点的联机桥：负责大厅握手（hello / start）以及战斗外的连接状态。
/// 作为 Autoload 注册，路径固定为 /root/NetBridge，两个客户端路径一致，RPC 才能对上。
/// </summary>
public partial class NetBridge : Node
{
    public static NetBridge Instance { get; private set; }

    /// <summary>客户端收到开局配置（大厅据此切场景）。</summary>
    public event Action<NetBattleConfig> StartReceived;

    /// <summary>客户端收到“继续断线对局”的状态包。</summary>
    public event Action<NetResumePayload> BattleResumeReceived;

    /// <summary>主机收到客户端的重连请求（peerId, token）。</summary>
    public event Action<int, string> ResumeRequested;

    /// <summary>主机收到客户端就绪（带客户端自己的卡组）。</summary>
    public event Action<int, NetClientHello> ClientHello;

    /// <summary>客户端收到房间信息（地图/难度），据此回自己的卡组。</summary>
    public event Action<NetRoomOffer> OfferReceived;

    /// <summary>有对端连上（主机侧用：给新客户端发房间信息）。</summary>
    public event Action<int> PeerConnected;

    /// <summary>对端断开。</summary>
    public event Action<int> PeerLeft;

    private bool _clientSceneRequested;
    private bool _helloAnswered;
    private bool _offerArrived;
    private ulong _helloFallbackAt;
    private ulong _helloRetryUntil;

    public override void _Ready()
    {
        Instance = this;
        Name = "NetBridge";
        ProcessMode = ProcessModeEnum.Always;
        // 调试：--lan-deck a,b,c 覆盖本进程卡组（要在发 hello 之前生效）
        string[] deckArgs = OS.GetCmdlineUserArgs();
        int deckFlag = System.Array.IndexOf(deckArgs, "--lan-deck");
        if (deckFlag >= 0 && deckFlag + 1 < deckArgs.Length)
        {
            Session.DebugDeckOverride = deckArgs[deckFlag + 1]
                .Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += () => LanSession.LinkReady = true;
    }

    public bool ClientConnected => Multiplayer.MultiplayerPeer is ENetMultiplayerPeer enet &&
                                   enet.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>更换底层 peer 后重新绑定（房主迁移时用）。</summary>
    public void RebindPeer()
    {
        Multiplayer.MultiplayerPeer = LanSession.Peer;
        _clientSceneRequested = false;
        _helloAnswered = false;
        _offerArrived = false;
        _helloRetryUntil = 0;
        SetProcess(!LanSession.IsHost);
    }

    /// <summary>取消当前联机操作：关掉端口/断开连接并复位握手状态。</summary>
    public void Abort()
    {
        LanSession.Leave();
        _clientSceneRequested = false;
        _helloAnswered = false;
        _offerArrived = false;
        _helloRetryUntil = 0;
        SetProcess(false);
        GD.Print("[Net] 已取消联机操作");
    }

    /// <summary>主机开始监听。</summary>
    public bool Host(int port, out string error)
    {
        bool ok = LanSession.StartHost(port, out error);
        if (ok)
            AttachPeer();
        return ok;
    }

    /// <summary>加入主机。</summary>
    public bool Join(string ip, int port, out string error)
    {
        bool ok = LanSession.Join(ip, port, out error);
        if (ok)
        {
            AttachPeer();
            // 连上后等主机的房间信息，再回发自己的卡组
            _helloRetryUntil = Time.GetTicksMsec() + 20000;
            _helloFallbackAt = Time.GetTicksMsec() + 1500;
            _offerArrived = false;
            _helloAnswered = false;
            SetProcess(true);
        }

        return ok;
    }

    private void AttachPeer()
    {
        Multiplayer.MultiplayerPeer = LanSession.Peer;
    }

    private void OnPeerConnected(long id)
    {
        GD.Print($"[Net] 对端连接：{id}");
        LanSession.LinkReady = true;
        PeerConnected?.Invoke((int)id);
    }

    private void OnPeerDisconnected(long id)
    {
        GD.Print($"[Net] 对端断开：{id}");
        LanSession.LinkReady = false;
        PeerLeft?.Invoke((int)id);
    }

    public override void _Process(double delta)
    {
        // 连上后先等主机的“房间信息”，再回 hello；如果迟迟没有 offer，就用本机默认配置先回一次
        if (LanSession.Active && !LanSession.IsHost && !_helloAnswered &&
            ClientConnected && Time.GetTicksMsec() < _helloRetryUntil)
        {
            if (!_offerArrived && Time.GetTicksMsec() > _helloFallbackAt)
                SendHello();
        }
    }

    /// <summary>客户端收到开局配置后置位，停止重发 hello 并通知大厅。</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcStart(string json)
    {
        if (LanSession.IsHost)
            return;

        NetBattleConfig cfg = NetBattleConfig.FromJson(json);
        if (cfg == null)
            return;

        LanSession.Config = cfg;
        _clientSceneRequested = true;
        StartReceived?.Invoke(cfg);
    }

    /// <summary>客户端：把「自己的卡组 + 基地等级」发给主机。</summary>
    public void SendHello()
    {
        if (LanSession.IsHost || _helloAnswered)
            return;

        string mapId = string.IsNullOrEmpty(LanSession.OfferedMapId) ? Session.MapId : LanSession.OfferedMapId;
        MapDef map = GameDatabase.Map(mapId);
        var hello = new NetClientHello
        {
            HqLevel = Session.StartHqLevel,
            Deck = Session.EffectiveDeck(map).ToArray(),
        };

        _helloAnswered = true;
        RpcId(1, "RpcHello", hello.ToJson());
        GD.Print($"[Net] 已发送我方配置：{hello.Deck.Length} 张卡 / 基地 Lv{hello.HqLevel}");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcHello(string json)
    {
        if (!LanSession.IsHost)
            return;

        NetClientHello hello = NetClientHello.FromJson(json) ?? new NetClientHello();
        int sender = Multiplayer.GetRemoteSenderId();
        ClientHello?.Invoke(sender, hello);
    }

    /// <summary>客户端：请求主机恢复当前对局（走稳定的 NetBridge 节点）。</summary>
    public void SendResumeRequest(string token)
    {
        RpcId(1, "RpcResumeRequest", token);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcResumeRequest(string token)
    {
        if (!LanSession.IsHost)
            return;

        ResumeRequested?.Invoke(Multiplayer.GetRemoteSenderId(), token);
    }

    /// <summary>主机：请求客户端进入断线重连的对局。</summary>
    public void SendBattleResume(int peerId, string json)
    {
        RpcId(peerId, "RpcBattleResume", json);
    }

    /// <summary>客户端：收到断线对局状态包 → 暂存并直接进入战斗场景（用开局配置的卡组）。</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcBattleResume(string json)
    {
        if (LanSession.IsHost)
            return;

        NetResumePayload payload = NetResumePayload.FromJson(json);
        if (payload?.Config == null)
            return;

        if (payload.AssignActor >= 0)
            LanSession.AssignActor(payload.AssignActor);
        if (payload.HostLocked)
            LanSession.SetHostLocked(true);
        if (payload.ClientSeqBase > LanSession.ClientSeq)
            LanSession.ClientSeq = payload.ClientSeqBase;

        NetResumeStore.Pending = payload;
        LanSession.Config = payload.Config;
        Session.LanBattle = true;
        Session.MapId = payload.Config.MapId;
        Session.Difficulty = (AIDifficulty)payload.Config.Difficulty;

        GD.Print($"[Net] 收到断线对局恢复包，回放 {payload.History?.Length ?? 0} 条动作");
        GetTree().ChangeSceneToFile("res://scenes/battleScene.tscn");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcOffer(string json)
    {
        if (LanSession.IsHost)
            return;

        NetRoomOffer offer = NetRoomOffer.FromJson(json);
        if (offer == null)
            return;

        _offerArrived = true;
        LanSession.OfferedMapId = offer.MapId;
        LanSession.OfferedDifficulty = offer.Difficulty;
        Session.StartHqLevel = Mathf.Clamp(offer.HqLevel, 1, 3);

        // 有了地图信息，再回自己的卡组（会用这张地图的默认卡组兜底）
        _helloAnswered = false;
        SendHello();
        OfferReceived?.Invoke(offer);
    }

    /// <summary>主机：把房间信息（地图/难度）发给客户端。</summary>
    public void SendOffer(string mapId, int difficulty, int hqLevel, int peerId)
    {
        RpcId(peerId, "RpcOffer",
            new NetRoomOffer { MapId = mapId, Difficulty = difficulty, HqLevel = hqLevel }.ToJson());
    }

    /// <summary>主机：发送开局配置给客户端。</summary>
    public void SendStart(NetBattleConfig cfg, int peerId)
    {
        RpcId(peerId, "RpcStart", cfg.ToJson());
    }
}
