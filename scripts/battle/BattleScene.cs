using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SevenDay.Core;
using SevenDay.Data;
using SevenDay.UI;

namespace SevenDay.Battle;

/// <summary>
/// 战斗场景根节点：建立战场、处理选点/出牌/移动/攻击、驱动回合与 AI。
/// 交互规则来自 设计草案/战斗UI参考-4、-5、-6。
/// </summary>
public partial class BattleScene : Node3D
{
    [Export] public string MapId = "map_01";
    [Export] public int DifficultyIndex = 1;
    [Export] public bool PlayerMovesFirst = true;

    private BattleState _state;
    private HexMapView _map;
    private CameraRig _rig;
    private BattleHud _hud;
    private Vfx _vfx;

    private UnitInst _selUnit;
    private int _infoUnitHp = -1;
    private BuildingInst _selBuilding;
    private HexCoord? _hover;
    private int _selCard = -1;

    /// <summary>正在等玩家选一张手牌弃掉（物资运输）。</summary>
    private bool _discardMode;
    private HexCoord? _buildTarget;
    private bool _dragPan;
    private bool _rightDragging;
    private bool _rightMoved;
    private Vector2 _rightStart;
    private Vector2 _lastMouse;
    private Vector2 _mouseScreen;
    private bool _enemyBusy;
    private ResultScreen _result;
    private bool _resultShown;

    /// <summary>场景刚加载时窗口会抢焦点，容易把「按下」当成一次点击，先屏蔽 0.4 秒。</summary>
    private ulong _inputGateUntil;

    public BattleState State => _state;

    public override void _Ready()
    {
        AnimSpeed.ApplyFromSettings();      // 加速模式（全局 TimeScale）
        if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
            GD.Print($"[Speed] 加速模式={AnimSpeed.Fast} TimeScale={Engine.TimeScale}");

        AudioManager.StopStinger();      // 重开一局时清掉上一局的结算音效
        GameDatabase.EnsureLoaded();
        ToonMaterial.Disabled = OS.GetCmdlineUserArgs().Contains("--nomats");
        _mouseScreen = GetViewport().GetVisibleRect().Size * 0.5f;
        BuildEnvironment();
        _inputGateUntil = Time.GetTicksMsec() + 400;

        // 调试：--map map_02 直接指定地图
        string[] args = OS.GetCmdlineUserArgs();
        int mapFlag = System.Array.IndexOf(args, "--map");
        if (mapFlag >= 0 && mapFlag + 1 < args.Length)
            Session.MapId = args[mapFlag + 1];

        string mapId = string.IsNullOrEmpty(Session.MapId) ? MapId : Session.MapId;
        MapDef map = GameDatabase.Map(mapId);
        if (map == null)
        {
            GD.PushError("[BattleScene] 找不到地图数据");
            return;
        }

        _state = BattleState.Create(map, Session.Difficulty);

        _map = new HexMapView { Name = "HexMap" };
        AddChild(_map);
        _map.Build(_state);

        _rig = new CameraRig { Name = "CameraRig" };
        AddChild(_rig);
        _rig.FocusOn(MapCenter());
        float span = Mathf.Max(map.Width * HexLayout.Sqrt3, map.Height * 1.5f);
        _rig.FrameMap(span, 1.62f);
        _rig.SetPitch(55f);
        _rig.SetVerticalOffset(-1.4f);
        _rig.SaveInitialView();

        _hud = new BattleHud { Name = "Hud" };
        AddChild(_hud);
        _hud.SetHealthCamera(_rig.Camera);
        WireHud();
        _hud.ShowTransitionMask();
        // 延迟到场景初始化完成再预热（_Ready 里 root 正忙于挂子节点）
        Callable.From(SevenDay.UI.CardInspectOverlay.Prewarm).CallDeferred();

        _vfx = Vfx.Create(this);
        _vfx.Shake = (amount, duration) => _rig.Shake(amount, duration);

        // 单位/建筑阵亡时播对应的粒子
        if (_map != null)
        {
            _map.DeathFx = (world, owner, isUnit, scale) =>
            {
                Color accent = ToonMaterial.OutlineFor(owner) ?? new Color("8A6A42");
                if (isUnit)
                    _vfx.UnitDeath(world, accent, scale);
                else
                    _vfx.BuildingDeath(world, accent, scale);
            };
        }

        _state.BeginTurn(PlayerId.Player);
        // 每回合抽 5 张，开局不再额外补牌（否则直接顶到上限）
        _map.SyncEntities();
        _map.RefreshZones();
        _hud.Setup(_state);
        _hud.Toast(Loc.T("battle.intro"));
        AudioManager.SetIntensity(0);

        // 开局抽牌动画
        int drawCount = _state.DrawnIndicesThisTurn.Count;
        int producedCount = _state.ProducedThisTurn.Count;

        // 编号等入场动画播完再出，避免「牌还没飞到、数字先出现」
        float drawEnd = drawCount > 0 ? (drawCount - 1) * 0.07f + 0.34f : 0f;
        float produceEnd = producedCount > 0 ? 0.14f + 0.5f + (producedCount - 1) * 0.12f : 0f;

        _hud.HideOrderHints();
        _hud.PlayDrawAnimation(_state.DrawnIndicesThisTurn);
        PlayProduceAnimations(0.15f);
        _hud.ShowOrderHintsAfter(Mathf.Max(drawEnd, produceEnd) + 0.05f);

        // 开发调试：--autoplay 让 AI 替双方轮流走，用来跑通整局逻辑
        if (OS.GetCmdlineUserArgs().Contains("--autoplay"))
        {
            var t = GetTree().CreateTimer(0.3);
            t.Timeout += RunAutoStep;
        }

        // 开发调试：--demo-victory / --demo-defeat 直接结束对局，验证结算音效
        if (OS.GetCmdlineUserArgs().Contains("--demo-victory"))
            CallDeferred(nameof(DemoForceEnd), true);

        if (OS.GetCmdlineUserArgs().Contains("--demo-defeat"))
            CallDeferred(nameof(DemoForceEnd), false);


        // 开发调试：--demo-inspect 打开第一张手牌的放大查看器
        if (OS.GetCmdlineUserArgs().Contains("--demo-inspect"))
            CallDeferred(nameof(DemoInspect));

        // 开发调试：--demo-cardsound 依次触发打出/弃牌/抽牌音效
        if (OS.GetCmdlineUserArgs().Contains("--demo-cardsound"))
            CallDeferred(nameof(DemoCardSounds));

        // 开发调试：--demo-missile-badge 看发射井产出卡的「消耗」角标
        if (OS.GetCmdlineUserArgs().Contains("--demo-missile-badge"))
            CallDeferred(nameof(DemoMissileBadge));

        // 开发调试：--demo-missile-exhaust 验证导弹袭击进消耗堆
        if (OS.GetCmdlineUserArgs().Contains("--demo-missile-exhaust"))
            CallDeferred(nameof(DemoMissileExhaust));

        // 开发调试：--demo-pile-layers 验证牌堆查看器在飞牌之上
        if (OS.GetCmdlineUserArgs().Contains("--demo-pile-layers"))
            CallDeferred(nameof(DemoPileLayers));

        // 开发调试：--demo-reclaim 验证战场扫荡
        if (OS.GetCmdlineUserArgs().Contains("--demo-reclaim"))
            CallDeferred(nameof(DemoReclaim));

        // 开发调试：--demo-exchange-rate 验证汇率调节卡
        if (OS.GetCmdlineUserArgs().Contains("--demo-exchange-rate"))
            CallDeferred(nameof(DemoExchangeRate));

        // 开发调试：--demo-keyword-cycle 验证所有实例关键字跨牌堆
        if (OS.GetCmdlineUserArgs().Contains("--demo-keyword-cycle"))
            CallDeferred(nameof(DemoKeywordCycle));

        // 开发调试：--demo-ethereal-cycle 验证虚无标记跨牌堆传递
        if (OS.GetCmdlineUserArgs().Contains("--demo-ethereal-cycle"))
            CallDeferred(nameof(DemoEtherealCycle));

        // 开发调试：--demo-ethereal-play 验证虚无卡打出后关键字保留
        if (OS.GetCmdlineUserArgs().Contains("--demo-ethereal-play"))
            CallDeferred(nameof(DemoEtherealPlayKeep));

        // 开发调试：--demo-keybinds 验证数字键选牌 / E 键结束回合
        if (OS.GetCmdlineUserArgs().Contains("--demo-keybinds"))
            CallDeferred(nameof(DemoKeybinds));

        // 开发调试：--demo-play-ethereal 验证同名牌里打出正确的那张
        if (OS.GetCmdlineUserArgs().Contains("--demo-play-ethereal"))
            CallDeferred(nameof(DemoPlayEthereal));

        // 开发调试：--demo-play-dup / --demo-produce-full
        if (OS.GetCmdlineUserArgs().Contains("--demo-play-dup"))
            CallDeferred(nameof(DemoPlayDuplicate));
        if (OS.GetCmdlineUserArgs().Contains("--demo-produce-full"))
            CallDeferred(nameof(DemoProduceFull));

        // 开发调试：--demo-produce-fly 验证建筑产卡飞行动画
        if (OS.GetCmdlineUserArgs().Contains("--demo-produce-fly"))
            CallDeferred(nameof(DemoProduceFly));

        // 开发调试：--demo-produce-toggle 验证建筑产卡开关
        if (OS.GetCmdlineUserArgs().Contains("--demo-produce-toggle"))
            CallDeferred(nameof(DemoProduceToggle));

        // 开发调试：--demo-spawn-rightclick 验证右键收起出兵面板
        if (OS.GetCmdlineUserArgs().Contains("--demo-spawn-rightclick"))
            CallDeferred(nameof(DemoSpawnRightClick));

        // 开发调试：--demo-spawn-table 验证各等级出兵表
        if (OS.GetCmdlineUserArgs().Contains("--demo-spawn-table"))
            CallDeferred(nameof(DemoSpawnTable));

        // 开发调试：--demo-spawn-overflow 验证面板纵向滚动
        if (OS.GetCmdlineUserArgs().Contains("--demo-spawn-overflow"))
            CallDeferred(nameof(DemoSpawnOverflow));

        // 开发调试：--demo-spawn-view 看出兵面板外观（一行三个）
        if (OS.GetCmdlineUserArgs().Contains("--demo-spawn-view"))
            CallDeferred(nameof(DemoSpawnView));

        // 开发调试：--demo-spawn-lv1 复现 Lv1 出兵面板点击问题
        if (OS.GetCmdlineUserArgs().Contains("--demo-spawn-lv1"))
            CallDeferred(nameof(DemoSpawnLv1));

        // 开发调试：--demo-spawn-upgrade 验证升级后出兵面板刷新
        if (OS.GetCmdlineUserArgs().Contains("--demo-spawn-upgrade"))
            CallDeferred(nameof(DemoSpawnUpgrade));

        // 开发调试：--demo-spawn 验证兵营/机坪出兵
        if (OS.GetCmdlineUserArgs().Contains("--demo-spawn"))
            CallDeferred(nameof(DemoSpawn));

        // 开发调试：--demo-income 验证收入飘字
        if (OS.GetCmdlineUserArgs().Contains("--demo-income"))
            CallDeferred(nameof(DemoIncome));

        // 开发调试：--demo-terrain 验证各地形的移动/射程/收入/控制半径
        if (OS.GetCmdlineUserArgs().Contains("--demo-terrain"))
            CallDeferred(nameof(DemoTerrain));

        // 开发调试：--demo-demolish 验证拆除返还 65% 本金
        if (OS.GetCmdlineUserArgs().Contains("--demo-demolish"))
            CallDeferred(nameof(DemoDemolish));

        // 开发调试：--demo-drone-same 验证无人机站在敌方建筑上攻击也自爆
        if (OS.GetCmdlineUserArgs().Contains("--demo-drone-same"))
            CallDeferred(nameof(DemoDroneSameCell));

        // 开发调试：--demo-drone 验证 FPV 无人机自杀式攻击
        if (OS.GetCmdlineUserArgs().Contains("--demo-drone"))
            CallDeferred(nameof(DemoDrone));

        // 开发调试：--demo-supply 验证「物资运输」抽 3 弃 1
        if (OS.GetCmdlineUserArgs().Contains("--demo-supply"))
            CallDeferred(nameof(DemoSupply));

        // 开发调试：--demo-deploy 放几个单位并选中，用来检查移动/攻击高亮
        if (OS.GetCmdlineUserArgs().Contains("--demo-deploy"))
            CallDeferred(nameof(DemoDeploy));

        // 开发调试：--closeup 把镜头怼到己方指挥部，检查模型/描边细节
        if (OS.GetCmdlineUserArgs().Contains("--closeup"))
        {
            BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
            if (hq != null)
            {
                _rig.FocusOn(HexLayout.ToWorld(hq.Coord, 0.8f));
                _rig.FrameMap(3.2f);
                _rig.SetPitch(32f);
                _rig.SetVerticalOffset(0f);
            }
        }

        // 开发调试：--topdown 垂直俯视指挥部，用来核对模型是否在六边形正中
        if (OS.GetCmdlineUserArgs().Contains("--topdown"))
        {
            BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
            if (hq != null)
            {
                _rig.FocusOn(HexLayout.ToWorld(hq.Coord, 0.5f));
                _rig.FrameMap(3.0f);
                _rig.SetPitch(88f);
                _rig.SetVerticalOffset(0f);
            }
        }

        // 开发调试：--diag 打印建筑视图的可见性与世界包围盒
        if (OS.GetCmdlineUserArgs().Contains("--diag"))
        {
            var t2 = GetTree().CreateTimer(0.5);
            t2.Timeout += DumpBuildingDiagnostics;
        }

        // 开发调试：--demo-hand 选中第一张卡，展示扇形手牌抬起 + 高亮
        if (OS.GetCmdlineUserArgs().Contains("--demo-hand"))
        {
            var t3 = GetTree().CreateTimer(0.4);
            t3.Timeout += () => OnCardClicked(0);
        }

        // 开发调试：--demo-drag 把第一张卡拖到地图上松手，验证拖拽出牌
        if (OS.GetCmdlineUserArgs().Contains("--demo-drag"))
            DemoDragFlow();

        // 开发调试：--demo-reorder 把第一张卡左右拖，验证手牌换位
        if (OS.GetCmdlineUserArgs().Contains("--demo-reorder"))
            DemoReorderFlow();

        // 开发调试：--demo-stack 在同一格叠三个单位，检查逐层抬升
        if (OS.GetCmdlineUserArgs().Contains("--demo-stack"))
        {
            var t4 = GetTree().CreateTimer(0.4);
            t4.Timeout += () =>
            {
                BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
                if (hq == null)
                    return;

                HexCoord? spot = null;
                foreach (HexCoord c in HexCoord.Spiral(hq.Coord, 1))
                {
                    if (c != hq.Coord && _state.CanDeployCard("unit_infantry", PlayerId.Player, c, out _))
                    {
                        spot = c;
                        break;
                    }
                }

                if (spot == null)
                    return;

                _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
                _state.SpawnUnit("tank", PlayerId.Player, spot.Value);
                _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
                _map.SyncEntities();

                _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.0f));
                _rig.FrameMap(3.2f);
                _rig.SetPitch(30f);
                _rig.SetVerticalOffset(0f);
            };
        }

        // 开发调试：--demo-arrow 选中一张需要地块的卡，并把鼠标移到合法格上，展示指示箭头
        if (OS.GetCmdlineUserArgs().Contains("--demo-arrow"))
            DemoArrowFlow();

        // 开发调试：--demo-orbit 右键拖动旋转视角，1.5 秒后按左 Ctrl 复位
        if (OS.GetCmdlineUserArgs().Contains("--demo-orbit"))
            DemoOrbitFlow();

        // 开发调试：--demo-pan 中键向下拖动，检查平移方向
        if (OS.GetCmdlineUserArgs().Contains("--demo-pan"))
            DemoPanFlow();

        // 开发调试：--demo-build 点一个空格子打开建造菜单，检查目标格指示
        if (OS.GetCmdlineUserArgs().Contains("--demo-build"))
            DemoBuildFlow();

        // 开发调试：--demo-hq 摆 3 个不同等级的指挥部，检查每级渲染与尺寸
        if (OS.GetCmdlineUserArgs().Contains("--demo-hq"))
            DemoHqFlow();

        // 开发调试：--demo-hq-close 单个 Lv1 指挥部近距离看是否悬空
        if (OS.GetCmdlineUserArgs().Contains("--demo-hq-close"))
            DemoHqCloseFlow();

        // 开发调试：--demo-barracks 单个 Lv1 兵营，检查主体是否渲染
        if (OS.GetCmdlineUserArgs().Contains("--demo-barracks"))
            DemoBuildingFlow("barracks", 1);

        // 开发调试：--demo-factions 敌我单位/建筑各摆几个，检查阵营描边色
        if (OS.GetCmdlineUserArgs().Contains("--demo-factions"))
            DemoFactionsFlow();

        // 开发调试：--demo-attack 选一个我方单位，看可攻击格子的红色标记
        if (OS.GetCmdlineUserArgs().Contains("--demo-attack"))
            DemoAttackFlow();

        // 开发调试：--demo-extrafire 建一座炮台+旁边放敌人，测额外开炮
        if (OS.GetCmdlineUserArgs().Contains("--demo-extrafire"))
            DemoExtraFireFlow();

        // 开发调试：--demo-hqupgrade 选中指挥部并升级，检查按钮与等级变化
        if (OS.GetCmdlineUserArgs().Contains("--demo-hqupgrade"))
            DemoHqUpgradeFlow();

        // 开发调试：--demo-anims 打印每个兵种模型里自带的动画
        if (OS.GetCmdlineUserArgs().Contains("--demo-anims"))
            DemoAnimsFlow();

        // 开发调试：--demo-unitanim 检查移动朝向与攻击动作
        if (OS.GetCmdlineUserArgs().Contains("--demo-unitanim"))
            DemoUnitAnimFlow();

        // 开发调试：--demo-barracks3 兵营三级并排
        if (OS.GetCmdlineUserArgs().Contains("--demo-barracks3"))
            DemoLevelsFlow("barracks");

        // 开发调试：--demo-turret3 炮台三级并排（检查炮管/炮塔是否穿帮）
        if (OS.GetCmdlineUserArgs().Contains("--demo-turret3"))
        {
            DemoLevelsFlow("gunTurret");
            _map.DumpMeshNames();
        }

        // 开发调试：--demo-aa3 防空炮三级并排
        if (OS.GetCmdlineUserArgs().Contains("--demo-aa3"))
        {
            DemoLevelsFlow("aaGun");
            _map.DumpMeshNames();
        }

        // 开发调试：--demo-aa1 单个 Lv1 防空炮近距离看
        if (OS.GetCmdlineUserArgs().Contains("--demo-aa1"))
            DemoBuildingFlow("aaGun", 1);

        // 开发调试：--demo-tankturret 坦克车身不动、炮塔转向目标
        if (OS.GetCmdlineUserArgs().Contains("--demo-tankturret"))
            DemoTankTurretFlow();

        // 开发调试：--demo-tanktree 打印坦克模型的节点树（找炮塔节点）
        if (OS.GetCmdlineUserArgs().Contains("--demo-tanktree"))
            DemoTankTreeFlow();

        // 开发调试：--demo-levels=<defId> 把某个建筑的三级并排
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--demo-levels="))
            {
                DemoLevelsFlow(arg["--demo-levels=".Length..]);
                if (OS.GetCmdlineUserArgs().Contains("--dump"))
                    _map.DumpMeshNames();
            }
        }

        // 开发调试：--demo-health 放几个残血单位/建筑，检查血条样式
        if (OS.GetCmdlineUserArgs().Contains("--demo-health"))
            DemoHealthFlow();

        // 开发调试：--demo-select-cycle 同格多单位反复点击轮换选中
        if (OS.GetCmdlineUserArgs().Contains("--demo-select-cycle"))
            DemoSelectCycleFlow();

        // 开发调试：--demo-rotor 放一架直升机，检查旋翼是否常驻旋转
        if (OS.GetCmdlineUserArgs().Contains("--demo-rotor"))
            DemoRotorFlow();

        // 开发调试：--demo-rotortree 打印直升机旋翼相关动画轨道
        if (OS.GetCmdlineUserArgs().Contains("--demo-rotortree"))
            DemoRotorTreeFlow();

        // 开发调试：--demo-preview-build 悬停建造项，预览建造后的样子
        if (OS.GetCmdlineUserArgs().Contains("--demo-preview-build"))
            DemoPreviewBuildFlow();

        // 开发调试：--demo-dump-anims=<glb路径> 打印模型里的动画名
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--demo-dump-anims="))
            {
                var scene = GD.Load<PackedScene>(arg["--demo-dump-anims=".Length..]);
                if (scene != null)
                {
                    Node n = scene.Instantiate();
                    foreach (Node c in n.FindChildren("*", "AnimationPlayer", true, false))
                    {
                        if (c is AnimationPlayer ap)
                            GD.Print($"[Anims] {string.Join(",", ap.GetAnimationList())}");
                    }

                    n.QueueFree();
                }
            }
        }

        // 开发调试：--demo-missile-card 导弹井发射导弹（卡牌特效）
        if (OS.GetCmdlineUserArgs().Contains("--demo-missile-card"))
            DemoMissileCardFlow();

        // 开发调试：--demo-pile-hover 悬停牌堆时放大
        if (OS.GetCmdlineUserArgs().Contains("--demo-pile-hover"))
            DemoPileHoverFlow();

        // 开发调试：--demo-global-burn 全局卡（强行军）打出时的烧卡特效
        if (OS.GetCmdlineUserArgs().Contains("--demo-global-burn"))
            DemoGlobalBurnFlow();

        // 开发调试：--demo-death-fx 单位/建筑阵亡粒子
        if (OS.GetCmdlineUserArgs().Contains("--demo-death-fx"))
            DemoDeathFxFlow();

        // 开发调试：--demo-dup-play 打出一张时不应连带另一张同名卡消失
        if (OS.GetCmdlineUserArgs().Contains("--demo-dup-play"))
            DemoDupPlayFlow();

        // 开发调试：--demo-ethereal 建筑发的兵种卡带「虚无」
        if (OS.GetCmdlineUserArgs().Contains("--demo-ethereal"))
            DemoEtherealFlow();

        // 开发调试：--demo-exhaust-burn 消耗卡打出时的烧卡特效
        if (OS.GetCmdlineUserArgs().Contains("--demo-exhaust-burn"))
            DemoExhaustBurnFlow();

        // 开发调试：--demo-pile-flow 消耗/保留/弃牌 的状态流转
        if (OS.GetCmdlineUserArgs().Contains("--demo-pile-flow"))
            DemoPileFlow();

        // 开发调试：--demo-piles 牌堆 UI / 关键字 / 弃牌
        if (OS.GetCmdlineUserArgs().Contains("--demo-piles"))
            DemoPilesFlow();

        // 开发调试：--demo-missile-silo-link 选导弹卡时自动选中对应发射井
        if (OS.GetCmdlineUserArgs().Contains("--demo-missile-silo-link"))
            DemoMissileSiloLinkFlow();

        // 开发调试：--demo-burn-card 发射井被毁 → 导弹卡燃烧消失
        if (OS.GetCmdlineUserArgs().Contains("--demo-burn-card"))
            DemoBurnCardFlow();

        // 开发调试：--demo-hp-damage 血量越低攻击越低 + 面板彩色攻击力
        if (OS.GetCmdlineUserArgs().Contains("--demo-hp-damage"))
            DemoHpDamageFlow();

        // 开发调试：--demo-build-with-units 有驻军的格子也能进建造菜单
        if (OS.GetCmdlineUserArgs().Contains("--demo-build-with-units"))
            DemoBuildWithUnitsFlow();

        // 开发调试：--demo-water-build 验证水面不能建造
        if (OS.GetCmdlineUserArgs().Contains("--demo-water-build"))
            DemoWaterBuildFlow();

        // 开发调试：--demo-water-deploy 检查陆军不能部署到水面
        if (OS.GetCmdlineUserArgs().Contains("--demo-water-deploy"))
            DemoWaterDeployFlow();

        // 开发调试：--demo-missile-nosilo 没有发射井时打导弹卡
        if (OS.GetCmdlineUserArgs().Contains("--demo-missile-nosilo"))
            DemoMissileNoSiloFlow();

        // 开发调试：--demo-enemy-turn 结束我方回合，观察 AI 行动时的特效
        if (OS.GetCmdlineUserArgs().Contains("--demo-enemy-turn"))
            DemoEnemyTurnFlow();

        // 开发调试：--demo-kill 命中瞬间才结算 + 死亡缩放动画
        if (OS.GetCmdlineUserArgs().Contains("--demo-kill"))
            DemoKillFlow();

        // 开发调试：--demo-check-enemy-cards 检查敌方生产建筑是否会发牌给我方
        if (OS.GetCmdlineUserArgs().Contains("--demo-check-enemy-cards"))
            DemoCheckEnemyCardsFlow();

        // 开发调试：--demo-repair-building 建筑维修按钮
        if (OS.GetCmdlineUserArgs().Contains("--demo-repair-building"))
            DemoRepairBuildingFlow();

        // 开发调试：--demo-repair 维修卡治疗一格内的己方单位
        if (OS.GetCmdlineUserArgs().Contains("--demo-repair"))
            DemoRepairFlow();

        // 开发调试：--demo-strike-hq 验证空袭卡不能砸敌方指挥部
        if (OS.GetCmdlineUserArgs().Contains("--demo-strike-hq"))
            DemoStrikeHqFlow();

        // 开发调试：--demo-turret-kill 防御建筑击杀后单位视图立即消失
        if (OS.GetCmdlineUserArgs().Contains("--demo-turret-kill"))
            DemoTurretKillFlow();

        // 开发调试：--demo-missile-launcher 导弹车抛物线攻击
        if (OS.GetCmdlineUserArgs().Contains("--demo-missile-launcher"))
            DemoMissileLauncherFlow();

        // 开发调试：--demo-fighter-attack 战斗机对地导弹
        if (OS.GetCmdlineUserArgs().Contains("--demo-fighter-attack"))
            DemoFighterAttackFlow();

        // 开发调试：--demo-vfx=<explosion|missile|heal|bullet> 单独触发一个特效
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--demo-vfx="))
                DemoVfxFlow(arg["--demo-vfx=".Length..]);
        }

        // 开发调试：--demo-move-anim 验证落地后移动动画会走完一轮再切 Idle
        if (OS.GetCmdlineUserArgs().Contains("--demo-move-anim"))
            DemoMoveAnimFlow();

        // 开发调试：--demo-pop 部署缩放动画
        if (OS.GetCmdlineUserArgs().Contains("--demo-pop"))
            DemoPopFlow();

        // 开发调试：--demo-bar-diag 选中建筑后点空地，检查血条位置与隐藏
        if (OS.GetCmdlineUserArgs().Contains("--demo-bar-diag"))
            DemoBarDiagFlow();

        // 开发调试：--demo-move-building 验证陆军能进己方建筑格、不能进敌方建筑格
        if (OS.GetCmdlineUserArgs().Contains("--demo-move-building"))
            DemoMoveBuildingFlow();

        // 开发调试：--demo-mixed-cycle 同格「建筑+单位」混合轮询选中
        if (OS.GetCmdlineUserArgs().Contains("--demo-mixed-cycle"))
            DemoMixedCycleFlow();

        // 开发调试：--demo-fly 出牌飞行动画（截图用）
        if (OS.GetCmdlineUserArgs().Contains("--demo-fly"))
            DemoFlyFlow();

        // 开发调试：--demo-upgrade-hover 悬停升级→点升级→移开，检查外观不会退回
        if (OS.GetCmdlineUserArgs().Contains("--demo-upgrade-hover"))
            DemoUpgradeHoverFlow();

        // 开发调试：--demo-preview-upgrade 悬停升级按钮，预览下一级
        if (OS.GetCmdlineUserArgs().Contains("--demo-preview-upgrade"))
            DemoPreviewUpgradeFlow();

        // 开发调试：--demo-buildtree=<defId> 打印建筑模型的节点树
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--demo-buildtree="))
                DemoBuildTreeFlow(arg["--demo-buildtree=".Length..]);
        }

        // 开发调试：--demo-fit 把所有建筑摆成一排，俯视检查大小与居中
        if (OS.GetCmdlineUserArgs().Contains("--demo-fit"))
            DemoFitFlow();

        // 开发调试：--demo-click 用合成鼠标事件真的点卡牌+格子，验证交互链路
        if (OS.GetCmdlineUserArgs().Contains("--demo-click"))
            DemoClickFlow();
    }

    private void DumpBuildingDiagnostics()
    {
        foreach (Node3D view in _map.GetChildren().OfType<Node3D>()
                     .SelectMany(n => n.GetChildren().OfType<Node3D>()))
        {
            if (!view.Name.ToString().StartsWith("B_"))
                continue;

            foreach (MeshInstance3D mi in view.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
            {
                Aabb g = mi.GlobalTransform * mi.GetAabb();
                string chain = "";
                Node n = mi;
                while (n != null && n != _map)
                {
                    chain += $"{n.Name}({(n is CanvasItem ci ? ci.Visible : true)}>{(n is Node3D n3v ? n3v.Visible : true)})/";
                    n = n.GetParent();
                }

                GD.Print($"[Diag] {view.Name} mesh={mi.Name} vis={mi.Visible} inTree={mi.IsVisibleInTree()} " +
                         $"worldPos={g.Position} size={g.Size} chain={chain}");
            }
        }
    }

    private async void DemoClickFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.7), SceneTreeTimer.SignalName.Timeout);

        int handBefore = _state.Player.Hand.Count;
        int unitsBefore = _state.Units.Count;

        if (!_hud.TryGetCardRect(0, out Rect2 cardRect))
        {
            GD.Print("[DemoClick] 拿不到卡牌矩形");
            GetTree().Quit(1);
            return;
        }

        Click(cardRect.Position + cardRect.Size * 0.5f);
        await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);

        CardDef card = GameDatabase.Card(_state.Player.Hand.Count > 0 ? _state.Player.Hand[0] : "");
        HexCoord? target = null;
        if (card != null)
        {
            foreach (Cell c in _state.Cells.Values)
            {
                if (_state.CanDeployCard(card.Id, PlayerId.Player, c.Coord, out _))
                {
                    target = c.Coord;
                    break;
                }
            }
        }

        if (target == null)
        {
            GD.Print("[DemoClick] 找不到可部署的格子");
            GetTree().Quit(1);
            return;
        }

        Vector3 world = HexLayout.ToWorld(target.Value, HexMapView.TileTop);
        Vector2 screen = _rig.Camera.UnprojectPosition(world);
        Click(screen);
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        int handAfter = _state.Player.Hand.Count;
        int unitsAfter = _state.Units.Count;
        GD.Print($"[DemoClick] 手牌 {handBefore}->{handAfter}，单位 {unitsBefore}->{unitsAfter}，" +
                 $"卡牌点击={(handAfter < handBefore ? "OK" : "FAIL")} 部署={(unitsAfter > unitsBefore ? "OK" : "FAIL")}");

        // 再验证「选中单位 → 点目标格移动」
        UnitInst unit = _state.Units.FirstOrDefault(u => u.Owner == PlayerId.Player);
        HexCoord from = unit?.Coord ?? default;
        if (unit == null)
        {
            GetTree().Quit(1);
            return;
        }

        Click(_rig.Camera.UnprojectPosition(HexLayout.ToWorld(from, HexMapView.TileTop)));
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        HashSet<HexCoord> moves = _state.MoveRange(unit);
        HexCoord? dest = moves.Cast<HexCoord?>().OrderByDescending(c => HexCoord.Distance(c.Value, from)).FirstOrDefault();
        if (dest == null)
        {
            GD.Print("[DemoClick] 没有可移动的格子");
            GetTree().Quit(1);
            return;
        }

        Click(_rig.Camera.UnprojectPosition(HexLayout.ToWorld(dest.Value, HexMapView.TileTop)));
        await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);

        bool movedOk = unit.Coord == dest.Value;
        GD.Print($"[DemoClick] 移动 {from} -> {unit.Coord}（目标 {dest.Value}）={(movedOk ? "OK" : "FAIL")}");

        bool ok = handAfter < handBefore && unitsAfter > unitsBefore && movedOk;
        GD.Print($"[DemoClick] 总结={(ok ? "PASS" : "FAIL")}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private static void SendButton(Vector2 pos, bool pressed, MouseButton button = MouseButton.Left)
    {
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = button,
            Pressed = pressed,
            Position = pos,
            GlobalPosition = pos,
        });
    }

    private static void SendMotion(Vector2 pos, Vector2 prev)
    {
        Input.ParseInputEvent(new InputEventMouseMotion
        {
            Position = pos,
            GlobalPosition = pos,
            Relative = pos - prev,
        });
    }

    /// <summary>选中一张需要地块的卡，把鼠标移到合法格上，用于截图检查指示箭头。</summary>
    private async void DemoArrowFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        OnCardClicked(0);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        CardDef card = GameDatabase.Card(_state.Player.Hand[0]);
        HexCoord? target = null;
        foreach (Cell c in _state.Cells.Values)
        {
            if (card != null && _state.CanDeployCard(card.Id, PlayerId.Player, c.Coord, out _))
            {
                target = c.Coord;
                break;
            }
        }

        if (target == null)
            return;

        Vector2 p = _rig.Camera.UnprojectPosition(HexLayout.ToWorld(target.Value, HexMapView.TileTop));
        SendMotion(p, p);
        UpdateHover(p);
    }

    /// <summary>把第一张卡真的拖到地图格子上松手，验证拖拽出牌链路。</summary>
    private async void DemoDragFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.7), SceneTreeTimer.SignalName.Timeout);

        int handBefore = _state.Player.Hand.Count;
        int unitsBefore = _state.Units.Count;

        if (!_hud.TryGetCardRect(0, out Rect2 rect))
        {
            GD.Print("[DemoDrag] 拿不到卡牌矩形");
            GetTree().Quit(1);
            return;
        }

        CardDef card = GameDatabase.Card(_state.Player.Hand[0]);
        HexCoord? target = null;
        foreach (Cell c in _state.Cells.Values)
        {
            if (card != null && _state.CanDeployCard(card.Id, PlayerId.Player, c.Coord, out _))
            {
                target = c.Coord;
                break;
            }
        }

        if (target == null)
        {
            GD.Print("[DemoDrag] 找不到可部署的格子");
            GetTree().Quit(1);
            return;
        }

        Vector2 start = rect.Position + rect.Size * 0.5f;
        Vector2 end = _rig.Camera.UnprojectPosition(HexLayout.ToWorld(target.Value, HexMapView.TileTop));

        SendButton(start, true);
        await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);

        Vector2 prev = start;
        for (int i = 1; i <= 8; i++)
        {
            Vector2 p = start.Lerp(end, i / 8f);
            SendMotion(p, prev);
            prev = p;
            await ToSignal(GetTree().CreateTimer(0.03), SceneTreeTimer.SignalName.Timeout);
        }

        SendButton(end, false);
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        int handAfter = _state.Player.Hand.Count;
        int unitsAfter = _state.Units.Count;
        bool ok = handAfter < handBefore && unitsAfter > unitsBefore;
        GD.Print($"[DemoDrag] 手牌 {handBefore}->{handAfter}，单位 {unitsBefore}->{unitsAfter} " +
                 $"拖拽出牌={(ok ? "OK" : "FAIL")}");
        GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>中键向下拖动，验证平移方向（地图应该跟着光标往下走）。</summary>
    private async void DemoPanFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Pan] before {_rig.DebugState()}");

        Vector2 start = new(640f, 300f);
        SendButton(start, true, MouseButton.Middle);
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);

        Vector2 prev = start;
        for (int i = 1; i <= 8; i++)
        {
            Vector2 p = start + new Vector2(0f, i * 25f); // 向下拖 200px
            SendMotion(p, prev);
            prev = p;
            await ToSignal(GetTree().CreateTimer(0.02), SceneTreeTimer.SignalName.Timeout);
        }

        SendButton(prev, false, MouseButton.Middle);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Pan] after  {_rig.DebugState()}");
    }

    /// <summary>放一个敌方单位在附近，选我方单位，检查可攻击格子的红色六边形标记。</summary>
    private async void DemoAttackFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        if (hq == null)
            return;

        HexCoord? spot = null;
        foreach (HexCoord c in HexCoord.Spiral(hq.Coord, 1))
        {
            if (_state.CanDeployCard("unit_infantry", PlayerId.Player, c, out _))
            {
                spot = c;
                break;
            }
        }

        if (spot == null)
            return;

        UnitInst mine = _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
        UnitInst foe = _state.SpawnUnit("infantry", PlayerId.Enemy, spot.Value.Neighbor(0));

        _map.SyncEntities();
        SelectUnit(mine);
        RefreshAll();

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.0f));
        _rig.FrameMap(3.0f);
        _rig.SetPitch(38f);
        _rig.SetVerticalOffset(0f);

        GD.Print($"[Attack] unit={mine.Coord} foe={foe.Coord} range={mine.Def.Range} " +
                 $"cells={_state.AttackRangeCells(mine).Count()}");
    }

    /// <summary>移动朝向 + 攻击动作的回归检查。</summary>
    private async void DemoUnitAnimFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        if (hq == null)
            return;

        HexCoord? spot = null;
        foreach (HexCoord c in HexCoord.Spiral(hq.Coord, 1))
        {
            if (_state.CanDeployCard("unit_infantry", PlayerId.Player, c, out _))
            {
                spot = c;
                break;
            }
        }

        if (spot == null)
            return;

        UnitInst u = _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
        HexCoord goal = spot.Value.Neighbor(3);          // 往某个方向走一格
        HexCoord foeCoord = goal.Neighbor(3);

        _map.SyncEntities();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[UAnim] 初始 yaw={_map.DebugUnitYaw(u.Id):F1}（默认按阵营 ±90）");

        _state.TryMove(u, goal, out _);
        _map.SyncEntities();
        _rig.FocusOn(HexLayout.ToWorld(goal, 1.0f));
        _rig.FrameMap(4.0f);
        _rig.SetPitch(32f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[UAnim] 移动中状态={_map.DebugUnitAnimState(u.Id)}（应为 Move）");

        await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);
        float movedYaw = _map.DebugUnitYaw(u.Id);
        Vector3 from = HexLayout.ToWorld(spot.Value, 0);
        Vector3 to = HexLayout.ToWorld(goal, 0);
        float expect = Mathf.RadToDeg(Mathf.Atan2(to.X - from.X, to.Z - from.Z));
        GD.Print($"[UAnim] 移动后 yaw={movedYaw:F1} 期望={expect:F1} " +
                 $"状态={_map.DebugUnitAnimState(u.Id)}（应为 Idle）");

        UnitInst foe = _state.SpawnUnit("infantry", PlayerId.Enemy, foeCoord);
        _map.FaceUnitToward(u, HexLayout.ToWorld(foeCoord, HexMapView.TileTop));
        _map.PlayUnitAttack(u.Id);

        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[UAnim] 攻击中={_map.DebugUnitAttacking(u.Id)} yaw={_map.DebugUnitYaw(u.Id):F1}");

        await ToSignal(GetTree().CreateTimer(1.4), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[UAnim] 1.55s 后攻击中={_map.DebugUnitAttacking(u.Id)}（应回到 false）");
    }

    /// <summary>打印每个兵种 glb 里的动画片段。</summary>
    private void DemoAnimsFlow()
    {
        foreach (UnitDef def in GameDatabase.Units.Values)
        {
            if (string.IsNullOrEmpty(def.Model) || !ResourceLoader.Exists(def.Model))
            {
                GD.Print($"[Anim] {def.Id}: 没有模型");
                continue;
            }

            var scene = GD.Load<PackedScene>(def.Model);
            Node inst = scene.Instantiate();
            var names = new List<string>();

            foreach (Node n in inst.FindChildren("*", "AnimationPlayer", true, false))
            {
                if (n is not AnimationPlayer ap)
                    continue;
                var list = ap.GetAnimationList();
                foreach (string a in list)
                {
                    Godot.Animation anim = ap.GetAnimation(a);
                    names.Add($"{a}({anim?.Length ?? 0f:F2}s)");
                }
            }

            GD.Print($"[Anim] {def.Id}: {(names.Count == 0 ? "无动画" : string.Join(", ", names))}");
            inst.QueueFree();
        }
    }

    /// <summary>选中指挥部并连续升级，验证指挥部可以升级。</summary>
    private async void DemoHqUpgradeFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        if (hq == null)
            return;

        _state.Player.Gold = 5000;
        SelectBuilding(hq);
        RefreshAll();

        _rig.FocusOn(HexLayout.ToWorld(hq.Coord, 1.0f));
        _rig.FrameMap(3.2f);
        _rig.SetPitch(38f);
        _rig.SetVerticalOffset(0f);

        GD.Print($"[HqUpgrade] start level={hq.Level} gold={_state.Player.Gold} " +
                 $"cost={hq.Def.UpgradeCostAt(hq.Level)}");

        if (!OS.GetCmdlineUserArgs().Contains("--apply"))
            return;

        OnUpgradeClicked();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[HqUpgrade] after1 level={hq.Level} gold={_state.Player.Gold} hp={hq.Hp}/{hq.MaxHp}");

        OnUpgradeClicked();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[HqUpgrade] after2 level={hq.Level} gold={_state.Player.Gold}");

        OnUpgradeClicked();
        GD.Print($"[HqUpgrade] after3 level={hq.Level} gold={_state.Player.Gold}");

        _map.SyncEntities();
        RefreshAll();
        ShowBuildingInfo(hq);
    }

    /// <summary>炮台额外开炮：建炮台、放敌人、选建筑，然后直接调用一次。</summary>
    private async void DemoExtraFireFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 5);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null &&
                _state.CanBuildAt(c, PlayerId.Player))
                spot = c;
        }

        if (spot == null)
        {
            GD.Print("[ExtraFire] 找不到可建造的格子");
            return;
        }

        BuildingInst turret = OS.GetCmdlineUserArgs().Contains("--aa")
            ? _state.SpawnBuilding("aaGun", PlayerId.Player, spot.Value)
            : _state.SpawnBuilding("gunTurret", PlayerId.Player, spot.Value);

        // 防空炮只打空中目标，用直升机当靶子；炮台用步兵
        UnitInst foe = OS.GetCmdlineUserArgs().Contains("--aa")
            ? _state.SpawnUnit("helicopter", PlayerId.Enemy, spot.Value.Neighbor(2))
            : _state.SpawnUnit("infantry", PlayerId.Enemy, spot.Value.Neighbor(2));

        _map.SyncEntities();
        _map.RefreshZones();
        SelectBuilding(turret);
        RefreshAll();

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.0f));
        _rig.FrameMap(2.6f);
        _rig.SetPitch(36f);
        _rig.SetVerticalOffset(0f);

        GD.Print($"[ExtraFire] before gold={_state.Player.Gold} turret=({spot.Value}) " +
                 $"foe=({foe.Coord}) foeHp={foe.Hp} cost={turret.Def.ExtraFireCost}");

        if (!OS.GetCmdlineUserArgs().Contains("--apply"))
            return;

        OnExtraFireClicked();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[ExtraFire] after gold={_state.Player.Gold} foeHp={foe.Hp} " +
                 $"alive={foe.Alive} extraFired={turret.ExtraFired}");

        // 再点一次应该被拒绝
        OnExtraFireClicked();
        GD.Print($"[ExtraFire] secondTry gold={_state.Player.Gold} extraFired={turret.ExtraFired}");
    }

    /// <summary>敌我双方单位与建筑并排，检查阵营描边色。</summary>
    private void DemoFactionsFlow()
    {
        var cells = new List<HexCoord>();
        for (int r = 3; r <= 5 && cells.Count < 4; r++)
        {
            for (int q = -2; q < 6 && cells.Count < 4; q++)
            {
                var c = new HexCoord(q, r);
                Cell cell = _state.Cell(c);
                if (cell != null && cell.Building == null && cell.Units.Count == 0)
                    cells.Add(c);
            }
        }

        if (cells.Count < 4)
            return;

        _state.SpawnUnit("infantry", PlayerId.Player, cells[0]);
        _state.SpawnUnit("tank", PlayerId.Player, cells[1]);
        _state.SpawnUnit("infantry", PlayerId.Enemy, cells[2]);
        _state.SpawnUnit("tank", PlayerId.Enemy, cells[3]);

        _state.SpawnBuilding("city", PlayerId.Player, cells[1]);
        _state.SpawnBuilding("city", PlayerId.Enemy, cells[3]);

        _map.SyncEntities();
        _map.RefreshZones();

        float ax = 0, az = 0;
        foreach (HexCoord c in cells)
        {
            Vector3 w = HexLayout.ToWorld(c);
            ax += w.X;
            az += w.Z;
        }

        _rig.FocusOn(new Vector3(ax / cells.Count, 1.0f, az / cells.Count));
        _rig.FrameMap(4.5f);
        _rig.SetPitch(38f);
        _rig.SetVerticalOffset(0f);
    }

    /// <summary>坦克炮塔独立转向：车身朝向不变，只转炮塔。</summary>
    private async void DemoTankTurretFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        if (hq == null)
            return;

        HexCoord? spot = null;
        foreach (HexCoord c in HexCoord.Spiral(hq.Coord, 2))
        {
            if (_state.Cell(c) != null && _state.Cell(c).Building == null &&
                _state.Cell(c).Units.Count == 0)
            {
                spot = c;
                break;
            }
        }

        if (spot == null)
            return;

        UnitInst tank = _state.SpawnUnit("tank", PlayerId.Player, spot.Value);
        HexCoord foe = spot.Value.Neighbor(2);       // 目标方向（和车头朝向不同）
        _state.SpawnUnit("infantry", PlayerId.Enemy, foe);

        _map.SyncEntities();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        Vector3 targetWorld = HexLayout.ToWorld(foe, HexMapView.TileTop);
        bool turretOnly = _map.FaceUnitTurretToward(tank, targetWorld);

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.2f));
        _rig.FrameMap(2.6f);
        _rig.SetPitch(28f);
        _rig.SetVerticalOffset(0f);

        GD.Print($"[Turret] 车身yaw={_map.DebugUnitYaw(tank.Id):F1} " +
                 $"炮塔相对yaw={_map.DebugTurretYaw(tank.Id):F1} turretOnly={turretOnly}");

        for (int i = 0; i < 4; i++)
        {
            await ToSignal(GetTree().CreateTimer(0.12), SceneTreeTimer.SignalName.Timeout);
            GD.Print($"[Turret] +{(i + 1) * 0.12:F2}s 炮塔yaw={_map.DebugTurretYaw(tank.Id):F1}");
        }

        if (OS.GetCmdlineUserArgs().Contains("--apply"))
        {
            _map.PlayUnitAttack(tank.Id);
            await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
            GD.Print($"[Turret] 开火后 车身yaw={_map.DebugUnitYaw(tank.Id):F1} " +
                     $"炮塔yaw={_map.DebugTurretYaw(tank.Id):F1}");
        }
    }

    /// <summary>造几个残血单位/建筑，检查血条。</summary>
    private async void DemoHealthFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        var spots = new List<HexCoord>();
        for (int r = 4; r <= 5 && spots.Count < 3; r++)
        {
            for (int q = -2; q < 6 && spots.Count < 3; q++)
            {
                var c = new HexCoord(q, r);
                Cell cell = _state.Cell(c);
                if (cell != null && cell.Building == null && cell.Units.Count == 0)
                    spots.Add(c);
            }
        }

        if (spots.Count < 3)
            return;

        UnitInst mine = _state.SpawnUnit("infantry", PlayerId.Player, spots[0]);
        UnitInst foe = _state.SpawnUnit("infantry", PlayerId.Enemy, spots[1]);
        BuildingInst city = _state.SpawnBuilding("city", PlayerId.Player, spots[2]);

        mine.Hp = Mathf.RoundToInt(mine.MaxHp * 0.62f);
        foe.Hp = Mathf.RoundToInt(foe.MaxHp * 0.22f);
        city.Hp = Mathf.RoundToInt(city.MaxHp * 0.45f);

        _map.SyncEntities();
        _map.RefreshZones();
        SelectUnit(mine);

        float ax = 0, az = 0;
        foreach (HexCoord c in spots)
        {
            Vector3 w = HexLayout.ToWorld(c);
            ax += w.X;
            az += w.Z;
        }

        _rig.FocusOn(new Vector3(ax / spots.Count, 1.2f, az / spots.Count));
        _rig.FrameMap(5.0f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        GD.Print($"[Health] 我方={mine.Hp}/{mine.MaxHp} 敌方={foe.Hp}/{foe.MaxHp} " +
                 $"城市={city.Hp}/{city.MaxHp}");

        if (!OS.GetCmdlineUserArgs().Contains("--apply"))
            return;

        // 等血条稳定，然后打掉一大截，观察渐变
        await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);
        foe.Hp = Mathf.RoundToInt(foe.MaxHp * 0.05f);
        city.Hp = Mathf.RoundToInt(city.MaxHp * 0.12f);

        await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Health] 0.10s 敌军 {_hud.DebugHealth(false, foe.Id)} | " +
                 $"城市 {_hud.DebugHealth(true, city.Id)}");

        await ToSignal(GetTree().CreateTimer(0.9), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Health] 1.00s 敌军 {_hud.DebugHealth(false, foe.Id)} | " +
                 $"城市 {_hud.DebugHealth(true, city.Id)}");
    }

    /// <summary>同格叠三个单位，反复点击同一格，检查选中轮换与黄色描边。</summary>
    private async void DemoSelectCycleFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 5);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null &&
                _state.Cell(c).Units.Count == 0)
                spot = c;
        }

        if (spot == null)
            return;

        UnitInst a = _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
        UnitInst b = _state.SpawnUnit("tank", PlayerId.Player, spot.Value);
        UnitInst c2 = _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);

        _map.SyncEntities();
        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.0f));
        _rig.FrameMap(2.4f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        Vector2 p = _rig.Camera.UnprojectPosition(HexLayout.ToWorld(spot.Value, HexMapView.TileTop));
        GD.Print($"[Cycle] 三个单位 a={a.Id} b={b.Id} c={c2.Id} 位置={spot.Value}");

        for (int i = 1; i <= 3; i++)
        {
            SendMotion(p, p);
            UpdateHover(p);
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
            OnLeftClick(p);
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);

            UnitInst sel = _selUnit;
            GD.Print($"[Cycle] 第{i}次点击 选中={sel?.Id} " +
                     $"描边={_map.DebugUnitOutline(sel?.Id ?? -1)}");
        }
    }

    /// <summary>放一架直升机，检查旋翼是否一直在转。</summary>
    private async void DemoRotorFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 5);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null &&
                _state.Cell(c).Units.Count == 0)
                spot = c;
        }

        if (spot == null)
            return;

        UnitInst heli = _state.SpawnUnit("helicopter", PlayerId.Player, spot.Value);
        _map.SyncEntities();

        if (OS.GetCmdlineUserArgs().Contains("--range-log"))
            GD.Print($"[Range] 武装直升机 射程={GameDatabase.Unit("helicopter").Range} 移动={GameDatabase.Unit("helicopter").Move}");

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.6f));
        _rig.FrameMap(2.4f);
        _rig.SetPitch(24f);
        _rig.SetVerticalOffset(0f);

        for (int i = 0; i < 4; i++)
        {
            await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);
            GD.Print($"[Rotor] +{(i + 1) * 0.35:F2}s {_map.DebugUnitRotor(heli.Id)} " +
                     $"anim={_map.DebugUnitAnimState(heli.Id)}");
        }
    }

    /// <summary>打开建造菜单并模拟悬停「兵营」，检查建造预览。</summary>
    private async void DemoPreviewBuildFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 5);
            if (_state.CanBuildAt(c, PlayerId.Player))
                spot = c;
        }

        if (spot == null)
            return;

        _buildTarget = spot.Value;
        _map.SetBuildMarker(spot.Value);

        var defs = GameDatabase.Buildings.Values
            .Where(d => d.Id != "hq")
            .OrderBy(d => d.UpgradeCostAt(0))
            .ToList();
        _hud.ShowBuildMenu(defs, _state.Player.Gold);
        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);

        string hover = "barracks";
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--hover="))
                hover = arg["--hover=".Length..];
        }

        _hud.DebugHoverBuild(hover);

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.0f));
        _rig.FrameMap(3.6f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        GD.Print($"[Preview] 建造预览 {hover} @ {spot.Value}");
    }

    /// <summary>导弹袭击卡：从发射井升空 → 高空掠过 → 砸到目标格。</summary>
    private async void DemoMissileCardFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? siloCell = null;
        HexCoord? target = null;
        for (int q = -2; q < 8 && siloCell == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.CanBuildAt(c, PlayerId.Player))
                siloCell = c;
        }

        if (siloCell == null)
            return;

        // 目标：离发射井 3 格左右的一格
        target = siloCell.Value.Neighbor(0).Neighbor(0).Neighbor(0);

        _state.SpawnBuilding("missileSilo", PlayerId.Player, siloCell.Value);
        _state.SpawnUnit("infantry", PlayerId.Enemy, target.Value);
        _map.SyncEntities();
        _map.RefreshZones();

        _rig.FocusOn(HexLayout.ToWorld(target.Value, 3.2f));
        _rig.FrameMap(8.5f);
        _rig.SetPitch(26f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        _state.Player.Gold = 2000;
        _state.Player.Hand.Add("magic_missile");
        var card = GameDatabase.Card("magic_missile");

        UnitInst foe = _state.Units.First(u => u.Owner == PlayerId.Enemy && u.Alive);
        int hpBefore = foe.Hp;

        // 走真实路径：先播特效，命中才结算
        TryPlayGlobal(card, target.Value);
        GD.Print($"[MissileCard] 打出瞬间 敌人HP={foe.Hp}（应等于 {hpBefore}）待结算={_pendingCard != null}");

        int siloId = _state.Buildings.First(b => b.DefId == "missileSilo").Id;
        GD.Print($"[MissileCard] 发射井动画 状态={_map.DebugBuildingAnim(siloId)}");

        await ToSignal(GetTree().CreateTimer(1.4), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[MissileCard] 命中后 敌人存活={foe.Alive} HP={foe.Hp}");
    }

    /// <summary>鼠标悬停牌堆按钮时应轻微放大。</summary>
    private async void DemoPileHoverFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);

        Vector2 pile = _hud.PileCenter(PileView.PileKind.Deck);
        Vector2 screen = pile;      // PileCenter 已经是屏幕坐标
        GD.Print($"[PileHover] 牌堆中心={screen} 悬停前缩放={_hud.DebugPileScale():F2}");

        Input.WarpMouse(screen);
        SendMotion(screen, screen);

        await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[PileHover] 悬停后缩放={_hud.DebugPileScale():F2}（应约 1.08）");
    }

    /// <summary>「强行军」是消耗卡：打出时要有烧卡特效。</summary>
    private async void DemoGlobalBurnFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        _state.Player.Gold = 2000;
        _state.Player.Hand.Clear();
        _state.Player.Hand.Add("magic_boost");
        RefreshAll();

        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[GlobalBurn] 打出前 手牌={string.Join(",", _state.Player.Hand)} 消耗={_state.Player.Exhaust.Count}");
        OnCardClicked(0);   // 选中
        OnCardClicked(0);   // 再点一次 → 打出

        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[GlobalBurn] 打出后 手牌={string.Join(",", _state.Player.Hand)} " +
                 $"消耗={string.Join(",", _state.Player.Exhaust)}");
    }

    /// <summary>单位与建筑的阵亡粒子演示。</summary>
    private async void DemoDeathFxFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        var spots = new List<HexCoord>();
        foreach (Cell c in _state.Cells.Values)
        {
            if (spots.Count >= 3)
                break;
            if (c.Building == null && c.Terrain != TerrainType.Water)
                spots.Add(c.Coord);
        }

        if (spots.Count < 3)
            return;

        UnitInst ally = _state.SpawnUnit("tank", PlayerId.Player, spots[0]);
        UnitInst foe = _state.SpawnUnit("infantry", PlayerId.Enemy, spots[1]);
        BuildingInst city = _state.SpawnBuilding("city", PlayerId.Enemy, spots[2]);

        _map.SyncEntities();
        _map.RefreshZones();

        _rig.FocusOn(HexLayout.ToWorld(spots[1], 1.2f));
        _rig.FrameMap(3.6f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        // 直接让它们“阵亡”：从状态里移除，SyncEntities 会播阵亡粒子
        _state.Units.Remove(ally);
        _state.Units.Remove(foe);
        _state.Buildings.Remove(city);
        _map.SyncEntities();

        GD.Print("[DeathFx] 已移除 1 坦克 / 1 步兵 / 1 城市，观察粒子");
    }

    /// <summary>手里有三张同名卡时，打出一张只应少一张。</summary>
    private async void DemoDupPlayFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        foreach (Cell c in _state.Cells.Values)
        {
            if (_state.CanDeployCard("unit_infantry", PlayerId.Player, c.Coord, out _))
            {
                spot = c.Coord;
                break;
            }
        }

        if (spot == null)
            return;

        _state.Player.Hand.Clear();
        _state.Player.Hand.Add("unit_infantry");
        _state.Player.Hand.Add("unit_infantry");
        _state.Player.Hand.Add("unit_infantry");
        _state.Player.Hand.Add("unit_tank");
        _state.Player.Gold = 2000;
        RefreshAll();

        GD.Print($"[DupPlay] 出牌前 手牌={string.Join(",", _state.Player.Hand)} " +
                 $"可见卡={_hud.DebugVisibleCardCount()}");

        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);

        OnCardClicked(1);                 // 选中间那张步兵
        TryPlayCardAt(1, spot.Value);

        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[DupPlay] 出牌后 手牌={string.Join(",", _state.Player.Hand)} " +
                 $"可见卡={_hud.DebugVisibleCardCount()} 弃牌={string.Join(",", _state.Player.Discard)}");
    }

    /// <summary>建筑发出的兵种卡应带「虚无」：回合结束进消耗堆。</summary>
    private async void DemoEtherealFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        foreach (Cell c in _state.Cells.Values)
        {
            if (_state.CanBuildAt(c.Coord, PlayerId.Player))
            {
                spot = c.Coord;
                break;
            }
        }

        if (spot == null)
            return;

        _state.SpawnBuilding("barracks", PlayerId.Player, spot.Value);
        _state.SpawnBuilding("helipad", PlayerId.Player, spot.Value.Neighbor(0));

        _state.Player.Hand.Clear();
        _state.Player.Ethereal.Clear();
        _state.Player.Discard.Clear();
        _state.Player.Exhaust.Clear();

        _state.BeginTurn(PlayerId.Player);
        CallDeferred(nameof(ShowOpeningIncome));
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();

        var flags = new List<string>();
        for (int i = 0; i < _state.Player.Hand.Count; i++)
            flags.Add($"{_state.Player.Hand[i]}{(_state.IsEtherealAt(i) ? "(虚无)" : "")}");

        GD.Print($"[Ethereal] 手牌={string.Join(",", flags)}");
        GD.Print($"[Ethereal] 标记列表={string.Join(",", _state.Player.Ethereal)}");

        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        var rects = new List<(string Id, Rect2 Rect)>();
        for (int i = 0; i < _state.Player.Hand.Count; i++)
        {
            if (_hud.TryGetHandCardRect(i, out Rect2 r))
                rects.Add((_state.Player.Hand[i], r));
        }

        _state.DiscardHand(PlayerId.Player);
        RefreshAll();
        AnimateEndOfTurnDiscards(rects);

        GD.Print($"[Ethereal] 回合结束 手牌={string.Join(",", _state.Player.Hand)} " +
                 $"弃牌={string.Join(",", _state.Player.Discard)} " +
                 $"消耗={string.Join(",", _state.Player.Exhaust)} " +
                 $"虚无耗掉={string.Join(",", _state.Player.EtherealExhausted)}");

        await ToSignal(GetTree().CreateTimer(1.3), SceneTreeTimer.SignalName.Timeout);

        // 打开消耗堆查看器：虚无卡照样要显示关键字
        OnPileClicked((int)PileView.PileKind.Exhaust);
    }

    /// <summary>打出一张「消耗」卡：应看到卡被烧掉，然后计入消耗堆。</summary>
    private async void DemoExhaustBurnFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        foreach (Cell c in _state.Cells.Values)
        {
            if (c.Building == null && c.Terrain != TerrainType.Water)
            {
                spot = c.Coord;
                break;
            }
        }

        if (spot == null)
            return;

        _state.SpawnUnit("infantry", PlayerId.Enemy, spot.Value);
        _state.Player.Gold = 2000;

        _state.Player.Hand.Clear();
        _state.Player.Hand.Add("unit_infantry");
        _state.Player.Hand.Add("magic_airstrike");     // 消耗卡
        _state.Player.Hand.Add("magic_repair");       // 保留卡（对比）
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.4f));
        _rig.FrameMap(5.0f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        int idx = _state.Player.Hand.IndexOf("magic_airstrike");
        GD.Print($"[ExhaustBurn] 打空袭 index={idx} 手上={string.Join(",", _state.Player.Hand)}");
        ApplyCardAt(idx, spot.Value);

        await ToSignal(GetTree().CreateTimer(2.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[ExhaustBurn] 之后 手牌={string.Join(",", _state.Player.Hand)} " +
                 $"消耗={string.Join(",", _state.Player.Exhaust)}");
    }

    /// <summary>验证「消耗 / 保留 / 回合结束弃牌」的状态流转。</summary>
    private void DemoPileFlow()
    {
        _state.Player.Hand.Clear();
        _state.Player.Discard.Clear();
        _state.Player.Exhaust.Clear();

        _state.Player.Gold = 2000;
        _state.Player.Hand.Add("magic_airstrike");   // 消耗
        _state.Player.Hand.Add("magic_repair");      // 保留
        _state.Player.Hand.Add("unit_infantry");     // 普通

        Cell foe = _state.Cells.Values.FirstOrDefault(c =>
            c.Units.Any(u => u.Alive && u.Owner == PlayerId.Enemy));

        if (foe == null)
        {
            HexCoord? spot = _state.Cells.Values
                .Where(c => c.Building == null && c.Terrain != TerrainType.Water)
                .Select(c => (HexCoord?)c.Coord)
                .FirstOrDefault();
            if (spot != null)
            {
                _state.SpawnUnit("infantry", PlayerId.Enemy, spot.Value);
                foe = _state.Cell(spot.Value);
            }
        }

        GD.Print($"[PileFlow] 初始 手牌={string.Join(",", _state.Player.Hand)}");

        if (foe != null)
        {
            _state.PlayGlobalCard(_state.Player, "magic_airstrike", foe.Coord, out string err);
            GD.Print($"[PileFlow] 打空袭 err={err}");
        }

        GD.Print($"[PileFlow] 打完后 手牌={string.Join(",", _state.Player.Hand)} " +
                 $"弃牌={string.Join(",", _state.Player.Discard)} " +
                 $"消耗={string.Join(",", _state.Player.Exhaust)}");

        _state.DiscardHand(PlayerId.Player);
        GD.Print($"[PileFlow] 回合结束弃牌 手牌={string.Join(",", _state.Player.Hand)} " +
                 $"（应只剩维修） 弃牌={string.Join(",", _state.Player.Discard)}");
    }

    /// <summary>验证三堆 UI、关键字、回合结束弃牌与消耗。</summary>
    private async void DemoPilesFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.7), SceneTreeTimer.SignalName.Timeout);

        _state.Player.Gold = 2000;
        _state.Player.Hand.Add("magic_airstrike");   // 消耗
        _state.Player.Hand.Add("magic_repair");      // 保留
        RefreshAll();

        GD.Print($"[Piles] 手牌={string.Join(",", _state.Player.Hand)}");
        GD.Print($"[Piles] 牌堆 抽={_state.Player.Deck.Count} 弃={_state.Player.Discard.Count} " +
                 $"消耗={_state.Player.Exhaust.Count}");

        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        // 打开抽牌堆查看器
        OnPileClicked((int)PileView.PileKind.Deck);

        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);

        // 关掉查看器，打一张消耗卡，然后结束回合看弃牌
        _hud.ShowPile("抽牌堆", _state.Player.Deck);   // 保持可见便于截图

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
    }

    /// <summary>选择导弹卡时，应自动选中它关联的发射井。</summary>
    private async void DemoMissileSiloLinkFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        var spots = new List<HexCoord>();
        foreach (Cell c in _state.Cells.Values)
        {
            if (spots.Count >= 2)
                break;
            if (_state.CanBuildAt(c.Coord, PlayerId.Player))
                spots.Add(c.Coord);
        }

        if (spots.Count < 2)
            return;

        BuildingInst siloA = _state.SpawnBuilding("missileSilo", PlayerId.Player, spots[0]);
        BuildingInst siloB = _state.SpawnBuilding("missileSilo", PlayerId.Player, spots[1]);

        _state.Player.Hand.Clear();
        _state.Player.Produced.Clear();
        _state.Player.Hand.Add("magic_missile");
        _state.Player.Produced.Add((siloB.Id, "magic_missile"));   // 故意指向 B

        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();

        _rig.FocusOn(HexLayout.ToWorld(spots[1], 1.0f));
        _rig.FrameMap(3.4f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        OnCardClicked(0);
        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[SiloLink] A={siloA.Id} B={siloB.Id} 选中建筑={( _selBuilding?.Id.ToString() ?? "无")}" +
                 $" 应该等于 B");

        // 真打一张，看导弹从哪座井飞出来
        _state.Player.Gold = 2000;
        HexCoord targetCell = spots[1].Neighbor(0).Neighbor(0);
        GD.Print($"[SiloLink] 发射井 A@{siloA.Coord} B@{siloB.Coord} 目标={targetCell}");
        TryPlayGlobal(GameDatabase.Card("magic_missile"), targetCell);
    }

    /// <summary>发射井被拆除 → 手牌里的导弹卡烧掉。</summary>
    private async void DemoBurnCardFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        // 找两格放两座发射井，用来验证「只烧对应井产出的卡」
        var spots = new List<HexCoord>();
        foreach (Cell c in _state.Cells.Values)
        {
            if (spots.Count >= 2)
                break;
            if (_state.CanBuildAt(c.Coord, PlayerId.Player))
                spots.Add(c.Coord);
        }

        if (spots.Count < 2)
            return;

        BuildingInst siloA = _state.SpawnBuilding("missileSilo", PlayerId.Player, spots[0]);
        BuildingInst siloB = _state.SpawnBuilding("missileSilo", PlayerId.Player, spots[1]);

        _state.Player.Hand.Clear();
        _state.Player.Produced.Clear();
        _state.Player.Hand.Add("magic_missile");      // A 的
        _state.Player.Produced.Add((siloA.Id, "magic_missile"));
        _state.Player.Hand.Add("unit_infantry");
        _state.Player.Hand.Add("magic_missile");      // B 的
        _state.Player.Produced.Add((siloB.Id, "magic_missile"));

        _map.SyncEntities();
        RefreshAll();

        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[BurnCard] 拆除 A 前手牌={string.Join(",", _state.Player.Hand)} " +
                 $"来源={string.Join(",", _state.Player.Produced.Select(p => $"{p.SiloId}:{p.Card}"))}");

        // 只拆 A → 只应烧掉 A 的那张
        _state.Demolish(siloA);
        _map.SyncEntities();
        RefreshAll();

        await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[BurnCard] 拆除 A 后手牌={string.Join(",", _state.Player.Hand)} " +
                 $"（应剩 1 张导弹卡，属于 B）");
    }

    /// <summary>验证「血量越低攻击越低」与面板上的彩色攻击力。</summary>
    private async void DemoHpDamageFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.CanDeployCard("unit_infantry", PlayerId.Player, c, out _))
                spot = c;
        }

        if (spot == null)
            return;

        UnitInst u = _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
        _map.SyncEntities();

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.1f));
        _rig.FrameMap(2.4f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        SelectUnit(u);
        GD.Print($"[HpDmg] 满血 {u.Hp}/{u.MaxHp} 攻击={BattleState.EffectiveDamage(u)}/{u.Def.Damage}");

        await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);

        foreach (float p in new[] { 0.6f, 0.5f, 0.2f })
        {
            u.Hp = Mathf.Max(1, Mathf.RoundToInt(u.MaxHp * p));
            await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
            GD.Print($"[HpDmg] 血量 {p:P0} → {u.Hp}/{u.MaxHp} " +
                     $"攻击={BattleState.EffectiveDamage(u)}（满血 {u.Def.Damage}）");
        }
    }

    /// <summary>验证「有己方驻军的格子」也能通过轮询进入建造菜单。</summary>
    private async void DemoBuildWithUnitsFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.CanBuildAt(c, PlayerId.Player))
                spot = c;
        }

        if (spot == null)
            return;

        UnitInst a = _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
        UnitInst b = _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
        _map.SyncEntities();
        _map.RefreshZones();

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.2f));
        _rig.FrameMap(2.8f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        Vector2 p = _rig.Camera.UnprojectPosition(HexLayout.ToWorld(spot.Value, HexMapView.TileTop));
        GD.Print($"[BuildUnits] 格子={spot.Value} 单位={a.Id},{b.Id}");

        for (int i = 1; i <= 4; i++)
        {
            SendMotion(p, p);
            UpdateHover(p);
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
            OnLeftClick(p);
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);

            string sel = _selUnit != null ? $"单位 {_selUnit.Id}"
                : _selBuilding != null ? "建筑"
                : _hud.DebugBuildMenuVisible ? "建造菜单" : "无";
            GD.Print($"[BuildUnits] 第{i}次点击 → {sel} 建造目标={_buildTarget}");
        }

        // 在驻军格上真的造一座
        bool ok = _state.TryBuild(PlayerId.Player, "city", spot.Value, out string err);
        GD.Print($"[BuildUnits] 在驻军格建造 TryBuild={ok} err={err}");
        _map.SyncEntities();
    }

    /// <summary>验证水面不能建造。</summary>
    private void DemoWaterBuildFlow()
    {
        Cell water = _state.Cells.Values.FirstOrDefault(c => c.Terrain == TerrainType.Water);
        if (water == null)
        {
            GD.Print("[WaterBuild] 这张地图没有水面");
            return;
        }

        bool can = _state.CanBuildAt(water.Coord, PlayerId.Player);
        int gold = _state.Player.Gold;
        bool built = _state.TryBuild(PlayerId.Player, "city", water.Coord, out string err);

        GD.Print($"[WaterBuild] 水面格 {water.Coord} CanBuild={can} TryBuild={built} " +
                 $"err={err} 金币 {gold}→{_state.Player.Gold}");
    }

    /// <summary>陆军不能部署到水面；空军可以。</summary>
    private void DemoWaterDeployFlow()
    {
        Cell water = _state.Cells.Values.FirstOrDefault(c => c.Terrain == TerrainType.Water);
        if (water == null)
        {
            GD.Print("[WaterDeploy] 这张地图没有水面");
            return;
        }

        bool groundOk = _state.CanDeployCard("unit_infantry", PlayerId.Enemy, water.Coord, out string groundErr);
        bool airOk = _state.CanDeployCard("unit_helicopter", PlayerId.Enemy, water.Coord, out string airErr);

        GD.Print($"[WaterDeploy] 水面格 {water.Coord} 陆军可部署={groundOk} err={groundErr}");
        GD.Print($"[WaterDeploy] 水面格 {water.Coord} 空军可部署={airOk} err={airErr}（控制区可能挡住，属正常）");

        // 扫一遍场上有没有「陆军站在水上」的异常
        int bad = _state.Units.Count(u => u.Alive && !u.IsAir &&
                                          _state.Cell(u.Coord)?.Terrain == TerrainType.Water);
        GD.Print($"[WaterDeploy] 场上站在水上的陆军数量={bad}（应为 0）");
    }

    /// <summary>没有导弹发射井时打出导弹卡：看特效与结算是否正常。</summary>
    private async void DemoMissileNoSiloFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null)
                spot = c;
        }

        if (spot == null)
            return;

        UnitInst foe = _state.SpawnUnit("infantry", PlayerId.Enemy, spot.Value);
        _state.Player.Gold = 2000;
        _state.Player.Hand.Add("magic_missile");
        _map.SyncEntities();

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 2.0f));
        _rig.FrameMap(5.0f);
        _rig.SetPitch(32f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        int siloCount = _state.Buildings.Count(b => b.Owner == PlayerId.Player && b.DefId == "missileSilo");
        GD.Print($"[NoSilo] 发射井数量={siloCount} 敌人HP={foe.Hp}");

        var card = GameDatabase.Card("magic_missile");
        TryPlayGlobal(card, spot.Value);
        GD.Print($"[NoSilo] 出牌后 敌人HP={foe.Hp} 待结算={_pendingCard != null}");

        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[NoSilo] 命中后 敌人存活={foe.Alive} HP={foe.Hp}");
    }

    /// <summary>结束我方回合并观察敌方 AI 的行动特效。</summary>
    private async void DemoEnemyTurnFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        // 摆几个敌我单位，保证 AI 有事可做
        HexCoord? mine = null;
        for (int q = -2; q < 8 && mine == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.CanDeployCard("unit_infantry", PlayerId.Player, c, out _))
                mine = c;
        }

        if (mine == null)
            return;

        _state.SpawnUnit("infantry", PlayerId.Player, mine.Value);
        UnitInst foe = _state.SpawnUnit("tank", PlayerId.Enemy, mine.Value.Neighbor(0));
        _state.Enemy.Gold = 900;
        _map.SyncEntities();
        _map.RefreshZones();

        _rig.FocusOn(HexLayout.ToWorld(mine.Value, 1.0f));
        _rig.FrameMap(5.0f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[EnemyTurn] 结束我方回合，观察 AI（敌方坦克 {foe.Id}）");
        OnEndTurn();

        await ToSignal(GetTree().CreateTimer(4.0), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[EnemyTurn] 敌方回合结束 阶段={_state.Phase}");
    }

    /// <summary>验证「命中瞬间才结算伤害」与死亡缩放动画。</summary>
    private async void DemoKillFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.CanDeployCard("unit_tank", PlayerId.Player, c, out _))
                spot = c;
        }

        if (spot == null)
            return;

        UnitInst tank = _state.SpawnUnit("tank", PlayerId.Player, spot.Value);
        UnitInst foe = _state.SpawnUnit("infantry", PlayerId.Enemy, spot.Value.Neighbor(0));
        foe.Hp = 30;
        _map.SyncEntities();

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.4f));
        _rig.FrameMap(3.0f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        SelectUnit(tank);
        await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);

        Vector2 p = _rig.Camera.UnprojectPosition(
            HexLayout.ToWorld(spot.Value.Neighbor(0), HexMapView.TileTop));
        SendMotion(p, p);
        UpdateHover(p);
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);

        OnLeftClick(p);
        GD.Print($"[Kill] 点击瞬间 敌人HP={foe.Hp} 待结算={_pendingAttack != null}");

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Kill] 命中后 敌人存活={foe.Alive} HP={foe.Hp} 视图数={_map.DebugUnitViewCount()}");
    }

    /// <summary>确认敌方兵营/机场/导弹井只给敌方发牌，不会塞进我方手牌。</summary>
    private async void DemoCheckEnemyCardsFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        // 找几格给敌方盖生产建筑
        var spots = new List<HexCoord>();
        foreach (Cell c in _state.Cells.Values)
        {
            if (c.Building != null)
                continue;
            spots.Add(c.Coord);
            if (spots.Count >= 3)
                break;
        }

        if (spots.Count < 3)
            return;

        _state.SpawnBuilding("barracks", PlayerId.Enemy, spots[0]);
        _state.SpawnBuilding("helipad", PlayerId.Enemy, spots[1]);
        _state.SpawnBuilding("missileSilo", PlayerId.Enemy, spots[2]);
        _map.SyncEntities();

        var before = new List<string>(_state.Player.Hand);
        GD.Print($"[CardCheck] 我方手牌（前）= {string.Join(",", before)}");
        GD.Print($"[CardCheck] 敌方手牌（前）= {string.Join(",", _state.Enemy.Hand)}");

        // 敌方回合开始 → 敌方建筑发牌
        _state.BeginTurn(PlayerId.Enemy);
        GD.Print($"[CardCheck] 敌方手牌（后）= {string.Join(",", _state.Enemy.Hand)}");

        var after = _state.Player.Hand;
        bool same = before.Count == after.Count && new HashSet<string>(before).SetEquals(after);
        GD.Print($"[CardCheck] 我方手牌（后）= {string.Join(",", after)} 是否未变={same}");

        // 再走一次我方回合开始（我方 HQ 会抽牌，这是设计内的）
        _state.BeginTurn(PlayerId.Player);
        GD.Print($"[CardCheck] 我方回合开始后 = {string.Join(",", _state.Player.Hand)}（+我方 HQ 抽牌）");
    }

    /// <summary>建筑维修：按损失血量计价，修满回血。</summary>
    private async void DemoRepairBuildingFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.CanBuildAt(c, PlayerId.Player))
                spot = c;
        }

        if (spot == null)
            return;

        string kind = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--turret") >= 0 ? "gunTurret" : "city";
        BuildingInst city = _state.SpawnBuilding(kind, PlayerId.Player, spot.Value);
        city.Hp = Mathf.RoundToInt(city.MaxHp * 0.35f);
        _state.Player.Gold = 1000;
        _map.SyncEntities();
        _map.RefreshZones();

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.0f));
        _rig.FrameMap(2.6f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        SelectBuilding(city);
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        int cost = BattleState.RepairCost(city);
        GD.Print($"[RepairB] 血量 {city.Hp}/{city.MaxHp} 维修费={cost}$ 金币={_state.Player.Gold}");

        OnRepairClicked();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[RepairB] 维修后 血量 {city.Hp}/{city.MaxHp} 金币={_state.Player.Gold}");

        // 满血时应该没有维修按钮
        GD.Print($"[RepairB] 满血维修费={BattleState.RepairCost(city)}$（应为 0）");
    }

    /// <summary>维修卡：治疗指定格内的所有己方单位。</summary>
    private async void DemoRepairFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.CanDeployCard("unit_infantry", PlayerId.Player, c, out _))
                spot = c;
        }

        if (spot == null)
            return;

        UnitInst a = _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
        UnitInst b = _state.SpawnUnit("tank", PlayerId.Player, spot.Value);
        a.Hp = 40;
        b.Hp = 90;
        _map.SyncEntities();

        _state.Player.Gold = 1000;
        _state.Player.Hand.Add("magic_repair");

        int ha = a.Hp;
        int hb = b.Hp;
        string result = _state.PlayGlobalCard(_state.Player, "magic_repair", spot.Value, out string err);
        GD.Print($"[Repair] 结果={result} err={err} " +
                 $"步兵 {ha}→{a.Hp} 坦克 {hb}→{b.Hp}");

        // 正常路径：治疗特效（截图看这个）
        _map.SyncEntities();
        _vfx.Heal(HexLayout.ToWorld(spot.Value, HexMapView.TileTop + 0.2f), 1.0f);

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.2f));
        _rig.FrameMap(2.8f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        // 稍后再跑「空格子」的失败路径，免得提示盖住截图
        await ToSignal(GetTree().CreateTimer(0.9), SceneTreeTimer.SignalName.Timeout);

        HexCoord? empty = null;
        foreach (Cell c in _state.Cells.Values)
        {
            if (c.Building == null && c.Units.Count == 0)
            {
                empty = c.Coord;
                break;
            }
        }

        if (empty != null)
        {
            _state.Player.Hand.Add("magic_repair");
            _state.PlayGlobalCard(_state.Player, "magic_repair", empty.Value, out string err2);
            GD.Print($"[Repair] 空格子 err={err2}");

            // 走 UI 路径：不该触发飞行动画（卡不会消失）
            _map.SyncEntities();
            bool consumed = TryPlayCardAt(_state.Player.Hand.Count - 1, empty.Value);
            GD.Print($"[Repair] 空格子路径 卡消耗={consumed} 飞行中={_cardFlying}（飞行中应为 False）");
        }
    }

    /// <summary>验证「直接伤害卡不能打指挥部」。</summary>
    private async void DemoStrikeHqFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Enemy && b.IsHq);
        if (hq == null)
            return;

        _state.Player.Gold = 5000;
        if (!_state.Player.Hand.Contains("magic_airstrike"))
            _state.Player.Hand.Add("magic_airstrike");

        int hpBefore = hq.Hp;
        string r1 = _state.PlayGlobalCard(_state.Player, "magic_airstrike", hq.Coord, out string err1);
        GD.Print($"[StrikeHQ] 打指挥部 → error={err1} hp {hpBefore}→{hq.Hp}（hp 应不变）");

        // 走界面路径：应当「立刻」提示，不播特效、不飞卡
        int idx = _state.Player.Hand.IndexOf("magic_airstrike");
        if (idx >= 0)
        {
            bool ok = TryPlayCardAt(idx, hq.Coord);
            GD.Print($"[StrikeHQ] 界面路径 播放={ok} 飞行中={_cardFlying} 待结算={_pendingCard != null}" +
                     "（都应为 False）");
        }

        // 再试一个普通敌方单位格：应该能正常打
        HexCoord? free = null;
        foreach (Cell c in _state.Cells.Values)
        {
            if (c.Building == null && c.Units.Count == 0 && HexCoord.Distance(c.Coord, hq.Coord) > 2)
            {
                free = c.Coord;
                break;
            }
        }

        UnitInst foe = null;
        if (free != null)
        {
            foe = _state.SpawnUnit("infantry", PlayerId.Enemy, free.Value);
            _map.SyncEntities();
        }

        if (foe != null)
        {
            if (!_state.Player.Hand.Contains("magic_airstrike"))
                _state.Player.Hand.Add("magic_airstrike");
            string r2 = _state.PlayGlobalCard(_state.Player, "magic_airstrike", foe.Coord, out string err2);
            GD.Print($"[StrikeHQ] 打普通单位 → error={err2} result={r2}");
        }

        _map.SyncEntities();
        _rig.FocusOn(HexLayout.ToWorld(hq.Coord, 1.2f));
        _rig.FrameMap(3.6f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);
    }

    /// <summary>炮台打死敌方单位后，视图应立即消失（不用等下一回合）。</summary>
    private async void DemoTurretKillFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null)
                spot = c;
        }

        if (spot == null)
            return;

        HexCoord turretCell = spot.Value;
        HexCoord foeCell = turretCell.Neighbor(0);

        _state.SpawnBuilding("gunTurret", PlayerId.Player, turretCell);
        UnitInst foe = _state.SpawnUnit("infantry", PlayerId.Enemy, foeCell);
        foe.Hp = 1;     // 一炮必死
        _map.SyncEntities();
        _map.RefreshZones();

        _rig.FocusOn(HexLayout.ToWorld(foeCell, 0.8f));
        _rig.FrameMap(3.4f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[TurretKill] 开火前 单位数={_state.Units.Count(u => u.Alive)} 视图数={_map.DebugUnitViewCount()}");

        var volleys = _state.ResolveBuildingAttacks(PlayerId.Player);
        GD.Print($"[TurretKill] 战报={string.Join("；", volleys)}");
        PlayVolleys();

        await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[TurretKill] 开火后 单位数={_state.Units.Count(u => u.Alive)} 视图数={_map.DebugUnitViewCount()}");
    }

    /// <summary>导弹车攻击：抛物线弹道。</summary>
    private async void DemoMissileLauncherFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null)
                spot = c;
        }

        if (spot == null)
            return;

        HexCoord fromCell = spot.Value;
        HexCoord toCell = fromCell.Neighbor(0);

        UnitInst launcher = _state.SpawnUnit("missileLauncher", PlayerId.Player, fromCell);
        _state.SpawnUnit("infantry", PlayerId.Enemy, toCell);
        _map.SyncEntities();
        _map.RefreshZones();

        _rig.FocusOn(HexLayout.ToWorld(toCell, 1.2f));
        _rig.FrameMap(4.2f);
        _rig.SetPitch(32f);
        _rig.SetVerticalOffset(0f);

        SelectUnit(launcher);
        await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);

        Vector2 p = _rig.Camera.UnprojectPosition(HexLayout.ToWorld(toCell, HexMapView.TileTop));
        SendMotion(p, p);
        UpdateHover(p);
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
        OnLeftClick(p);

        GD.Print($"[MissileCar] {fromCell} → {toCell}");
    }

    /// <summary>战斗机攻击地面目标，检查空对地导弹特效。</summary>
    private async void DemoFighterAttackFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null)
                spot = c;
        }

        if (spot == null)
            return;

        HexCoord fromCell = spot.Value;
        HexCoord toCell = spot.Value.Neighbor(0);

        UnitInst jet = _state.SpawnUnit("fighter", PlayerId.Player, fromCell);
        _state.SpawnUnit("infantry", PlayerId.Enemy, toCell);
        _map.SyncEntities();
        _map.RefreshZones();

        _rig.FocusOn(HexLayout.ToWorld(toCell, 1.4f));
        _rig.FrameMap(3.6f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        SelectUnit(jet);
        await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);

        Vector2 p = _rig.Camera.UnprojectPosition(HexLayout.ToWorld(toCell, HexMapView.TileTop));
        SendMotion(p, p);
        UpdateHover(p);
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
        OnLeftClick(p);

        GD.Print($"[JetAtk] 战斗机 {fromCell} → {toCell} 可攻击={_state.CanAttack(jet, toCell)}");
    }

    /// <summary>单独播放一个特效，方便截图检查。</summary>
    private async void DemoVfxFlow(string kind)
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null)
                spot = c;
        }

        if (spot == null)
            return;

        Vector3 at = HexLayout.ToWorld(spot.Value, HexMapView.TileTop);
        _rig.FocusOn(at);
        _rig.FrameMap(2.6f);
        _rig.SetPitch(36f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);

        switch (kind)
        {
            case "missile":
                _vfx.MissileStrike(at + Vector3.Up * 0.2f, 1.2f);
                break;
            case "heal":
                _vfx.Heal(at + Vector3.Up * 0.2f, 1.0f);
                break;
            case "bullet":
                _vfx.BulletHit(at + Vector3.Up * 0.8f, 0.7f);
                break;
            default:
                _vfx.Explosion(at + Vector3.Up * 0.35f, 1.2f);
                break;
        }

        GD.Print($"[Vfx] {kind} @ {spot.Value}");
    }

    /// <summary>战斗机移动到位后，确认移动动画会先把这一轮播完再切 Idle。</summary>
    private async void DemoMoveAnimFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? from = null;
        HexCoord? to = null;
        for (int q = -2; q < 8 && to == null; q++)
        {
            var c = new HexCoord(q, 4);
            Cell cell = _state.Cell(c);
            if (cell == null || cell.Building != null || cell.Units.Count > 0)
                continue;

            HexCoord nb = c.Neighbor(3);
            Cell ncell = _state.Cell(nb);
            if (ncell == null || ncell.Building != null || ncell.Units.Count > 0)
                continue;

            from = c;
            to = nb;
        }

        if (from == null || to == null)
            return;

        UnitInst jet = _state.SpawnUnit("fighter", PlayerId.Player, from.Value);
        _map.SyncEntities();

        _rig.FocusOn(HexLayout.ToWorld(to.Value, 2.0f));
        _rig.FrameMap(4.2f);
        _rig.SetPitch(32f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[MoveAnim] 移动前 {_map.DebugUnitAnimState(jet.Id)} yaw={_map.DebugUnitYaw(jet.Id):F1}");

        _state.TryMove(jet, to.Value, out _);
        _map.SyncEntities();
        float yawStart = _map.DebugUnitYaw(jet.Id);

        for (int i = 0; i < 4; i++)
        {
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
            GD.Print($"[MoveAnim] 转向 t+{(i + 1) * 0.05:F2}s yaw={_map.DebugUnitYaw(jet.Id):F1}（起始 {yawStart:F1}）");
        }

        for (int i = 0; i < 6; i++)
        {
            await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);
            GD.Print($"[MoveAnim] t+{(i + 1) * 0.35:F2}s {_map.DebugUnitAnimState(jet.Id)}");
        }
    }

    /// <summary>同时部署一座建筑和一个单位，用来截取缩放动画。</summary>
    private async void DemoPopFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 4);
            if (_state.CanBuildAt(c, PlayerId.Player))
                spot = c;
        }

        if (spot == null)
            return;

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.2f));
        _rig.FrameMap(3.4f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        _state.SpawnBuilding("city", PlayerId.Player, spot.Value);
        _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
        _map.SyncEntities();
        _map.RefreshZones();

        GD.Print($"[Pop] 部署 city + infantry @ {spot.Value}");
    }

    /// <summary>选中建筑 → 点空地，检查血条是否隐藏、位置是否稳定。</summary>
    private async void DemoBarDiagFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? cityCell = null;
        HexCoord? emptyCell = null;
        for (int q = -2; q < 8; q++)
        {
            var c = new HexCoord(q, 5);
            Cell cell = _state.Cell(c);
            if (cell == null || cell.Building != null || cell.Units.Count > 0)
                continue;

            if (cityCell == null && _state.CanBuildAt(c, PlayerId.Player))
                cityCell = c;
            else if (cityCell != null && emptyCell == null)
                emptyCell = c;
        }

        if (cityCell == null || emptyCell == null)
            return;

        BuildingInst city = _state.SpawnBuilding("city", PlayerId.Player, cityCell.Value);
        city.Hp = Mathf.RoundToInt(city.MaxHp * 0.5f);
        _map.SyncEntities();
        _map.RefreshZones();

        _rig.FocusOn(HexLayout.ToWorld(cityCell.Value, 1.2f));
        _rig.FrameMap(4.0f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        SelectBuilding(city);
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[BarDiag] 选中建筑 选中={_selBuilding != null} 描边={_map.DebugBuildingOutline(city.Id)}");

        // 可选：移动视角，检查血条是否跟着世界坐标走（而不是钉在屏幕上）
        if (OS.GetCmdlineUserArgs().Contains("--pan"))
        {
            await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
            _rig.Pan(new Vector2(90f, 30f));
            _rig.Rotate(10f);
            GD.Print("[BarDiag] 已平移/旋转视角");
        }

        Vector2 emptyScreen = _rig.Camera.UnprojectPosition(
            HexLayout.ToWorld(emptyCell.Value, HexMapView.TileTop));
        SendMotion(emptyScreen, emptyScreen);
        UpdateHover(emptyScreen);
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);

        if (!OS.GetCmdlineUserArgs().Contains("--nohide"))
            OnLeftClick(emptyScreen);

        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[BarDiag] 点空地后 选中建筑={_selBuilding != null} 描边={_map.DebugBuildingOutline(city.Id)}");
    }

    /// <summary>陆军能否走进己方/敌方建筑格。</summary>
    private void DemoMoveBuildingFlow()
    {
        HexCoord? spot = null;
        for (int q = -2; q < 6 && spot == null; q++)
        {
            var c = new HexCoord(q, 3);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null)
                spot = c;
        }

        if (spot == null)
            return;

        HexCoord mineCell = spot.Value;
        HexCoord enemyCell = spot.Value.Neighbor(0);
        HexCoord startCell = spot.Value.Neighbor(1);
        HexCoord enemyStart = enemyCell.Neighbor(1);

        // 一格放己方城市，一格放敌方城市（方便对比）
        _state.SpawnBuilding("city", PlayerId.Player, mineCell);
        _state.SpawnBuilding("city", PlayerId.Enemy, enemyCell);

        UnitInst ground = _state.SpawnUnit("infantry", PlayerId.Player, startCell);
        UnitInst air = _state.SpawnUnit("helicopter", PlayerId.Player, enemyStart);

        var groundRange = _state.MoveRange(ground);
        var airRange = _state.MoveRange(air);

        GD.Print($"[MoveB] 陆军 起始={startCell} 己方建筑格={mineCell} 在范围内={groundRange.Contains(mineCell)}");
        GD.Print($"[MoveB] 陆军 敌方建筑格={enemyCell} 在范围内={groundRange.Contains(enemyCell)}（应为 False）");
        GD.Print($"[MoveB] 空军 敌方建筑格={enemyCell} 在范围内={airRange.Contains(enemyCell)}（应为 True）");

        bool okMine = _state.TryMove(ground, mineCell, out string err1);
        GD.Print($"[MoveB] 陆军走进己方建筑格 ok={okMine} err={err1} 位置={ground.Coord}");

        _map.SyncEntities();
        _map.RefreshZones();

        _rig.FocusOn(HexLayout.ToWorld(mineCell, 1.2f));
        _rig.FrameMap(3.4f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);
    }

    /// <summary>同格叠「建筑 + 两个单位」，反复点击检查轮询顺序。</summary>
    private async void DemoMixedCycleFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 5);
            if (_state.CanBuildAt(c, PlayerId.Player))
                spot = c;
        }

        if (spot == null)
            return;

        BuildingInst city = _state.SpawnBuilding("city", PlayerId.Player, spot.Value);
        UnitInst a = _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
        UnitInst b = _state.SpawnUnit("tank", PlayerId.Player, spot.Value);

        _map.SyncEntities();
        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.2f));
        _rig.FrameMap(3.0f);
        _rig.SetPitch(34f);
        _rig.SetVerticalOffset(0f);

        Vector2 p = _rig.Camera.UnprojectPosition(HexLayout.ToWorld(spot.Value, HexMapView.TileTop));
        GD.Print($"[Mixed] 格子={spot.Value} 建筑={city.Id} 单位={a.Id},{b.Id}");

        for (int i = 1; i <= 4; i++)
        {
            SendMotion(p, p);
            UpdateHover(p);
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
            OnLeftClick(p);
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);

            string sel = _selUnit != null ? $"单位 {_selUnit.Id}" : _selBuilding != null ? "建筑" : "无";
            string outline = _selUnit != null
                ? _map.DebugUnitOutline(_selUnit.Id)
                : _selBuilding != null ? _map.DebugBuildingOutline(_selBuilding.Id) : "-";
            GD.Print($"[Mixed] 第{i}次点击 选中={sel} 描边={outline}");
        }
    }

    /// <summary>出牌飞行动画（截图用）。</summary>
    private async void DemoFlyFlow()    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        OnCardClicked(0);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        CardDef card = GameDatabase.Card(_state.Player.Hand[0]);
        HexCoord? spot = null;
        foreach (Cell c in _state.Cells.Values)
        {
            if (card != null && _state.CanDeployCard(card.Id, PlayerId.Player, c.Coord, out _))
            {
                spot = c.Coord;
                break;
            }
        }

        if (spot == null)
            return;

        Vector2 p = _rig.Camera.UnprojectPosition(HexLayout.ToWorld(spot.Value, HexMapView.TileTop));
        SendMotion(p, p);
        UpdateHover(p);
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);

        int before = _state.Units.Count(u => u.Alive);
        OnLeftClick(p);
        GD.Print($"[Fly] 开始飞行 card={card?.Id} target={spot.Value} unitsBefore={before}");

        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
        int after = _state.Units.Count(u => u.Alive);
        GD.Print($"[Fly] 飞行结束 单位数 {before} → {after}（应为 +1）");
    }

    /// <summary>悬停升级 → 点升级 → 移开鼠标，验证外观停在新等级。</summary>
    private async void DemoUpgradeHoverFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 5);
            if (_state.CanBuildAt(c, PlayerId.Player))
                spot = c;
        }

        if (spot == null)
            return;

        BuildingInst city = _state.SpawnBuilding("city", PlayerId.Player, spot.Value);
        _state.Player.Gold = 5000;
        _map.SyncEntities();
        SelectBuilding(city);
        RefreshAll();

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.0f));
        _rig.FrameMap(2.8f);
        _rig.SetPitch(32f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[UpgHover] Lv{city.Level} 高度={_map.DebugBuildingTop(city.Id):F2}");

        _hud.DebugHoverUpgrade();       // 预览 Lv2
        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[UpgHover] 预览中 Lv{city.Level} 高度={_map.DebugBuildingTop(city.Id):F2}");

        OnUpgradeClicked();             // 真升级
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[UpgHover] 升级后 Lv{city.Level} 高度={_map.DebugBuildingTop(city.Id):F2}");

        OnPreviewUnhovered();           // 移开鼠标
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[UpgHover] 移开后 Lv{city.Level} 高度={_map.DebugBuildingTop(city.Id):F2}（应仍是升级后的高度）");
    }

    /// <summary>选一座一级城市并模拟悬停「升级」，检查升级预览。</summary>
    private async void DemoPreviewUpgradeFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 5);
            if (_state.CanBuildAt(c, PlayerId.Player))
                spot = c;
        }

        if (spot == null)
            return;

        BuildingInst city = _state.SpawnBuilding("city", PlayerId.Player, spot.Value);
        _state.Player.Gold = 5000;
        _map.SyncEntities();
        SelectBuilding(city);
        RefreshAll();

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.0f));
        _rig.FrameMap(2.8f);
        _rig.SetPitch(32f);
        _rig.SetVerticalOffset(0f);

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        _hud.DebugHoverUpgrade();
        GD.Print($"[Preview] 升级预览 city Lv1 → Lv{_selBuilding.Level + 1} " +
                 $"描边={_map.DebugBuildingOutline(city.Id)}");

        if (!OS.GetCmdlineUserArgs().Contains("--apply"))
            return;

        // 移开鼠标 → 应该恢复成真实等级的外观
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        _map.ClearBuildingPreview();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Preview] 移开后恢复 Lv{_selBuilding.Level}");
    }

    /// <summary>打印直升机模型节点树与旋翼动画轨道。</summary>
    private void DemoRotorTreeFlow()
    {
        UnitDef def = GameDatabase.Unit("helicopter");
        if (def == null || string.IsNullOrEmpty(def.Model) || !ResourceLoader.Exists(def.Model))
            return;

        Node inst = GD.Load<PackedScene>(def.Model).Instantiate();
        GD.Print("[Rotor] ==== 直升机 ====");
        Walk(inst, 0);

        foreach (Node n in inst.FindChildren("*", "AnimationPlayer", true, false))
        {
            if (n is not AnimationPlayer ap)
                continue;

            foreach (string clip in ap.GetAnimationList())
            {
                if (!clip.Contains("Rotor"))
                    continue;

                Godot.Animation anim = ap.GetAnimation(clip);
                if (anim == null)
                    continue;

                GD.Print($"[Rotor] {clip} len={anim.Length:F2}s tracks={anim.GetTrackCount()}");
                for (int i = 0; i < anim.GetTrackCount(); i++)
                {
                    GD.Print($"[Rotor]   path={anim.TrackGetPath(i)} type={anim.TrackGetType(i)} " +
                             $"keys={anim.TrackGetKeyCount(i)}");
                }
            }
        }

        inst.QueueFree();

        static void Walk(Node node, int depth)
        {
            string indent = new(' ', depth * 2);
            string extra = node switch
            {
                MeshInstance3D mi => $" (mesh={mi.Mesh?.ResourceName})",
                _ => "",
            };
            GD.Print($"[Rotor] {indent}{node.Name} <{node.GetClass()}>{extra}");
            if (depth > 3)
                return;
            foreach (Node child in node.GetChildren())
                Walk(child, depth + 1);
        }
    }

    /// <summary>打印某个建筑模型的节点树。</summary>
    private void DemoBuildTreeFlow(string defId)
    {
        BuildingDef def = GameDatabase.Building(defId);
        if (def == null || string.IsNullOrEmpty(def.Model) || !ResourceLoader.Exists(def.Model))
        {
            GD.Print($"[Tree] {defId}: 找不到模型");
            return;
        }

        Node inst = GD.Load<PackedScene>(def.Model).Instantiate();
        GD.Print($"[Tree] ==== {defId} ====");
        Walk(inst, 0);
        inst.QueueFree();

        static void Walk(Node node, int depth)
        {
            string indent = new(' ', depth * 2);
            string extra = node switch
            {
                MeshInstance3D mi => $" (mesh={mi.Mesh?.ResourceName})",
                AnimationPlayer ap => $" (anims={ap.GetAnimationList().Length})",
                _ => "",
            };
            GD.Print($"[Tree] {indent}{node.Name} <{node.GetClass()}>{extra}");

            if (depth > 3)
                return;

            foreach (Node child in node.GetChildren())
                Walk(child, depth + 1);
        }
    }

    /// <summary>打印坦克模型节点树，找出炮塔节点。</summary>
    private void DemoTankTreeFlow()
    {
        UnitDef def = GameDatabase.Unit("tank");
        if (def == null || string.IsNullOrEmpty(def.Model) || !ResourceLoader.Exists(def.Model))
            return;

        Node inst = GD.Load<PackedScene>(def.Model).Instantiate();
        GD.Print("[Tree] 坦克模型节点树：");
        Walk(inst, 0);

        // 打印几个片段的轨道路径，看谁在动炮塔
        foreach (Node n in inst.FindChildren("*", "AnimationPlayer", true, false))
        {
            if (n is not AnimationPlayer ap)
                continue;
            foreach (string clip in ap.GetAnimationList())
            {
                Godot.Animation anim = ap.GetAnimation(clip);
                if (anim == null)
                    continue;
                if (!clip.Contains("Hull") && !clip.Contains("Turret"))
                    continue;
                for (int i = 0; i < anim.GetTrackCount(); i++)
                {
                    GD.Print($"[Track] {clip} -> {anim.TrackGetPath(i)}");
                }
            }
        }

        inst.QueueFree();

        static void Walk(Node node, int depth)
        {
            string indent = new(' ', depth * 2);
            string extra = node switch
            {
                MeshInstance3D mi => $" (mesh={mi.Mesh?.ResourceName})",
                AnimationPlayer ap => $" (anims={ap.GetAnimationList().Length})",
                _ => "",
            };
            string rot = node is Node3D n3 && !n3.Rotation.IsZeroApprox()
                ? $" rot={n3.RotationDegrees}"
                : "";
            GD.Print($"[Tree] {indent}{node.Name} <{node.GetClass()}>{extra}{rot}");

            if (depth > 4)
                return;

            foreach (Node child in node.GetChildren())
                Walk(child, depth + 1);
        }
    }

    /// <summary>把某个建筑的三级并排摆出来对比。</summary>
    private void DemoLevelsFlow(string defId)
    {
        ModelUtil.Verbose = true;

        var spots = new List<HexCoord>();
        for (int q = -3; q < 8 && spots.Count < 3; q++)
        {
            var c = new HexCoord(q, 6);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null)
                spots.Add(c);
        }

        for (int i = 0; i < spots.Count && i < 3; i++)
            _state.SpawnBuilding(defId, PlayerId.Player, spots[i], i + 1);

        _map.SyncEntities();
        _map.RefreshZones();

        if (spots.Count > 0)
        {
            Vector3 center = HexLayout.ToWorld(spots[spots.Count / 2], 1.0f);
            _rig.FocusOn(center);
            _rig.FrameMap(4.2f);
            _rig.SetPitch(30f);
            _rig.SetVerticalOffset(0f);
        }
    }

    /// <summary>单独摆一个指定建筑的某等级，打印网格与高度分布。</summary>
    private void DemoBuildingFlow(string defId, int level)
    {
        ModelUtil.Verbose = true;

        HexCoord? spot = null;
        for (int q = -3; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 6);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null)
                spot = c;
        }

        if (spot == null)
            return;

        BuildingInst b = _state.SpawnBuilding(defId, PlayerId.Player, spot.Value, level);
        _map.SyncEntities();
        _map.RefreshZones();
        _map.DumpMeshNames();
        _map.DumpVertexHeights(b.Id);
        _map.ScaleBuildingView(b.Id, 2.2f);

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.0f));
        _rig.FrameMap(2.0f);
        _rig.SetPitch(22f);
        _rig.SetVerticalOffset(0f);
    }

    /// <summary>单个 Lv1 指挥部近距离多角度检查（看是否悬空）。</summary>
    private void DemoHqCloseFlow()
    {
        ModelUtil.Verbose = true;

        HexCoord? spot = null;
        for (int q = -3; q < 8; q++)
        {
            var c = new HexCoord(q, 6);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null)
            {
                spot = c;
                break;
            }
        }

        if (spot == null)
            return;

        BuildingInst b = _state.SpawnBuilding("hq", PlayerId.Player, spot.Value, 1);
        _map.SyncEntities();
        _map.RefreshZones();
        _map.DumpMeshNames();
        _map.DumpVertexHeights(b.Id);
        _map.ScaleBuildingView(b.Id, 3f);

        _rig.FocusOn(HexLayout.ToWorld(spot.Value, 1.2f));
        _rig.FrameMap(2.0f);
        _rig.SetPitch(16f);
        _rig.SetVerticalOffset(0f);
    }

    /// <summary>摆 Lv1/Lv2/Lv3 三个指挥部，打印每级可见网格。</summary>
    private void DemoHqFlow()
    {
        ModelUtil.Verbose = true;
        // 在 r=6 这一行上找 3 个连续空格
        var row = 6;
        var spots = new List<HexCoord>();
        for (int q = -3; q < 8 && spots.Count < 3; q++)
        {
            var c = new HexCoord(q, row);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null)
                spots.Add(c);
        }

        for (int i = 0; i < spots.Count && i < 3; i++)
            _state.SpawnBuilding("hq", PlayerId.Player, spots[i], i + 1);

        _map.SyncEntities();
        _map.RefreshZones();
        _map.DumpBuildingBounds(_state);
        _map.DumpMeshNames();

        if (spots.Count > 0)
        {
            Vector3 center = HexLayout.ToWorld(spots[spots.Count / 2], 1.0f);
            _rig.FocusOn(center);
            _rig.FrameMap(4.5f);
            _rig.SetPitch(40f);
            _rig.SetVerticalOffset(0f);
        }
    }

    /// <summary>把所有建筑摆成一行，俯视检查占地大小和是否居中。</summary>
    private void DemoFitFlow()
    {
        List<string> ids = GameDatabase.Buildings.Values
            .Where(d => d.Id != "hq")
            .Select(d => d.Id)
            .ToList();

        int row = 4;
        int col = -4;
        float sumX = 0, sumZ = 0;
        int placed = 0;

        foreach (string id in ids)
        {
            HexCoord? spot = null;
            for (int q = col; q < col + 12; q++)
            {
                var c = new HexCoord(q, row);
                if (_state.Cell(c) != null && _state.Cell(c).Building == null)
                {
                    spot = c;
                    break;
                }
            }

            if (spot == null)
                continue;

            _state.SpawnBuilding(id, PlayerId.Player, spot.Value);
            Vector3 w = HexLayout.ToWorld(spot.Value);
            sumX += w.X;
            sumZ += w.Z;
            placed++;
            col = spot.Value.Q + 2;
        }

        _map.SyncEntities();
        _map.RefreshZones();
        _map.DumpBuildingBounds(_state);

        if (placed > 0)
        {
            _rig.FocusOn(new Vector3(sumX / placed, 0.6f, sumZ / placed));
            _rig.FrameMap(7.5f);
            _rig.SetPitch(58f);
            _rig.SetVerticalOffset(-1f);
        }
    }

    /// <summary>点一个己方可建造的空格，打开建造菜单；带 --build-apply 时再真的建一座。</summary>
    private async void DemoBuildFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        foreach (Cell c in _state.Cells.Values)
        {
            if (_state.CanBuildAt(c.Coord, PlayerId.Player))
            {
                spot = c.Coord;
                break;
            }
        }

        if (spot == null)
        {
            GD.Print("[Build] 找不到可建造的格子");
            return;
        }

        Vector2 p = _rig.Camera.UnprojectPosition(HexLayout.ToWorld(spot.Value, HexMapView.TileTop));
        SendMotion(p, p);
        UpdateHover(p);
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);

        OnLeftClick(p);
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Build] clicked={spot.Value} target={_buildTarget}");

        if (!OS.GetCmdlineUserArgs().Contains("--build-apply"))
            return;

        OnBuildClicked("city");
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        BuildingInst built = _state.BuildingAt(spot.Value);
        GD.Print($"[Build] built={built?.DefId} at={built?.Coord} " +
                 $"onTarget={(built != null && built.Coord == spot.Value)} " +
                 $"gold={_state.Player.Gold}");

        GetTree().Quit(built != null && built.Coord == spot.Value ? 0 : 1);
    }

    /// <summary>右键拖动旋转视角 + 左 Ctrl 复位，用于回归测试。</summary>
    private async void DemoOrbitFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);

        Vector3 before = _rig.Camera.GlobalPosition;
        GD.Print($"[Orbit] at0.6s {_rig.DebugState()}");

        Vector2 start = new(640f, 400f);
        SendButton(start, true, MouseButton.Right);
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);

        Vector2 prev = start;
        for (int i = 1; i <= 10; i++)
        {
            Vector2 p = start + new Vector2(i * 26f, 0f);
            SendMotion(p, prev);
            prev = p;
            await ToSignal(GetTree().CreateTimer(0.02), SceneTreeTimer.SignalName.Timeout);
        }

        SendButton(prev, false, MouseButton.Right);
        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);

        Vector3 afterDrag = _rig.Camera.GlobalPosition;
        GD.Print($"[Orbit] before={before} afterDrag={afterDrag} moved={before.DistanceTo(afterDrag):F2}");

        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);

        Input.ParseInputEvent(new InputEventKey
        {
            Keycode = Key.Ctrl,
            PhysicalKeycode = Key.Ctrl,
            Location = KeyLocation.Left,
            Pressed = true,
        });
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);

        Vector3 afterReset = _rig.Camera.GlobalPosition;
        GD.Print($"[Orbit] afterReset={afterReset} backToStart={before.DistanceTo(afterReset):F2}");
        GD.Print($"[Orbit] afterResetState {_rig.DebugState()}");
    }

    /// <summary>把第一张卡左右拖一段距离，验证换位。</summary>
    private async void DemoReorderFlow()
    {
        await ToSignal(GetTree().CreateTimer(0.7), SceneTreeTimer.SignalName.Timeout);

        string before = string.Join(",", _state.Player.Hand);
        if (!_hud.TryGetCardRect(0, out Rect2 rect))
        {
            GetTree().Quit(1);
            return;
        }

        Vector2 start = rect.Position + rect.Size * 0.5f;
        Vector2 end = start + new Vector2(392f, 0f);

        SendButton(start, true);
        await ToSignal(GetTree().CreateTimer(0.08), SceneTreeTimer.SignalName.Timeout);

        Vector2 prev = start;
        for (int i = 1; i <= 6; i++)
        {
            Vector2 p = start.Lerp(end, i / 6f);
            SendMotion(p, prev);
            prev = p;
            await ToSignal(GetTree().CreateTimer(0.03), SceneTreeTimer.SignalName.Timeout);
        }

        SendButton(end, false);
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        string after = string.Join(",", _state.Player.Hand);
        bool moved = before != after;
        GD.Print($"[DemoReorder] 前={before}");
        GD.Print($"[DemoReorder] 后={after}");
        GD.Print($"[DemoReorder] 换位={(moved ? "OK" : "FAIL")}");
        GetTree().Quit(moved ? 0 : 1);
    }

    private static void Click(Vector2 pos)
    {
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = pos,
            GlobalPosition = pos,
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
            Position = pos,
            GlobalPosition = pos,
        });
    }

    /// <summary>验证「物资运输」：抽 3 张 → 进入选牌弃牌 → 点一张弃掉。</summary>
    private async void DemoSupply()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        _state.Player.Gold = 999;
        _state.Player.Hand.Insert(0, "magic_supply");
        RefreshAll();

        int handBefore = _state.Player.Hand.Count;
        int deckBefore = _state.Player.Deck.Count;
        GD.Print($"[Supply] 出牌前 手牌={handBefore} 牌库={deckBefore} 金币={_state.Player.Gold}");

        PlayGlobalFromHand(0, GameDatabase.Card("magic_supply"));
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Supply] 出牌后 手牌={_state.Player.Hand.Count} 牌库={_state.Player.Deck.Count} " +
                 $"待弃={_state.PendingDiscard} 选牌模式={_discardMode}");

        if (_discardMode && _state.Player.Hand.Count > 0)
        {
            int discardBefore = _state.Player.Discard.Count;
            TryDiscardHandCard(0);
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

            GD.Print($"[Supply] 弃牌后 手牌={_state.Player.Hand.Count} 弃牌堆={discardBefore}→{_state.Player.Discard.Count} " +
                     $"待弃={_state.PendingDiscard} 选牌模式={_discardMode}");
        }
    }

    /// <summary>验证 FPV 无人机：冲刺 → 爆炸 → 造成伤害 → 自身消失。</summary>
    private async void DemoDrone()
    {
        await ToSignal(GetTree().CreateTimer(0.55), SceneTreeTimer.SignalName.Timeout);

        // 找一块己方控制区放无人机
        HexCoord? spot = null;
        for (int q = -7; q < 8 && spot == null; q++)
        {
            for (int r = 0; r < 10 && spot == null; r++)
            {
                var c = new HexCoord(q, r);
                Cell cell = _state.Cell(c);
                if (cell == null || cell.Building != null || cell.Units.Count > 0)
                    continue;
                if (cell.ControlPlayer && !cell.ControlEnemy)
                    spot = c;
            }
        }

        if (spot == null)
        {
            GD.Print("[Drone] 找不到部署点");
            return;
        }

        UnitInst drone = _state.SpawnUnit("fpvDrone", PlayerId.Player, spot.Value);

        // 旁边找一格放个敌方步兵当靶子
        HexCoord? foeCell = _state.Cells.Keys
            .Where(c => HexCoord.Distance(c, spot.Value) == 1)
            .Cast<HexCoord?>()
            .FirstOrDefault(c => _state.Cell(c!.Value).Building == null && _state.Cell(c.Value).Units.Count == 0);

        if (foeCell == null)
        {
            GD.Print("[Drone] 找不到目标格");
            return;
        }

        UnitInst foe = _state.SpawnUnit("infantry", PlayerId.Enemy, foeCell.Value);
        _map.SyncEntities();

        AudioManager.PlayDeploy("fpvDrone");
        GD.Print($"[Drone] 旋翼 0.0s = {_map.DebugUnitRotor(drone.Id)}");
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Drone] 旋翼 0.3s = {_map.DebugUnitRotor(drone.Id)}");

        // 镜头拉近看冲刺与爆炸
        _rig.FocusOn(HexLayout.ToWorld(foeCell.Value, 0.7f));
        _rig.FrameMap(4.2f);
        _rig.SetPitch(38f);
        _rig.SetVerticalOffset(0f);
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        int hpBefore = foe.Hp;
        int unitsBefore = _state.Units.Count(u => u.Owner == PlayerId.Player);
        GD.Print($"[Drone] 攻击前 敌方HP={hpBefore} 我方单位={unitsBefore} 敌方单位={_state.Units.Count(u => u.Owner == PlayerId.Enemy)}");

        _map.PlayUnitAttack(drone.Id);
        float delay = PlayAttackVfx(drone, foeCell.Value);
        await ToSignal(GetTree().CreateTimer(Mathf.Max(delay, 0.05f)), SceneTreeTimer.SignalName.Timeout);

        string log = _state.PerformAttack(drone, foeCell.Value);
        _map.SyncEntities();
        _map.RefreshZones();

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        bool foeAlive = _state.Units.Any(u => u.Id == foe.Id && u.Alive);
        bool droneAlive = _state.Units.Any(u => u.Id == drone.Id && u.Alive);
        GD.Print($"[Drone] 战报={log}");
        GD.Print($"[Drone] 攻击后 敌方HP={(foeAlive ? foe.Hp.ToString() : "阵亡")} 无人机存活={droneAlive} " +
                 $"我方单位={_state.Units.Count(u => u.Owner == PlayerId.Player)}");
    }
    /// <summary>验证「无人机站在敌方建筑同一格上攻击」也要自爆。</summary>
    private async void DemoDroneSameCell()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        // 场上没有敌方建筑就直接造一座防空炮当靶子
        BuildingInst foe = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Enemy && !b.IsDead && !b.IsHq);
        if (foe == null)
        {
            HexCoord? spot = _state.Cells.Keys
                .Where(c => _state.Cell(c) != null && _state.Cell(c).Building == null)
                .Cast<HexCoord?>()
                .FirstOrDefault();

            if (spot == null)
            {
                GD.Print("[DroneSame] 找不到空地放靶子");
                return;
            }

            foe = _state.SpawnBuilding("aaGun", PlayerId.Enemy, spot.Value);
        }

        // 把无人机直接放到那座建筑所在的格子上（单位可以站在建筑上）
        UnitInst drone = _state.SpawnUnit("fpvDrone", PlayerId.Player, foe.Coord);
        _map.SyncEntities();

        _rig.FocusOn(HexLayout.ToWorld(foe.Coord, 0.7f));
        _rig.FrameMap(4.0f);
        _rig.SetPitch(38f);
        _rig.SetVerticalOffset(0f);
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[DroneSame] 攻击前 无人机格={drone.Coord} 建筑格={foe.Coord} 建筑HP={foe.Hp}");

        string log = _state.PerformAttack(drone, foe.Coord);
        _map.SyncEntities();

        GD.Print($"[DroneSame] 战报={log}");
        GD.Print($"[DroneSame] 无人机存活={_state.Units.Any(u => u.Id == drone.Id && u.Alive)} " +
                 $"建筑HP={foe.Hp}");
    }
    /// <summary>直接把一方指挥部打爆，验证胜利/失败流程与音效。</summary>
    private async void DemoForceEnd(bool win)
    {
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);

        PlayerId loser = win ? PlayerId.Enemy : PlayerId.Player;
        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == loser && b.IsHq && !b.IsDead);
        if (hq == null)
        {
            GD.Print("[ForceEnd] 找不到要摧毁的指挥部");
            return;
        }

        hq.Hp = 0;
        _state.CheckVictory();
        GD.Print($"[ForceEnd] win={win} winner={_state.Winner} phase={_state.Phase}");

        _map.SyncEntities();
        RefreshAll();
        CheckGameOver();
    }
    /// <summary>验证拆除返还：建造 → 升级 → 拆除，打印金币变化与返还比例。</summary>
    private async void DemoDemolish()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        HexCoord? spot = null;
        for (int q = -2; q < 8 && spot == null; q++)
        {
            var c = new HexCoord(q, 5);
            if (_state.Cell(c) != null && _state.Cell(c).Building == null && _state.CanBuildAt(c, PlayerId.Player))
                spot = c;
        }

        if (spot == null)
        {
            GD.Print("[Demolish] 找不到可建造的格子");
            return;
        }

        _state.Player.Gold = 2000;
        BuildingInst b = _state.SpawnBuilding("gunTurret", PlayerId.Player, spot.Value);
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();

        int buildCost = b.Def.UpgradeCostAt(0);
        int upgradeCost = b.Def.UpgradeCostAt(1);
        b.Level = 2;

        int refund = BattleState.DemolishRefund(b);
        int before = _state.Player.Gold;
        GD.Print($"[Demolish] 建造={buildCost} 升级={upgradeCost} 本金={buildCost + upgradeCost} " +
                 $"返还={refund}（应≈{(buildCost + upgradeCost) * 0.65f:F0}）");

        SelectBuilding(b);
        OnDemolishClicked();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Demolish] 金币 {before} → {_state.Player.Gold}（+{_state.Player.Gold - before}）");
    }
    /// <summary>验证四张卡牌音效：打出 / 抽牌 / 弃牌 / 洗牌。</summary>
    private async void DemoCardSounds()
    {
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);

        // 1) 打出一张不需要地块的卡（全局卡最容易自动打出）
        int idx = _state.Player.Hand.FindIndex(id => GameDatabase.Card(id)?.KindEnum == CardKind.Global);
        if (idx < 0)
        {
            _state.Player.Gold = 999;
            _state.Player.Hand.Insert(0, "magic_boost");
            RefreshAll();
            idx = 0;
        }

        _state.Player.Gold = Mathf.Max(_state.Player.Gold, 999);
        OnCardClicked(idx);
        OnCardClicked(idx);
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        GD.Print("[CardSound] 已尝试打出一张卡");

        // 2) 结束回合 → 弃牌音效
        OnEndTurn();
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print("[CardSound] 已结束回合（弃牌 + 敌方行动 + 抽牌）");
    }
    /// <summary>打开手牌第一张的放大查看器，用于截图/验证。</summary>
    private async void DemoInspect()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        if (_state.Player.Hand.Count == 0)
            return;

        SevenDay.UI.CardInspectOverlay.Show(
            GameDatabase.Card(_state.Player.Hand[0]), _state.IsEtherealAt(0));

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--close-inspect") >= 0)
        {
            await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
            SevenDay.UI.CardInspectOverlay.Close();
            GD.Print("[Inspect] 已触发关闭动画");
        }
    }
    /// <summary>验证地形效果：在各类地形上放单位/建筑，打印生效数值。</summary>
    private async void DemoTerrain()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        var counts = new Dictionary<TerrainType, int>();
        foreach (Cell cc in _state.Cells.Values)
            counts[cc.Terrain] = counts.TryGetValue(cc.Terrain, out int n) ? n + 1 : 1;

        GD.Print($"[Map] {_state.Map.Id} 地形统计：" +
                 string.Join("，", counts.Select(kv => $"{kv.Key}={kv.Value}")));

        (TerrainType Terrain, string Name)[] kinds =
        {
            (TerrainType.Dirt, "泥土"),
            (TerrainType.Grass, "草地"),
            (TerrainType.Sand, "沙地"),
            (TerrainType.Rock, "岩地"),
            (TerrainType.Snow, "雪地"),
        };

        int guard = 0;
        foreach ((TerrainType terrain, string name) in kinds)
        {
            HexCoord? spot = null;
            foreach (Cell c in _state.Cells.Values)
            {
                if (c.Terrain == terrain && c.Building == null && c.Units.Count == 0 &&
                    c.Terrain != TerrainType.Water)
                {
                    spot = c.Coord;
                    break;
                }
            }

            if (spot == null)
            {
                // 地图上没有这种地形：临时把一块空地改成它来测
                foreach (Cell c in _state.Cells.Values)
                {
                    if (c.Building == null && c.Units.Count == 0 && c.Terrain != TerrainType.Water)
                    {
                        c.Terrain = terrain;
                        spot = c.Coord;
                        break;
                    }
                }
            }

            if (spot == null)
            {
                GD.Print($"[Terrain] {name}: 找不到可用格子");
                continue;
            }

            UnitInst u = _state.SpawnUnit("infantry", PlayerId.Player, spot.Value);
            int move = _state.MoveStepsOf(u);
            int range = _state.AttackRangeOf(u);
            GD.Print($"[Terrain] {name}: 步兵 移动={move}（基础 {u.Def.Move}） 射程={range}（基础 {u.Def.Range}）");

            if (true)
            {
                // 同一格再放一座城市，检查收入 / 控制半径
                BuildingInst b = _state.SpawnBuilding("city", PlayerId.Player, spot.Value);
                if (b != null)
                {
                    GD.Print($"[Terrain] {name}: 城市 收入={_state.IncomeOf(b)} 控制半径={b.ControlRadius}");
                }
            }

            _map.SyncEntities();
            _map.RefreshZones();
            RefreshAll();
        }

        GD.Print("[Terrain] 完成");
    }
    /// <summary>验证收入飘字：造两座城市后立即触发一轮玩家收入。</summary>
    private async void DemoIncome()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        if (hq != null)
        {
            int made = 0;
            foreach (HexCoord c in HexCoord.Spiral(hq.Coord, 2))
            {
                if (c == hq.Coord || _state.Cell(c)?.Building != null)
                    continue;

                _state.SpawnBuilding("city", PlayerId.Player, c);
                if (++made >= 2)
                    break;
            }
        }

        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        ShowIncomePopups(PlayerId.Player);
        GD.Print("[Income] 已触发收入飘字");
    }
    /// <summary>验证出兵：造 Lv3 兵营/机坪，选中兵营并在本格出兵。</summary>
    private async void DemoSpawn()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        if (hq == null)
            return;

        var spots = HexCoord.Spiral(hq.Coord, 2)
            .Where(c => _state.Cell(c)?.Building == null && c != hq.Coord)
            .Take(2)
            .ToList();
        if (spots.Count < 2)
            return;

        _state.Player.Gold = 2000;
        BuildingInst barracks = _state.SpawnBuilding("barracks", PlayerId.Player, spots[0]);
        BuildingInst helipad = _state.SpawnBuilding("helipad", PlayerId.Player, spots[1]);
        barracks.Level = 3;
        barracks.Hp = barracks.MaxHp;
        helipad.Level = 3;
        helipad.Hp = helipad.MaxHp;

        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();

        SelectBuilding(barracks);
        GD.Print($"[Spawn] 兵营可出：{string.Join(",", BattleState.SpawnOptions(barracks))}");
        GD.Print($"[Spawn] 机坪可出：{string.Join(",", BattleState.SpawnOptions(helipad))}");

        int before = _state.Player.Gold;
        OnSpawnClicked("unit_tank");
        GD.Print($"[Spawn] 出兵后金币 {before} → {_state.Player.Gold}；本格单位数={_state.Cell(barracks.Coord).Units.Count}");
        GD.Print($"[Spawn] 再出一次：" + (_state.CanSpawnFrom(barracks, "unit_tank", out string why) ? "允许" : why));

        UnitInst fresh = _state.Cell(barracks.Coord).Units.LastOrDefault();
        if (fresh != null)
        {
            GD.Print($"[Spawn] 新兵状态 moved={fresh.Moved} attacked={fresh.Attacked} 可移动格数={_state.MoveRange(fresh).Count}");
        }

        RefreshAll();
        RefreshSpawnPanel(barracks);
        GD.Print($"[Spawn] 选中兵营后 面板可见={_hud.SpawnVisible}");

        // 在出兵面板范围内点一下，验证点击不会穿透到地图格子
        if (_hud.SpawnVisible)
        {
            Vector2 panelCenter = _hud.SpawnPanelGlobalCenter();
            SendClick(panelCenter);
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            GD.Print($"[Spawn] 面板内点击后 选中建筑={( _selBuilding != null ? _selBuilding.DefId : "无")}（应仍为 barracks）");
        }

        // 点一个没有建筑的格子，面板应自动收起
        _selBuilding = null;
        RefreshSpawnPanel(_selBuilding);
        GD.Print($"[Spawn] 取消选中后面板可见={_hud.SpawnVisible}");
    }
    /// <summary>验证升级后出兵面板立即刷新：兵营从 Lv1 升到 Lv3。</summary>
    private async void DemoSpawnUpgrade()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        if (hq == null)
            return;

        HexCoord? spot = HexCoord.Spiral(hq.Coord, 2)
            .FirstOrDefault(c => c != hq.Coord && _state.Cell(c)?.Building == null);
        if (spot == null)
            return;

        _state.Player.Gold = 5000;
        BuildingInst barracks = _state.SpawnBuilding("barracks", PlayerId.Player, spot.Value);
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        SelectBuilding(barracks);

        GD.Print($"[Upgrade] Lv{barracks.Level} 可出：{string.Join(",", BattleState.SpawnOptions(barracks))}");

        OnUpgradeClicked();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Upgrade] Lv{barracks.Level} 可出：{string.Join(",", BattleState.SpawnOptions(barracks))} 面板可见={_hud.SpawnVisible}");

        OnUpgradeClicked();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Upgrade] Lv{barracks.Level} 可出：{string.Join(",", BattleState.SpawnOptions(barracks))} 面板可见={_hud.SpawnVisible}");
    }
    /// <summary>复现：Lv1 兵营只有一个出兵项，检查是否可点、点击是否穿透。</summary>
    private async void DemoSpawnLv1()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        HexCoord? spot = HexCoord.Spiral(hq.Coord, 2)
            .FirstOrDefault(c => c != hq.Coord && _state.Cell(c)?.Building == null);

        _state.Player.Gold = 320;
        BuildingInst barracks = _state.SpawnBuilding("barracks", PlayerId.Player, spot.Value);
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        SelectBuilding(barracks);

        foreach (string cardId in BattleState.SpawnOptions(barracks))
        {
            bool ok = _state.CanSpawnFrom(barracks, cardId, out string why);
            GD.Print($"[Lv1] {cardId} ready={ok} 原因={(ok ? "-" : why)} 金币={_state.Player.Gold} 花费={BattleState.UnitCost(cardId)}");
        }

        GD.Print($"[Lv1] 面板可见={_hud.SpawnVisible} 面板矩形={_hud.SpawnPanelRect()}");

        GD.Print($"[Lv1] 按钮数量={_hud.SpawnButtonCount()}（应为 1）");

        // 点第一个出兵按钮：应扣费并原地生成
        Rect2 btnRect = _hud.SpawnButtonRect(0);
        SendClick(btnRect.Position + btnRect.Size * 0.5f);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Lv1] 点击后：金币={_state.Player.Gold} 本格单位={_state.Cell(barracks.Coord).Units.Count} " +
                 $"选中建筑={(_selBuilding != null ? _selBuilding.DefId : "无")} 面板可见={_hud.SpawnVisible}");
    }
    /// <summary>出兵面板外观验证：Lv3 兵营，一行三个，并点击面板空白处验证不穿透。</summary>
    private async void DemoSpawnView()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        HexCoord? spot = HexCoord.Spiral(hq.Coord, 2)
            .FirstOrDefault(c => c != hq.Coord && _state.Cell(c)?.Building == null);

        _state.Player.Gold = 2000;
        BuildingInst barracks = _state.SpawnBuilding("barracks", PlayerId.Player, spot.Value);
        barracks.Level = 3;
        barracks.Hp = barracks.MaxHp;

        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        SelectBuilding(barracks);

        GD.Print($"[View] 面板可见={_hud.SpawnVisible} 按钮数={_hud.SpawnButtonCount()} 矩形={_hud.SpawnPanelRect()}");

        // 悬停到第二个按钮上，验证放大动画
        Rect2 hoverRect = _hud.SpawnButtonRect(1);
        Input.WarpMouse(hoverRect.Position + hoverRect.Size * 0.5f);
        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-click") >= 0)
        {
            Input.WarpMouse(new Vector2(640f, 40f));   // 移开鼠标，避免 tooltip 挡住截图
            return;
        }

        // 面板中心点一下（这块区域原本会被牌堆图标吃掉）
        SendClick(_hud.SpawnPanelGlobalCenter());
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[View] 中心点击后：面板={_hud.SpawnVisible} 选中={(_selBuilding != null ? _selBuilding.DefId : "无")}");
    }
    /// <summary>验证出兵面板一行三个 + 纵向滚动（塞 6 项）。</summary>
    private async void DemoSpawnOverflow()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        var options = new List<(string CardId, string Name, int Cost, string Icon, bool Ready, string Tip)>();
        string[] cards = { "unit_infantry", "unit_fpv_drone", "unit_tank", "unit_helicopter", "unit_fighter", "unit_missile_launcher" };
        foreach (string cardId in cards)
        {
            CardDef card = GameDatabase.Card(cardId);
            if (card == null)
                continue;

            options.Add((cardId, Loc.Name(card.NameZh, card.NameEn), card.Cost,
                "res://gameAsset/textures/cardface/cardface_" + card.Unit + ".png", true, ""));
        }

        _hud.ShowSpawnPanel("出兵（溢出测试 6 项）", options);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Overflow] 按钮数={_hud.SpawnButtonCount()} 矩形={_hud.SpawnPanelRect()}");
    }
    /// <summary>验证新出兵表：兵营 Lv1-Lv3、机坪 Lv1-Lv3。</summary>
    private async void DemoSpawnTable()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        var spots = HexCoord.Spiral(hq.Coord, 2)
            .Where(c => c != hq.Coord && _state.Cell(c)?.Building == null)
            .Take(2)
            .ToList();

        _state.Player.Gold = 5000;
        BuildingInst barracks = _state.SpawnBuilding("barracks", PlayerId.Player, spots[0]);
        BuildingInst helipad = _state.SpawnBuilding("helipad", PlayerId.Player, spots[1]);
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();

        for (int lv = 1; lv <= 3; lv++)
        {
            barracks.Level = lv;
            GD.Print($"[Table] 兵营 Lv{lv}: {string.Join(",", BattleState.SpawnOptions(barracks))}");
        }

        for (int lv = 1; lv <= 3; lv++)
        {
            helipad.Level = lv;
            GD.Print($"[Table] 机坪 Lv{lv}: {string.Join(",", BattleState.SpawnOptions(helipad))}");
        }

        barracks.Level = 3;
        SelectBuilding(barracks);
        GD.Print($"[Table] 满级兵营按钮数={_hud.SpawnButtonCount()}");

        foreach (string cardId in BattleState.SpawnOptions(barracks))
        {
            bool ok = _state.CanSpawnFrom(barracks, cardId, out string why);
            GD.Print($"[Table] {cardId} 可出兵={ok} {(ok ? "" : "原因=" + why)} 花费={BattleState.UnitCost(cardId)} 金币={_state.Player.Gold}");
        }
    }
    /// <summary>验证导弹袭击打出后进消耗堆（而不是弃牌堆）。</summary>
    private async void DemoMissileExhaust()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        CardDef card = GameDatabase.Card("magic_missile");
        GD.Print($"[Missile] 卡牌定义 Exhaust={card?.Exhaust} Retain={card?.Retain}");

        _state.Player.Gold = 2000;   // 保证两张都打得起

        // 模拟发射井产出一张，验证「只有产出的那张消耗」
        _state.Player.ProducedExhaust.Add("magic_missile");

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Enemy && b.IsHq);
        HexCoord target = hq.Coord.Neighbor(1);
        if (_state.Cell(target)?.Building != null)
            target = hq.Coord.Neighbor(3);

        _state.SpawnUnit("infantry", PlayerId.Enemy, target);
        _map.SyncEntities();

        _state.Player.Hand.Add("magic_missile");
        RefreshAll();

        int handCount = _state.Player.Hand.Count;
        int exhaustBefore = _state.Player.Exhaust.Count;
        int discardBefore = _state.Player.Discard.Count;

        bool played = TryPlayCardAt(handCount - 1, target);
        GD.Print($"[Missile] 尝试打出={played}");
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Missile] 打出后 LastPlayedExhaust={_state.LastPlayedExhaust}");
        GD.Print($"[Missile] 消耗堆 {exhaustBefore} → {_state.Player.Exhaust.Count}；弃牌堆 {discardBefore} → {_state.Player.Discard.Count}");

        // 再打一张「卡组里的」同名牌，应该进弃牌堆
        int exhaust2 = _state.Player.Exhaust.Count;
        int discard2 = _state.Player.Discard.Count;
        _state.SpawnUnit("infantry", PlayerId.Enemy, target);   // 换一个新的靶子
        _map.SyncEntities();
        _state.Player.Hand.Add("magic_missile");
        RefreshAll();
        bool played2 = TryPlayCardAt(_state.Player.Hand.Count - 1, target);
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Missile] 普通同名卡 打出={played2} LastPlayedExhaust={_state.LastPlayedExhaust} " +
                 $"消耗堆 {exhaust2} → {_state.Player.Exhaust.Count}，弃牌堆 {discard2} → {_state.Player.Discard.Count}");
    }
    /// <summary>只把「发射井产出的导弹卡」放进手牌，用来截图看角标。</summary>
    private async void DemoMissileBadge()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        _state.Player.Hand.Add("magic_missile");
        _state.Player.ProducedExhaust.Add("magic_missile");
        RefreshAll();
        GD.Print($"[Badge] 手牌末张是否产出消耗={_state.IsProducedExhaustAt(_state.Player.Hand.Count - 1)}");
    }
    /// <summary>验证右键关闭出兵面板。</summary>
    private async void DemoSpawnRightClick()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        HexCoord? spot = HexCoord.Spiral(hq.Coord, 2)
            .FirstOrDefault(c => c != hq.Coord && _state.Cell(c)?.Building == null);

        BuildingInst barracks = _state.SpawnBuilding("barracks", PlayerId.Player, spot.Value);
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        SelectBuilding(barracks);

        GD.Print($"[RightClick] 选中后 面板={_hud.SpawnVisible} 建筑={(_selBuilding != null ? _selBuilding.DefId : "无")}");

        Vector2 pos = new(640f, 300f);
        Input.WarpMouse(pos);
        SendButton(pos, true, MouseButton.Right);
        SendButton(pos, false, MouseButton.Right);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[RightClick] 右键后 面板={_hud.SpawnVisible} 建筑={(_selBuilding != null ? _selBuilding.DefId : "无")}");
    }
    private void CaptureProduceButton(string expected)
    {
        GD.Print($"[Produce] 按钮文案={_hud.DebugProduceText()}（期望含 {expected}）");
    }
    /// <summary>验证建筑产卡开关：导弹井关掉后不再发导弹卡。</summary>
    private async void DemoProduceToggle()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        HexCoord? spot = HexCoord.Spiral(hq.Coord, 2)
            .FirstOrDefault(c => c != hq.Coord && _state.Cell(c)?.Building == null);

        _state.Player.Gold = 3000;
        BuildingInst silo = _state.SpawnBuilding("missileSilo", PlayerId.Player, spot.Value);
        silo.Level = 3;                       // 每回合都产
        silo.Hp = silo.MaxHp;
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();

        int Before() => _state.Player.Hand.Count(x => x == "magic_missile");

        // 开：产卡（先清手牌，避免手牌上限挡住产卡）
        silo.ProduceEnabled = true;
        _state.Player.Hand.Clear();
        _state.BeginTurn(PlayerId.Player);
        GD.Print($"[Produce] 开启时导弹卡={Before()}");

        // 关：不产卡
        silo.ProduceEnabled = false;
        _state.Player.Hand.Clear();
        _state.BeginTurn(PlayerId.Player);
        GD.Print($"[Produce] 关闭时导弹卡={Before()}");

        // 界面：先看「关」，再打开看「开」
        SelectBuilding(silo);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        CaptureProduceButton("关");

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--keep-off") >= 0)
            return;

        silo.ProduceEnabled = true;
        ShowBuildingInfo(silo);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        CaptureProduceButton("开");
    }
    /// <summary>验证建筑产卡飞行动画：兵营发一张兵种卡，从建筑飞到手牌。</summary>
    private async void DemoProduceFly()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        HexCoord? spot = HexCoord.Spiral(hq.Coord, 2)
            .FirstOrDefault(c => c != hq.Coord && _state.Cell(c)?.Building == null);

        BuildingInst barracks = _state.SpawnBuilding("barracks", PlayerId.Player, spot.Value);
        _map.SyncEntities();
        _map.RefreshZones();

        _state.Player.Hand.Clear();
        _state.BeginTurn(PlayerId.Player);
        RefreshAll();

        GD.Print($"[Fly] 产出记录={_state.ProducedThisTurn.Count} 手牌={_state.Player.Hand.Count}");
        PlayProduceAnimations(0.1f);
    }
    /// <summary>验证打出一张牌后，同名牌不会少、手牌不留空位。</summary>
    private async void DemoPlayDuplicate()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        // 手里放三张同名步兵，再放一张坦克
        _state.Player.Hand.Clear();
        _state.Player.Hand.Add("unit_infantry");
        _state.Player.Hand.Add("unit_tank");
        _state.Player.Hand.Add("unit_infantry");
        _state.Player.Hand.Add("unit_infantry");
        _state.Player.Gold = 2000;
        RefreshAll();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Dup] 打出前 手牌={string.Join(",", _state.Player.Hand)} 可见卡={_hud.VisibleHandCards()}/{_hud.HandCardCount()}");

        HexCoord? spot = HexCoord.Spiral(_state.Buildings.First(b => b.Owner == PlayerId.Player && b.IsHq).Coord, 2)
            .FirstOrDefault(c => c != _state.Buildings.First(b => b.Owner == PlayerId.Player && b.IsHq).Coord);
        TryPlayCardAt(0, spot.Value);
        await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Dup] 打出后 手牌={string.Join(",", _state.Player.Hand)} 可见卡={_hud.VisibleHandCards()}/{_hud.HandCardCount()}");
    }

    /// <summary>验证手牌满时建筑不再产卡。</summary>
    private async void DemoProduceFull()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        HexCoord? spot = HexCoord.Spiral(hq.Coord, 2).FirstOrDefault(c => c != hq.Coord && _state.Cell(c)?.Building == null);
        BuildingInst barracks = _state.SpawnBuilding("barracks", PlayerId.Player, spot.Value);
        _map.SyncEntities();
        _map.RefreshZones();

        _state.Player.Hand.Clear();
        for (int i = 0; i < 10; i++)
            _state.Player.Hand.Add("unit_infantry");   // 顶满 10 张

        _state.BeginTurn(PlayerId.Player);
        GD.Print($"[Full] 手牌满时产卡后手牌数={_state.Player.Hand.Count} 产出记录={_state.ProducedThisTurn.Count}");
    }
    /// <summary>验证同名牌里「打出普通的那张，不会误删虚无那张」。</summary>
    private async void DemoPlayEthereal()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        HexCoord hqCoord = _state.Buildings.First(b => b.Owner == PlayerId.Player && b.IsHq).Coord;
        HexCoord? spot = HexCoord.Spiral(hqCoord, 2).FirstOrDefault(c => c != hqCoord && _state.Cell(c)?.Building == null);

        _state.Player.Gold = 3000;
        _state.Player.Hand.Clear();
        _state.Player.Ethereal.Clear();
        _state.Player.Hand.Add("unit_infantry");      // 0：虚无那张
        _state.Player.Ethereal.Add("unit_infantry");
        _state.Player.Hand.Add("unit_infantry");      // 1：普通那张
        RefreshAll();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Eth] 打出前 0是虚无={_state.IsEtherealAt(0)} 1是虚无={_state.IsEtherealAt(1)}");

        // 打出普通那张（下标 1）
        TryPlayCardAt(1, spot.Value);
        await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Eth] 打出(普通)后 手牌={string.Join(",", _state.Player.Hand)} 虚无数={_state.Player.Ethereal.Count} " +
                 $"剩余是虚无={(_state.Player.Hand.Count > 0 && _state.IsEtherealAt(0))}");

        // 再来一次：这次打出虚无那张（下标 0）
        _state.Player.Hand.Clear();
        _state.Player.Ethereal.Clear();
        _state.Player.Hand.Add("unit_infantry");
        _state.Player.Ethereal.Add("unit_infantry");
        _state.Player.Hand.Add("unit_infantry");
        RefreshAll();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        TryPlayCardAt(0, spot.Value);
        await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Eth] 打出(虚无)后 手牌={string.Join(",", _state.Player.Hand)} 虚无数={_state.Player.Ethereal.Count} " +
                 $"剩余是虚无={(_state.Player.Hand.Count > 0 && _state.IsEtherealAt(0))}");
    }
    /// <summary>验证数字键选牌与 E 键结束回合。</summary>
    private async void DemoKeybinds()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Key] 手牌数={_state.Player.Hand.Count} 选中={_selCard}");

        PressKey(Key.Key3);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Key] 按 3 后 选中={_selCard}（期望 2）");

        PressKey(Key.Key0);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Key] 按 0 后 选中={_selCard}（手牌不足 10 张应保持 2）");

        int before = _state.TurnNumber;
        PressKey(Key.E);
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Key] 按 E 后 阶段={_state.Phase} 回合 {before}→{_state.TurnNumber}");
    }

    private static void PressKey(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
    }
    /// <summary>验证虚无卡打出后，弃牌堆里仍然带「虚无」关键字。</summary>
    private async void DemoEtherealPlayKeep()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        HexCoord hq = _state.Buildings.First(b => b.Owner == PlayerId.Player && b.IsHq).Coord;
        HexCoord? spot = HexCoord.Spiral(hq, 2).FirstOrDefault(c => c != hq && _state.Cell(c)?.Building == null);

        _state.Player.Gold = 2000;
        _state.Player.Hand.Clear();
        _state.Player.Ethereal.Clear();
        _state.Player.Hand.Add("unit_infantry");
        _state.Player.Ethereal.Add("unit_infantry");    // 这张是建筑发的虚无卡
        RefreshAll();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        TryPlayCardAt(0, spot.Value);
        await ToSignal(GetTree().CreateTimer(0.9), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[EthKeep] 弃牌堆={string.Join(",", _state.Player.Discard)} " +
                 $"虚无标记={string.Join(",", _state.Player.EtherealDiscarded)} " +
                 $"LastPlayedEthereal={_state.LastPlayedEthereal}");

        OnPileClicked((int)PileView.PileKind.Discard);
    }
    /// <summary>验证虚无标记跨牌堆传递：弃牌堆 → 抽牌堆 → 手牌。</summary>
    private async void DemoEtherealCycle()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        PlayerState me = _state.Player;
        me.Hand.Clear();
        me.Ethereal.Clear();
        me.Deck.Clear();
        me.Discard.Clear();
        me.EtherealDiscarded.Clear();
        me.EtherealDeck.Clear();

        // 弃牌堆里有一张「虚无」步兵
        me.Discard.Add("unit_infantry");
        me.EtherealDiscarded.Add("unit_infantry");

        GD.Print($"[Cycle] 洗牌前 弃牌={me.Discard.Count} 虚无标记(弃)={me.EtherealDiscarded.Count}");

        // 牌库空 → 触发洗回 → 再抽一张
        _state.DrawCard(me, 1);
        RefreshAll();

        GD.Print($"[Cycle] 抽牌后 手牌={string.Join(",", me.Hand)} 手牌虚无={me.Ethereal.Count} " +
                 $"牌库虚无={me.EtherealDeck.Count} 是虚无={me.Hand.Count > 0 && _state.IsEtherealAt(0)}");

        // 再验证：手牌这张丢回弃牌堆
        _state.DiscardOne(me, 0);
        GD.Print($"[Cycle] 弃掉后 弃牌={string.Join(",", me.Discard)} 虚无标记(弃)={me.EtherealDiscarded.Count}");
    }
    /// <summary>验证「产出消耗」标记也跨堆保留：手牌→弃牌→洗回→手牌。</summary>
    private async void DemoKeywordCycle()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        PlayerState me = _state.Player;
        me.Hand.Clear();
        me.Ethereal.Clear();
        me.ProducedExhaust.Clear();
        me.Deck.Clear();
        me.Discard.Clear();
        me.EtherealDiscarded.Clear();
        me.EtherealDeck.Clear();
        me.ProducedExhaustDiscarded.Clear();
        me.ProducedExhaustDeck.Clear();

        me.Hand.Add("magic_missile");
        me.ProducedExhaust.Add("magic_missile");    // 发射井产出的那张

        _state.DiscardOne(me, 0);
        GD.Print($"[Kw] 弃掉后 弃牌={string.Join(",", me.Discard)} 产出消耗标记(弃)={me.ProducedExhaustDiscarded.Count}");

        _state.DrawCard(me, 1);
        RefreshAll();
        GD.Print($"[Kw] 洗回并抽到 手牌={string.Join(",", me.Hand)} 产出消耗标记(手)={me.ProducedExhaust.Count} " +
                 $"是产出消耗={(_state.Player.Hand.Count > 0 && _state.IsProducedExhaustAt(0))}");
    }
    /// <summary>验证「汇率调节」：花费 0，获得现有资金 5%。</summary>
    private async void DemoExchangeRate()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        _state.Player.Hand.Clear();
        _state.Player.Hand.Add("magic_exchange_rate");
        _state.Player.Gold = 1000;
        RefreshAll();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        int before = _state.Player.Gold;
        string result = _state.PlayGlobalCard(_state.Player, "magic_exchange_rate", null, out string error);
        RefreshAll();

        GD.Print($"[Exchange] 结果={result} 错误={error} 金币 {before} → {_state.Player.Gold}");
    }
    /// <summary>验证「战场扫荡」：从消耗堆取回一张。</summary>
    private async void DemoReclaim()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        PlayerState me = _state.Player;
        me.Hand.Clear();
        me.Exhaust.Clear();
        me.EtherealExhausted.Clear();
        me.Ethereal.Clear();

        me.Hand.Add("magic_reclaim");
        me.Gold = 1000;
        me.Exhaust.Add("magic_airstrike");
        me.Exhaust.Add("unit_tank");
        me.EtherealExhausted.Add("unit_tank");    // 这张在消耗堆里带虚无
        me.Ethereal.Add("unit_tank");
        me.Ethereal.RemoveAt(me.Ethereal.Count - 1);   // 只是历史标记，手牌里没有

        RefreshAll();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        string result = _state.PlayGlobalCard(me, "magic_reclaim", null, out string error);
        GD.Print($"[Reclaim] 打出={result} 错误={error} 待选={_state.PendingReclaimExhaust} 金币={me.Gold}");

        if (_state.PendingReclaimExhaust)
            OpenReclaimPicker();

        await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);

        int handIndex = _state.ReclaimFromExhaust(me, 1);   // 取回坦克（带虚无标记的那张）
        _hud.ClosePile();
        RefreshAll();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[Reclaim] 取回后 手牌={string.Join(",", me.Hand)} 消耗堆={string.Join(",", me.Exhaust)} " +
                 $"虚无={string.Join(",", me.Ethereal)} 手牌下标={handIndex}");
    }
    /// <summary>验证牌堆查看器层级：打开选择面板时飞牌不能盖在它上面。</summary>
    private async void DemoPileLayers()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);

        PlayerState me = _state.Player;
        me.Hand.Clear();
        me.Exhaust.Clear();
        me.Hand.Add("magic_reclaim");
        me.Gold = 1000;
        me.Exhaust.Add("magic_airstrike");
        me.Exhaust.Add("unit_tank");
        RefreshAll();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        _state.PlayGlobalCard(me, "magic_reclaim", null, out _);
        if (_state.PendingReclaimExhaust)
            OpenReclaimPicker();

        // 面板开着的同时让一张卡从消耗堆位置飞出来，检查层级
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
        _hud.PlayProduceAnimation(GameDatabase.Card("unit_tank"),
            _hud.PileCenter(PileView.PileKind.Exhaust), 0, 0f);
    }
    private void DemoDeploy()
    {
        BuildingInst hq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        if (hq == null)
            return;

        int placed = 0;
        foreach (HexCoord c in HexCoord.Spiral(hq.Coord, 2))
        {
            if (c == hq.Coord || _state.Cell(c)?.Building != null)
                continue;
            _state.SpawnUnit(placed % 2 == 0 ? "infantry" : "tank", PlayerId.Player, c);
            if (++placed >= 4)
                break;
        }

        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();

        UnitInst first = _state.Units.FirstOrDefault(u => u.Owner == PlayerId.Player);
        if (first != null)
            SelectUnit(first);
    }

    private void RunAutoStep()
    {
        if (_state == null || _state.Phase == BattlePhase.GameOver)
        {
            CheckGameOver();
            return;
        }

        string log = new AIController(_state, _state.Difficulty, _state.CurrentPlayer).RunTurn();
        GD.Print($"[AutoPlay] 回合{_state.TurnNumber} {_state.CurrentPlayer}: {log}");

        _map.SyncEntities();
        _map.RefreshZones();

        if (_state.Phase != BattlePhase.GameOver)
            _state.EndTurn();

        RefreshAll();
        CheckGameOver();

        if (_state.Phase == BattlePhase.GameOver)
        {
            GD.Print($"[AutoPlay] 结束：{_state.Winner}，共 {_state.TurnNumber} 回合");
            GetTree().Quit();
            return;
        }

        if (_state.TurnNumber >= 40)
        {
            GD.Print("[AutoPlay] 达到回合上限，结束测试");
            GetTree().Quit();
            return;
        }

        var timer = GetTree().CreateTimer(0.12);
        timer.Timeout += RunAutoStep;
    }

    private Vector3 MapCenter()
    {
        if (_state == null)
            return Vector3.Zero;
        float sx = 0, sz = 0;
        foreach (HexCoord c in _state.Cells.Keys)
        {
            Vector3 p = HexLayout.ToWorld(c);
            sx += p.X;
            sz += p.Z;
        }

        int n = Mathf.Max(1, _state.Cells.Count);
        return new Vector3(sx / n, 0, sz / n);
    }

    private void BuildEnvironment()
    {
        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightEnergy = 1.05f,
            // 黄昏的暖光：比正午更橙一点
            LightColor = new Color(1f, 0.84f, 0.64f),
            ShadowEnabled = true,
            RotationDegrees = new Vector3(-46, -34, 0),
        };
        AddChild(sun);

        // 火烧云天空盒
        var skyMaterial = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://gameAsset/shaders/sunset_sky.gdshader"),
        };
        var sky = new Sky
        {
            SkyMaterial = skyMaterial,
            RadianceSize = Sky.RadianceSizeEnum.Size128,
            ProcessMode = Sky.ProcessModeEnum.Realtime,
        };

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            // 环境光带一点晚霞的暖色，模型暗部不会是死灰
            AmbientLightColor = new Color(0.62f, 0.45f, 0.38f),
            AmbientLightEnergy = 0.85f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            TonemapExposure = 0.92f,
            TonemapWhite = 1.0f,
            // 轻雾增加纵深，颜色贴近地平线；但不要影响天空，否则整片橙
            FogEnabled = false,
            FogLightColor = new Color(0.95f, 0.62f, 0.42f),
            FogLightEnergy = 0.5f,
            FogDensity = 0.0025f,
            FogAerialPerspective = 0.1f,
            FogSkyAffect = 0f,
        };
        var we = new WorldEnvironment { Name = "WorldEnvironment", Environment = env };
        AddChild(we);
    }

    // ─────────────────────────── 特效 ───────────────────────────

    /// <summary>把本回合防御建筑的开火记录播成特效（曳光 + 命中/爆炸）。</summary>
    private void PlayVolleys()
    {
        if (_vfx == null || _state == null || _state.Volleys.Count == 0)
            return;

        float delay = 0.05f;
        foreach ((HexCoord from, HexCoord to, string kind) in _state.Volleys)
        {
            Vector3 a = HexLayout.ToWorld(from, HexMapView.TileTop + 0.55f);
            Vector3 b = HexLayout.ToWorld(to, HexMapView.TileTop + 0.45f);
            float d = delay;
            delay += 0.12f;

            if (kind == "aa")
            {
                // 防空炮：空中爆开的炮弹
                _vfx.Tracer(a, b + Vector3.Up * 1.4f, new Color("FFE9A8"), 0.03f);
                var timer = GetTree().CreateTimer(d);
                timer.Timeout += () => _vfx.Explosion(b + Vector3.Up * 1.5f, 0.55f);
            }
            else
            {
                // 炮台 / 指挥部：炮弹落地爆炸
                _vfx.MuzzleFlash(a, 0.5f);
                var timer = GetTree().CreateTimer(d);
                timer.Timeout += () => _vfx.Explosion(b, 0.75f);
            }
        }

        _state.Volleys.Clear();

        // 视图的清理延后到「炮弹落地」那一刻，避免单位在爆炸前就消失
        var sync = GetTree().CreateTimer(Mathf.Max(0.12f, delay));
        sync.Timeout += () => _map?.SyncEntities();
    }

    /// <summary>等待特效命中的这一击：命中瞬间才结算伤害、清尸体、刷新界面。</summary>
    private (UnitInst Unit, HexCoord Target)? _pendingAttack;

    private void ResolvePendingAttack()
    {
        if (_pendingAttack == null)
            return;

        (UnitInst attacker, HexCoord coord) = _pendingAttack.Value;
        _pendingAttack = null;

        if (attacker == null || !attacker.Alive)
            return;

        string log = _state.PerformAttack(attacker, coord);
        _hud.Toast(string.IsNullOrEmpty(log) ? "没有可攻击的目标" : log);

        foreach (UnitInst t in _state.UnitsAt(coord))
            _map.PunchUnit(t.Id);
        if (_state.BuildingAt(coord) is { } hitBuilding)
            _map.PunchBuilding(hitBuilding.Id);

        // 自杀式单位打完就没了：别再选中一个死掉的单位
        if (attacker.Alive)
        {
            SelectUnit(attacker);
        }
        else
        {
            ClearSelection();
            _map.SyncEntities();
        }

        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        CheckGameOver();
    }

    /// <summary>单位开火：步兵/直升机打子弹，坦克打炮弹，战斗机打导弹。</summary>
    private float PlayAttackVfx(UnitInst attacker, HexCoord target)
    {
        if (_vfx == null || attacker == null)
            return 0f;

        Vector3 from = _map.UnitBarAnchor(attacker) - Vector3.Up * 1.35f;
        Vector3 to = HexLayout.ToWorld(target, HexMapView.TileTop + 0.45f);

        switch (attacker.DefId)
        {
            case "fighter":
                // 从飞机身上发射一枚小导弹，飞到目标格再爆炸
                _vfx.MissileFromTo(from + Vector3.Up * 0.15f, to, 0.85f, 0.45f, 0.30f);
                return 0.30f;

            case "missileLauncher":
                // 导弹车：抛物线弹道
                _vfx.MissileFromTo(from + Vector3.Up * 0.35f, to, 0.95f, 0.55f, 0.45f, arc: 1.7f);
                return 0.45f;

            case "tank":
                _vfx.MuzzleFlash(from + Vector3.Up * 0.1f, 0.55f);
                _vfx.Tracer(from, to, new Color("FFC46A"), 0.045f);
                var shell = GetTree().CreateTimer(0.09);
                shell.Timeout += () => _vfx.Explosion(to, 0.85f);
                return 0.09f;

            case "helicopter":
                _vfx.MuzzleFlash(from, 0.4f);
                _vfx.Tracer(from, to, new Color("FFD98A"), 0.03f);
                var rocket = GetTree().CreateTimer(0.12);
                rocket.Timeout += () => _vfx.Explosion(to, 0.6f);
                return 0.12f;

            case "fpvDrone":
            {
                // 自杀式无人机：不开枪、只俯冲，撞上去才炸
                AudioManager.PlayMove(attacker.DefId);
                float dash = _map.DashUnitTo(attacker.Id, target, 0.36f);

                var boom = GetTree().CreateTimer(Mathf.Max(0.12f, dash));
                boom.Timeout += () =>
                {
                    _vfx.Explosion(to + Vector3.Up * 0.45f, 1.25f);
                    _vfx.Explosion(to + Vector3.Up * 0.1f, 0.8f);
                };

                return Mathf.Max(0.12f, dash) + 0.04f;
            }

            default:
                // 步兵等轻武器：连续几发曳光 + 命中火花
                for (int i = 0; i < 3; i++)
                {
                    float d = i * 0.06f;
                    var t = GetTree().CreateTimer(d);
                    t.Timeout += () =>
                    {
                        _vfx.MuzzleFlash(from, 0.3f);
                        _vfx.Tracer(from, to, new Color("FFE9A8"), 0.03f);
                    };

                    var hit = GetTree().CreateTimer(d + 0.05f);
                    float spread = 0.18f;
                    hit.Timeout += () => _vfx.BulletHit(
                        to + new Vector3((float)GD.RandRange(-spread, spread),
                            (float)GD.RandRange(0f, spread), (float)GD.RandRange(-spread, spread)),
                        0.45f);
                }

                return 0.05f;
        }
    }

    // ─────────────────────────── HUD 事件 ───────────────────────────

    private void WireHud()
    {
        _hud.CardClicked += OnCardClicked;
        _hud.CardDoubleClicked += OnCardDoubleClicked;
        _hud.CardDragPlayed += OnCardDragPlayed;
        _hud.CardDropped += OnCardDropped;
        _hud.CardReordered += OnCardReordered;
        _hud.EndTurnClicked += OnEndTurn;
        _hud.BuildClicked += OnBuildClicked;
        _hud.UpgradeClicked += OnUpgradeClicked;
        _hud.RepairClicked += OnRepairClicked;
        _hud.DemolishClicked += OnDemolishClicked;
        _hud.SpawnClicked += OnSpawnClicked;
        _hud.ProduceToggled += OnProduceToggled;
        _hud.ExtraFireClicked += OnExtraFireClicked;
        _hud.BuildHovered += OnBuildHovered;
        _hud.UpgradeHovered += OnUpgradeHovered;
        _hud.PreviewUnhovered += OnPreviewUnhovered;
        _hud.CancelClicked += () => { ClearSelection(); RefreshAll(); };
        _hud.PileClicked += OnPileClicked;
    }

    /// <summary>点底部牌堆：弹出查看器。</summary>
    private void OnPileClicked(int kind)
    {
        switch (kind)
        {
            case (int)PileView.PileKind.Discard:
            {
                // 打出后进弃牌堆的虚无卡，查看器里也要显示关键字
                List<string> ids = _state.Player.Discard;
                var etherealIndices = new List<int>();
                var pool = new List<string>(_state.Player.EtherealDiscarded);

                for (int i = 0; i < ids.Count; i++)
                {
                    int k = pool.IndexOf(ids[i]);
                    if (k < 0)
                        continue;

                    etherealIndices.Add(i);
                    pool.RemoveAt(k);
                }

                _hud.ShowPile("弃牌堆", ids, etherealIndices, BuildPileFlagIndices(ids, _state.Player.ProducedExhaustDiscarded));
                break;
            }
            case (int)PileView.PileKind.Exhaust:
            {
                // 消耗堆里被「虚无」耗掉的卡，查看器里也要显示关键字
                List<string> ids = _state.Player.Exhaust;
                var etherealIndices = new List<int>();
                var pool = new List<string>(_state.Player.EtherealExhausted);

                for (int i = 0; i < ids.Count; i++)
                {
                    int k = pool.IndexOf(ids[i]);
                    if (k < 0)
                        continue;

                    etherealIndices.Add(i);
                    pool.RemoveAt(k);
                }

                _hud.ShowPile("消耗堆", ids, etherealIndices);
                break;
            }
            default:
            {
                List<string> ids = _state.Player.Deck;
                var etherealIndices = new List<int>();
                var pool = new List<string>(_state.Player.EtherealDeck);

                for (int i = 0; i < ids.Count; i++)
                {
                    int k = pool.IndexOf(ids[i]);
                    if (k < 0)
                        continue;

                    etherealIndices.Add(i);
                    pool.RemoveAt(k);
                }

                _hud.ShowPile("抽牌堆", ids, etherealIndices, BuildPileFlagIndices(ids, _state.Player.ProducedExhaustDeck));
                break;
            }
        }
    }

    /// <summary>把「标记列表」映射成牌堆里对应的下标（按出现次数配对）。</summary>
    private static List<int> BuildPileFlagIndices(List<string> ids, List<string> flags)
    {
        var indices = new List<int>();
        var pool = new List<string>(flags);

        for (int i = 0; i < ids.Count; i++)
        {
            int k = pool.IndexOf(ids[i]);
            if (k < 0)
                continue;

            indices.Add(i);
            pool.RemoveAt(k);
        }

        return indices;
    }
    /// <summary>「战场扫荡」：从消耗堆里选一张拿回手牌。</summary>
    private void OpenReclaimPicker()
    {
        List<string> ids = _state.Player.Exhaust;
        if (ids.Count == 0)
        {
            _state.PendingReclaimExhaust = false;
            _hud.Toast("消耗堆是空的");
            return;
        }

        _hud.ShowPile("消耗堆", ids, null, null, index =>
        {
            int handIndex = _state.ReclaimFromExhaust(_state.Player, index);
            _hud.ClosePile();

            if (handIndex >= 0)
            {
                RefreshAll();
                CardDef def = GameDatabase.Card(_state.Player.Hand[handIndex]);
                _hud.PlayProduceAnimation(def, _hud.PileCenter(PileView.PileKind.Exhaust), handIndex, 0.05f);
                _hud.Toast($"取回 {def?.NameZh}");
            }
        });
    }
    /// <summary>选中这张导弹袭击卡对应的发射井（优先用产出它的那座）。</summary>
    private void SelectRelatedMissileSilo(string cardId)
    {
        // 手牌里这张卡是从哪座井产出的
        var entry = _state.Player.Produced.FirstOrDefault(p => p.Card == cardId);
        BuildingInst silo = null;

        if (entry.SiloId > 0)
            silo = _state.Buildings.FirstOrDefault(b => b.Id == entry.SiloId && !b.IsDead);

        // 兜底：自己任意一座还活着的井
        silo ??= _state.Buildings.FirstOrDefault(b =>
            b.Owner == PlayerId.Player && !b.IsDead && b.DefId == "missileSilo");

        if (silo == null)
            return;

        _selBuilding = silo;
        _map.SetSelectedUnit(-1);
        _map.SetSelectedBuilding(silo.Id);
        ShowBuildingInfo(silo);
    }

    private void OnCardClicked(int index)
    {
        if (_state.Phase != BattlePhase.PlayerTurn)
            return;

        if (index < 0 || index >= _state.Player.Hand.Count)
            return;

        // 「物资运输」的弃牌阶段：点哪张弃哪张
        if (_discardMode)
        {
            TryDiscardHandCard(index);
            return;
        }

        CardDef card = GameDatabase.Card(_state.Player.Hand[index]);
        if (card == null)
            return;

        if (_selCard == index)
        {
            // 再点一次：不需要地块的卡直接打出，其它卡取消选中
            if (card.KindEnum == CardKind.Global)
            {
                PlayGlobalFromHand(index, card);
                return;
            }

            _selCard = -1;
            _hud.SetSelectedCard(-1);
            ClearSelection();
            RefreshAll();
            return;
        }

        SelectCard(index, card);
    }

    private void SelectCard(int index, CardDef card)
    {
        _selCard = index;
        _hud.SetSelectedCard(index);
        _selUnit = null;
        _selBuilding = null;
        _hud.HideInfo();
        _hud.HideSpawnPanel();
        CloseBuildMenu();
        _map.ClearHints();

        // 导弹袭击卡：自动选中它关联的那座发射井，方便看是从哪飞出来的
        if (card.Effect == "missile")
            SelectRelatedMissileSilo(card.Id);

        if (card.KindEnum == CardKind.Unit)
        {
            var spots = _state.Cells.Values
                .Where(c => _state.CanDeployCard(card.Id, PlayerId.Player, c.Coord, out _))
                .Select(c => c.Coord);
            _map.ShowHints(spots, new Color("7FD35B"));
            _hud.Toast($"已选择「{card.NameZh}」：点击高亮格子，或把卡拖到格子上");
        }
        else if (card.KindEnum == CardKind.Strike)
        {
            if (card.Effect == "repair")
            {
                // 维修卡：高亮「有己方单位」的格子
                var mine = _state.Cells.Values
                    .Where(c => c.Units.Any(u => u.Alive && u.Owner == PlayerId.Player))
                    .Select(c => c.Coord);
                _map.ShowHints(mine, new Color("8FE36B"));
                _hud.Toast($"已选择「{card.NameZh}」：点击有己方单位的格子进行维修");
                return;
            }

            // 敌方指挥部不参与直接伤害卡（只能用部队推进解决），高亮时排除掉
            var enemies = _state.Cells.Values
                .Where(c => !(c.Building is { IsDead: false } b && b.IsHq && b.Owner == PlayerId.Enemy))
                .Where(c => c.Units.Any(u => u.Alive && u.Owner == PlayerId.Enemy) ||
                            (c.Building != null && c.Building.Owner == PlayerId.Enemy))
                .Select(c => c.Coord);
            _map.ShowHints(enemies, new Color("E8563F"));
            _hud.Toast($"已选择「{card.NameZh}」：点击要打击的格子（不能直接打指挥部）");
        }
        else
        {
            _hud.Toast($"已选择「{card.NameZh}」：再次点击 / 双击 / 向上拖出即可使用");
        }
    }

    /// <summary>双击卡牌：不需要地块的直接打出。</summary>
    private void OnCardDoubleClicked(int index)
    {
        if (_state.Phase != BattlePhase.PlayerTurn || index < 0 || index >= _state.Player.Hand.Count)
            return;

        CardDef card = GameDatabase.Card(_state.Player.Hand[index]);
        if (card == null)
            return;

        if (card.KindEnum == CardKind.Global)
        {
            PlayGlobalFromHand(index, card);
            return;
        }

        SelectCard(index, card);
        _hud.Toast($"「{card.NameZh}」需要指定地块：点高亮格或把卡拖过去");
    }

    /// <summary>向上拖出阈值：不需要地块的卡直接打出。</summary>
    private void OnCardDragPlayed(int index)
    {
        if (_state.Phase != BattlePhase.PlayerTurn || index < 0 || index >= _state.Player.Hand.Count)
            return;

        CardDef card = GameDatabase.Card(_state.Player.Hand[index]);
        if (card == null)
            return;

        if (card.KindEnum == CardKind.Global)
        {
            PlayGlobalFromHand(index, card);
            return;
        }

        SelectCard(index, card);
        _hud.Toast($"「{card.NameZh}」需要指定地块：点高亮格或把卡拖过去");
    }

    /// <summary>把卡拖到地图上松手：在该格打出。</summary>
    private void OnCardDropped(int index, Vector2 screenPos)
    {
        if (_state.Phase != BattlePhase.PlayerTurn)
            return;

        if (!_rig.TryPickGround(screenPos, HexMapView.TileTop, out Vector3 world))
            return;

        HexCoord coord = HexLayout.FromWorld(world);
        if (_state.Cell(coord) == null)
            return;

        _selCard = index;
        TryPlayCardAt(index, coord);
    }

    /// <summary>
    /// 选中「需要指定地块」的卡时，画一条从卡牌指向鼠标的箭头；
    /// 鼠标落在合法格上时箭头吸到格心并变绿。
    /// </summary>
    private void UpdateTargetingArrow()
    {
        if (_state == null || _hud == null || _rig == null)
            return;

        if (_state.Phase != BattlePhase.PlayerTurn || _selCard < 0 || _selCard >= _state.Player.Hand.Count)
        {
            _hud.HideArrow();
            return;
        }

        CardDef card = GameDatabase.Card(_state.Player.Hand[_selCard]);
        if (card == null || card.KindEnum == CardKind.Global)
        {
            _hud.HideArrow();
            return;
        }

        Vector2 mouse = _mouseScreen;
        Vector2 tip = mouse;
        bool valid = false;

        if (_rig.TryPickGround(mouse, HexMapView.TileTop, out Vector3 world))
        {
            HexCoord coord = HexLayout.FromWorld(world);
            Cell cell = _state.Cell(coord);
            if (cell != null)
            {
                if (card.KindEnum == CardKind.Unit)
                {
                    valid = _state.CanDeployCard(card.Id, PlayerId.Player, coord, out _);
                }
                else
                {
                    valid = cell.Units.Any(u => u.Alive && u.Owner == PlayerId.Enemy) ||
                            (cell.Building != null && cell.Building.Owner == PlayerId.Enemy);
                }

                // 吸到格子中心，指向更明确
                tip = _rig.Camera.UnprojectPosition(HexLayout.ToWorld(coord, HexMapView.TileTop + 0.06f));
            }
        }

        _hud.AimArrow(_selCard, tip, valid);

        if (OS.GetCmdlineUserArgs().Contains("--diag-arrow") && Time.GetTicksMsec() - _arrowDiagMs > 400)
        {
            _arrowDiagMs = Time.GetTicksMsec();
            GD.Print($"[Arrow] mouse={mouse} picked={_rig.TryPickGround(mouse, HexMapView.TileTop, out _)} " +
                     $"tip={tip} valid={valid} card={card.Id}/{card.KindEnum} hover={_hover}");
        }
    }

    private ulong _arrowDiagMs;

    public override void _Process(double delta)
    {
        UpdateTargetingArrow();
        UpdateHealthBars();
    }

    private readonly List<HealthBars.Bar> _barBuffer = new();

    /// <summary>血条：满血的普通单位不显示，受伤/选中/建筑才显示。</summary>
    private void UpdateHealthBars()
    {
        if (_state == null || _hud == null)
            return;

        // 选中的单位血量变化时，面板里的攻击力也要跟着实时刷新
        if (_selUnit != null && _selUnit.Alive && _selUnit.Hp != _infoUnitHp)
            ShowUnitInfo(_selUnit);

        _barBuffer.Clear();

        foreach (UnitInst u in _state.Units)
        {
            if (!u.Alive)
                continue;

            float ratio = u.MaxHp > 0 ? u.Hp / (float)u.MaxHp : 0f;
            bool selected = _selUnit == u;
            if (ratio >= 0.999f && !selected)
                continue;

            _barBuffer.Add(new HealthBars.Bar
            {
                Id = u.Id,
                World = _map.UnitBarAnchor(u),
                Ratio = ratio,
                Building = false,
                Accent = ToonMaterial.OutlineFor(u.Owner) ?? new Color("3B2E16"),
                Selected = selected,
            });
        }

        foreach (BuildingInst b in _state.Buildings)
        {
            if (b.IsDead)
                continue;

            float ratio = b.MaxHp > 0 ? b.Hp / (float)b.MaxHp : 0f;
            bool selected = _selBuilding == b;
            if (ratio >= 0.999f && !selected)
                continue;

            _barBuffer.Add(new HealthBars.Bar
            {
                Id = b.Id,
                World = _map.BuildingBarAnchor(b),
                Ratio = ratio,
                Building = true,
                Accent = ToonMaterial.OutlineFor(b.Owner) ?? new Color("3B2E16"),
                Selected = selected,
            });
        }

        _hud.SetHealthBars(_barBuffer);

        if ((OS.GetCmdlineUserArgs().Contains("--demo-health") ||
             OS.GetCmdlineUserArgs().Contains("--demo-bar-diag")) &&
            Time.GetTicksMsec() - _healthDiagMs > 900)
        {
            _healthDiagMs = Time.GetTicksMsec();
            var parts = new List<string>();
            foreach (HealthBars.Bar bar in _barBuffer)
            {
                Vector2 sp = _rig.Camera.UnprojectPosition(bar.World);
                parts.Add($"{(bar.Building ? "B" : "U")}:{(int)(bar.Ratio * 100)}%" +
                          $"w({bar.World.X:F1},{bar.World.Y:F1},{bar.World.Z:F1})" +
                          $"@({sp.X:F0},{sp.Y:F0})");
            }

            GD.Print($"[Health] 条数={_barBuffer.Count} {string.Join(" ", parts)}");
        }
    }

    private ulong _healthDiagMs;

    private void OnCardReordered(int from, int to)
    {        _state.MoveCardInHand(_state.Player, from, to);
        if (_selCard == from)
            _selCard = to;
        else if (_selCard == to)
            _selCard = from;
        RefreshAll();
        _hud.SetSelectedCard(_selCard);
    }

    /// <summary>在某格打出第 index 张手牌（点地图 / 拖到地图都走这里）。</summary>
    private bool _cardFlying;

    /// <summary>
    /// 打出需要指定格子的卡：先播「卡牌沿曲线飞向目标格」的动画，落地后才真正生效。
    /// </summary>
    private bool TryPlayCardAt(int index, HexCoord coord)
    {
        if (_cardFlying)
            return false;

        if (index < 0 || index >= _state.Player.Hand.Count)
            return false;

        CardDef card = GameDatabase.Card(_state.Player.Hand[index]);
        if (card == null)
            return false;

        if (card.KindEnum != CardKind.Unit && card.KindEnum != CardKind.Strike)
            return false;

        // 先做一次完整校验：打不出去就当场提示，连特效都不播
        if (card.KindEnum == CardKind.Unit &&
            !_state.CanDeployCard(card.Id, PlayerId.Player, coord, out _))
        {
            return ApplyCardAt(index, coord);
        }

        if (card.KindEnum == CardKind.Strike &&
            !_state.CanPlayGlobalCard(_state.Player, card.Id, coord, out string why))
        {
            _hud.Toast(why);
            RefreshAll();
            return false;
        }

        Vector2 target = _rig.Camera.UnprojectPosition(
            HexLayout.ToWorld(coord, HexMapView.TileTop + 0.25f));

        _cardFlying = true;
        _hud.SetHandCardVisible(index, false);
        _hud.FlyCard(index, card, target, () =>
        {
            _cardFlying = false;
            ApplyCardAt(index, coord);
        });
        return true;
    }

    /// <summary>卡牌落地后真正生效。</summary>
    private bool ApplyCardAt(int index, HexCoord coord)
    {
        if (index < 0 || index >= _state.Player.Hand.Count)
            return false;

        CardDef card = GameDatabase.Card(_state.Player.Hand[index]);
        if (card == null)
            return false;

        // 出牌前记下这张牌在手上的位置，打完让它飞向牌堆
        int indexBeforePlay = index;
        _hud.TryGetHandCardRect(index, out _pendingPlayedRect);

        if (card.KindEnum == CardKind.Strike)
        {
            TryPlayGlobal(card, coord);
            return true;
        }

        if (card.KindEnum != CardKind.Unit)
        {
            _hud.Toast($"「{card.NameZh}」不需要指定地块");
            return false;
        }

        if (!_state.CanDeployCard(card.Id, PlayerId.Player, coord, out string err))
        {
            _hud.Toast(err);
            return false;
        }

        _state.PlayIndex = indexBeforePlay;
        _state.PlayUnitCard(_state.Player, card.Id, coord, out _);
        _state.PlayIndex = -1;
        _selCard = -1;
        _hud.SetSelectedCard(-1);
        _map.ClearHints();
        AudioManager.PlayDeploy(card.Unit);
        _map.FlashCell(coord, new Color("7FD35B"));
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        _hud.Toast($"已部署 {card.NameZh}");

        // 打完的卡飞向弃牌堆 / 消耗堆
        AnimateCardToPile(card, indexBeforePlay);
        return true;
    }

    /// <summary>从手牌打出一张「不需要地块」的卡：先记下卡面位置，消耗卡才能播烧卡特效。</summary>
    private void PlayGlobalFromHand(int index, CardDef card)
    {
        _pendingCardIndex = index;          // 记住打的是第几张，同名卡才能打出正确的那张

        if (_hud.TryGetHandCardRect(index, out Rect2 rect))
            _pendingPlayedRect = rect;

        TryPlayGlobal(card, null);
    }

    private void TryPlayGlobal(CardDef card, HexCoord? target)
    {
        // 再校验一次（防止状态在这期间变了）：不合法就只弹提示，不播任何特效
        if (!_state.CanPlayGlobalCard(_state.Player, card.Id, target, out string why))
        {
            _hud.Toast(why);
            _selCard = -1;
            _hud.SetSelectedCard(-1);
            _map.ClearHints();
            RefreshAll();
            return;
        }

        // 先播特效，等「导弹/爆炸命中那一刻」再结算伤害与死亡清除
        if (target != null)
        {
            // 导弹卡要从「产出它的那座发射井」发射
            int siloId = 0;
            if (card.Effect == "missile")
            {
                var entry = _state.Player.Produced.FirstOrDefault(p => p.Card == card.Id);
                siloId = entry.SiloId;
            }

            float impact = PlayCardVfx(PlayerId.Player, card, target.Value, siloId);
            _pendingCard = (card.Id, target.Value);

            var timer = GetTree().CreateTimer(Mathf.Max(0.05f, impact));
            timer.Timeout += ResolvePendingCard;
            return;
        }

        // 不需要格子的卡：直接结算
        ResolveCardNow(card.Id, null);
    }

    /// <summary>打击卡/维修卡的特效，返回「命中延迟」（秒）；siloId 指定从哪座发射井发射。</summary>
    private float PlayCardVfx(PlayerId owner, CardDef card, HexCoord target, int siloId = 0)
    {
        if (_vfx == null)
            return 0.05f;

        Vector3 at = HexLayout.ToWorld(target, HexMapView.TileTop + 0.2f);

        switch (card.Effect)
        {
            case "airstrike":
                for (int i = 0; i < 3; i++)
                    _vfx.MissileStrike(at, 1.05f, 4.2f, 0.5f, i * 0.14f);
                return 0.5f + 0.28f;      // 最后一发落地

            case "missile":
            {
                // 导弹井发射：从井口升空 → 高空掠过 → 砸到目标格
                // 优先用「产出这张卡」的那座井；找不到再退回自己的任意一座井
                BuildingInst silo = siloId > 0
                    ? _state.Buildings.FirstOrDefault(b => b.Id == siloId && !b.IsDead)
                    : null;

                silo ??= _state.Buildings.FirstOrDefault(b =>
                    b.Owner == owner && !b.IsDead && b.DefId == "missileSilo");

                if (silo != null)
                {
                    if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--audio-log") >= 0)
                        GD.Print($"[CardVfx] 导弹从发射井 {silo.Id}@{silo.Coord} 发射（请求 siloId={siloId}）");

                    Vector3 from = HexLayout.ToWorld(silo.Coord,
                        HexMapView.TileTop + Mathf.Max(0.6f, _map.DebugBuildingTop(silo.Id)));

                    // 发射井自己的开火动画（模型里带的 Fire_Silo_Missile）
                    _map.PlayBuildingAnim(silo.Id, "Fire_Silo_Missile", "Fire_Silo_Flame");

                    _vfx.MissileLaunchFrom(from, at, 1.5f, 1.1f);
                    return 1.1f;
                }

                // 没有发射井（理论上不该发生）就用高空直落兜底
                _vfx.MissileStrike(at, 1.4f, 4.5f, 0.55f);
                return 0.55f;
            }

            case "repair":
                // 维修一格内的己方单位：地面绿光 + 每个单位身上冒绿星
                _vfx.Heal(at, 0.9f);
                foreach (UnitInst u in _state.UnitsAt(target)
                             .Where(u => u.Owner == owner && u.Alive).ToList())
                {
                    _vfx.Heal(_map.UnitBarAnchor(u) - Vector3.Up * 1.2f, 0.55f);
                }

                return 0.15f;
        }

        return 0.05f;
    }

    /// <summary>等待特效命中的这张卡。</summary>
    private (string CardId, HexCoord Target)? _pendingCard;
    private int _pendingCardIndex = -1;

    /// <summary>出牌时记下手牌矩形，结算后从那里飞向牌堆。</summary>
    private Rect2 _pendingPlayedRect;

    /// <summary>把打完的卡从手牌位置飞到弃牌堆/消耗堆；消耗卡先烧掉再进消耗堆。</summary>
    private void AnimateCardToPile(CardDef card, int fallbackIndex)
    {
        if (card == null)
            return;

        Rect2 rect = _pendingPlayedRect;
        if ((rect.Size.X <= 0 || rect.Size.Y <= 0) && fallbackIndex >= 0)
            _hud.TryGetHandCardRect(fallbackIndex, out rect);

        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
            return;

        AudioManager.PlayCardUsed();

        // 消耗判定看状态层的结果：定义里的 exhaust 关键字，或「发射井产出的这一张」
        if (_state.LastPlayedExhaust)
        {
            // 消耗：原地烧掉 → 烧完再算进消耗堆
            _hud.BurnCardAt(card, rect, 0f, () => _hud.BumpPile(PileView.PileKind.Exhaust));
        }
        else
        {
            _hud.PlayToPileAnimation(card, rect, false, playSound: false);
        }

        _pendingPlayedRect = default;
    }

    private void ResolvePendingCard()
    {
        if (_pendingCard == null)
            return;

        (string cardId, HexCoord target) = _pendingCard.Value;
        _pendingCard = null;
        ResolveCardNow(cardId, target);
    }

    /// <summary>真正结算这张卡（命中之后才调用）。</summary>
    private void ResolveCardNow(string cardId, HexCoord? target)
    {
        _state.PlayIndex = _pendingCardIndex;
        string result = _state.PlayGlobalCard(_state.Player, cardId, target, out string error);

        if (_state.PendingReclaimExhaust)
            OpenReclaimPicker();
        _state.PlayIndex = -1;
        if (!string.IsNullOrEmpty(error))
        {
            _hud.Toast(error);
            _selCard = -1;
            _hud.SetSelectedCard(-1);
            _map.ClearHints();
            RefreshAll();
            return;
        }

        // 进入弃牌阶段时不再弹小提示（有大号横幅）
        if (_state.PendingDiscard <= 0)
            _hud.Toast(result);

        _selCard = -1;
        _hud.SetSelectedCard(-1);
        _map.ClearHints();
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        AnimateCardToPile(GameDatabase.Card(cardId), -1);
        CheckGameOver();

        if (_state.PendingDiscard > 0)
            EnterDiscardMode();
    }

    /// <summary>回合结束弃掉的手牌：普通卡飞向弃牌堆，虚无卡原地烧掉。</summary>
    private void AnimateEndOfTurnDiscards(List<(string Id, Rect2 Rect)> handRects)
    {
        if (_state.DiscardedThisTurn.Count == 0 && _state.ExhaustedThisTurn.Count == 0)
            return;

        var pool = new List<(string Id, Rect2 Rect)>(handRects);
        int stagger = 0;

        bool TryTake(string id, out Rect2 rect)
        {
            int idx = pool.FindIndex(p => p.Id == id);
            rect = default;
            if (idx < 0)
                return false;
            rect = pool[idx].Rect;
            pool.RemoveAt(idx);
            return true;
        }

        // 虚无：原地烧掉（烧完才计入消耗堆）
        foreach (string id in _state.ExhaustedThisTurn)
        {
            if (!TryTake(id, out Rect2 rect))
                continue;

            CardDef def = GameDatabase.Card(id);
            float delay = stagger++ * 0.05f;
            var timer = GetTree().CreateTimer(delay);
            timer.Timeout += () =>
            {
                AudioManager.PlayCardDiscarded();
                _hud.BurnCardAt(def, rect);
            };
        }

        // 普通弃牌：飞向弃牌堆
        foreach (string id in _state.DiscardedThisTurn)
        {
            if (!TryTake(id, out Rect2 rect))
                continue;

            CardDef def = GameDatabase.Card(id);
            float delay = stagger++ * 0.05f;
            var timer = GetTree().CreateTimer(delay);
            timer.Timeout += () => _hud.PlayToPileAnimation(def, rect, false);
        }
    }

    /// <summary>
    /// 「物资运输」抽完牌后的弃牌阶段：手牌整体泛绿提示，点一张就弃掉。
    /// </summary>
    private void EnterDiscardMode()
    {
        if (_state.PendingDiscard <= 0 || _state.Player.Hand.Count == 0)
        {
            _state.PendingDiscard = 0;
            ExitDiscardMode();
            return;
        }

        _discardMode = true;
        ClearSelection();
        _hud.SetSelectedCard(-1);
        _hud.SetHandPickMode(true);
        RefreshAll();
    }

    private void ExitDiscardMode()
    {
        _discardMode = false;
        _hud?.SetHandPickMode(false);
    }

    private void TryDiscardHandCard(int index)
    {
        if (index < 0 || index >= _state.Player.Hand.Count)
            return;

        string id = _state.Player.Hand[index];
        CardDef def = GameDatabase.Card(id);

        Rect2 rect = default;
        bool hasRect = _hud.TryGetHandCardRect(index, out rect);

        if (!_state.DiscardOne(_state.Player, index))
            return;

        _state.PendingDiscard = Mathf.Max(0, _state.PendingDiscard - 1);

        if (hasRect && def != null)
            _hud.PlayToPileAnimation(def, rect, _state.Player.Exhaust.Contains(id));

        RefreshAll();

        if (_state.PendingDiscard <= 0)
            ExitDiscardMode();
    }

    private void OnEndTurn()
    {
        _hud.HideSpawnPanel();
        if (_state.Phase != BattlePhase.PlayerTurn || _enemyBusy)
            return;

        if (_discardMode)
        {
            _hud.Toast("请先选择一张手牌弃掉");
            return;
        }

        ClearSelection();

        // 记下手牌位置：回合结束弃掉的牌要从这里飞向弃牌堆
        var handRects = new List<(string Id, Rect2 Rect)>();
        for (int i = 0; i < _state.Player.Hand.Count; i++)
        {
            if (_hud.TryGetHandCardRect(i, out Rect2 r))
                handRects.Add((_state.Player.Hand[i], r));
        }

        _state.EndTurn();
        PlayVolleys();
        AudioManager.SetIntensity(_state.TurnNumber < 4 ? 0 : _state.TurnNumber < 8 ? 1 : 2);
        RefreshAll();
        AnimateEndOfTurnDiscards(handRects);

        if (_state.Phase == BattlePhase.EnemyTurn)
            CallDeferred(nameof(RunEnemyTurnDeferred));
    }

    private async void RunEnemyTurnDeferred()
    {
        _enemyBusy = true;
        RefreshAll();
        _hud.Toast("敌方行动中…");

        await ToSignal(GetTree().CreateTimer(0.45), SceneTreeTimer.SignalName.Timeout);

        // 让 AI 的攻击与打击卡也走特效：钩子里播特效、命中才结算
        var ai = new AIController(_state, _state.Difficulty);
        ai.AttackHook = PlayAiAttackAsync;
        ai.StrikeHook = PlayAiStrikeAsync;

        string log = await ai.RunTurnAsync();

        if (!string.IsNullOrEmpty(_state.LastBuildingLog))
            log = _state.LastBuildingLog + (string.IsNullOrEmpty(log) ? "" : "；" + log);

        _map.SyncEntities();
        _map.RefreshZones();
        _hud.Toast(string.IsNullOrEmpty(log) ? "敌方结束了回合" : "敌方：" + log);

        if (_state.Phase == BattlePhase.GameOver)
        {
            _enemyBusy = false;
            CheckGameOver();
            return;
        }

        _state.EndTurn();
        ShowIncomePopups(_state.CurrentPlayer);
        PlayVolleys();
        _enemyBusy = false;
        RefreshAll();

        // 新回合抽牌：洗回提示 + 从抽牌堆飞进手牌
        if (_state.LastDrawReshuffled)
            _hud.PlayReshuffleAnimation();
        int drawCount = _state.DrawnIndicesThisTurn.Count;
        int producedCount = _state.ProducedThisTurn.Count;

        // 编号等入场动画播完再出，避免「牌还没飞到、数字先出现」
        float drawEnd = drawCount > 0 ? (drawCount - 1) * 0.07f + 0.34f : 0f;
        float produceEnd = producedCount > 0 ? 0.14f + 0.5f + (producedCount - 1) * 0.12f : 0f;

        _hud.HideOrderHints();
        _hud.PlayDrawAnimation(_state.DrawnIndicesThisTurn);
        PlayProduceAnimations(0.15f);
        _hud.ShowOrderHintsAfter(Mathf.Max(drawEnd, produceEnd) + 0.05f);

        CheckGameOver();
    }

    /// <summary>AI 单位开火：播开火特效 → 命中瞬间结算。</summary>
    private async Task PlayAiAttackAsync(UnitInst attacker, HexCoord target)
    {
        Vector3 world = HexLayout.ToWorld(target, HexMapView.TileTop);
        if (!_map.FaceUnitTurretToward(attacker, world))
            _map.FaceUnitToward(attacker, world);

        _map.PlayUnitAttack(attacker.Id);
        AudioManager.PlayAttack(attacker.DefId);
        _map.FlashCell(target, new Color("E8563F"));

        float impact = PlayAttackVfx(attacker, target);
        await ToSignal(GetTree().CreateTimer(Mathf.Max(impact, 0.03f)), SceneTreeTimer.SignalName.Timeout);

        _state.PerformAttack(attacker, target);
        _map.SyncEntities();
        _map.RefreshZones();

        // 一点点间隔，连续攻击不至于糊成一团
        await ToSignal(GetTree().CreateTimer(0.14f), SceneTreeTimer.SignalName.Timeout);
    }

    /// <summary>AI 打击卡：播导弹/空袭特效 → 命中瞬间结算。</summary>
    private async Task PlayAiStrikeAsync(string cardId, HexCoord target)
    {
        CardDef card = GameDatabase.Card(cardId);
        if (card == null)
            return;

        // AI 也用「产出这张卡」的那座井发射
        int siloId = 0;
        if (card.Effect == "missile")
        {
            var entry = _state.Enemy.Produced.FirstOrDefault(p => p.Card == cardId);
            siloId = entry.SiloId;
        }

        float impact = PlayCardVfx(PlayerId.Enemy, card, target, siloId);
        await ToSignal(GetTree().CreateTimer(Mathf.Max(impact, 0.05f)), SceneTreeTimer.SignalName.Timeout);

        _state.PlayGlobalCard(_state.Enemy, cardId, target, out _);
        _map.SyncEntities();
        _map.RefreshZones();

        await ToSignal(GetTree().CreateTimer(0.12f), SceneTreeTimer.SignalName.Timeout);
    }

    // ─────────────────────────── 建造 / 升级 ───────────────────────────

    private void OnBuildClicked(string defId)
    {
        if (_selBuilding != null)
        {
            CloseBuildMenu();
            return;
        }

        if (_buildTarget == null)
        {
            _hud.Toast("先点一个己方控制区内的空格子");
            return;
        }

        HexCoord target = _buildTarget.Value;
        if (!_state.TryBuild(PlayerId.Player, defId, target, out string error))
        {
            _hud.Toast(error);
            return;
        }

        CloseBuildMenu();
        AudioManager.PlaySelect(defId);
        BuildingInst built = _state.BuildingAt(target);
        if (built != null)
            _map.PunchBuilding(built.Id);
        _map.FlashCell(target, new Color("7FD35B"));
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        _hud.Toast("建造完成");
    }

    /// <summary>关闭建造菜单，同时清掉目标格指示与预览面板。</summary>
    private void CloseBuildMenu()
    {
        _hud.HideBuildMenu();
        _buildTarget = null;
        _map?.SetBuildMarker(null);
        _map?.ClearBuildingPreview();

        // 建造预览的信息面板也一起收起来（选中建筑时会随即重新显示）
        if (_selBuilding == null)
            _hud.HideInfo();
    }

    /// <summary>维修选中的建筑：按损失血量扣钱，回满血并播回血特效。</summary>
    private void OnRepairClicked()
    {
        if (_selBuilding == null || _selBuilding.Owner != PlayerId.Player)
            return;

        int cost = BattleState.RepairCost(_selBuilding);
        if (!_state.TryRepair(_selBuilding, out string error))
        {
            _hud.Toast(error);
            return;
        }

        AudioManager.PlayUi("ui_click");
        _vfx?.Heal(_map.BuildingBarAnchor(_selBuilding) - Vector3.Up * 0.9f, 1.0f);
        _hud.Toast($"维修完成，花费 {cost}$");
        _map.PunchBuilding(_selBuilding.Id);
        ShowBuildingInfo(_selBuilding);
        RefreshAll();
    }

    private void OnUpgradeClicked()
    {
        if (_selBuilding == null || _selBuilding.Owner != PlayerId.Player)
            return;

        // 先结束悬停预览，避免「升级后模型退回上一级」
        _map.ClearBuildingPreview();

        if (!_state.TryUpgrade(_selBuilding, out string error))
        {
            _hud.Toast(error);
            return;
        }

        AudioManager.PlayUpgrade(_selBuilding.DefId);
        _map.PunchBuilding(_selBuilding.Id);
        _map.SyncEntities();
        _map.ReapplySelection();
        _map.RefreshZones();
        ShowBuildingInfo(_selBuilding);
        RefreshAll();
        RefreshSpawnPanel(_selBuilding);   // 升级后立刻刷新出兵面板（新等级解锁的兵种马上出现）
        _hud.Toast("升级完成");
    }

    private void OnDemolishClicked()
    {
        if (_selBuilding == null || _selBuilding.Owner != PlayerId.Player || _selBuilding.IsHq)
            return;

        int refund = BattleState.DemolishRefund(_selBuilding);
        _state.Demolish(_selBuilding);
        _selBuilding = null;
        _hud.HideInfo();
        _hud.HideSpawnPanel();
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        _hud.Toast(refund > 0 ? $"已拆除，返还 {refund}$" : "已拆除");
    }

    // ─────────────────────────── 输入 ───────────────────────────

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_state == null)
            return;

        if (@event is InputEventKey { Pressed: true, Echo: false } numKey)
        {
            // 数字键 1~9 / 0 → 选中对应位置的手牌（0 对应第 10 张）
            int slot = numKey.Keycode switch
            {
                Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2, Key.Key4 => 3, Key.Key5 => 4,
                Key.Key6 => 5, Key.Key7 => 6, Key.Key8 => 7, Key.Key9 => 8, Key.Key0 => 9,
                Key.Kp1 => 0, Key.Kp2 => 1, Key.Kp3 => 2, Key.Kp4 => 3, Key.Kp5 => 4,
                Key.Kp6 => 5, Key.Kp7 => 6, Key.Kp8 => 7, Key.Kp9 => 8, Key.Kp0 => 9,
                _ => -1,
            };

            if (slot >= 0)
            {
                OnCardClicked(slot);
                GetViewport().SetInputAsHandled();
                return;
            }

            // E 键结束回合
            if (numKey.Keycode == Key.E)
            {
                OnEndTurn();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (@event is InputEventMouseButton mb)
        {
            switch (mb.ButtonIndex)
            {
                case MouseButton.WheelUp when mb.Pressed:
                    _rig.Zoom(-1);
                    return;
                case MouseButton.WheelDown when mb.Pressed:
                    _rig.Zoom(1);
                    return;
                case MouseButton.Left when mb.Pressed:
                    OnLeftClick(mb.Position);
                    return;
                case MouseButton.Right:
                    if (mb.Pressed && _hud.TerrainVisible)
                    {
                        // 右键先关掉左上角的地形检视面板
                        _hud.HideTerrain();
                    }

                    if (mb.Pressed && _hud.SpawnVisible)
                    {
                        // 出兵面板开着：右键直接收起面板并取消建筑选中
                        _hud.HideSpawnPanel();
                        ClearSelection();
                        RefreshAll();
                        return;
                    }

                    if (mb.Pressed)
                    {
                        _rightDragging = true;
                        _rightMoved = false;
                        _rightStart = mb.Position;
                        _lastMouse = mb.Position;
                    }
                    else
                    {
                        _rightDragging = false;
                        // 只是点了一下右键（没拖动）→ 取消选择
                        if (!_rightMoved)
                            OnRightClick();
                    }
                    return;
                case MouseButton.Middle:
                    _dragPan = mb.Pressed;
                    _lastMouse = mb.Position;
                    return;
            }
        }

        if (@event is InputEventMouseMotion motion)
        {
            _mouseScreen = motion.Position;

            if (_rightDragging)
            {
                Vector2 d = motion.Position - _lastMouse;
                _lastMouse = motion.Position;

                // 水平拖动转视角，垂直拖动调俯角
                _rig.Rotate(d.X * 0.30f);
                _rig.Tilt(-d.Y * 0.14f);

                if (motion.Position.DistanceTo(_rightStart) > 6f)
                    _rightMoved = true;
                return;
            }

            if (_dragPan)
            {
                _rig.Pan(motion.Position - _lastMouse);
                _lastMouse = motion.Position;
            }
            else
            {
                UpdateHover(motion.Position);
            }
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            // 左 Ctrl：视角复位到开局取景
            if (key.Keycode == Key.Ctrl && key.Location != KeyLocation.Right)
            {
                _rig.ResetView();
                _hud.Toast("视角已复位");
                return;
            }

            switch (key.Keycode)
            {
                case Key.Q:
                    _rig.Rotate(-22.5f);
                    break;
                case Key.R:
                    _rig.Rotate(22.5f);      // E 键改作「结束回合」
                    break;
                case Key.Escape:
                    OnRightClick();
                    break;
                case Key.Space:
                    OnEndTurn();
                    break;
            }
        }
    }

    private ulong _lastHexSfxMs;
    private HexCoord? _lastHexSfxTile;

    /// <summary>格子的「选择」音效：同一格不重复，且两次至少间隔 120ms。</summary>
    private void PlayHexTileSound(float volumeDb, HexCoord? tile = null)
    {
        ulong now = Time.GetTicksMsec();

        if (tile != null && _lastHexSfxTile != null && _lastHexSfxTile.Value == tile.Value &&
            now - _lastHexSfxMs < 400)
        {
            return;
        }

        if (now - _lastHexSfxMs < 120)
            return;

        _lastHexSfxMs = now;
        if (tile != null)
            _lastHexSfxTile = tile;

        AudioManager.PlaySfx("res://gameAsset/audios/sfx/select_hexTile.ogg", volumeDb);
    }
    private void UpdateHover(Vector2 screenPos)
    {
        if (!_rig.TryPickGround(screenPos, HexMapView.TileTop, out Vector3 world))
        {
            _hover = null;
            _map.ShowHover(null);
            return;
        }

        HexCoord coord = HexLayout.FromWorld(world);
        if (_state.Cell(coord) == null)
        {
            _hover = null;
            _map.ShowHover(null);
            return;
        }

        bool changed = _hover == null || _hover.Value != coord;
        _hover = coord;
        _map.ShowHover(coord);

        // 选卡/选目标时，鼠标移到新格子上给一声轻提示（带 120ms 去抖，扫过一排不会连响）
        if (changed && _selCard >= 0)
            PlayHexTileSound(-6f, coord);
    }

    private void OnLeftClick(Vector2 screenPos)
    {
        OnLeftClickCore(screenPos);

        // 点击后同步出兵面板：只有「选中了能出兵的己方建筑」才显示，其余一律收起
        RefreshSpawnPanel(_selBuilding);
    }

    private void OnLeftClickCore(Vector2 screenPos)
    {
        if (Time.GetTicksMsec() < _inputGateUntil)
            return;

        // 还有一击在等特效命中结算，先别接受新指令
        if (_pendingAttack != null || _pendingCard != null)
            return;

        if (!_rig.TryPickGround(screenPos, HexMapView.TileTop, out Vector3 world))
            return;

        HexCoord coord = HexLayout.FromWorld(world);
        Cell cell = _state.Cell(coord);
        if (cell == null)
            return;

        if (_state.Phase != BattlePhase.PlayerTurn)
            return;

        _hud.ShowTerrain(cell.Terrain);      // 选中任意格子：左上角显示地形预览与效果
        PlayHexTileSound(-3f, coord);

        // 1) 正在选卡
        if (_selCard >= 0 && _selCard < _state.Player.Hand.Count)
        {
            if (TryPlayCardAt(_selCard, coord))
                return;

            // 点到无效区域：取消选中（参考-4）
            _selCard = -1;
            _hud.SetSelectedCard(-1);
            _map.ClearHints();
            ClearSelection();
            return;
        }

        // 2) 已选中单位 → 移动或攻击
        if (_selUnit != null && _selUnit.Alive)
        {
            if (_state.CanAttack(_selUnit, coord))
            {
                UnitInst attacker = _selUnit;

                AudioManager.PlayAttack(attacker.DefId);
                // 攻击时转向目标：有炮塔的只转炮塔，其余整体转身；然后播放一次攻击动作
                Vector3 targetWorld = HexLayout.ToWorld(coord, HexMapView.TileTop);
                if (!_map.FaceUnitTurretToward(attacker, targetWorld))
                    _map.FaceUnitToward(attacker, targetWorld);
                _map.PlayUnitAttack(attacker.Id);
                _map.FlashCell(coord, new Color("E8563F"));

                // 先播特效，等「命中那一刻」再结算伤害（伤害检测 / 死亡清除都在爆炸瞬间）
                float impact = PlayAttackVfx(attacker, coord);

                _pendingAttack = (attacker, coord);
                var timer = GetTree().CreateTimer(Mathf.Max(impact, 0.03f));
                timer.Timeout += ResolvePendingAttack;
                return;
            }

            if (_state.MoveRange(_selUnit).Contains(coord))
            {
                AudioManager.PlayMove(_selUnit.DefId);
                _state.TryMove(_selUnit, coord, out _);
                _map.SyncEntities();
                SelectUnit(_selUnit);
                RefreshAll();
                return;
            }
        }

        // 3) 点己方单位 / 己方建筑 → 选中；同一格有多个目标时反复点击轮换
        //    轮询队列 = 格内所有己方单位（按顺序）+ 该格的己方建筑（排在最后），
        //    这样单位压在建筑上时也能点选到下面的建筑。
        //    轮询队列 = 格内所有己方单位 + 该格的己方建筑 + 「在此建造」
        //    （有驻军的格子也能通过轮询进入建造菜单）
        var cycle = new List<(UnitInst Unit, BuildingInst Building, bool Build)>();

        foreach (UnitInst u in cell.Units.Where(x => x.Alive && x.Owner == PlayerId.Player))
            cycle.Add((u, null, false));

        if (cell.Building != null && cell.Building.Owner == PlayerId.Player && !cell.Building.IsDead)
            cycle.Add((null, cell.Building, false));

        // 该格还没有建筑、且在己方控制区内 → 加一个「建造」选项
        if (cell.Building == null && _state.CanBuildAt(coord, PlayerId.Player))
            cycle.Add((null, null, true));

        if (cycle.Count > 0)
        {
            int index = -1;
            if (_selUnit != null && _selUnit.Coord == coord)
                index = cycle.FindIndex(e => e.Unit == _selUnit);
            else if (_selBuilding != null && _selBuilding.Coord == coord)
                index = cycle.FindIndex(e => e.Building == _selBuilding);
            else if (_buildTarget == coord)
                index = cycle.FindIndex(e => e.Build);

            (UnitInst Unit, BuildingInst Building, bool Build) pick = cycle[(index + 1) % cycle.Count];

            if (pick.Unit != null)
            {
                AudioManager.PlaySelect(pick.Unit.DefId);
                SelectUnit(pick.Unit);
            }
            else if (pick.Building != null)
            {
                SelectBuilding(pick.Building);
            }
            else
            {
                OpenBuildAt(coord);
            }

            return;
        }

        // 4) 点建筑（敌方建筑，或没有己方单位压着的建筑）
        if (cell.Building != null)
        {
            SelectBuilding(cell.Building);
            return;
        }

        // 5) 空地 → 己方控制区内弹建造菜单
        OpenBuildAt(coord);
    }

    /// <summary>在指定格打开建造菜单（同时清掉单位/建筑的选中状态）。</summary>
    private void OpenBuildAt(HexCoord coord)
    {
        // 注意用 ClearSelection()：它会同时把单位/建筑的黄色描边与血条的「选中」状态清掉
        ClearSelection();
        _map.ClearHints();
        _hud.HideInfo();

        if (_state.CanBuildAt(coord, PlayerId.Player))
        {
            _hover = coord;
            _buildTarget = coord;
            _map.SetBuildMarker(coord);
            var defs = GameDatabase.Buildings.Values
                .Where(d => d.Id != "hq")
                .OrderBy(d => d.UpgradeCostAt(0))
                .ToList();
            _hud.ShowBuildMenu(defs, _state.Player.Gold);
        }
        else
        {
            CloseBuildMenu();
            _hud.Toast("这里不能建造（需要地面、己方控制区，且不与敌方控制区重叠）");
        }
    }

    private void OnRightClick()
    {
        _selCard = -1;
        _hud.SetSelectedCard(-1);
        ClearSelection();
        CloseBuildMenu();
        RefreshAll();
    }

    private void SelectUnit(UnitInst u)
    {
        _selUnit = u;
        _selBuilding = null;
        _hud.HideSpawnPanel();
        _hud.ShowTerrain(_state.TerrainAt(u.Coord));
        CloseBuildMenu();
        _map.ClearHints();
        _map.SetSelectedBuilding(-1);
        _map.SetSelectedUnit(u?.Id ?? -1);

        if (u.Owner == PlayerId.Player)
        {
            _map.ShowHints(_state.MoveRange(u), new Color("5BC0EB"));

            // 已经攻击过的单位不再标「可攻击」
            if (!u.Attacked)
            {
                var targets = _state.AttackTargets(u)
                    .Select(t => t.building != null ? t.building.Coord : t.unit.Coord)
                    .ToHashSet();
                _map.ShowAttackMarks(_state.AttackRangeCells(u).ToList(), targets);
            }
        }

        ShowUnitInfo(u);
    }

    /// <summary>刷新单位信息面板（攻击力按血量变色，实时调用）。</summary>
    private void ShowUnitInfo(UnitInst u)
    {
        if (u == null || _hud == null)
            return;

        // 攻击力：血量越低越弱（最低 35%），文字按血量比例上色
        int dmg = BattleState.EffectiveDamage(u);
        float ratio = BattleState.HpRatio(u);
        string atkColor = ratio > 0.67f ? "5FBF5A" : ratio > 0.34f ? "E5B436" : "E8563F";
        string atkText = $"[color=#{atkColor}]{dmg}[/color]";
        if (dmg < u.Def.Damage)
            atkText += $"（满血 {u.Def.Damage}）";

        _infoUnitHp = u.Hp;

        int move = _state.MoveStepsOf(u);
        int range = _state.AttackRangeOf(u);

        string moveText = move == u.Def.Move ? $"{move}" : $"{move}（地形 {u.Def.Move}）";
        string rangeText = range == u.Def.Range ? $"{range}" : $"{range}（地形 {u.Def.Range}）";
        string terrainLine = TerrainEffectLine(_state.TerrainAt(u.Coord));

        _hud.ShowInfo(
            $"{u.Def.NameZh}  {u.Hp}/{u.MaxHp}",
            $"攻击 {atkText}　移动 {moveText}　射程 {rangeText}\n" +
            $"{(u.IsAir ? "空中单位，可越过地形" : "地面单位")}" +
            $"{(u.Def.CanHitAir ? "，可对空" : "")}\n" +
            (string.IsNullOrEmpty(terrainLine) ? "" : terrainLine + "\n") +
            (u.SpawnedThisTurn
                ? "本回合刚登场，下回合才能行动"
                : $"{(u.Moved ? "已移动" : "可移动")}　{(u.Attacked ? "已攻击" : "可攻击")}"));
    }

    /// <summary>在每座产出金币的建筑上方弹出「+N」金色飘字（逐个错开）。</summary>
    /// <summary>播放本回合建筑产出的卡：从建筑格飞到各自的手牌位置。</summary>
    private void PlayProduceAnimations(float baseDelay = 0f)
    {
        if (_state.ProducedThisTurn.Count == 0)
            return;

        // 等手牌扇形重新排布完成（新卡会先出现在占位位置），再算目标格并起飞
        SceneTreeTimer wait = GetTree().CreateTimer(0.14f);
        wait.Timeout += () =>
        {
            int i = 0;
            foreach ((string card, HexCoord from, int handIndex) in _state.ProducedThisTurn)
            {
                Vector3 world = HexLayout.ToWorld(from, HexMapView.TileTop) + new Vector3(0f, 0.9f, 0f);
                Vector2 screen = _rig.Camera.UnprojectPosition(world);
                _hud.PlayProduceAnimation(GameDatabase.Card(card), screen, handIndex, baseDelay + i * 0.12f);
                i++;
            }
        };
    }
    private void ShowOpeningIncome() => ShowIncomePopups(PlayerId.Player);
    private void ShowIncomePopups(PlayerId who)
    {
        int i = 0;
        foreach ((BuildingInst building, int amount) in _state.IncomeSources(who))
        {
            Vector3 world = HexLayout.ToWorld(building.Coord, HexMapView.TileTop) +
                           new Vector3(i * 0.30f, 1.35f + i * 0.72f, 0f);
            Vector2 screen = _rig.Camera.UnprojectPosition(world);
            _hud.ShowIncomePopup(screen, amount, i * 0.12f);
            i++;
        }
    }
    /// <summary>地形效果一句话说明（泥土无效果时返回空串）。</summary>
    private static string TerrainEffectLine(TerrainType terrain) => terrain switch
    {
        TerrainType.Grass => "草地：本格建筑收入 +20%",
        TerrainType.Sand => "沙地：本格陆军移动 -1（至少 1）",
        TerrainType.Rock => "岩地：本格陆军移动 +1",
        TerrainType.Snow => "雪地：本格单位射程 +1 / 建筑控制半径 +1",
        TerrainType.Water => "水面：陆军无法进入",
        _ => "",
    };
    /// <summary>调试用：在指定屏幕坐标模拟一次左键点击。</summary>
    private void SendClick(Vector2 pos)
    {
        Input.WarpMouse(pos);
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = pos, GlobalPosition = pos,
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = pos, GlobalPosition = pos,
        });
    }
    /// <summary>切换建筑是否产卡。</summary>
    private void OnProduceToggled()
    {
        if (_selBuilding == null || _selBuilding.Owner != PlayerId.Player)
            return;

        _selBuilding.ProduceEnabled = !_selBuilding.ProduceEnabled;
        ShowBuildingInfo(_selBuilding);
        RefreshAll();
        _hud.Toast(_selBuilding.ProduceEnabled ? "已开启产卡" : "已关闭产卡");
    }
    /// <summary>点击出兵面板上的兵种：扣费并在建筑本格召唤。</summary>
    private void OnSpawnClicked(string cardId)
    {
        if (_selBuilding == null || _state.Phase != BattlePhase.PlayerTurn)
            return;

        UnitInst spawned = _state.TrySpawnFromBuilding(_selBuilding, cardId, out string reason);
        if (spawned == null)
        {
            _hud.Toast(string.IsNullOrEmpty(reason) ? "无法出兵" : reason);
            return;
        }

        AudioManager.PlayDeploy(spawned.DefId);
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        RefreshSpawnPanel(_selBuilding);
        _hud.Toast($"已出兵：{Loc.Name(spawned.Def.NameZh, spawned.Def.NameEn)}");
    }
    /// <summary>选中兵营 / 直升机坪时显示出兵面板，其余情况隐藏。</summary>
    private void RefreshSpawnPanel(BuildingInst b)
    {
        if (b == null || b.Owner != PlayerId.Player || _state.Phase != BattlePhase.PlayerTurn ||
            b.Def?.SpawnByLevel == null || b.Def.SpawnByLevel.Length == 0)
        {
            _hud.HideSpawnPanel();
            return;
        }

        var options = new List<(string CardId, string Name, int Cost, string Icon, bool Ready, string Tip)>();
        foreach (string cardId in BattleState.SpawnOptions(b))
        {
            CardDef card = GameDatabase.Card(cardId);
            if (card == null)
                continue;

            bool ready = _state.CanSpawnFrom(b, cardId, out string reason);
            string icon = "res://gameAsset/textures/cardface/cardface_" + card.Unit + ".png";
            string name = Loc.Name(card.NameZh, card.NameEn);
            string tip = ready ? $"{name}  {card.Cost}$" : $"{name}：{reason}";

            options.Add((cardId, name, card.Cost, icon, ready, tip));
        }

        string title = $"{Loc.Name(b.Def.NameZh, b.Def.NameEn)}  Lv.{b.Level}";
        if (b.SpawnUsed)
            title += "（本回合已出兵）";

        _hud.ShowSpawnPanel(title, options);
    }
    /// <summary>选中一座建筑：信息面板 + 控制区高亮 + （有攻击力时）红色可攻击范围。</summary>
    private void SelectBuilding(BuildingInst b)
    {
        if (b == null)
            return;

        _selUnit = null;
        _selBuilding = b;
        _hud.ShowTerrain(_state.TerrainAt(b.Coord));
        RefreshSpawnPanel(b);
        AudioManager.PlaySelect(b.DefId);
        _map.ClearHints();
        _map.SetSelectedUnit(-1);
        _map.SetSelectedBuilding(b.Owner == PlayerId.Player ? b.Id : -1);
        CloseBuildMenu();
        ShowBuildingInfo(b);

        if (b.Owner != PlayerId.Player)
            return;

        RefreshBuildingHighlights(b);
    }

    /// <summary>建筑的控制区蓝色高亮 + 有攻击力时的红色可攻击范围。</summary>
    private void RefreshBuildingHighlights(BuildingInst b)
    {
        if (b == null || b.Owner != PlayerId.Player)
            return;

        _map.ClearHints();
        _map.ShowHints(HexCoord.Spiral(b.Coord, b.ControlRadius), new Color("3D7BD6"));

        if (b.Def.AttackRange > 0)
        {
            _map.ShowAttackMarks(
                _state.BuildingRangeCells(b).ToList(),
                _state.BuildingTargetCells(b));
        }
    }

    private void ShowBuildingInfo(BuildingInst b, bool showDelta = false)
    {
        int level = b.Level;
        int next = Mathf.Min(level + 1, b.Def.MaxLevel);
        bool canUp = level < b.Def.MaxLevel;

        // 只有悬停「升级」按钮时才显示「=> 新值」，平时只显示当前数值
        var lines = new List<string>(BuildStatLines(b.Def, level, next, canUp && showDelta,
            _state.TerrainAt(b.Coord)));

        string upgrade = canUp
            ? $"升级到 Lv.{next} 需要 {b.Def.UpgradeCostAt(level)}$"
            : "已满级";

        // 炮台 / 防空炮可以花钱多打一次
        bool canExtra = b.Owner == PlayerId.Player && b.Def.ExtraFireCost > 0 &&
                        b.Def.AttackRange > 0;
        bool extraReady = canExtra && !b.ExtraFired && _state.Player.Gold >= b.Def.ExtraFireCost;
        if (canExtra && !b.ExtraFired)
            upgrade += $"\n额外开火 {b.Def.ExtraFireCost}$（每回合一次）";

        lines.Add(upgrade);

        string title = $"{b.Def.NameZh}  Lv.{level}  {b.Hp}/{b.MaxHp}";
        int repairCost = b.Owner == PlayerId.Player ? BattleState.RepairCost(b) : 0;

        _hud.ShowInfo(title, string.Join("\n", lines),
            b.Def.Preview, showButtons: b.Owner == PlayerId.Player,
            extraFireCost: canExtra ? b.Def.ExtraFireCost : 0, extraFireReady: extraReady,
            showDemolish: !b.IsHq, canUpgrade: canUp,
            repairCost: repairCost,
            repairAffordable: _state.Player.Gold >= repairCost,
            demolishRefund: b.Owner == PlayerId.Player && !b.IsHq
                ? BattleState.DemolishRefund(b)
                : 0,
            showProduce: b.Owner == PlayerId.Player && BattleState.IsCardProducer(b.DefId),
            produceOn: b.ProduceEnabled);
    }

    /// <summary>建造前的信息预览：右侧面板显示一级的数值（没有按钮）。</summary>
    private void ShowBuildPreviewInfo(string defId)
    {
        BuildingDef def = GameDatabase.Building(defId);
        if (def == null)
            return;

        var lines = new List<string> { $"建造花费 {def.UpgradeCostAt(0)}$" };
        // 建造预览不显示「=> 新值」，保持和平时一致
        lines.AddRange(BuildStatLines(def, 1, Mathf.Min(2, def.MaxLevel), false,
            _state.TerrainAt(_buildTarget ?? HexCoord.Zero)));

        if (def.MaxLevel > 1)
            lines.Add($"升级到 Lv.2 需要 {def.UpgradeCostAt(1)}$");

        _hud.ShowInfo($"{def.NameZh}  Lv.1", string.Join("\n", lines),
            def.Preview, showButtons: false);
    }

    /// <summary>生成「每回合金币 / 攻击 / 控制半径」这几行，有下一级时带绿色新值。</summary>
    private static IEnumerable<string> BuildStatLines(BuildingDef def, int level, int next, bool canUp,
                                                      TerrainType terrain = TerrainType.Dirt)
    {
        const string Green = "5FBF5A";

        string Stat(string label, int current, int upcoming, string prefix = "", string suffix = "")
        {
            string cur = $"{prefix}{current}{suffix}";
            if (!canUp || current == upcoming)
                return $"{label} {cur}";

            return $"{label} {cur} => [color=#{Green}]{prefix}{upcoming}{suffix}[/color]";
        }

        int income = def.IncomeAt(level);
        int incomeNext = canUp ? def.IncomeAt(next) : income;
        if (terrain == TerrainType.Grass)
        {
            income = Mathf.RoundToInt(income * 1.2f);
            incomeNext = Mathf.RoundToInt(incomeNext * 1.2f);
        }
        if (income > 0 || incomeNext > 0)
            yield return Stat("每回合金币", income, incomeNext, "+");

        if (def.AttackRange > 0)
        {
            int dmg = def.DamageAt(level);
            int dmgNext = canUp ? def.DamageAt(next) : dmg;
            yield return $"{Stat("攻击", dmg, dmgNext)}　射程 {def.AttackRange}";
        }

        int radiusBonus = terrain == TerrainType.Snow ? 1 : 0;
        int radius = def.ControlAt(level) + radiusBonus;
        int radiusNext = (canUp ? def.ControlAt(next) : def.ControlAt(level)) + radiusBonus;
        yield return Stat("控制半径", radius, radiusNext);
    }

    /// <summary>悬停建造项：在目标格上临时摆出该建筑，右侧面板同步显示它的数值。</summary>
    private void OnBuildHovered(string defId)
    {
        if (_buildTarget == null)
            return;

        _map.PreviewBuildingAt(defId, _buildTarget.Value, PlayerId.Player);
        ShowBuildPreviewInfo(defId);
    }

    /// <summary>鼠标离开按钮：清掉模型预览，面板恢复到选中建筑（或隐藏）。</summary>
    private void OnPreviewUnhovered()
    {
        _map.ClearBuildingPreview();

        if (_selBuilding != null)
            ShowBuildingInfo(_selBuilding);
        else
            _hud.HideInfo();
    }

    /// <summary>悬停「升级」：把选中建筑临时换成下一级的模型，并把数值切成「=> 新值」。</summary>
    private void OnUpgradeHovered()
    {
        if (_selBuilding == null || _selBuilding.Owner != PlayerId.Player)
            return;
        if (_selBuilding.Level >= _selBuilding.Def.MaxLevel)
            return;

        _map.PreviewBuildingLevel(_selBuilding, _selBuilding.Level + 1);
        ShowBuildingInfo(_selBuilding, showDelta: true);
    }

    private void OnExtraFireClicked()    {
        if (_selBuilding == null)
            return;

        BuildingInst b = _selBuilding;
        if (!_state.TryExtraFire(b, out string message))
        {
            _hud.Toast(message);
            return;
        }

        _hud.Toast(message);
        AudioManager.PlayAttack(b.DefId);
        _map.FlashCell(b.Coord, new Color("FF6A4D"));
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        ShowBuildingInfo(b);
        RefreshBuildingHighlights(b);
        CheckGameOver();
    }

    private void ClearSelection()
    {
        _selUnit = null;
        _selBuilding = null;
        _map.ClearHints();
        _map.SetSelectedUnit(-1);
        _map.SetSelectedBuilding(-1);
        _hud.HideInfo();
        _hud.HideSpawnPanel();   // 取消选中建筑 → 出兵面板一起收起
    }

    private void RefreshAll()
    {
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--anim-log") >= 0)
            GD.Print($"[Refresh] t={Time.GetTicksMsec()}");
        // 手牌要重建之前，先把「因为失去发射井而被移除的卡」烧掉（需要旧布局的卡面位置）
        FlushBurnedCards();

        _hud.RefreshAll();
        _map.ShowHover(_hover);
    }

    /// <summary>把本帧因失去发射井而移除的己方卡牌，播一个燃烧特效。</summary>
    private void FlushBurnedCards()
    {
        if (_state == null || _state.RemovedCards.Count == 0)
            return;

        var mine = _state.RemovedCards
            .Where(r => r.Owner == PlayerId.Player)
            .Select(r => r.Card)
            .ToList();

        _state.RemovedCards.Clear();

        if (mine.Count > 0)
            _hud.BurnCards(mine);
    }

    private void CheckGameOver()
    {
        if (_state.Phase != BattlePhase.GameOver || _resultShown)
            return;

        _resultShown = true;

        BuildingInst playerHq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Player && b.IsHq);
        BuildingInst enemyHq = _state.Buildings.FirstOrDefault(b => b.Owner == PlayerId.Enemy && b.IsHq);
        int playerHp = playerHq?.Hp ?? 0;
        int enemyHp = enemyHq?.Hp ?? 0;
        bool win = _state.Winner == PlayerId.Player;
        string mapName = Loc.Name(_state.Map.NameZh, _state.Map.NameEn);

        MatchHistory.EnsureLoaded();
        MatchHistory.Add(new MatchRecord
        {
            Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            MapId = _state.Map.Id,
            MapName = mapName,
            Difficulty = (int)_state.Difficulty,
            Turns = _state.TurnNumber,
            Win = win,
            PlayerHqHp = Mathf.Max(0, playerHp),
            EnemyHqHp = Mathf.Max(0, enemyHp),
        });

        Session.LastTurns = _state.TurnNumber;
        Session.LastWin = win;

        AudioManager.StopBgm(0.8f);

        // 胜利 / 失败音效（走独立通道，不会被战场 SFX 抢掉）
        if (win)
            AudioManager.PlayVictory();
        else
            AudioManager.PlayDefeat();

        _hud.Toast(win ? Loc.T("battle.victory") : Loc.T("battle.defeat"));

        _result = new ResultScreen();
        AddChild(_result);
        _result.RematchClicked += () => GetTree().ReloadCurrentScene();
        _result.MenuClicked += () => GetTree().ChangeSceneToFile("res://scenes/mainMenu.tscn");
        _result.Show(win, _state.TurnNumber, mapName, (int)_state.Difficulty, playerHp, enemyHp);
    }
}
