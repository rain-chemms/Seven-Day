using System;
using Godot;

namespace SevenDay.Net;

/// <summary>
/// 战斗场景内的联机节点（两端路径一致：/root/BattleScene/NetBattle）。
/// 只负责转发：客户端 → 主机（Intent），主机 → 客户端（已确认的 Intent）。
/// 规则校验与执行在 BattleScene 里，两端走同一段代码。
/// </summary>
public partial class NetBattleNode : Node
{
    public static NetBattleNode Instance { get; private set; }

    /// <summary>主机收到客户端的动作请求。</summary>
    public event Action<NetIntent> IntentReceived;

    /// <summary>客户端收到主机确认并广播的动作。</summary>
    public event Action<NetIntent> AppliedReceived;

    /// <summary>主机拒绝某个动作（附带原因）。</summary>
    public event Action<string> RejectReceived;

    /// <summary>收到对端心跳。</summary>
    public event Action HeartbeatReceived;

    private int _seq;
    private Action _onServerDisconnected;
    private Action _onConnectionFailed;

    public override void _Ready()
    {
        Instance = this;
        Name = "NetBattle";
        LanSession.AttachTo(this);
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        // 客户端掉线（服务端断开 / 连接失败）同样进入重连流程
        _onServerDisconnected = () => AppliedReceived?.Invoke(null);
        _onConnectionFailed = () => AppliedReceived?.Invoke(null);
        Multiplayer.ServerDisconnected += _onServerDisconnected;
        Multiplayer.ConnectionFailed += _onConnectionFailed;
    }

    public override void _ExitTree()
    {
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        if (_onServerDisconnected != null) Multiplayer.ServerDisconnected -= _onServerDisconnected;
        if (_onConnectionFailed != null) Multiplayer.ConnectionFailed -= _onConnectionFailed;
        if (Instance == this) Instance = null;
    }

    private void OnPeerDisconnected(long id)
    {
        AppliedReceived?.Invoke(null);   // 传 null 表示对端掉线
    }

    /// <summary>客户端：把本地动作发给主机。</summary>
    public void SendIntentToHost(NetIntent intent)
    {
        RpcId(1, "RpcIntent", intent.ToJson());
    }

    /// <summary>主机：把已执行的动作广播给所有客户端。</summary>
    public void BroadcastApplied(NetIntent intent)
    {
        // 所有广播统一使用主机分配的全局序号（客户端自己的序号放在 LocalSeq 里带回）
        intent.Seq = ++_seq;

        Rpc("RpcApplied", intent.ToJson());
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcIntent(string json)
    {
        if (!LanSession.IsHost)
            return;

        NetIntent intent = NetIntent.FromJson(json);
        if (intent == null)
            return;

        IntentReceived?.Invoke(intent);
    }

    /// <summary>主机：把拒绝原因发回客户端。</summary>
    public void SendReject(int peerId, string reason)
    {
        RpcId(peerId, "RpcReject", reason);
    }

    /// <summary>向对端发心跳（对端会自动回 pong）。</summary>
    public void SendHeartbeat()
    {
        Rpc("RpcHeartbeat", Time.GetTicksMsec());
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcHeartbeat(long stamp)
    {
        HeartbeatReceived?.Invoke();

        // 自动回 pong（带回原时间戳，便于以后做 RTT）
        RpcId(Multiplayer.GetRemoteSenderId(), "RpcHeartbeatAck", stamp);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcHeartbeatAck(long stamp)
    {
        HeartbeatReceived?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcReject(string reason)
    {
        if (LanSession.IsHost)
            return;

        RejectReceived?.Invoke(reason);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcApplied(string json)
    {
        if (LanSession.IsHost)
            return;

        NetIntent intent = NetIntent.FromJson(json);
        if (intent == null)
            return;

        AppliedReceived?.Invoke(intent);
    }
}
