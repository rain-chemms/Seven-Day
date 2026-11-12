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

        // 开发调试：--show-main 低等级也显示主体网格（检查各级网格是否互补）
        ModelUtil.ShowMainMeshAlways = OS.GetCmdlineUserArgs().Contains("--show-main");

        // 开发调试：--demo-barracks3 兵营三级并排
        if (OS.GetCmdlineUserArgs().Contains("--demo-barracks3"))
            DemoLevelsFlow("barracks");

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
            LightEnergy = 1.0f,
            LightColor = new Color(1f, 0.96f, 0.88f),
            ShadowEnabled = true,
            RotationDegrees = new Vector3(-52, -38, 0),
        };
        AddChild(sun);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color("BFD8E8"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("8FA6B8"),
            AmbientLightEnergy = 0.85f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
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
    }

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
    private bool TryPlayCardAt(int index, HexCoord coord)
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

    /// <summary>关闭建造菜单，同时清掉目标格指示。</summary>
    private void CloseBuildMenu()
    {
        _hud.HideBuildMenu();
        _buildTarget = null;
        _map?.SetBuildMarker(null);
    }

    private void OnUpgradeClicked()
    {
        if (_selBuilding == null || _selBuilding.Owner != PlayerId.Player)
            return;
        if (!_state.TryUpgrade(_selBuilding, out string error))
        {
            _hud.Toast(error);
            return;
        }

        AudioManager.PlayUpgrade(_selBuilding.DefId);
        _map.PunchBuilding(_selBuilding.Id);
        _map.SyncEntities();
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

        // 3) 点己方单位 → 选中
        UnitInst own = cell.Units.FirstOrDefault(u => u.Alive && u.Owner == PlayerId.Player);
        if (own != null)
        {
            AudioManager.PlaySelect(own.DefId);
            SelectUnit(own);
            return;
        }

        // 4) 点建筑
        if (cell.Building != null)
        {
            _selUnit = null;
            _selBuilding = cell.Building;
            AudioManager.PlaySelect(cell.Building.DefId);
            _map.ClearHints();
            CloseBuildMenu();
            ShowBuildingInfo(cell.Building);
            if (cell.Building.Owner == PlayerId.Player)
                _map.ShowHints(HexCoord.Spiral(cell.Building.Coord, cell.Building.ControlRadius),
                    cell.Building.Owner == PlayerId.Player ? new Color("3D7BD6") : new Color("D24A3A"));
            return;
        }

        // 5) 空地 → 己方控制区内弹建造菜单
        _selUnit = null;
        _selBuilding = null;
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

        if (u.Owner == PlayerId.Player)
        {
            _map.ShowHints(_state.MoveRange(u), new Color("5BC0EB"));
            var attacks = _state.AttackTargets(u)
                .Select(t => t.building != null ? t.building.Coord : t.unit.Coord)
                .Distinct();
            _map.ShowHints(attacks, new Color("E8563F"));
        }

        _hud.ShowInfo(
            $"{u.Def.NameZh}  {u.Hp}/{u.MaxHp}",
            $"攻击 {u.Def.Damage}　移动 {u.Def.Move}　射程 {u.Def.Range}\n" +
            $"{(u.IsAir ? "空中单位，可越过地形" : "地面单位")}" +
            $"{(u.Def.CanHitAir ? "，可对空" : "")}\n" +
            $"{(u.Moved ? "已移动" : "可移动")}　{(u.Attacked ? "已攻击" : "可攻击")}");
    }

    private void ShowBuildingInfo(BuildingInst b)
    {
        string title = $"{b.Def.NameZh}  Lv.{b.Level}  {b.Hp}/{b.MaxHp}";
        string income = b.Def.IncomeAt(b.Level) > 0 ? $"每回合金币 +{b.Def.IncomeAt(b.Level)}\n" : "";
        string attack = b.Def.AttackRange > 0 ? $"攻击 {b.Def.DamageAt(b.Level)}　射程 {b.Def.AttackRange}\n" : "";
        string upgrade = b.Level >= b.Def.MaxLevel
            ? "已满级"
            : $"升级到 Lv.{b.Level + 1} 需要 {b.Def.UpgradeCostAt(b.Level)}$";

        _hud.ShowInfo(title, $"{income}{attack}控制半径 {b.ControlRadius}\n{upgrade}",
            b.Def.Preview, showButtons: b.Owner == PlayerId.Player && !b.IsHq);
    }

    private void ClearSelection()
    {
        _selUnit = null;
        _selBuilding = null;
        _map.ClearHints();
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
