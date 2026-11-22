using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Core;
using SevenDay.Data;
using SevenDay.Net;
using SevenDay.UI;

namespace SevenDay.Battle;

/// <summary>
/// 战斗场景的联机部分（主机权威 + 动作同步）。
///
/// 视角约定：每个客户端都把自己的阵营当作 <see cref="PlayerId.Player"/>，
/// 对手永远是 <see cref="PlayerId.Enemy"/>，因此 HUD / 手牌 / 回合 UI 完全沿用单机代码。
/// Intent 里的 A（actor）0 = 主机、1 = 客户端，各端按自己的身份映射成 Player / Enemy。
/// </summary>
public partial class BattleScene
{
    private NetBattleNode _netBattle;
    private int _netLastSeq;
    private readonly List<NetIntent> _netHistory = new();
    private bool _netReplaying;
    private int _netReshuffleSerial;
    private readonly List<string> _netEnemyDeck = new();
    private bool _netFrozen;
    private Control _reconnectOverlay;
    private Label _reconnectLabel;
    private Label _reconnectTitle;
    private PanelContainer _reconnectPanel;
    private Tween _reconnectPulse;
    private Button _reconnectRejoinBtn;
    private bool _reconnectRejoinToMenu;
    private bool _reconnectLoopRunning;
    private bool _resumeReloading;
    private bool _netMatchEnded;
    private Timer _netHeartbeat;
    private ulong _netLastRxMsec;
    private ulong _netResumeSentMsec;
    private ulong _netIgnoreDisconnectUntilMsec;
    private ulong _netLastHostSaveMsec;
    private int _netLastClientSeq;
    private bool _netHostRestored;
    private const float HeartbeatInterval = 2.0f;   // 每 2 秒探一次
    private const float HeartbeatTimeout = 9.0f;    // 连续 9 秒没有任何对端消息 → 先进入确认流程

    private bool _netBusy;      // 正在播放“命中前”的动画（伤害延后到爆炸瞬间结算）
    private ulong _netBusySinceMsec;
    private readonly System.Collections.Generic.Queue<NetIntent> _netPending = new();

    /// <summary>客户端已发出、等待主机广播的本地出牌（防止同一张牌被连续打出）。</summary>
    private bool _netLocalDeployPending;

    public bool NetMode => Session.LanBattle && LanSession.Active;


    private int NetLocalActor => LanSession.LocalActor;

    /// <summary>本地是否轮到行动：镜像视角下本地永远是 Player。</summary>
    private bool NetMyTurn =>
        _state != null && _state.Phase == BattlePhase.PlayerTurn && _state.CurrentPlayer == PlayerId.Player;

    /// <summary>actor → 本地视角下的阵营。</summary>
    private PlayerId NetSideOfActor(int actor) =>
        actor == LanSession.LocalActor ? PlayerId.Player : PlayerId.Enemy;

    // ─────────────────────────── 开局 ───────────────────────────

    /// <summary>主机生成开局配置（地图/难度/双方卡组/基地等级/随机种子）。</summary>
    public static NetBattleConfig BuildNetConfig(MapDef map) => BuildNetConfig(map, null);

    /// <summary>带客户端配置的版本：客户端用自己保存的卡组与基地等级。</summary>
    public static NetBattleConfig BuildNetConfig(MapDef map, NetClientHello hello)
    {
        List<string> clientDeck = SanitizeDeck(hello?.Deck, map);

        var rng = new Random();
        return new NetBattleConfig
        {
            MapId = map.Id,
            Difficulty = (int)Session.Difficulty,
            HqHost = Session.StartHqLevel,
            // 初始基地等级由创建房间的主机决定，双方相同
            DeckHost = Session.EffectiveDeck(map).ToArray(),
            SeedHost = rng.Next(1, int.MaxValue),
            WeatherEnabled = Session.WeatherEnabled,
            WeatherGrace = Session.WeatherGraceTurns,
            WeatherMask = Session.WeatherMask,
            HqClient = Session.StartHqLevel,
            DeckClient = clientDeck.ToArray(),
            SeedClient = rng.Next(1, int.MaxValue),
        };
    }

    /// <summary>联机时按配置重建镜像状态；单机直接走原来的 Create。</summary>
    /// <summary>校验客户端卡组：卡必须存在且数量足够，否则回落到地图默认。</summary>
    private static List<string> SanitizeDeck(string[] deck, MapDef map)
    {
        if (deck == null || deck.Length < Session.DeckMin)
            return new List<string>(map.EnemyDeck);

        var list = new List<string>();
        foreach (string id in deck)
        {
            if (GameDatabase.Card(id) != null)
                list.Add(id);
        }

        return list.Count >= Session.DeckMin ? list : new List<string>(map.EnemyDeck);
    }

    /// <summary>清理残留的联机会话：标记还在但连接已断时按单机处理。</summary>
    private void NetSanitizeStale()
    {
        // 载入未完成对局时房主本来就在等客户端，不能把它当成“残留联机标记”清掉
        if (NetResumeStore.HasPending)
            return;

        if (!Session.LanBattle)
            return;

        if (LanSession.Active && LanSession.IsConnected)
            return;

        if (LanSession.Active)
            LanSession.Leave();

        Session.LanBattle = false;
        GD.Print("[Net] 检测到残留联机标记但未连接，按单机（人机）处理");
    }
    private BattleState NetCreateState(MapDef map)
    {
        if (!NetMode)
            return BattleState.Create(map, Session.Difficulty);

        NetBattleConfig cfg = LanSession.Config;
        if (cfg == null)
            cfg = LanSession.Config = BuildNetConfig(map);

        bool actorZero = LanSession.LocalActor == 0;
        return BattleState.Create(
            map,
            Session.Difficulty,
            (actorZero ? cfg.DeckHost : cfg.DeckClient).ToList(),
            (actorZero ? cfg.DeckClient : cfg.DeckHost).ToList(),
            actorZero ? cfg.HqHost : cfg.HqClient,
            actorZero ? cfg.HqClient : cfg.HqHost,
            actorZero ? cfg.SeedHost : cfg.SeedClient,
            actorZero ? cfg.SeedClient : cfg.SeedHost,
            !actorZero,   // actor 1 镜像：自己的基地在地图另一端
            cfg.WeatherEnabled,
            cfg.WeatherGrace,
            cfg.WeatherMask);
    }

    /// <summary>开局第一回合：主机先手（本地 Player），客户端先手是对方（本地 Enemy）。</summary>
    private void NetBeginFirstTurn()
    {
        // 先手方是 actor 0（最初建房方）；重连后仍按 actor 判断，而不是看当前谁是服务器
        _state.BeginTurn(!NetMode || LanSession.LocalActor == 0 ? PlayerId.Player : PlayerId.Enemy);
    }

    /// <summary>战斗场景就绪后挂上联机节点并监听 RPC。</summary>
    private void NetAttach()
    {
        if (!NetMode)
            return;

        _netBattle = new NetBattleNode { Name = "NetBattle" };
        AddChild(_netBattle);
        _netReshuffleSerial = _state.ReshuffleSerial;
        _netBattle.IntentReceived += OnNetIntentFromClient;
        if (NetBridge.Instance != null)
        {
            NetBridge.Instance.ClientHello -= OnClientHelloWhileSuspended;
            NetBridge.Instance.ClientHello += OnClientHelloWhileSuspended;
            NetBridge.Instance.ResumeRequested -= OnResumeRequestedFromClient;
            NetBridge.Instance.ResumeRequested += OnResumeRequestedFromClient;
        }
        NetStartHeartbeat();
        _netBattle.AppliedReceived += OnNetApplied;

        _netBattle.HeartbeatReceived += OnNetHeartbeatReceived;
        _netBattle.RejectReceived += reason =>
        {
            if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
                GD.Print($"[Net] 收到拒绝：{reason}");
            _hud.Toast(string.IsNullOrEmpty(reason) ? "动作无效" : reason);
            _netLocalDeployPending = false;
            // 被拒绝的动作不会被执行，清掉待重发队列，避免断线后反复补发无效动作
            if (!LanSession.IsHost)
                LanSession.PendingIntents.Clear();
        };

        // 敌方“配置卡组”（开局固定，不随抽牌变化）
        NetBattleConfig cfg = LanSession.Config;
        if (cfg != null)
        {
            _netEnemyDeck.Clear();
            _netEnemyDeck.AddRange(LanSession.LocalActor == 0
                ? (cfg.DeckClient ?? System.Array.Empty<string>())
                : (cfg.DeckHost ?? System.Array.Empty<string>()));
        }

        _hud.SetEnemyPileCards(_netEnemyDeck);
        _hud.SetEnemyPileVisible(true);

        // 房主载入未完成对局：已重建到断线时的状态，等对方加入
        if (_netHostRestored)
        {
            _netFrozen = true;
        _netLocalDeployPending = false;
            ShowReconnectOverlay(
                $"{Loc.T("lan.restore_loaded")}\n" +
                $"{Loc.T("lan.reconnect_ip")}: {LanSession.LocalIpv4()}\n" +
                Loc.T("lan.reconnect_wait_peer"));
            if (_reconnectRejoinBtn != null)
            {
                _reconnectRejoinToMenu = true;
                _reconnectRejoinBtn.Text = Loc.T("lan.reconnect_back_menu");
                _reconnectRejoinBtn.Visible = true;
            }
        }
        if (NetResumeStore.WasResumed && !LanSession.IsHost)
        {
            // 断线前已发出但没被确认的动作，重连后补发（主机按 Seq 去重）
            foreach (NetIntent pending in LanSession.PendingIntents.ToList())
                _netBattle.SendIntentToHost(pending);
        }

        if (NetResumeStore.WasResumed)
        {
            NetResumeStore.WasResumed = false;

            // 重连场景是“换场景 + RPC”同时发生，HUD 可能按旧尺寸布局，延迟强制重排一次
            GetTree().CreateTimer(0.2).Timeout += () =>
            {
                if (IsInstanceValid(this) && _hud != null && _state != null)
                {
                    _hud.ForceRelayout();
                    GD.Print("[Net] 重连后强制重排 HUD");
                }
            };
            _hud.Toast(Loc.T("lan.reconnect_resumed"));
        }
        else if (_state.CurrentPlayer == PlayerId.Player && _state.Phase == BattlePhase.PlayerTurn)
            _hud.Toast("联机对战：你的回合");
        else
            _hud.Toast("联机对战：等待对方行动…");

        if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
            GD.Print($"[Net] 已接入：host={LanSession.IsHost} 当前={_state.CurrentPlayer} phase={_state.Phase}");
    }

    // ─────────────────────────── 收发 ───────────────────────────

    private void OnNetIntentFromClient(NetIntent it)
    {
        _netLastRxMsec = Time.GetTicksMsec();
        if (LanSession.IsHost && !_netMatchEnded)
            HideReconnectOverlay();

        // 重发/重复的动作：主机只执行一次
        if (it.LocalSeq > 0 && it.LocalSeq <= _netLastClientSeq)
            return;
        int remoteActor = 1 - LanSession.LocalActor;
        if (_netBattle == null || it == null || it.A != remoteActor)
            return;

        // 投降不受回合限制（随时可投）
        if (it.Type == NetIntentType.Surrender)
        {
            if (!ValidateNetIntent(it, out string surrenderWhy))
            {
                _netBattle.SendReject(Multiplayer.GetRemoteSenderId(), surrenderWhy);
                return;
            }

            _netLastClientSeq = Mathf.Max(_netLastClientSeq, it.LocalSeq);
            _netBattle.BroadcastApplied(it);
            ExecuteNetIntent(it);
            return;
        }

        // 其它动作只有客户端的回合才接受
        PlayerId remoteSide = NetSideOfActor(remoteActor);
        if (_state.CurrentPlayer != remoteSide)
            return;

        // 出牌/攻击演出期间先排队，避免选择动作在待选状态建立前被判定
        if (_netBusy)
        {
            it.NeedsValidation = true;
            _netPending.Enqueue(it);
            return;
        }

        // 判定必须在任何动画之前：不合法直接拒绝，不广播、不播特效
        if (!ValidateNetIntent(it, out string why))
        {
            if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
                GD.Print($"[Net] 拒绝 {it.Type} choice={it.Choice} picks={string.Join(",", it.Picks ?? System.Array.Empty<int>())}：{why}");

            _netBattle.SendReject(Multiplayer.GetRemoteSenderId(), why);
            return;
        }

        _netLastClientSeq = Mathf.Max(_netLastClientSeq, it.Seq);
        _netBattle.BroadcastApplied(it);
        ExecuteNetIntent(it);
    }

    private void OnNetApplied(NetIntent it)
    {
        _netLastRxMsec = Time.GetTicksMsec();

        // 主机确认后清掉待重发队列（含之前所有更小序号）
        if (!LanSession.IsHost && it.A == LanSession.LocalActor && it.LocalSeq > 0)
            LanSession.PendingIntents.RemoveAll(x => x.LocalSeq <= it.LocalSeq);
        if (it == null)
        {
            OnNetPeerLost();
            return;
        }

        if (it.Seq <= _netLastSeq)
            return;

        _netLastSeq = it.Seq;
        ExecuteNetIntent(it);
    }

    private void OnNetPeerLost()
    {
        if (_netBattle == null || _netFrozen || _resumeReloading || _netMatchEnded ||
            _state is { Phase: BattlePhase.GameOver })
            return;

        _netFrozen = true;

        if (LanSession.IsHost)
        {
            // 房主权限固定：不迁移。先把对局存盘，再等对方重连。
            NetSaveUnfinishedMatch();
            _hud.Toast("对局已保存，等待对方重新连接…");
            ShowReconnectOverlay(
                $"{Loc.T("lan.reconnect_host_wait")}\n" +
                $"{Loc.T("lan.reconnect_ip")}: {LanSession.LocalIpv4()}");
            if (_reconnectRejoinBtn != null)
            {
                _reconnectRejoinToMenu = true;
                _reconnectRejoinBtn.Text = Loc.T("lan.reconnect_back_menu");
                _reconnectRejoinBtn.Visible = true;
            }
            return;
        }

        // 客户端：不断尝试重连房主；连不上就提示回大厅（对局由房主保存）
        ShowReconnectOverlay(Loc.T("lan.reconnect_client_wait"));
        if (_reconnectRejoinBtn != null)
        {
            _reconnectRejoinToMenu = false;
            _reconnectRejoinBtn.Text = Loc.T("lan.reconnect_rejoin");
            _reconnectRejoinBtn.Visible = false;
        }
        NetStartReconnectLoop(promoteAfter: false);
    }
    /// <summary>房主把未完成对局写入本地存档（下次进联机模式可选择载入）。</summary>
    private void NetSaveUnfinishedMatch()
    {
        if (!LanSession.IsHost || LanSession.Config == null)
            return;

        var payload = new NetResumePayload
        {
            Token = LanSession.MatchToken,
            Config = LanSession.Config,
            AssignActor = 1,          // 之后加入的一方固定是 actor 1
            HostLocked = true,
            History = _netHistory.Select(x => x.ToJson()).ToArray(),
        };

        string mapName = GameDatabase.Map(LanSession.Config.MapId)?.NameZh ?? LanSession.Config.MapId;
        LanSaveStore.Save(payload, mapName, _state.TurnNumber, LanSession.LastPort > 0 ? LanSession.LastPort : 7777);
    }
    /// <summary>对局结束：断开联机，双方各自控制结算画面（不再触发重连）。</summary>
    private void NetFinishMatch()
    {
        if (_netMatchEnded)
            return;

        _netMatchEnded = true;
        _netFrozen = false;
        LanSaveStore.Delete();
        _netLastRxMsec = Time.GetTicksMsec();   // 避免解冻瞬间心跳误判
        HideReconnectOverlay();
        GD.Print("[Net] 对局结束，断开联机连接");

        // 稍等一下，保证最后的结算数据/动作先送达，再断开
        GetTree().CreateTimer(0.5).Timeout += () =>
        {
            LanSession.Leave();
            Session.LanBattle = false;
        };
    }
    /// <summary>退出战斗场景时注销常驻 NetBridge 上的事件，避免旧场景处理新对局的连接。</summary>
    public override void _ExitTree()
    {
        if (NetBridge.Instance != null)
        {
            NetBridge.Instance.ClientHello -= OnClientHelloWhileSuspended;
            NetBridge.Instance.ResumeRequested -= OnResumeRequestedFromClient;
        }
    }
    // ─────────────────────────── 断线重连 ───────────────────────────

    /// <summary>心跳：每 2 秒发一次；7 秒收不到对端任何消息就判定掉线。</summary>
    private void NetStartHeartbeat()
    {
        if (_netHeartbeat != null || !NetMode)
            return;

        _netLastRxMsec = Time.GetTicksMsec();
        _netHeartbeat = new Timer
        {
            Name = "NetHeartbeat",
            WaitTime = HeartbeatInterval,
            Autostart = true,
            OneShot = false,
        };
        _netHeartbeat.Timeout += OnHeartbeatTick;
        AddChild(_netHeartbeat);
    }

    private void OnHeartbeatTick()
    {
        if (!NetMode || _netMatchEnded || _netFrozen || _resumeReloading || _netBattle == null)
            return;

        _netBattle.SendHeartbeat();

        // 房主定期自动存档：即使进程被强杀，也留有最近的对局
        if (LanSession.IsHost && !_netFrozen && _netHistory.Count > 0 &&
            Time.GetTicksMsec() - _netLastHostSaveMsec > 3000)
        {
            _netLastHostSaveMsec = Time.GetTicksMsec();
            NetSaveUnfinishedMatch();
        }

        ulong now = Time.GetTicksMsec();
        if (now - _netLastRxMsec > (ulong)(HeartbeatTimeout * 1000f))
        {
            GD.Print($"[Net] 心跳超时：{(now - _netLastRxMsec) / 1000f:F1}s 没有收到对端消息，判定掉线");
            _netLastRxMsec = now;
            OnNetPeerLost();
        }
    }

    private void OnNetHeartbeatReceived()
    {
        _netLastRxMsec = Time.GetTicksMsec();
    }
    /// <summary>重连数据里的配置要在建状态之前应用。</summary>
    private static void NetApplyPendingConfig()
    {
        if (NetResumeStore.HasPending && NetResumeStore.Pending.Config != null)
            LanSession.Config = NetResumeStore.Pending.Config;
    }

    /// <summary>有重连数据时回放动作历史；返回是否处于“重连恢复”流程。</summary>
    private bool NetResumeReplayIfNeeded()
    {
        NetResumePayload payload = NetResumeStore.Pending;
        NetResumeStore.Pending = null;
        if (payload == null)
            return false;

        foreach (string json in payload.History ?? System.Array.Empty<string>())
        {
            NetIntent it = NetIntent.FromJson(json);
            if (it == null)
                continue;

            ReplayApply(it);
            _netHistory.Add(it);
        }

        _netReshuffleSerial = _state.ReshuffleSerial;

        // 记录历史里客户端动作的最大序号（接管房主后继续去重）
        int remoteActor = 1 - LanSession.LocalActor;
        foreach (NetIntent h in _netHistory.Where(x => x.A == remoteActor))
            _netLastClientSeq = Mathf.Max(_netLastClientSeq, h.LocalSeq);

        NetResumeStore.WasResumed = true;
        _netHostRestored = LanSession.IsHost;
        _netIgnoreDisconnectUntilMsec = Time.GetTicksMsec() + 3000;

        if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
        {
            PlayerId hs = LanSession.LocalActor == 0 ? PlayerId.Player : PlayerId.Enemy;
            PlayerId cs = _state.OpponentOf(hs);
            GD.Print($"[Net] 恢复后 棋盘哈希={BoardHash()} 回合={_state.TurnNumber} 当前={_state.CurrentPlayer} " +
                     $"actor0方金币={_state.StateOf(hs).Gold} 手牌={_state.StateOf(hs).Hand.Count} " +
                     $"actor1方金币={_state.StateOf(cs).Gold} 手牌={_state.StateOf(cs).Hand.Count} " +
                     $"单位={_state.Units.Count}");
        }
        GD.Print($"[Net] 已恢复对局：回放 {_netHistory.Count} 条动作");
        return true;
    }

    /// <summary>只改状态的回放（不播动画、不碰 HUD，供重连重建用）。</summary>
    private void ReplayApply(NetIntent it)
    {
        PlayerId side = NetSideOfActor(it.A);
        PlayerState st = _state.StateOf(side);
        HexCoord to = new(it.Q, it.R);
        HexCoord from = new(it.Q2, it.R2);

        switch (it.Type)
        {
            case NetIntentType.Deploy:
            {
                if (it.Hand < 0 || it.Hand >= st.Hand.Count)
                    return;
                CardDef card = GameDatabase.Card(st.Hand[it.Hand]);
                if (card == null)
                    return;

                _state.PlayIndex = it.Hand;
                if (card.KindEnum == CardKind.Unit)
                    _state.PlayUnitCard(st, card.Id, to, out _);
                else
                    _state.PlayGlobalCard(st, card.Id, to, out _);
                _state.PlayIndex = -1;
                break;
            }

            case NetIntentType.Move:
            {
                UnitInst u = _state.UnitsAt(from).FirstOrDefault(x => x.Owner == side && x.Alive);
                if (u != null) _state.TryMove(u, to, out _);
                break;
            }

            case NetIntentType.Attack:
            {
                UnitInst u = _state.UnitsAt(from).FirstOrDefault(x => x.Owner == side && x.Alive);
                if (u != null) _state.PerformAttack(u, to);
                break;
            }

            case NetIntentType.Spawn:
            {
                BuildingInst b = _state.BuildingAt(to);
                if (b != null && b.Owner == side) _state.TrySpawnFromBuilding(b, it.Card, out _);
                break;
            }

            case NetIntentType.Build:
                _state.TryBuild(side, it.Id, to, out _);
                break;

            case NetIntentType.Upgrade: { BuildingInst b = OwnBuildingAt(to, side); if (b != null) _state.TryUpgrade(b, out _); break; }
            case NetIntentType.Repair: { BuildingInst b = OwnBuildingAt(to, side); if (b != null) _state.TryRepair(b, out _); break; }
            case NetIntentType.Demolish: { BuildingInst b = OwnBuildingAt(to, side); if (b != null) _state.Demolish(b); break; }

            case NetIntentType.Dive:
            {
                UnitInst u = _state.UnitsAt(to).FirstOrDefault(x => x.Owner == side && x.Alive);
                if (u != null) _state.ToggleSubmarineDive(u, out _);
                break;
            }

            case NetIntentType.Radar:
            {
                UnitInst u = _state.UnitsAt(to).FirstOrDefault(x => x.Owner == side && x.Alive);
                if (u != null) _state.UseCarrierRadar(u, out _, out _);
                break;
            }

            case NetIntentType.CarrierSpawn:
            {
                UnitInst u = _state.UnitsAt(to).FirstOrDefault(x => x.Owner == side && x.Alive);
                if (u != null) _state.TryCarrierSpawn(u, it.Card, out _);
                break;
            }
            case NetIntentType.EndTurn:
                _state.EndTurn();
                break;

            case NetIntentType.Surrender:
                _state.Phase = BattlePhase.GameOver;
                _state.Winner = _state.OpponentOf(side);
                break;
        }
    }

    /// <summary>主机断线等待期间，有新客户端连进来：直接把当前对局恢复包发给它（用开局配置的卡组）。</summary>
    private void OnClientHelloWhileSuspended(int peerId, NetClientHello hello)
    {
        if (!IsInstanceValid(this))
            return;

        // 对方已经连回来了：先把断线提示收掉
        if (LanSession.IsHost && NetMode && !_netMatchEnded)
            HideReconnectOverlay();

        // 对局进行中又有客户端连进来：视为断线重连，直接恢复（不要求当前已冻结，
        // 因为旧连接的断开事件可能晚于新连接的 hello 到达）
        if (!LanSession.IsHost || !NetMode || _netMatchEnded)
            return;

        var payload = new NetResumePayload
        {
            Token = LanSession.MatchToken,
            Config = LanSession.Config,
            AssignActor = 1 - LanSession.LocalActor,   // 新加入方使用另一个 actor
            HostLocked = LanSession.HostLocked,
            ClientSeqBase = _netLastClientSeq,
            History = _netHistory.Select(x => x.ToJson()).ToArray(),
        };

        NetBridge.Instance.SendBattleResume(peerId, payload.ToJson());
        _netResumeSentMsec = Time.GetTicksMsec();
        _netFrozen = false;
        _netLastRxMsec = Time.GetTicksMsec();   // 避免解冻瞬间心跳误判
        HideReconnectOverlay();
        _hud.Toast("对手已重新加入，继续对局");
        GD.Print($"[Net] 新客户端加入，发送断线对局恢复包（{_netHistory.Count} 条动作）");
    }
    /// <summary>主机：把配置 + 动作历史发回重连的客户端。</summary>
    private void OnResumeRequestedFromClient(int peerId, string token)
    {
        if (!IsInstanceValid(this))
            return;

        if (!LanSession.IsHost || _netBattle == null)
            return;

        if (!string.IsNullOrEmpty(LanSession.MatchToken) && token != LanSession.MatchToken)
        {
            GD.Print($"[Net] 重连令牌不匹配：{token} != {LanSession.MatchToken}");
            return;
        }

        var payload = new NetResumePayload
        {
            Token = LanSession.MatchToken,
            Config = LanSession.Config,
            AssignActor = 1 - LanSession.LocalActor,
            HostLocked = LanSession.HostLocked,
            ClientSeqBase = _netLastClientSeq,
            History = _netHistory.Select(x => x.ToJson()).ToArray(),
        };

        NetBridge.Instance.SendBattleResume(peerId, payload.ToJson());
        _netFrozen = false;
        _netLastRxMsec = Time.GetTicksMsec();   // 避免解冻瞬间心跳误判
        HideReconnectOverlay();
        _hud.Toast("对手已重连，继续对局");
        GD.Print($"[Net] 客户端已重连，发送 {_netHistory.Count} 条历史");
    }

    /// <summary>客户端：收到状态包 → 暂存并重载战斗场景，由 _Ready 回放。</summary>
    private void OnResumeStateReceived(string json)
    {
        if (LanSession.IsHost || _resumeReloading)
            return;

        NetResumePayload payload = NetResumePayload.FromJson(json);
        if (payload?.Config == null)
            return;

        _resumeReloading = true;
        if (payload.AssignActor >= 0)
            LanSession.AssignActor(payload.AssignActor);
        if (payload.HostLocked)
            LanSession.SetHostLocked(true);
        if (payload.ClientSeqBase > LanSession.ClientSeq)
            LanSession.ClientSeq = payload.ClientSeqBase;   // 接着主机已处理的序号继续

        NetResumeStore.Pending = payload;
        LanSession.Config = payload.Config;
        Session.LanBattle = true;

        GD.Print($"[Net] 收到重连状态，回放 {payload.History?.Length ?? 0} 条动作后重载场景");
        GetTree().CreateTimer(0.15).Timeout += () => GetTree().ReloadCurrentScene();
    }

    /// <summary>客户端重连循环：每 2 秒重试一次，连上后请求状态包。</summary>
    private async void NetStartReconnectLoop(bool promoteAfter = false)
    {
        if (_reconnectLoopRunning)
            return;

        _reconnectLoopRunning = true;
        int maxTries = promoteAfter ? 6 : 60;   // 需要接管时 12 秒内确认原房主是否真的没了
        for (int i = 0; i < maxTries; i++)
        {
            await ToSignal(GetTree().CreateTimer(2.0), SceneTreeTimer.SignalName.Timeout);
            if (!IsInstanceValid(this) || _resumeReloading || !Session.LanBattle)
                return;

            SetReconnectLabel(Loc.T("lan.reconnect_try", i + 1));

            // 还没连上才新建连接；已经连上就复用，避免每次重试都占一个新连接槽
            if (!LanSession.LinkReady)
            {
                if (!LanSession.ReconnectClient(out _))
                    continue;

                if (_netBattle != null)
                    _netBattle.Multiplayer.MultiplayerPeer = LanSession.Peer;

                await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
                if (!IsInstanceValid(this) || _resumeReloading)
                    return;
            }

            // 房主可能还在载入/初始化，多请求几次（同一连接上重复发，幂等）
            NetBridge.Instance?.SendResumeRequest(LanSession.MatchToken);

            for (int k = 0; k < 8 && !_resumeReloading; k++)
                await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
        }

        if (!IsInstanceValid(this) || _resumeReloading)
            return;

        // 一直连不上：认为原房主真的没了，由本机接管
        string promoteError = "";
        if (promoteAfter && !LanSession.IsHost && LanSession.PromoteToHost(out promoteError))
        {
            NetBridge.Instance?.RebindPeer();
            if (_netBattle != null)
                _netBattle.Multiplayer.MultiplayerPeer = LanSession.Peer;

            ShowReconnectOverlay(
                $"{Loc.T("lan.reconnect_promoted")}\n" +
                $"{Loc.T("lan.reconnect_ip")}: {LanSession.LocalIpv4()}\n" +
                Loc.T("lan.reconnect_wait_peer"));
            if (_reconnectRejoinBtn != null)
                _reconnectRejoinBtn.Visible = false;
            return;
        }

        if (promoteAfter && !string.IsNullOrEmpty(promoteError))
            GD.Print($"[Net] 未接管房主：{promoteError}");

        SetReconnectLabel(promoteAfter ? $"{Loc.T("lan.reconnect_failed")}\n{promoteError}" : Loc.T("lan.reconnect_failed"));
    }

    private void SetReconnectLabel(string text)
    {
        if (_reconnectLabel != null && IsInstanceValid(_reconnectLabel))
            _reconnectLabel.Text = text;
    }

    /// <summary>断线重连遮罩：沿用设置面板的材质/按钮样式，带淡入与呼吸动画。</summary>
    private void ShowReconnectOverlay(string text)
    {
        if (_reconnectOverlay == null)
        {
            var layer = new CanvasLayer { Name = "ReconnectLayer", Layer = 210 };
            AddChild(layer);

            _reconnectOverlay = new Control
            {
                Name = "ReconnectOverlay",
                Theme = UiTheme.Get(),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            _reconnectOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(_reconnectOverlay);

            var dim = new ColorRect
            {
                Color = new Color(0.03f, 0.02f, 0.02f, 0f),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _reconnectOverlay.AddChild(dim);

            var center = new CenterContainer();
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _reconnectOverlay.AddChild(center);

            _reconnectPanel = new PanelContainer { CustomMinimumSize = new Vector2(520, 0) };
            center.AddChild(_reconnectPanel);

            var outer = new MarginContainer();
            outer.AddThemeConstantOverride("margin_left", 26);
            outer.AddThemeConstantOverride("margin_right", 26);
            outer.AddThemeConstantOverride("margin_top", 22);
            outer.AddThemeConstantOverride("margin_bottom", 22);
            _reconnectPanel.AddChild(outer);

            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 14);
            outer.AddChild(box);

            // 标题行：图标 + 标题
            var titleRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            titleRow.AddThemeConstantOverride("separation", 10);
            box.AddChild(titleRow);

            var icon = new TextureRect
            {
                Texture = GD.Load<Texture2D>("res://gameAsset/textures/ui/icon/icon_defense.png"),
                CustomMinimumSize = new Vector2(28, 28),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            titleRow.AddChild(icon);

            var title = new Label { Text = Loc.T("lan.reconnect_title") };
            title.AddThemeFontSizeOverride("font_size", 26);
            title.AddThemeColorOverride("font_color", UiTheme.Ink);
            titleRow.AddChild(title);

            box.AddChild(new HSeparator());

            _reconnectTitle = title;
            _reconnectLabel = new Label
            {
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            _reconnectLabel.AddThemeFontSizeOverride("font_size", 16);
            _reconnectLabel.AddThemeColorOverride("font_color", UiTheme.SandDark);
            box.AddChild(_reconnectLabel);

            var hint = new Label
            {
                Text = Loc.T("lan.reconnect_hint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            hint.AddThemeFontSizeOverride("font_size", 13);
            hint.AddThemeColorOverride("font_color", UiTheme.Grey);
            box.AddChild(hint);

            box.AddChild(new HSeparator());

            _reconnectRejoinBtn = new Button
            {
                Text = Loc.T("lan.reconnect_rejoin"),
                CustomMinimumSize = new Vector2(0, 46),
                Visible = false,
            };
            _reconnectRejoinBtn.Pressed += () =>
            {
                LanSession.Leave();
                Session.LanBattle = false;

                // 房主：对局已保存在本地，回主菜单即可；客户端：回大厅重新加入
                string target = _reconnectRejoinToMenu
                    ? "res://scenes/mainMenu.tscn"
                    : "res://scenes/lanLobby.tscn";
                GetTree().ChangeSceneToFile(target);
            };
            box.AddChild(_reconnectRejoinBtn);

            var quit = new Button
            {
                Text = Loc.T("lan.reconnect_quit"),
                CustomMinimumSize = new Vector2(0, 46),
            };
            quit.AddThemeColorOverride("font_color", new Color("C0392B"));
            quit.AddThemeColorOverride("font_hover_color", new Color("E04B3A"));
            quit.AddThemeColorOverride("font_pressed_color", new Color("9B2C21"));
            quit.Pressed += () =>
            {
                SaveAbandonedRecord();
                LanSession.Leave();
                Session.LanBattle = false;
                GetTree().ChangeSceneToFile("res://scenes/mainMenu.tscn");
            };
            box.AddChild(quit);

            ButtonJuice.AttachAll(_reconnectOverlay);
            UiSound.AttachAll(_reconnectOverlay);

            // 打开动画：遮罩淡入 + 面板缩放
            _reconnectPanel.PivotOffset = _reconnectPanel.Size * 0.5f;
            _reconnectPanel.Scale = Vector2.One * 0.955f;
            _reconnectPanel.Modulate = new Color(1, 1, 1, 0);
            Tween open = CreateTween().SetParallel(true);
            open.TweenProperty(dim, "color:a", 0.6f, 0.18f);
            open.TweenProperty(_reconnectPanel, "modulate:a", 1f, 0.18f);
            open.TweenProperty(_reconnectPanel, "scale", Vector2.One, 0.22f)
                .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        }
        else
        {
            SetReconnectLabel(text);
        }

        _reconnectOverlay.Visible = true;
        StartReconnectPulse();
    }

    /// <summary>“重连中…”文字轻微呼吸，提示还在尝试。</summary>
    private void StartReconnectPulse()
    {
        if (_reconnectLabel == null || !IsInstanceValid(_reconnectLabel))
            return;

        _reconnectPulse?.Kill();
        _reconnectLabel.Modulate = Colors.White;
        _reconnectPulse = CreateTween();
        _reconnectPulse.SetLoops();
        _reconnectPulse.TweenProperty(_reconnectLabel, "modulate:a", 0.45f, 0.7f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        _reconnectPulse.TweenProperty(_reconnectLabel, "modulate:a", 1f, 0.7f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    private void HideReconnectOverlay()
    {
        _reconnectPulse?.Kill();
        _reconnectPulse = null;
        if (_reconnectOverlay != null && IsInstanceValid(_reconnectOverlay))
            _reconnectOverlay.Visible = false;
    }
    /// <summary>把中断的对局（配置 + 已执行动作）落到 user:// 便于事后恢复。</summary>
    private void SaveAbandonedRecord()
    {
        if (!Session.LanBattle || LanSession.Config == null || _netHistory.Count == 0)
            return;

        try
        {
            var payload = new NetResumePayload
            {
                Token = LanSession.MatchToken,
                Config = LanSession.Config,
                History = _netHistory.Select(x => x.ToJson()).ToArray(),
            };

            string path = $"user://lan_match_{System.DateTime.Now:yyyyMMdd_HHmmss}.json";
            using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            file?.StoreString(payload.ToJson());
            GD.Print($"[Net] 已记录中断对局：{path}（{_netHistory.Count} 条动作）");
        }
        catch (System.Exception e)
        {
            GD.PushWarning($"[Net] 记录中断对局失败：{e.Message}");
        }
    }
    /// <summary>本地发起一条动作：主机立即执行并广播，客户端只发请求。</summary>
    private void NetSendLocal(NetIntent it)
    {
        it.A = NetLocalActor;

        if (LanSession.IsHost)
        {
            // 主机本地动作同样先判定，不合法就不播动画
            if (!ValidateNetIntent(it, out string why))
            {
                _hud.Toast(why);
                return;
            }

            _netBattle?.BroadcastApplied(it);
            ExecuteNetIntent(it);
        }
        else
        {
            // 客户端：带序号发出并记入待确认队列（断线重连后补发）
            it.LocalSeq = ++LanSession.ClientSeq;
            LanSession.PendingIntents.Add(it);
            _netBattle?.SendIntentToHost(it);
        }
    }

    /// <summary>
    /// 两端共用同一段执行代码（按 actor 映射阵营）。
    /// 先做“状态变更”，再做“纯表现”动画：状态变化路径两端完全一致，
    /// 动画只依赖已经发生的状态，不会反过来影响同步。
    /// </summary>
    /// <summary>动作合法性判定：必须在任何动画之前完成（联机时由主机把关）。</summary>
    private bool ValidateCardChoice(NetIntent it, PlayerState st, out string why)
    {
        why = "";
        int[] picks = it.Picks ?? System.Array.Empty<int>();
        switch (it.Choice)
        {
            case "discard":
                if (_state.PendingDiscard <= 0 || picks.Length != 1 ||
                    picks[0] < 0 || picks[0] >= st.Hand.Count)
                {
                    why = "弃牌选择无效";
                    return false;
                }
                return true;

            case "duplicate":
                if (!_state.PendingDuplicatePick || picks.Length != 1 ||
                    picks[0] < 0 || picks[0] >= st.Hand.Count)
                {
                    why = "复制选择无效";
                    return false;
                }
                return true;

            case "exhaust":
                if (!_state.PendingExhaustPick || picks.Length != 1 ||
                    picks[0] < 0 || picks[0] >= st.Hand.Count)
                {
                    why = "消耗选择无效";
                    return false;
                }
                return true;

            case "retain":
                if (!_state.PendingRetainPick || picks.Length != 2)
                {
                    why = "保留选择数量无效";
                    return false;
                }
                foreach (int index in picks)
                {
                    if (index < 0 || index >= st.Hand.Count || _state.HasRetainAt(st, index))
                    {
                        why = "保留目标无效";
                        return false;
                    }
                }
                return true;

            case "random":
                if (_state.PendingRandomChoices.Count == 0 || picks.Length != 2)
                {
                    why = "随机卡选择无效";
                    return false;
                }
                foreach (int index in picks)
                {
                    if (index < 0 || index >= _state.PendingRandomChoices.Count)
                    {
                        why = "随机卡下标无效";
                        return false;
                    }
                }
                return true;

            case "deck":
                if (!_state.PendingDeckPick || picks.Length != 2)
                {
                    why = "抽牌堆选择无效";
                    return false;
                }
                foreach (int index in picks)
                {
                    if (index < 0 || index >= st.Deck.Count)
                    {
                        why = "抽牌堆下标无效";
                        return false;
                    }
                }
                return true;

            case "reinforce":
                if (_state.PendingReinforceChoices.Count == 0 || picks.Length != 1 ||
                    picks[0] < 0 || picks[0] >= _state.PendingReinforceChoices.Count)
                {
                    why = "增援选择无效";
                    return false;
                }
                return true;

            case "reclaim":
                if (!_state.PendingReclaimExhaust || picks.Length != 1 ||
                    picks[0] < 0 || picks[0] >= st.Exhaust.Count)
                {
                    why = "消耗堆选择无效";
                    return false;
                }
                return true;
        }

        why = "未知的卡牌选择";
        return false;
    }

    private bool ValidateNetIntent(NetIntent it, out string why)
    {
        why = "";
        PlayerId side = NetSideOfActor(it.A);
        PlayerState st = _state.StateOf(side);
        HexCoord to = new(it.Q, it.R);
        HexCoord from = new(it.Q2, it.R2);

        switch (it.Type)
        {
            case NetIntentType.Deploy:
            {
                if (it.Hand < 0 || it.Hand >= st.Hand.Count)
                {
                    why = "手牌位置无效";
                    return false;
                }

                CardDef card = GameDatabase.Card(st.Hand[it.Hand]);
                if (card == null)
                {
                    why = "卡牌不存在";
                    return false;
                }

                // 校验费用时必须用这张手牌的真实实例序号，
                // 否则同名卡里只有后面那张带 0 费标记时会误报金币不足。
                int previousPlayIndex = _state.PlayIndex;
                _state.PlayIndex = it.Hand;
                try
                {
                    if (it.NoTarget)
                    {
                        if (card.KindEnum != CardKind.Global)
                        {
                            why = "这张牌需要选择地块";
                            return false;
                        }

                        return _state.CanPlayGlobalCard(st, card.Id, null, out why);
                    }

                    if (card.KindEnum == CardKind.Unit)
                        return _state.CanDeployCard(card.Id, side, to, out why);

                    if (!_state.CanPlayGlobalCard(st, card.Id, to, out why))
                        return false;

                    return true;
                }
                finally
                {
                    _state.PlayIndex = previousPlayIndex;
                }
            }

            case NetIntentType.CardChoice:
                return ValidateCardChoice(it, st, out why);

            case NetIntentType.Move:
            {
                UnitInst u = _state.UnitsAt(from).FirstOrDefault(x => x.Owner == side && x.Alive);
                if (u == null)
                {
                    if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
                        GD.Print($"[NetDiag] Move from={from} to={to} side={side} A={it.A} localActor={LanSession.LocalActor} " +
                                 $"units={string.Join(";", _state.Units.Select(x => $"{x.Owner}@{x.Coord}:{x.Alive}"))}");
                    why = "没有可移动的单位";
                    return false;
                }

                if (!_state.MoveRange(u).Contains(to))
                {
                    why = "目标超出移动范围";
                    return false;
                }

                return true;
            }

            case NetIntentType.Attack:
            {
                UnitInst u = _state.UnitsAt(from).FirstOrDefault(x => x.Owner == side && x.Alive);
                if (u == null)
                {
                    why = "没有可攻击的单位";
                    return false;
                }

                if (!_state.CanAttack(u, to))
                {
                    why = "目标不在攻击范围内";
                    return false;
                }

                return true;
            }

            case NetIntentType.Spawn:
            {
                BuildingInst b = _state.BuildingAt(to);
                if (b == null || b.Owner != side)
                {
                    why = "没有可出兵的建筑";
                    return false;
                }

                return _state.CanSpawnFrom(b, it.Card, out why);
            }

            case NetIntentType.Build:
            {
                if (!_state.CanBuildAt(to, side, it.Id))
                {
                    why = "这里不能建造";
                    return false;
                }

                BuildingDef def = GameDatabase.Building(it.Id);
                if (def == null)
                {
                    why = "建筑不存在";
                    return false;
                }

                if (st.Gold < def.UpgradeCostAt(0))
                {
                    why = $"金币不足，建造需要 {def.UpgradeCostAt(0)}$";
                    return false;
                }

                return true;
            }

            case NetIntentType.Upgrade:
            {
                BuildingInst b = _state.BuildingAt(to);
                if (b == null || b.Owner != side)
                {
                    why = "没有可升级的建筑";
                    return false;
                }

                if (b.Level >= b.Def.MaxLevel)
                {
                    why = "已满级";
                    return false;
                }

                if (st.Gold < b.Def.UpgradeCostAt(b.Level))
                {
                    why = $"金币不足，升级需要 {b.Def.UpgradeCostAt(b.Level)}$";
                    return false;
                }

                return true;
            }

            case NetIntentType.Repair:
            {
                BuildingInst b = _state.BuildingAt(to);
                if (b == null || b.Owner != side)
                {
                    why = "没有可维修的建筑";
                    return false;
                }

                if (b.Hp >= b.MaxHp)
                {
                    why = "建筑不需要维修";
                    return false;
                }

                if (st.Gold < BattleState.RepairCost(b))
                {
                    why = $"金币不足，维修需要 {BattleState.RepairCost(b)}$";
                    return false;
                }

                return true;
            }

            case NetIntentType.Demolish:
            {
                BuildingInst b = _state.BuildingAt(to);
                if (b == null || b.Owner != side)
                {
                    why = "没有可拆除的建筑";
                    return false;
                }

                if (b.IsHq)
                {
                    why = "指挥部不能拆除";
                    return false;
                }

                return true;
            }

            case NetIntentType.Dive:
            {
                UnitInst u = _state.UnitsAt(to).FirstOrDefault(x => x.Owner == side && x.Alive);
                if (u == null || u.DefId != "submarine")
                {
                    why = "没有可切换的潜水艇";
                    return false;
                }

                if (u.DiveUsed)
                {
                    why = "本回合已经切换过潜浮状态";
                    return false;
                }

                return true;
            }

            case NetIntentType.Radar:
            {
                UnitInst u = _state.UnitsAt(to).FirstOrDefault(x => x.Owner == side && x.Alive);
                if (u == null || u.DefId != "carrier")
                {
                    why = "没有航空母舰";
                    return false;
                }

                if (u.RadarUsed)
                {
                    why = "本回合已经搜索过";
                    return false;
                }

                return true;
            }

            case NetIntentType.CarrierSpawn:
            {
                UnitInst u = _state.UnitsAt(to).FirstOrDefault(x => x.Owner == side && x.Alive);
                if (u == null || u.DefId != "carrier")
                {
                    why = "没有航空母舰";
                    return false;
                }

                return _state.CanCarrierSpawn(u, it.Card, out why);
            }

            case NetIntentType.EndTurn:
                if (_state.CurrentPlayer != side)
                {
                    why = "还没到你行动";
                    return false;
                }

                return true;
        }

        return true;
    }
    private void ExecuteNetIntent(NetIntent it)
    {
        if (_netBusy)
        {
            // 命中动画还没走完：按顺序排队，等结算完依次执行（保证状态顺序一致）
            _netPending.Enqueue(it);
            return;
        }

        PlayerId side = NetSideOfActor(it.A);
        PlayerState st = _state.StateOf(side);
        HexCoord to = new(it.Q, it.R);
        HexCoord from = new(it.Q2, it.R2);
        bool local = it.A == NetLocalActor;

        // ── 状态变更前：抓取表现需要的信息（之后状态就变了）──
        UnitInst actorUnit = null;             // 移动/攻击的发起单位
        CardDef deployCard = null;             // 打出的卡
        int deploySiloId = 0;                  // 导弹卡：产出它的发射井
        Rect2 handRect = default;
        bool hasHandRect = false;
        var discardRects = new List<(string Id, Rect2 Rect)>();

        switch (it.Type)
        {
            case NetIntentType.Deploy:
                if (it.Hand >= 0 && it.Hand < st.Hand.Count)
                {
                    deployCard = GameDatabase.Card(st.Hand[it.Hand]);
                    if (deployCard != null && deployCard.Effect == "missile")
                    {
                        var produced = st.Produced.FirstOrDefault(p => p.Card == deployCard.Id);
                        deploySiloId = produced.SiloId;
                    }

                    if (local)
                        hasHandRect = _hud.TryGetHandCardRect(it.Hand, out handRect);
                }

                break;

            case NetIntentType.Move:
            case NetIntentType.Attack:
                actorUnit = _state.UnitsAt(from).FirstOrDefault(x => x.Owner == side && x.Alive);
                break;

            case NetIntentType.Dive:
            case NetIntentType.Radar:
            case NetIntentType.CarrierSpawn:
                actorUnit = _state.UnitsAt(to).FirstOrDefault(x => x.Owner == side && x.Alive);
                break;
            case NetIntentType.EndTurn:
                if (local)
                {
                    for (int i = 0; i < _state.Player.Hand.Count; i++)
                    {
                        if (_hud.TryGetHandCardRect(i, out Rect2 r))
                            discardRects.Add((_state.Player.Hand[i], r));
                    }
                }

                break;
        }

        // ── 命中前动画：攻击 / 打击卡先播特效，伤害等爆炸瞬间再结算 ──
        bool prePlayed = false;
        float delay = 0f;

        if (it.Type == NetIntentType.Attack && actorUnit != null)
        {
            delay = PlayAttackPreVisual(actorUnit, to);
            prePlayed = true;
        }
        else if (it.Type == NetIntentType.Deploy && deployCard != null &&
                 deployCard.KindEnum != CardKind.Unit && !it.NoTarget)
        {
            PlayDeployPreVisual(local, hasHandRect, handRect, deployCard, to);
            delay = PlayCardVfx(side, deployCard, to, deploySiloId);
            prePlayed = true;
        }

        if (delay > 0.02f)
        {
            if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
                GD.Print($"[NetFx] impact-delay {it.Type} {delay:F2}s t={Time.GetTicksMsec()}");

            _netBusy = true;
            _netBusySinceMsec = Time.GetTicksMsec();
            var timer = GetTree().CreateTimer(delay);
            timer.Timeout += () =>
            {
                // 即使演出过程抛异常，也一定要解锁，避免整局卡在“演出中”
                try
                {
                    ApplyNetMutation(it, side, to, from, local, actorUnit, deployCard,
                        hasHandRect, handRect, discardRects, prePlayed);
                }
                catch (System.Exception e)
                {
                    GD.PushError($"[Net] 结算 {it.Type} 失败：{e}");
                    _hud.Toast("结算出错，已跳过该动作");
                }
                finally
                {
                    _netBusy = false;
                    PumpNetPending();
                }
            };
            return;
        }

        ApplyNetMutation(it, side, to, from, local, actorUnit, deployCard,
            hasHandRect, handRect, discardRects, false);
    }

    /// <summary>结算完一个动作后，把排队中的动作按顺序继续执行完（遇到需要延迟的会重新挂起）。</summary>
    private void PumpNetPending()
    {
        while (!_netBusy && _netPending.Count > 0)
        {
            NetIntent pending = _netPending.Dequeue();
            if (pending.NeedsValidation)
            {
                pending.NeedsValidation = false;
                if (LanSession.IsHost)
                {
                    PlayerId pendingSide = NetSideOfActor(pending.A);
                    string why = "";
                    if (_state.CurrentPlayer != pendingSide ||
                        !ValidateNetIntent(pending, out why))
                    {
                        if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
                            GD.Print($"[Net] queued rejected {pending.Type}：{why}");
                        _netBattle?.SendReject(Multiplayer.GetRemoteSenderId(),
                            string.IsNullOrEmpty(why) ? "动作顺序无效" : why);
                        continue;
                    }
                }
            }

            ExecuteNetIntent(pending);
        }
    }
    /// <summary>攻击的“命中前”表现：转向 + 开火动画 + 弹道，返回命中延迟。</summary>
    private float PlayAttackPreVisual(UnitInst attacker, HexCoord target)
    {
        _map.FlashCell(target, new Color("E8563F"));
        AudioManager.PlayAttack(attacker.DefId);

        Vector3 targetWorld = HexLayout.ToWorld(target, HexMapView.TileTop);
        if (attacker.Alive)
        {
            if (!_map.FaceUnitTurretToward(attacker, targetWorld))
                _map.FaceUnitToward(attacker, targetWorld);
            _map.PlayUnitAttack(attacker.Id);
        }

        return PlayAttackVfx(attacker, target);
    }

    /// <summary>打击卡的“命中前”表现：卡牌飞向落点。</summary>
    private void PlayDeployPreVisual(bool local, bool hasHandRect, Rect2 handRect, CardDef card, HexCoord target)
    {
        AudioManager.PlayDeploy(card.Unit);

        Rect2 fromRect = hasHandRect
            ? handRect
            : new Rect2(new Vector2(540f, 24f), new Vector2(200f, 132f));
        Vector2 screen = _rig.Camera.UnprojectPosition(
            HexLayout.ToWorld(target, HexMapView.TileTop + 0.25f));
        _hud.PlayCardFlyFrom(fromRect, card, screen);
    }

    /// <summary>真正改状态 + 收尾刷新（两端完全一致）。</summary>
    private void ApplyNetMutation(
        NetIntent it,
        PlayerId side,
        HexCoord to,
        HexCoord from,
        bool local,
        UnitInst actorUnit,
        CardDef deployCard,
        bool hasHandRect,
        Rect2 handRect,
        List<(string Id, Rect2 Rect)> discardRects,
        bool skipImpactVfx)
    {
        if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
            GD.Print($"[NetFx] apply {it.Type} skip={skipImpactVfx} t={Time.GetTicksMsec()}");

        PlayerState st = _state.StateOf(side);

        switch (it.Type)
        {
            case NetIntentType.Deploy:
            {
                if (it.Hand < 0 || it.Hand >= st.Hand.Count || deployCard == null)
                    return;

                string cardId = st.Hand[it.Hand];
                _state.PlayIndex = it.Hand;
                if (deployCard.KindEnum == CardKind.Unit)
                    _state.PlayUnitCard(st, cardId, to, out _);
                else
                    _state.PlayGlobalCard(st, cardId, it.NoTarget ? (HexCoord?)null : to, out _);
                _state.PlayIndex = -1;

                if (local)
                {
                    _pendingPlayedRect = handRect;
                    AnimateCardToPile(deployCard, it.Hand);
                }

                break;
            }

            case NetIntentType.CardChoice:
            {
                ApplyCardChoice(st, it.Choice, it.Picks);
                break;
            }

            case NetIntentType.Move:
            {
                if (actorUnit != null)
                    _state.TryMove(actorUnit, to, out _);
                break;
            }

            case NetIntentType.Attack:
            {
                if (actorUnit != null)
                    _state.PerformAttack(actorUnit, to);
                break;
            }

            case NetIntentType.Spawn:
            {
                BuildingInst b = _state.BuildingAt(to);
                if (b != null && b.Owner == side)
                    _state.TrySpawnFromBuilding(b, it.Card, out _);
                break;
            }

            case NetIntentType.Build:
                _state.TryBuild(side, it.Id, to, out _);
                break;

            case NetIntentType.Upgrade: { BuildingInst b = OwnBuildingAt(to, side); if (b != null) _state.TryUpgrade(b, out _); break; }
            case NetIntentType.Repair: { BuildingInst b = OwnBuildingAt(to, side); if (b != null) _state.TryRepair(b, out _); break; }
            case NetIntentType.Demolish: { BuildingInst b = OwnBuildingAt(to, side); if (b != null) _state.Demolish(b); break; }

            case NetIntentType.Dive:
                if (actorUnit != null)
                    _state.ToggleSubmarineDive(actorUnit, out _);
                break;

            case NetIntentType.Radar:
                if (actorUnit != null)
                    _state.UseCarrierRadar(actorUnit, out _, out _);
                break;

            case NetIntentType.CarrierSpawn:
                if (actorUnit != null)
                    _state.TryCarrierSpawn(actorUnit, it.Card, out _);
                break;
            case NetIntentType.EndTurn:
                _state.EndTurn();
                break;

            case NetIntentType.Surrender:
                _state.Phase = BattlePhase.GameOver;
                _state.Winner = _state.OpponentOf(side);
                break;
        }

        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        RefreshNetSelection();

        if (local && it.Type == NetIntentType.Deploy)
            _netLocalDeployPending = false;

        if (local && it.Type == NetIntentType.Deploy && HasPendingCardChoice)
            OpenPendingCardChoiceForLocal();

        PlayIntentVisuals(it, side, local, to, from, actorUnit, deployCard, hasHandRect, handRect,
            discardRects, skipImpactVfx);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--net-sync-log") >= 0)
        {
            string pBuild = string.Join(",", _state.Buildings.Where(b => b.Owner == PlayerId.Player)
                .OrderBy(b => b.Id).Select(b => $"{b.DefId}{b.Level}"));
            string eBuild = string.Join(",", _state.Buildings.Where(b => b.Owner == PlayerId.Enemy)
                .OrderBy(b => b.Id).Select(b => $"{b.DefId}{b.Level}"));
            GD.Print($"[Sync] actor={LanSession.LocalActor} type={it.Type} A={it.A} turn={_state.TurnNumber} " +
                     $"cur={_state.CurrentPlayer} goldP={_state.Player.Gold} goldE={_state.Enemy.Gold} " +
                     $"handP={string.Join(",", _state.Player.Hand)} handE={string.Join(",", _state.Enemy.Hand)} " +
                     $"deckP={string.Join(",", _state.Player.Deck)} deckE={string.Join(",", _state.Enemy.Deck)} " +
                     $"Pbuild={pBuild} Ebuild={eBuild}");
        }

        if (!_netReplaying)
            _netHistory.Add(it);

        CheckGameOver();
    }
    /// <summary>联机执行完动作后，刷新本地选中单位/建筑的范围高亮与信息面板。</summary>
    private void RefreshNetSelection()
    {
        if (_state == null)
            return;

        if (_state.Phase != BattlePhase.PlayerTurn)
        {
            ClearSelection();
            return;
        }

        if (_selUnit != null)
        {
            if (_selUnit.Alive)
                SelectUnit(_selUnit);
            else
                ClearSelection();
            return;
        }

        if (_selBuilding != null && !_selBuilding.IsDead)
            SelectBuilding(_selBuilding);
    }

    /// <summary>某条动作的表现层动画（纯装饰，不影响状态）。</summary>
    private void PlayIntentVisuals(
        NetIntent it,
        PlayerId side,
        bool local,
        HexCoord to,
        HexCoord from,
        UnitInst actorUnit,
        CardDef deployCard,
        bool hasHandRect,
        Rect2 handRect,
        List<(string Id, Rect2 Rect)> discardRects,
        bool skipImpactVfx)
    {
        if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
            GD.Print($"[NetFx] {it.Type} side={side} local={local} to={to} from={from}");

        switch (it.Type)
        {
            case NetIntentType.Deploy:
            {
                if (deployCard == null || it.NoTarget)
                    return;

                _map.FlashCell(to, new Color("7FD35B"));

                if (!skipImpactVfx)
                {
                    AudioManager.PlayDeploy(deployCard.Unit);

                    // 卡牌从手牌（自己的牌）或屏幕上方（对手的牌）飞向落点
                    Rect2 fromRect = hasHandRect
                        ? handRect
                        : new Rect2(new Vector2(540f, 24f), new Vector2(200f, 132f));
                    Vector2 target = _rig.Camera.UnprojectPosition(
                        HexLayout.ToWorld(to, HexMapView.TileTop + 0.25f));
                    _hud.PlayCardFlyFrom(fromRect, deployCard, target);

                    // 打击 / 维修类卡：补上对应的命中特效（与单机同一套 Vfx）
                    if (deployCard.KindEnum != CardKind.Unit)
                    {
                        if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
                            GD.Print($"[NetFx] global {deployCard.Id} effect={deployCard.Effect} at {to}");

                        PlayCardVfx(side, deployCard, to, 0);
                    }
                }

                break;
            }

            case NetIntentType.Move:
            {
                // 敌方深潜潜艇：本体已完全透明，任何起点/终点/脚下反馈都会暴露航迹，
                // 所以这一整段表现（音效、脉冲环、闪光）都跳过。
                bool hiddenMove = !local && actorUnit != null &&
                                  actorUnit.DefId == "submarine" && actorUnit.Submerged;

                if (actorUnit != null && !hiddenMove)
                {
                    AudioManager.PlayMove(actorUnit.DefId);
                    if (!local)
                    {
                        // 对手移动：脚下扩散环 + 起点/终点闪光，明确“谁从哪走到哪”
                        GD.Print($"[NetFx] remote-move unit={actorUnit.Id} {from}->{to}");
                        _map.PulseUnit(actorUnit.Id, new Color("F2C230"), 0.8f);
                        _map.FlashCell(from, new Color("5BC0EB"), 0.5f);
                    }
                }

                if (!hiddenMove)
                    _map.FlashCell(to, new Color("7FD35B"), 0.55f);
                break;
            }

            case NetIntentType.Attack:
            {
                _map.FlashCell(to, new Color("E8563F"));
                if (!skipImpactVfx && actorUnit != null)
                {
                    AudioManager.PlayAttack(actorUnit.DefId);
                    Vector3 targetWorld = HexLayout.ToWorld(to, HexMapView.TileTop);
                    if (actorUnit.Alive)
                    {
                        if (!_map.FaceUnitTurretToward(actorUnit, targetWorld))
                            _map.FaceUnitToward(actorUnit, targetWorld);
                        _map.PlayUnitAttack(actorUnit.Id);
                    }

                    PlayAttackVfx(actorUnit, to);
                }

                foreach (UnitInst t in _state.UnitsAt(to))
                    _map.PunchUnit(t.Id);
                if (_state.BuildingAt(to) is { } hitBuilding)
                    _map.PunchBuilding(hitBuilding.Id);
                break;
            }

            case NetIntentType.Spawn:
            {
                BuildingInst b = _state.BuildingAt(to);
                if (b != null)
                {
                    _map.PunchBuilding(b.Id);
                    AudioManager.PlayDeploy(b.DefId);
                }

                _map.FlashCell(to, new Color("7FD35B"));
                break;
            }

            case NetIntentType.Build:
            {
                // 新建模型由 SyncEntities 里的 PopIn 播「缩放 0 → 1」，这里不要再 Punch，
                // 否则会把生长动画直接顶掉（表现为只看到一半）
                AudioManager.PlaySelect(it.Id);
                _map.FlashCell(to, new Color("7FD35B"));

                BuildingInst builtNow = _state.BuildingAt(to);
                if (builtNow != null)
                    _map.PlayBuildingGrow(builtNow.Id);
                break;
            }

            case NetIntentType.Upgrade:
            {
                // 升级会换模型：显式播一次 0 → 1，保证动画一定出现
                BuildingInst b = _state.BuildingAt(to);
                if (b != null)
                {
                    AudioManager.PlayUpgrade(b.DefId);
                    _map.PlayBuildingGrow(b.Id);
                }

                break;
            }

            case NetIntentType.Repair:
            {
                BuildingInst b = _state.BuildingAt(to);
                if (b != null)
                {
                    AudioManager.PlayUi("ui_click");
                    _vfx?.Heal(_map.BuildingBarAnchor(b) - Vector3.Up * 0.9f, 1.0f);
                    _map.PunchBuilding(b.Id);
                }

                break;
            }

            case NetIntentType.Demolish:
                _map.FlashCell(to, new Color("E8563F"));
                break;

            case NetIntentType.Dive:
                AudioManager.PlayUi("dive_submarine");
                RefreshSubmarineVisuals();
                break;

            case NetIntentType.Radar:
                AudioManager.PlayUi("radar_carrier");
                RefreshSubmarineVisuals();
                break;

            case NetIntentType.CarrierSpawn:
                AudioManager.PlayDeploy("fighter");
                break;
            case NetIntentType.EndTurn:
            {
                PlayVolleys();
                ShowIncomePopups(_state.CurrentPlayer);

                // 任意一方洗回牌库都要在本地播动画（用累计计数对比，两端一致）
                if (_state.ReshuffleSerial != _netReshuffleSerial)
                {
                    _netReshuffleSerial = _state.ReshuffleSerial;
                    if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
                        GD.Print($"[NetFx] reshuffle anim serial={_netReshuffleSerial}");
                    _hud.PlayReshuffleAnimation();
                }

                if (discardRects.Count > 0)
                    AnimateEndOfTurnDiscards(discardRects);
                AudioManager.SetIntensity(_state.TurnNumber < 4 ? 0 : _state.TurnNumber < 8 ? 1 : 2);
                break;
            }

            case NetIntentType.Surrender:
                AudioManager.PlayDefeat();
                break;
        }
    }
    private BuildingInst OwnBuildingAt(HexCoord coord, PlayerId side)
    {
        BuildingInst b = _state.BuildingAt(coord);
        return b != null && b.Owner == side && !b.IsDead ? b : null;
    }

    // ─────────────────────────── UI 拦截 ───────────────────────────

    /// <summary>非本地回合：提示并拦住输入。</summary>
    private bool NetBlockIfNotMyTurn()
    {
        if (!NetMode)
            return false;

        if (_netMatchEnded)
            return true;

        if (_netFrozen)
        {
            _hud.Toast("等待重连…");
            return true;
        }

        // 演出保险：异常情况下最多卡 5 秒，超时强制解锁
        if (_netBusy && Time.GetTicksMsec() - _netBusySinceMsec > 5000)
        {
            GD.PushWarning("[Net] 演出超时，强制解除忙碌状态");
            _netBusy = false;
            PumpNetPending();
        }

        if (_netBusy)
        {
            _hud.Toast("攻击演出中…");
            return true;
        }

        if (NetMyTurn)
            return false;

        _hud.Toast("等待对方行动…");
        return true;
    }

    private bool NetSendDeploy(int handIndex, HexCoord coord, bool noTarget = false)
    {
        if (!NetMode)
            return false;

        // 客户端等待上一张牌的广播期间，忽略后续出牌点击
        if (!LanSession.IsHost && _netLocalDeployPending)
            return true;

        if (NetBlockIfNotMyTurn())
            return true;

        if (!LanSession.IsHost)
            _netLocalDeployPending = true;

        NetSendLocal(NetIntent.Deploy(NetLocalActor, handIndex, coord, noTarget));
        return true;
    }

    private bool NetSendCardChoice(string kind, int[] picks)
    {
        if (!NetMode)
            return false;

        if (NetBlockIfNotMyTurn())
            return true;

        NetSendLocal(NetIntent.CardChoice(NetLocalActor, kind, picks));
        return true;
    }

    private bool NetSendMove(HexCoord from, HexCoord to)
    {
        if (!NetMode)
            return false;

        if (NetBlockIfNotMyTurn())
            return true;

        NetSendLocal(NetIntent.Move(NetLocalActor, from, to));
        return true;
    }

    private bool NetSendAttack(HexCoord from, HexCoord target)
    {
        if (!NetMode)
            return false;

        if (NetBlockIfNotMyTurn())
            return true;

        NetSendLocal(NetIntent.Attack(NetLocalActor, from, target));
        return true;
    }

    private bool NetSendSpawn(HexCoord building, string cardId)
    {
        if (!NetMode)
            return false;

        if (NetBlockIfNotMyTurn())
            return true;

        NetSendLocal(NetIntent.Spawn(NetLocalActor, building, cardId));
        return true;
    }

    private bool NetSendDive(HexCoord at)
    {
        if (!NetMode)
            return false;

        if (NetBlockIfNotMyTurn())
            return true;

        NetSendLocal(NetIntent.Dive(NetLocalActor, at));
        return true;
    }

    private bool NetSendRadar(HexCoord at)
    {
        if (!NetMode)
            return false;

        if (NetBlockIfNotMyTurn())
            return true;

        NetSendLocal(NetIntent.Radar(NetLocalActor, at));
        return true;
    }

    private bool NetSendCarrierSpawn(HexCoord at, string cardId)
    {
        if (!NetMode)
            return false;

        if (NetBlockIfNotMyTurn())
            return true;

        NetSendLocal(NetIntent.CarrierSpawn(NetLocalActor, at, cardId));
        return true;
    }
    private bool NetSendBuild(string defId, HexCoord coord)
    {
        if (!NetMode)
            return false;

        if (NetBlockIfNotMyTurn())
            return true;

        NetSendLocal(NetIntent.Build(NetLocalActor, coord, defId));
        return true;
    }

    private bool NetSendBuildingOp(NetIntentType type, HexCoord coord)
    {
        if (!NetMode)
            return false;

        if (NetBlockIfNotMyTurn())
            return true;

        NetSendLocal(NetIntent.BuildingOp(NetLocalActor, type, coord));
        return true;
    }

    private bool NetSendEndTurn()
    {
        if (!NetMode)
            return false;

        if (NetBlockIfNotMyTurn())
            return true;

        NetSendLocal(NetIntent.EndTurn(NetLocalActor));
        return true;
    }

    private bool NetSendSurrender()
    {
        if (!NetMode)
            return false;

        NetSendLocal(NetIntent.Surrender(NetLocalActor));
        return true;
    }

    /// <summary>状态哈希：用于对比两端是否一致（不打日志也能肉眼看）。</summary>
    private string StateHash()
    {
        // 用“主机视角”的阵营算哈希，两端镜像状态才能直接对比
        PlayerId hostSide = LanSession.LocalActor == 0 ? PlayerId.Player : PlayerId.Enemy;
        PlayerId clientSide = _state.OpponentOf(hostSide);

        int h = 17;
        h = h * 31 + _state.TurnNumber;
        h = h * 31 + (_state.CurrentPlayer == hostSide ? 0 : 1);
        h = h * 31 + _state.StateOf(hostSide).Gold;
        h = h * 31 + _state.StateOf(clientSide).Gold;
        h = h * 31 + _state.StateOf(hostSide).Hand.Count;
        h = h * 31 + _state.StateOf(clientSide).Hand.Count;
        foreach (UnitInst u in _state.Units.OrderBy(u => u.Coord.Q).ThenBy(u => u.Coord.R).ThenBy(u => u.Hp))
        {
            h = h * 31 + u.Coord.Q;
            h = h * 31 + u.Coord.R;
            h = h * 31 + (u.Owner == hostSide ? 0 : 1);
            h = h * 31 + u.Hp;
        }

        return h.ToString("X8");
    }

    /// <summary>只含棋盘（金币/单位）的哈希：忽略回合与先手，便于对比两端是否同状态。</summary>
    private string BoardHash()
    {
        PlayerId hostSide = LanSession.LocalActor == 0 ? PlayerId.Player : PlayerId.Enemy;
        PlayerId clientSide = _state.OpponentOf(hostSide);

        int h = 7;
        h = h * 31 + _state.StateOf(hostSide).Gold;
        h = h * 31 + _state.StateOf(clientSide).Gold;
        h = h * 31 + _state.StateOf(hostSide).Hand.Count;
        h = h * 31 + _state.StateOf(clientSide).Hand.Count;

        foreach (UnitInst u in _state.Units.OrderBy(u => u.Coord.Q).ThenBy(u => u.Coord.R).ThenBy(u => u.Hp))
        {
            h = h * 31 + u.Coord.Q;
            h = h * 31 + u.Coord.R;
            h = h * 31 + (u.Owner == hostSide ? 0 : 1);
            h = h * 31 + u.Hp;
        }

        return h.ToString("X8");
    }

    /// <summary>调试：客户端在自己的回合点一次“结束回合”，验证切换回合不会掉线。</summary>
    private async void DemoClientEndTurn()
    {
        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        for (int i = 0; i < 40; i++)
        {
            if (!IsInstanceValid(this))
                return;
            if (NetMode && !LanSession.IsHost && _state.Phase == BattlePhase.PlayerTurn &&
                _state.CurrentPlayer == PlayerId.Player)
            {
                GD.Print($"[Demo] 客户端结束回合（第 {_state.TurnNumber} 回合）");
                OnEndTurn();
                return;
            }

            await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        }
    }
    /// <summary>调试：模拟“返回主菜单”（用于验证二次载入未完成对局）。</summary>
    private static bool _demoLeaveDone;

    private async void DemoLanLeave()
    {
        if (_demoLeaveDone)
            return;
        _demoLeaveDone = true;
        await ToSignal(GetTree().CreateTimer(5.0), SceneTreeTimer.SignalName.Timeout);
        GD.Print("[Demo] 对局中返回主菜单");
        LanSession.Leave();
        Session.LanBattle = false;
        GetTree().ChangeSceneToFile("res://scenes/mainMenu.tscn");
    }
    /// <summary>调试：模拟断线（主机踢掉客户端），验证重连恢复。</summary>
    private async void DemoLanKick()
    {
        await ToSignal(GetTree().CreateTimer(3.0), SceneTreeTimer.SignalName.Timeout);
        if (!NetMode || !LanSession.IsHost || LanSession.Peer is not ENetMultiplayerPeer enet)
            return;

        int[] peers = Multiplayer.GetPeers();
        if (peers.Length == 0)
        {
            GD.Print("[Drop] 没有已连接的客户端");
            return;
        }

        GD.Print($"[Drop] 主机踢掉客户端 {peers[0]}，模拟断线");
        enet.DisconnectPeer(peers[0]);
    }
    /// <summary>调试：发一条非法出牌，验证不会播动画。</summary>
    private async void DemoInvalidPlay()
    {
        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        for (int i = 0; i < 20 && NetMode && !NetMyTurn; i++)
            await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        if (!NetMode || !NetMyTurn)
            return;

        int hand = _state.Player.Hand.Count > 0 ? 0 : -1;
        if (hand < 0)
            return;

        // 敌方指挥部所在格：对己方单位卡一定非法
        BuildingInst enemyHq = _state.Buildings.FirstOrDefault(b => b.IsHq && b.Owner != PlayerId.Player);
        if (enemyHq == null)
            return;

        string before = StateHash();
        GD.Print($"[Invalid] 尝试把手牌{hand}打到敌方指挥部 {enemyHq.Coord} 哈希前={before}");
        NetSendDeploy(hand, enemyHq.Coord);
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Invalid] 哈希后={StateHash()}（期间自动对局可能另有动作，仅供参考）");
    }
    /// <summary>联机调试：--lan-auto 自动走一遍「结束回合 → 投降」。</summary>
    private async void DemoLanAuto()
    {
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Lan] host={LanSession.IsHost} 开局 当前={_state.CurrentPlayer} " +
                 $"手牌={_state.Player.Hand.Count} 敌方手牌={_state.Enemy.Hand.Count}");

        for (int i = 0; i < 6; i++)
        {
            await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);

            if (NetMode && NetMyTurn)
            {
                // 每回合尝试打出手里第一张能部署的单位卡，验证“出牌同步”
                for (int h = 0; h < _state.Player.Hand.Count; h++)
                {
                    string cardId = _state.Player.Hand[h];
                    CardDef card = GameDatabase.Card(cardId);
                    if (card == null || card.KindEnum != CardKind.Unit)
                        continue;

                    HexCoord? spot = null;
                    foreach (Cell cell in _state.Cells.Values)
                    {
                        if (_state.CanDeployCard(cardId, PlayerId.Player, cell.Coord, out _))
                        {
                            spot = cell.Coord;
                            break;
                        }
                    }

                    if (spot == null)
                        continue;

                    GD.Print($"[Lan] 部署 {cardId} @ {spot}");
                    NetSendDeploy(h, spot.Value);
                    await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
                    break;
                }
            }

            GD.Print($"[Lan] t={i} 当前={_state.CurrentPlayer} 回合={_state.TurnNumber} " +
                     $"我方金币={_state.Player.Gold} 敌方金币={_state.Enemy.Gold} " +
                     $"我方手牌={_state.Player.Hand.Count} 敌方手牌={_state.Enemy.Hand.Count} " +
                     $"我方单位={_state.Units.Count(u => u.Owner == PlayerId.Player)} " +
                     $"敌方单位={_state.Units.Count(u => u.Owner == PlayerId.Enemy)} phase={_state.Phase}" +
                     $" 哈希={StateHash()}");

            // 第 4 回合尝试升级一座己方建筑（验证升级生长动画）
            if (NetMode && NetMyTurn && i >= 3 && _state.Player.Gold >= 250)
            {
                BuildingInst up = _state.Buildings.FirstOrDefault(b =>
                    b.Owner == PlayerId.Player && !b.IsHq && !b.IsDead &&
                    b.Level < b.Def.MaxLevel && _state.Player.Gold >= b.Def.UpgradeCostAt(b.Level));
                if (up != null)
                {
                    GD.Print($"[Lan] 升级 {up.DefId}@{up.Coord} Lv{up.Level}→Lv{up.Level + 1}");
                    NetSendBuildingOp(NetIntentType.Upgrade, up.Coord);
                    await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
                    GD.Print($"[Lan] 升级后 t+0.15 缩放={_map.DebugBuildingScale(up.Id):F2}（应 <1）");
                    await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
                    GD.Print($"[Lan] 升级后 t+0.75 缩放={_map.DebugBuildingScale(up.Id):F2}（应为 1）");
                }
            }
            // 有能攻击的单位就打一次，验证“伤害在命中瞬间结算”
            if (NetMode && NetMyTurn)
            {
                UnitInst striker = _state.Units.FirstOrDefault(u =>
                    u.Owner == PlayerId.Player && u.Alive && !u.Attacked &&
                    _state.AttackTargets(u).Count() > 0);
                if (striker != null)
                {
                    var target = _state.AttackTargets(striker).First();
                    HexCoord tc = target.building != null ? target.building.Coord : target.unit.Coord;
                    int hpBefore = target.building != null ? target.building.Hp : target.unit.Hp;
                    GD.Print($"[Lan] 攻击 {striker.DefId}@{striker.Coord} -> {tc} 命中前HP={hpBefore}");
                    NetSendAttack(striker.Coord, tc);
                    await ToSignal(GetTree().CreateTimer(1.6), SceneTreeTimer.SignalName.Timeout);

                    int hpAfter = target.building != null ? target.building.Hp : target.unit.Hp;
                    GD.Print($"[Lan] 攻击后 HP={hpAfter} 差值={hpBefore - hpAfter}");
                }
            }
            // 手里有打击/维修卡就找合法目标打一张（验证联机特效）
            if (NetMode && NetMyTurn)
            {
                for (int h = 0; h < _state.Player.Hand.Count; h++)
                {
                    CardDef gc = GameDatabase.Card(_state.Player.Hand[h]);
                    if (gc == null || gc.KindEnum == CardKind.Unit)
                        continue;
                    if (gc.Effect != "airstrike" && gc.Effect != "missile" && gc.Effect != "repair")
                        continue;

                    HexCoord? spot = null;
                    foreach (Cell cell in _state.Cells.Values)
                    {
                        if (_state.CanPlayGlobalCard(_state.Player, gc.Id, cell.Coord, out _))
                        {
                            spot = cell.Coord;
                            break;
                        }
                    }

                    if (spot == null)
                        continue;

                    GD.Print($"[Lan] 打出 {gc.Id} @ {spot} effect={gc.Effect}");
                    NetSendDeploy(h, spot.Value);
                    await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
                    break;
                }
            }
            // 有闲置单位就先选中 + 移动一次，验证范围高亮会随操作刷新
            UnitInst mover = _state.Units.FirstOrDefault(u =>
                u.Owner == PlayerId.Player && u.Alive && !u.Moved && !u.SpawnedThisTurn &&
                _state.MoveRange(u).Count > 0);
            if (NetMode && NetMyTurn && mover != null)
            {
                SelectUnit(mover);
                int rangeBefore = _state.MoveRange(mover).Count;
                HexCoord dest = _state.MoveRange(mover).First();
                GD.Print($"[Lan] 移动前 选中={_selUnit?.Id} 可移动格={rangeBefore}");
                NetSendMove(mover.Coord, dest);
                await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
                GD.Print($"[Lan] 移动后 选中={_selUnit?.Id} 可移动格={_state.MoveRange(mover).Count} " +
                         $"已移动={mover.Moved} 攻击目标={_state.AttackTargets(mover).Count()}");
            }

            // 第 2 回合尝试造一座兵营，验证联机建造动画
            if (NetMode && NetMyTurn && i >= 1 && _state.Player.Gold >= 150 &&
                !_state.Buildings.Any(b => b.Owner == PlayerId.Player && b.DefId == "barracks"))
            {
                HexCoord? buildSpot = null;
                foreach (Cell cell in _state.Cells.Values)
                {
                    if (_state.CanBuildAt(cell.Coord, PlayerId.Player))
                    {
                        buildSpot = cell.Coord;
                        break;
                    }
                }

                if (buildSpot != null)
                {
                    GD.Print($"[Lan] 建造 barracks @ {buildSpot}");
                    NetSendBuild("barracks", buildSpot.Value);
                    await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
                }
            }

            if (NetMode && NetMyTurn)
                NetSendEndTurn();
        }

        // 静置 3 秒，让双端把所有 intent 消化完，再对比最终状态
        await ToSignal(GetTree().CreateTimer(3.0), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Lan] 最终 host={LanSession.IsHost} 回合={_state.TurnNumber} phase={_state.Phase} " +
                 $"主机方金币={_state.StateOf(LanSession.IsHost ? PlayerId.Player : PlayerId.Enemy).Gold} " +
                 $"客户端方金币={_state.StateOf(LanSession.IsHost ? PlayerId.Enemy : PlayerId.Player).Gold} " +
                 $"单位={_state.Units.Count} 棋盘哈希={BoardHash()}");
    }
}
