using System;
using System.Collections.Generic;
using System.Linq;
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

    private UnitInst _selUnit;
    private BuildingInst _selBuilding;
    private HexCoord? _hover;
    private int _selCard = -1;
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
        GameDatabase.EnsureLoaded();
        ToonMaterial.Disabled = OS.GetCmdlineUserArgs().Contains("--nomats");
        _mouseScreen = GetViewport().GetVisibleRect().Size * 0.5f;
        BuildEnvironment();
        _inputGateUntil = Time.GetTicksMsec() + 400;

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

        _state.BeginTurn(PlayerId.Player);
        // 补充设计：开局多给 3 张手牌，不然第一回合几乎没得打
        _state.DrawCard(_state.Player, 3);
        _map.SyncEntities();
        _map.RefreshZones();
        _hud.Setup(_state);
        _hud.Toast(Loc.T("battle.intro"));
        AudioManager.SetIntensity(0);

        // 开发调试：--autoplay 让 AI 替双方轮流走，用来跑通整局逻辑
        if (OS.GetCmdlineUserArgs().Contains("--autoplay"))
        {
            var t = GetTree().CreateTimer(0.3);
            t.Timeout += RunAutoStep;
        }

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

        BuildingInst turret = _state.SpawnBuilding("gunTurret", PlayerId.Player, spot.Value);
        UnitInst foe = _state.SpawnUnit("infantry", PlayerId.Enemy, spot.Value.Neighbor(2));

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
        _hud.DemolishClicked += OnDemolishClicked;
        _hud.ExtraFireClicked += OnExtraFireClicked;
        _hud.BuildHovered += OnBuildHovered;
        _hud.UpgradeHovered += OnUpgradeHovered;
        _hud.PreviewUnhovered += OnPreviewUnhovered;
        _hud.CancelClicked += () => { ClearSelection(); RefreshAll(); };
    }

    private void OnCardClicked(int index)
    {
        if (_state.Phase != BattlePhase.PlayerTurn)
            return;

        if (index < 0 || index >= _state.Player.Hand.Count)
            return;

        CardDef card = GameDatabase.Card(_state.Player.Hand[index]);
        if (card == null)
            return;

        if (_selCard == index)
        {
            // 再点一次：不需要地块的卡直接打出，其它卡取消选中
            if (card.KindEnum == CardKind.Global)
            {
                TryPlayGlobal(card, null);
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
        CloseBuildMenu();
        _map.ClearHints();

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
            var enemies = _state.Cells.Values
                .Where(c => c.Units.Any(u => u.Alive && u.Owner == PlayerId.Enemy) ||
                            (c.Building != null && c.Building.Owner == PlayerId.Enemy))
                .Select(c => c.Coord);
            _map.ShowHints(enemies, new Color("E8563F"));
            _hud.Toast($"已选择「{card.NameZh}」：点击要打击的格子");
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
            TryPlayGlobal(card, null);
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
            TryPlayGlobal(card, null);
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

        // 先做一次校验：打不出去就直接走原逻辑弹提示，别让它飞到一半才发现无效
        if (card.KindEnum == CardKind.Unit &&
            !_state.CanDeployCard(card.Id, PlayerId.Player, coord, out _))
        {
            return ApplyCardAt(index, coord);
        }

        if (card.KindEnum == CardKind.Strike)
        {
            bool blocked = _state.Player.Gold < card.Cost ||
                           (card.Effect == "airstrike" &&
                            _state.InEnemyAaRange(coord, PlayerId.Player));
            if (blocked)
                return ApplyCardAt(index, coord);
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

        _state.PlayUnitCard(_state.Player, card.Id, coord, out _);
        _selCard = -1;
        _hud.SetSelectedCard(-1);
        _map.ClearHints();
        AudioManager.PlayMove(card.Unit);
        _map.FlashCell(coord, new Color("7FD35B"));
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        _hud.Toast($"已部署 {card.NameZh}");
        return true;
    }

    private void TryPlayGlobal(CardDef card, HexCoord? target)
    {
        if (_state.Player.Gold < card.Cost)
        {
            _hud.Toast("金币不足");
            return;
        }

        string result = _state.PlayGlobalCard(_state.Player, card.Id, target, out string error);
        if (!string.IsNullOrEmpty(error))
        {
            _hud.Toast(error);
            return;
        }

        _hud.Toast(result);
        _selCard = -1;
        _hud.SetSelectedCard(-1);
        _map.ClearHints();
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        CheckGameOver();
    }

    private void OnEndTurn()
    {
        if (_state.Phase != BattlePhase.PlayerTurn || _enemyBusy)
            return;

        ClearSelection();
        _state.EndTurn();
        AudioManager.SetIntensity(_state.TurnNumber < 4 ? 0 : _state.TurnNumber < 8 ? 1 : 2);
        RefreshAll();

        if (_state.Phase == BattlePhase.EnemyTurn)
            CallDeferred(nameof(RunEnemyTurnDeferred));
    }

    private void RunEnemyTurnDeferred()
    {
        _enemyBusy = true;
        RefreshAll();
        _hud.Toast("敌方行动中…");

        var timer = GetTree().CreateTimer(0.45);
        timer.Timeout += () =>
        {
            string log = new AIController(_state, _state.Difficulty).RunTurn();
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
            _enemyBusy = false;
            RefreshAll();
            CheckGameOver();
        };
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
        _hud.Toast("升级完成");
    }

    private void OnDemolishClicked()
    {
        if (_selBuilding == null || _selBuilding.Owner != PlayerId.Player || _selBuilding.IsHq)
            return;

        _state.Demolish(_selBuilding);
        _selBuilding = null;
        _hud.HideInfo();
        _map.SyncEntities();
        _map.RefreshZones();
        RefreshAll();
        _hud.Toast("已拆除");
    }

    // ─────────────────────────── 输入 ───────────────────────────

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_state == null)
            return;

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
                case Key.E:
                    _rig.Rotate(22.5f);
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

        _hover = coord;
        _map.ShowHover(coord);
    }

    private void OnLeftClick(Vector2 screenPos)
    {
        if (Time.GetTicksMsec() < _inputGateUntil)
            return;

        if (!_rig.TryPickGround(screenPos, HexMapView.TileTop, out Vector3 world))
            return;

        HexCoord coord = HexLayout.FromWorld(world);
        Cell cell = _state.Cell(coord);
        if (cell == null)
            return;

        if (_state.Phase != BattlePhase.PlayerTurn)
            return;

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
                string log = _state.PerformAttack(attacker, coord);
                _hud.Toast(string.IsNullOrEmpty(log) ? "没有可攻击的目标" : log);

                AudioManager.PlayAttack(attacker.DefId);
                // 攻击时转向目标：有炮塔的只转炮塔，其余整体转身；然后播放一次攻击动作
                Vector3 targetWorld = HexLayout.ToWorld(coord, HexMapView.TileTop);
                if (!_map.FaceUnitTurretToward(attacker, targetWorld))
                    _map.FaceUnitToward(attacker, targetWorld);
                _map.PlayUnitAttack(attacker.Id);
                _map.FlashCell(coord, new Color("E8563F"));
                foreach (UnitInst t in _state.UnitsAt(coord))
                    _map.PunchUnit(t.Id);
                if (_state.BuildingAt(coord) is { } hitBuilding)
                    _map.PunchBuilding(hitBuilding.Id);

                SelectUnit(attacker);
                _map.SyncEntities();
                _map.RefreshZones();
                RefreshAll();
                CheckGameOver();
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
        var cycle = new List<(UnitInst Unit, BuildingInst Building)>();
        foreach (UnitInst u in cell.Units.Where(x => x.Alive && x.Owner == PlayerId.Player))
            cycle.Add((u, null));

        if (cell.Building != null && cell.Building.Owner == PlayerId.Player && !cell.Building.IsDead)
            cycle.Add((null, cell.Building));

        if (cycle.Count > 0)
        {
            int index = -1;
            if (_selUnit != null && _selUnit.Coord == coord)
                index = cycle.FindIndex(e => e.Unit == _selUnit);
            else if (_selBuilding != null && _selBuilding.Coord == coord)
                index = cycle.FindIndex(e => e.Building == _selBuilding);

            (UnitInst Unit, BuildingInst Building) pick = cycle[(index + 1) % cycle.Count];
            if (pick.Unit != null)
            {
                AudioManager.PlaySelect(pick.Unit.DefId);
                SelectUnit(pick.Unit);
            }
            else
            {
                SelectBuilding(pick.Building);
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
        //    注意用 ClearSelection()：它会同时把单位/建筑的黄色描边与血条的「选中」状态清掉
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
            _hud.Toast("这里不能建造（需要在己方控制区、且不与敌方控制区重叠）");
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

        _hud.ShowInfo(
            $"{u.Def.NameZh}  {u.Hp}/{u.MaxHp}",
            $"攻击 {u.Def.Damage}　移动 {u.Def.Move}　射程 {u.Def.Range}\n" +
            $"{(u.IsAir ? "空中单位，可越过地形" : "地面单位")}" +
            $"{(u.Def.CanHitAir ? "，可对空" : "")}\n" +
            $"{(u.Moved ? "已移动" : "可移动")}　{(u.Attacked ? "已攻击" : "可攻击")}");
    }

    /// <summary>选中一座建筑：信息面板 + 控制区高亮 + （有攻击力时）红色可攻击范围。</summary>
    private void SelectBuilding(BuildingInst b)
    {
        if (b == null)
            return;

        _selUnit = null;
        _selBuilding = b;
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
        var lines = new List<string>(BuildStatLines(b.Def, level, next, canUp && showDelta));

        string upgrade = canUp
            ? $"升级到 Lv.{next} 需要 {b.Def.UpgradeCostAt(level)}$"
            : "已满级";

        // 炮台之类可以花钱多打一炮
        bool canExtra = b.Owner == PlayerId.Player && b.Def.ExtraFireCost > 0 &&
                        b.Def.AttackRange > 0;
        bool extraReady = canExtra && !b.ExtraFired && _state.Player.Gold >= b.Def.ExtraFireCost;
        if (canExtra && !b.ExtraFired)
            upgrade += $"\n额外开炮 {b.Def.ExtraFireCost}$（每回合一次）";

        lines.Add(upgrade);

        string title = $"{b.Def.NameZh}  Lv.{level}  {b.Hp}/{b.MaxHp}";
        _hud.ShowInfo(title, string.Join("\n", lines),
            b.Def.Preview, showButtons: b.Owner == PlayerId.Player,
            extraFireCost: canExtra ? b.Def.ExtraFireCost : 0, extraFireReady: extraReady,
            showDemolish: !b.IsHq, canUpgrade: canUp);
    }

    /// <summary>建造前的信息预览：右侧面板显示一级的数值（没有按钮）。</summary>
    private void ShowBuildPreviewInfo(string defId)
    {
        BuildingDef def = GameDatabase.Building(defId);
        if (def == null)
            return;

        var lines = new List<string> { $"建造花费 {def.UpgradeCostAt(0)}$" };
        // 建造预览不显示「=> 新值」，保持和平时一致
        lines.AddRange(BuildStatLines(def, 1, Mathf.Min(2, def.MaxLevel), false));

        if (def.MaxLevel > 1)
            lines.Add($"升级到 Lv.2 需要 {def.UpgradeCostAt(1)}$");

        _hud.ShowInfo($"{def.NameZh}  Lv.1", string.Join("\n", lines),
            def.Preview, showButtons: false);
    }

    /// <summary>生成「每回合金币 / 攻击 / 控制半径」这几行，有下一级时带绿色新值。</summary>
    private static IEnumerable<string> BuildStatLines(BuildingDef def, int level, int next, bool canUp)
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
        if (income > 0 || incomeNext > 0)
            yield return Stat("每回合金币", income, incomeNext, "+");

        if (def.AttackRange > 0)
        {
            int dmg = def.DamageAt(level);
            int dmgNext = canUp ? def.DamageAt(next) : dmg;
            yield return $"{Stat("攻击", dmg, dmgNext)}　射程 {def.AttackRange}";
        }

        int radius = def.ControlAt(level);
        int radiusNext = canUp ? def.ControlAt(next) : radius;
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
    }

    private void RefreshAll()
    {
        _hud.RefreshAll();
        _map.ShowHover(_hover);
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
        _hud.Toast(win ? Loc.T("battle.victory") : Loc.T("battle.defeat"));

        _result = new ResultScreen();
        AddChild(_result);
        _result.RematchClicked += () => GetTree().ReloadCurrentScene();
        _result.MenuClicked += () => GetTree().ChangeSceneToFile("res://scenes/mainMenu.tscn");
        _result.Show(win, _state.TurnNumber, mapName, (int)_state.Difficulty, playerHp, enemyHp);
    }
}
