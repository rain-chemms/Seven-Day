using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.Battle;

/// <summary>
/// 把 BattleState 渲染成 3D 战场：六边形地块、控制区描边、选中高亮、建筑与单位模型。
/// </summary>
public partial class HexMapView : Node3D
{
    public const float TileTop = 0.45f;

    private static readonly Color PlayerZone = new("3D7BD6");
    private static readonly Color EnemyZone = new("D24A3A");
    private static readonly Color ContestedZone = new("E0A020");
    private static readonly Color MoveHint = new("5BC0EB");
    private static readonly Color AttackHint = new("E8563F");
    private static readonly Color HoverHint = new("FFF2B0");

    private BattleState _state;
    private Mesh _hexMesh;
    private Mesh _ringMesh;
    private Mesh _borderMesh;
    private Mesh _markerRingMesh;
    private Mesh _attackFillMesh;
    private Mesh _attackRingMesh;
    private readonly Dictionary<HexCoord, Node3D> _attackMarks = new();
    private Mesh _fillMesh;

    private readonly Dictionary<TerrainType, Material> _terrainMats = new();
    private readonly Dictionary<HexCoord, MeshInstance3D> _tiles = new();
    private readonly Dictionary<HexCoord, MeshInstance3D> _zoneRings = new();
    private readonly Dictionary<HexCoord, MeshInstance3D> _hints = new();
    private readonly Dictionary<int, Node3D> _buildingViews = new();
    private readonly Dictionary<int, int> _buildingLevels = new();
    private readonly Dictionary<int, float> _buildingHeights = new();
    private readonly Dictionary<int, Node3D> _unitViews = new();
    private readonly Dictionary<int, UnitAnimator> _unitAnimators = new();
    private readonly Dictionary<int, Tween> _unitMoveTweens = new();
    private readonly Dictionary<int, (string Path, float Scale, PlayerId Owner)> _unitMaterials = new();
    private readonly Dictionary<int, float> _buildingTops = new();

    // 建造 / 升级预览
    private Node3D _previewView;
    private string _previewBuildDefId;
    private HexCoord _previewBuildCoord;
    private int _previewBuildingId = -1;
    private int _previewSavedLevel;

    /// <summary>当前被选中（黄色描边）的单位 id，-1 表示没有。</summary>
    private int _selectedUnitId = -1;

    private Node3D _ringRoot;
    private Node3D _borderRoot;
    private Node3D _hintRoot;
    private Node3D _buildingRoot;
    private Node3D _unitRoot;
    private MeshInstance3D _hover;
    private Node3D _buildMarker;

    private StandardMaterial3D _ringMatPlayer;
    private StandardMaterial3D _ringMatEnemy;
    private StandardMaterial3D _ringMatContested;
    private StandardMaterial3D _borderMat;

    public override void _Ready()
    {
        _borderRoot = NewChild("TileBorders");
        _ringRoot = NewChild("ZoneRings");
        _hintRoot = NewChild("Hints");
        _buildingRoot = NewChild("Buildings");
        _unitRoot = NewChild("Units");

        LoadMeshes();
        BuildMaterials();

        _hover = new MeshInstance3D { Name = "Hover", Mesh = _fillMesh, Visible = false };
        // 不做深度穿透，否则会盖在建筑上面
        _hover.MaterialOverride = MakeUnshaded(new Color(HoverHint, 0.5f), noDepth: false);
        AddChild(_hover);
    }

    private Node3D NewChild(string name)
    {
        var n = new Node3D { Name = name };
        AddChild(n);
        return n;
    }

    private void LoadMeshes()
    {
        var scene = GD.Load<PackedScene>("res://gameAsset/models/hexTile/hexTile.gltf");
        Node inst = scene.Instantiate();
        MeshInstance3D mi = inst.GetChildren().OfType<MeshInstance3D>().FirstOrDefault()
                            ?? inst as MeshInstance3D;
        if (mi?.Mesh != null)
        {
            _hexMesh = mi.Mesh;
        }
        else
        {
            GD.PushError("[HexMapView] hexTile 模型里找不到 Mesh");
            _hexMesh = new PlaneMesh { Size = new Vector2(1.732f, 2f) };
        }

        inst.QueueFree();

        _ringMesh = BuildRingMesh(0.80f, 0.93f);
        _fillMesh = BuildFillMesh(0.98f);
        // 地块描边：贴着六边形内圈的一条深色带（0.92~1.0 半径）
        _borderMesh = BuildRingMesh(0.92f, 1.0f);
        // 建造目标格用的粗金圈
        _markerRingMesh = BuildRingMesh(0.72f, 0.92f);
        // 攻击范围标记：比格子小一圈的六边形
        _attackFillMesh = BuildFillMesh(0.62f);
        _attackRingMesh = BuildRingMesh(0.50f, 0.64f);
    }

    private void BuildMaterials()
    {
        (TerrainType t, string file)[] list =
        {
            (TerrainType.Grass, "hexTile_grass.png"),
            (TerrainType.Sand, "hexTile_sand.png"),
            (TerrainType.Dirt, "hexTile_dirt.png"),
            (TerrainType.Rock, "hexTile_rock.png"),
            (TerrainType.Snow, "hexTile_snow.png"),
            (TerrainType.Water, "hexTile_water.png"),
        };

        foreach ((TerrainType t, string file) in list)
        {
            var mat = new StandardMaterial3D
            {
                AlbedoTexture = GD.Load<Texture2D>("res://gameAsset/models/hexTile/" + file),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,
            };
            _terrainMats[t] = mat;
        }

        _ringMatPlayer = MakeUnshaded(new Color(PlayerZone, 0.95f), noDepth: false);
        _ringMatEnemy = MakeUnshaded(new Color(EnemyZone, 0.95f), noDepth: false);
        _ringMatContested = MakeUnshaded(new Color(ContestedZone, 0.95f), noDepth: false);

        // 地块描边：不透明、无光照，和 3渲2 描边同色 #3B2E16
        _borderMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.231f, 0.180f, 0.086f, 1f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    private static StandardMaterial3D MakeUnshaded(Color c, bool noDepth = false)
    {
        var m = new StandardMaterial3D
        {
            AlbedoColor = c,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        if (noDepth)
        {
            m.NoDepthTest = true;
            m.RenderPriority = 2;
        }

        return m;
    }

    // ─────────────────────────── 地图 ───────────────────────────

    public void Build(BattleState state)
    {
        _state = state;

        foreach (MeshInstance3D m in _tiles.Values)
            m.QueueFree();
        _tiles.Clear();

        foreach (Cell cell in state.Cells.Values)
        {
            var mi = new MeshInstance3D
            {
                Name = $"Tile_{cell.Coord.Q}_{cell.Coord.R}",
                Mesh = _hexMesh,
                Position = HexLayout.ToWorld(cell.Coord),
                MaterialOverride = _terrainMats[cell.Terrain],
            };
            AddChild(mi);
            _tiles[cell.Coord] = mi;

            var border = new MeshInstance3D
            {
                Name = $"Border_{cell.Coord.Q}_{cell.Coord.R}",
                Mesh = _borderMesh,
                Position = HexLayout.ToWorld(cell.Coord, TileTop + 0.004f),
                MaterialOverride = _borderMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _borderRoot.AddChild(border);
        }

        RefreshZones();
    }

    /// <summary>重画控制区描边。建筑增删/升级后调用。</summary>
    public void RefreshZones()
    {
        if (_state == null)
            return;

        foreach (MeshInstance3D m in _zoneRings.Values)
            m.QueueFree();
        _zoneRings.Clear();

        foreach (Cell cell in _state.Cells.Values)
        {
            if (!cell.ControlPlayer && !cell.ControlEnemy)
                continue;

            Material mat = cell.Contested
                ? _ringMatContested
                : cell.ControlPlayer ? _ringMatPlayer : _ringMatEnemy;

            var mi = new MeshInstance3D
            {
                Mesh = _ringMesh,
                Position = HexLayout.ToWorld(cell.Coord, TileTop + 0.012f),
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _ringRoot.AddChild(mi);
            _zoneRings[cell.Coord] = mi;
        }
    }

    // ─────────────────────────── 高亮 ───────────────────────────

    public void ClearHints()
    {
        foreach (MeshInstance3D m in _hints.Values)
            m.QueueFree();
        _hints.Clear();

        foreach (Node3D n in _attackMarks.Values)
            n.QueueFree();
        _attackMarks.Clear();
    }

    /// <summary>
    /// 标记「可攻击的格子」：比格子小一圈的红色六边形（内环 + 半透明填充）。
    /// 有敌人的格子用更实的红色，单纯在射程内的格子淡一点。
    /// </summary>
    public void ShowAttackMarks(IEnumerable<HexCoord> cells, ICollection<HexCoord> targets)
    {
        foreach (HexCoord c in cells)
        {
            if (_attackMarks.ContainsKey(c))
                continue;

            bool hasTarget = targets != null && targets.Contains(c);

            var root = new Node3D { Name = "AttackMark", Position = HexLayout.ToWorld(c, TileTop + 0.014f) };
            _hintRoot.AddChild(root);

            root.AddChild(new MeshInstance3D
            {
                Mesh = _attackFillMesh,
                MaterialOverride = MakeUnshaded(new Color("E8563F", hasTarget ? 0.46f : 0.20f), noDepth: false),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });

            root.AddChild(new MeshInstance3D
            {
                Mesh = _attackRingMesh,
                MaterialOverride = MakeUnshaded(new Color("FF6A4D", hasTarget ? 1f : 0.55f), noDepth: true),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });

            _attackMarks[c] = root;
        }
    }

    public void ShowHints(IEnumerable<HexCoord> coords, Color color)
    {
        foreach (HexCoord c in coords)
        {
            if (_hints.ContainsKey(c))
                continue;
            var mi = new MeshInstance3D
            {
                Mesh = _fillMesh,
                Position = HexLayout.ToWorld(c, TileTop + 0.008f),
                MaterialOverride = MakeUnshaded(new Color(color, 0.42f), noDepth: false),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _hintRoot.AddChild(mi);
            _hints[c] = mi;
        }
    }

    public void ShowHover(HexCoord? coord)
    {
        if (_hover == null)
            return;
        if (coord == null || !_state.Cells.ContainsKey(coord.Value))
        {
            _hover.Visible = false;
            return;
        }

        _hover.Visible = true;
        _hover.Position = HexLayout.ToWorld(coord.Value, TileTop + 0.02f);
    }

    // ─────────────────────────── 建筑 / 单位 ───────────────────────────

    public void SyncEntities()
    {
        if (_state == null)
            return;

        var seenBuildings = new HashSet<int>();
        foreach (BuildingInst b in _state.Buildings)
        {
            seenBuildings.Add(b.Id);

            bool recreate = !_buildingViews.ContainsKey(b.Id);
            if (!recreate && _buildingLevels.TryGetValue(b.Id, out int shown) && shown != b.Level)
            {
                // 升级了（玩家或 AI）→ 换模型：重新实例化，顺便重新按新等级缩放
                _buildingViews[b.Id].QueueFree();
                _buildingViews.Remove(b.Id);
                _buildingLevels.Remove(b.Id);
                recreate = true;
            }

            if (recreate)
                _buildingViews[b.Id] = CreateBuildingView(b);
        }

        foreach (int id in _buildingViews.Keys.Where(id => !seenBuildings.Contains(id)).ToList())
        {
            _buildingViews[id].QueueFree();
            _buildingViews.Remove(id);
        }

        var seenUnits = new HashSet<int>();
        foreach (UnitInst u in _state.Units.Where(u => u.Alive))
        {
            seenUnits.Add(u.Id);
            if (!_unitViews.ContainsKey(u.Id))
            {
                _unitViews[u.Id] = CreateUnitView(u);
            }
            else
            {
                // 位置变了就补间过去，形成移动动画（站上/离开建筑时的抬高也是渐变）
                Node3D view = _unitViews[u.Id];
                Vector3 target = UnitPosition(u);
                if (!view.Position.IsEqualApprox(target))
                {
                    float dist = view.Position.DistanceTo(target);
                    float dur = Mathf.Clamp(dist * 0.22f, 0.28f, 0.6f);
                    Node3D movingView = view;
                    // 移动时面朝前进方向
                    FaceViewToward(movingView, target);

                    // 上一次的位移动画先杀掉，避免两条补间叠加、Finished 乱序
                    if (_unitMoveTweens.TryGetValue(u.Id, out Tween previous) && previous != null &&
                        previous.IsValid())
                    {
                        previous.Kill();
                    }

                    Tween tween = view.GetTree()?.CreateTween();
                    if (tween != null)
                    {
                        _unitMoveTweens[u.Id] = tween;
                        tween.TweenProperty(movingView, "position", target, dur)
                            .SetTrans(Tween.TransitionType.Sine)
                            .SetEase(Tween.EaseType.InOut);

                        if (_unitAnimators.TryGetValue(u.Id, out UnitAnimator mover))
                        {
                            // 走动时播放 Move，落地换回 Idle；炮塔回正
                            mover.PlayMove();
                            mover.ResetTurret();
                            tween.Finished += () =>
                            {
                                // 只有真的到地方了才回 Idle（防止中途又有新目标）
                                if (movingView.Position.IsEqualApprox(target))
                                    mover.PlayIdle();
                                _unitMoveTweens.Remove(u.Id);
                            };
                        }
                    }
                }
            }
        }

        foreach (int id in _unitViews.Keys.Where(id => !seenUnits.Contains(id)).ToList())
        {
            _unitViews[id].QueueFree();
            _unitViews.Remove(id);
            _unitAnimators.Remove(id);
            _unitMoveTweens.Remove(id);
            _unitMaterials.Remove(id);
        }
    }

    /// <summary>建筑目标格指示：金色脉动圈，提示「建筑会落在这里」。</summary>
    public void SetBuildMarker(HexCoord? coord)
    {
        if (coord == null)
        {
            if (_buildMarker != null)
            {
                _buildMarker.QueueFree();
                _buildMarker = null;
            }

            return;
        }

        if (_buildMarker == null)
        {
            _buildMarker = new Node3D { Name = "BuildMarker" };
            AddChild(_buildMarker);

            var fill = new MeshInstance3D
            {
                Mesh = _fillMesh,
                MaterialOverride = MakeUnshaded(new Color("F0C052", 0.42f), noDepth: false),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _buildMarker.AddChild(fill);

            var ring = new MeshInstance3D
            {
                Mesh = _markerRingMesh,
                MaterialOverride = MakeUnshaded(new Color("FFD35A", 1f), noDepth: true),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _buildMarker.AddChild(ring);

            // 悬在格子上方的金色小徽标：上下浮动 + 缓慢旋转
            var badge = new MeshInstance3D
            {
                Name = "Badge",
                Mesh = _fillMesh,
                Scale = Vector3.One * 0.55f,
                Position = new Vector3(0, 1.25f, 0),
                MaterialOverride = MakeUnshaded(new Color("FFD35A", 0.95f), noDepth: true),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _buildMarker.AddChild(badge);

            Tween badgeTween = CreateTween();
            badgeTween.SetLoops();
            badgeTween.SetParallel(true);
            badgeTween.TweenProperty(badge, "position:y", 1.55f, 0.55f)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
            badgeTween.TweenProperty(badge, "rotation:y", Mathf.Tau, 1.6f)
                .SetTrans(Tween.TransitionType.Linear);
            badgeTween.Chain();
            badgeTween.TweenProperty(badge, "position:y", 1.15f, 0.55f)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);

            // 地面圈呼吸脉动
            Tween pulse = CreateTween();
            pulse.SetLoops();
            pulse.TweenProperty(_buildMarker, "scale", new Vector3(1.08f, 1f, 1.08f), 0.55f)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
            pulse.TweenProperty(_buildMarker, "scale", new Vector3(1f, 1f, 1f), 0.55f)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        }

        _buildMarker.Position = HexLayout.ToWorld(coord.Value, TileTop + 0.02f);
        _buildMarker.Visible = true;
    }

    /// <summary>调试：放大某个建筑视图，便于近距离看结构。</summary>
    public void ScaleBuildingView(int buildingId, float k)
    {
        if (_buildingViews.TryGetValue(buildingId, out Node3D view))
            view.Scale = Vector3.One * k;
    }

    /// <summary>调试：把某个建筑每个网格（含隐藏的）的顶点按高度分桶。</summary>
    public void DumpVertexHeights(int buildingId)
    {
        if (!_buildingViews.TryGetValue(buildingId, out Node3D view))
            return;

        foreach (MeshInstance3D mi in view.FindChildren("*", "MeshInstance3D", true, false)
                     .Cast<MeshInstance3D>())
        {
            if (mi.Mesh == null)
                continue;

            var pts = new List<Vector3>();
            Vector3[] faces = mi.Mesh.GetFaces();
            int step = Mathf.Max(1, faces.Length / 4000);
            for (int i = 0; i < faces.Length; i += step)
                pts.Add(mi.Transform * faces[i]);

            if (pts.Count == 0)
                continue;

            float minY = pts.Min(p => p.Y);
            float maxY = pts.Max(p => p.Y);
            const int Bins = 10;
            var counts = new int[Bins];
            foreach (Vector3 p in pts)
            {
                int b = Mathf.Clamp((int)((p.Y - minY) / Mathf.Max(maxY - minY, 0.001f) * Bins), 0, Bins - 1);
                counts[b]++;
            }

            string hist = string.Join(" ", counts.Select((c, i) =>
                $"{minY + (maxY - minY) * i / Bins:F1}:{c}"));
            GD.Print($"[Heights] {mi.Name} vis={mi.Visible} y={minY:F2}..{maxY:F2} {hist}");
        }
    }

    /// <summary>调试：打印每个建筑里每个网格的可见性与包围盒。</summary>
    public void DumpMeshNames()
    {
        foreach (KeyValuePair<int, Node3D> kv in _buildingViews)
        {
            foreach (MeshInstance3D mi in kv.Value.FindChildren("*", "MeshInstance3D", true, false)
                         .Cast<MeshInstance3D>())
            {
                GD.Print($"[Mesh] b{kv.Key} {kv.Value.Name} node={mi.Name} " +
                         $"res={mi.Mesh?.ResourceName} vis={mi.Visible} inTree={mi.IsVisibleInTree()} " +
                         $"chain={mi.GetMeta("origChain", "").AsString()} aabb={mi.GetAabb()}");
            }
        }
    }

    /// <summary>调试：打印每个建筑视图的世界包围盒与中心偏移。</summary>
    public void DumpBuildingBounds(BattleState state)
    {
        foreach (BuildingInst b in state.Buildings)
        {
            if (!_buildingViews.TryGetValue(b.Id, out Node3D view))
                continue;

            Aabb? total = null;
            Transform3D inv = Transform.AffineInverse();
            foreach (MeshInstance3D mi in view.FindChildren("*", "MeshInstance3D", true, false)
                         .Cast<MeshInstance3D>())
            {
                if (mi.Mesh == null || !mi.IsVisibleInTree())
                    continue;
                Aabb a = inv * mi.GlobalTransform * mi.GetAabb();
                total = total == null ? a : total.Value.Merge(a);
            }

            if (total == null)
                continue;

            Aabb box = total.Value;
            Vector3 hexCenter = HexLayout.ToWorld(b.Coord, TileTop);
            Vector3 aabbCenter = box.Position + box.Size * 0.5f;
            Vector3 offset = aabbCenter - hexCenter;
            GD.Print($"[Fit] {b.DefId} Lv{b.Level} size=({box.Size.X:F2}, {box.Size.Z:F2}) " +
                     $"centerOff=({offset.X:F2}, {offset.Z:F2})");
        }
    }
    public void FlashCell(HexCoord coord, Color color, float seconds = 0.45f)
    {
        var mi = new MeshInstance3D
        {
            Mesh = _fillMesh,
            Position = HexLayout.ToWorld(coord, TileTop + 0.03f),
            MaterialOverride = MakeUnshaded(color, noDepth: true),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mi);

        var tween = CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(mi, "scale", new Vector3(1.6f, 1f, 1.6f), seconds);
        tween.TweenProperty(mi.MaterialOverride, "albedo_color", new Color(color, 0f), seconds);
        tween.SetParallel(false);
        tween.TweenCallback(Callable.From(mi.QueueFree));
    }

    /// <summary>建筑升级/受击的缩放弹一下。</summary>
    public void PunchBuilding(int buildingId)
    {
        if (!_buildingViews.TryGetValue(buildingId, out Node3D view))
            return;
        view.Scale = Vector3.One * 1.18f;
        CreateTween().TweenProperty(view, "scale", Vector3.One, 0.35f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    /// <summary>单位受击时缩一下。</summary>
    public void PunchUnit(int unitId)
    {
        if (!_unitViews.TryGetValue(unitId, out Node3D view))
            return;
        view.Scale = Vector3.One * 0.82f;
        CreateTween().TweenProperty(view, "scale", Vector3.One, 0.3f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    /// <summary>同格单位横向散开的半径（相对六边形外接半径 1.0）。</summary>
    private const float UnitSpreadRadius = 0.42f;

    /// <summary>每多一层，再往外错开这么多，形成错落感（不叠在一起）。</summary>
    private const float UnitStackStagger = 0.09f;

    /// <summary>散开半径的上限，别把单位推出格子（六边形内切半径约 0.866）。</summary>
    private const float UnitSpreadMax = 0.76f;

    /// <summary>同格单位每多一个只往上抬一点点（米）。</summary>
    private const float UnitStackLift = 0.08f;

    /// <summary>
    /// 单位站位：
    ///  · 默认站在格子顶面；
    ///  · 这一格有建筑时抬到建筑屋顶，避免穿模；
    ///  · 同一格有多个单位时，按顺序把它们横向散到格子边角，每个只抬高一点点。
    /// 位置变化由调用方的补间做渐变。
    /// </summary>
    private Vector3 UnitPosition(UnitInst u)
    {
        Vector3 p = HexLayout.ToWorld(u.Coord);
        float y = TileTop;

        Cell cell = _state?.Cell(u.Coord);
        if (cell?.Building != null && _buildingTops.TryGetValue(cell.Building.Id, out float top) && top > 0f)
            y = TileTop + top * 0.96f;

        if (cell != null)
        {
            GetStackSlot(cell, u, out int index, out int count);
            Vector2 offset = SpreadOffset(index, count);
            p.X += offset.X;
            p.Z += offset.Y;
            y += index * UnitStackLift;
        }

        if (u.IsAir)
            y += 1.25f;

        p.Y = y;
        return p;
    }

    /// <summary>该单位在这一格里排第几（只数活着的单位），以及这一格一共几个。</summary>
    private static void GetStackSlot(Cell cell, UnitInst u, out int index, out int count)
    {
        index = 0;
        count = 0;

        foreach (UnitInst other in cell.Units)
        {
            if (!other.Alive)
                continue;
            if (other.Id == u.Id)
                index = count;
            count++;
        }
    }

    /// <summary>
    /// 把同格的单位均匀散到格子内圈：1 个站中间，2 个左右分开，
    /// 3 个以上沿圆均匀分布（最多用到六边形的 6 个方位）。
    /// 每一层再额外往外错开一点点（<see cref="UnitStackStagger"/>），
    /// 这样叠在一起时每个单位都露得出来，不会糊成一坨。
    /// </summary>
    private static Vector2 SpreadOffset(int index, int count)
    {
        if (count <= 1)
            return Vector2.Zero;

        float radius = Mathf.Min(UnitSpreadRadius + index * UnitStackStagger, UnitSpreadMax);

        if (count == 2)
            return new Vector2(index == 0 ? -radius : radius, 0f);

        float step = Mathf.Tau / count;
        float angle = -Mathf.Pi * 0.5f + index * step;
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
    }

    private Node3D CreateBuildingView(BuildingInst b, int? levelOverride = null)
    {
        int level = levelOverride ?? b.Level;
        var root = new Node3D { Name = "B_" + b.DefId + "_" + b.Id, Position = HexLayout.ToWorld(b.Coord, TileTop) };
        _buildingRoot.AddChild(root);

        string path = string.IsNullOrEmpty(b.Def.Model) ? "" : b.Def.Model;
        // 1.70 = 六边形内切圆直径（0.866×2）留一点余量：不管建筑怎么转，底座都不会顶出格子
        Node3D model = ModelUtil.SpawnModel(path, root, 1.70f, level, out float height,
            outlineColor: ToonMaterial.OutlineFor(b.Owner));
        if (model != null)
        {
            model.Name = b.DefId;
            _buildingLevels[b.Id] = b.Level;
            _buildingHeights[b.Id] = height;

            // 当前等级「实际可见」的高度（单位站屋顶、血条位置都用它；
            // height 是按全部网格量的，会用成三级的高度）
            float top = 0f;
            Transform3D invRoot = root.GlobalTransform.AffineInverse();
            foreach (MeshInstance3D mi in root.FindChildren("*", "MeshInstance3D", true, false)
                         .Cast<MeshInstance3D>())
            {
                if (mi.Mesh == null || !mi.Visible)
                    continue;
                Aabb a = invRoot * mi.GlobalTransform * mi.GetAabb();
                top = Mathf.Max(top, a.Position.Y + a.Size.Y);
            }

            _buildingTops[b.Id] = top > 0.01f ? top : height;
        }
        else
        {
            // 没有模型时用色块占位，保证逻辑可测
            var mi = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(1.1f, 0.8f, 1.1f) },
                Position = new Vector3(0, 0.4f, 0),
                MaterialOverride = MakeUnshaded(b.Owner == PlayerId.Player
                    ? new Color("6C7C4A")
                    : new Color("C2543E")),
            };
            root.AddChild(mi);
        }

        RotateToward(root, b.Owner);
        return root;
    }

    private Node3D CreateUnitView(UnitInst u)
    {
        Vector3 target = UnitPosition(u);
        // 先站在格子顶面，再用补间升到目标高度（要站在建筑上时是「缓缓升上去」）
        Vector3 start = new(target.X, TileTop + (u.IsAir ? 1.25f : 0f), target.Z);

        var root = new Node3D { Name = "U_" + u.DefId + "_" + u.Id, Position = start };
        _unitRoot.AddChild(root);

        if (!start.IsEqualApprox(target))
        {
            CreateTween()
                .TweenProperty(root, "position", target, 0.55f)
                .SetDelay(0.05f)
                .SetTrans(Tween.TransitionType.Sine)
                .SetEase(Tween.EaseType.InOut);
        }

        string path = string.IsNullOrEmpty(u.Def.Model) ? "" : u.Def.Model;
        Node3D model = ModelUtil.SpawnModel(path, root, u.IsAir ? 1.3f : 1.05f, 0, out _,
            outlineColor: ToonMaterial.OutlineFor(u.Owner));
        if (model != null)
        {
            model.Name = u.DefId;
            _unitMaterials[u.Id] = (path, model.Scale.X, u.Owner);

            UnitAnimator animator = UnitAnimator.Build(model);
            if (animator.HasAnimations)
            {
                _unitAnimators[u.Id] = animator;
                animator.PlayIdle();
            }
            else
            {
                animator.QueueFree();
            }
        }
        else
        {
            var mi = new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.28f, Height = 0.9f },
                Position = new Vector3(0, 0.45f, 0),
                MaterialOverride = MakeUnshaded(u.Owner == PlayerId.Player
                    ? new Color("3D7BD6")
                    : new Color("D24A3A")),
            };
            root.AddChild(mi);
        }

        RotateToward(root, u.Owner);
        return root;
    }

    /// <summary>玩家在左、敌人在右，模型朝向大致相对。</summary>
    private static void RotateToward(Node3D node, PlayerId owner)
    {
        node.RotationDegrees = new Vector3(0, owner == PlayerId.Player ? 90 : -90, 0);
    }

    /// <summary>让单位转向某个世界坐标（模型的正面是本地 +Z）。</summary>
    public void FaceUnitToward(UnitInst u, Vector3 worldTarget)
    {
        if (u == null || !_unitViews.TryGetValue(u.Id, out Node3D view))
            return;

        FaceViewToward(view, worldTarget);
    }

    /// <summary>把视图的朝向补间到「面向 target」。</summary>
    private void FaceViewToward(Node3D view, Vector3 worldTarget)
    {
        Vector3 dir = worldTarget - view.Position;
        dir.Y = 0f;
        if (dir.LengthSquared() < 0.0001f)
            return;

        float yaw = Mathf.RadToDeg(Mathf.Atan2(dir.X, dir.Z));
        CreateTween()
            .TweenProperty(view, "rotation_degrees:y", yaw, 0.22f)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
    }

    /// <summary>调试：单位当前朝向（度）。</summary>
    public float DebugUnitYaw(int unitId) =>
        _unitViews.TryGetValue(unitId, out Node3D v) ? v.RotationDegrees.Y : 0f;

    /// <summary>
    /// 有炮塔的单位（坦克）只转炮塔、车身保持原朝向；没有炮塔的返回 false，由调用方整体转身。
    /// </summary>
    public bool FaceUnitTurretToward(UnitInst u, Vector3 worldTarget)
    {
        if (u == null || !_unitViews.TryGetValue(u.Id, out Node3D view))
            return false;
        if (!_unitAnimators.TryGetValue(u.Id, out UnitAnimator animator) || !animator.HasTurret)
            return false;

        Vector3 dir = worldTarget - view.Position;
        dir.Y = 0f;
        if (dir.LengthSquared() < 0.0001f)
            return true;

        float worldYaw = Mathf.RadToDeg(Mathf.Atan2(dir.X, dir.Z));
        animator.SetTurretYaw(worldYaw - view.RotationDegrees.Y);
        return true;
    }

    /// <summary>炮塔回到车头方向（移动时用）。</summary>
    public void ResetUnitTurret(int unitId)
    {
        if (_unitAnimators.TryGetValue(unitId, out UnitAnimator animator))
            animator.ResetTurret();
    }

    /// <summary>调试：单位当前描边色（十六进制）。</summary>
    public string DebugUnitOutline(int unitId)
    {
        if (!_unitViews.TryGetValue(unitId, out Node3D view))
            return "no-view";

        foreach (MeshInstance3D mi in view.FindChildren("*", "MeshInstance3D", true, false)
                     .Cast<MeshInstance3D>())
        {
            if (mi.MaterialOverride is not ShaderMaterial cel || cel.NextPass is not ShaderMaterial outline)
                continue;

            var c = (Vector3)outline.GetShaderParameter("outline_color");
            return new Color(c.X, c.Y, c.Z).ToHtml(false);
        }

        return "no-material";
    }

    /// <summary>调试：单位旋翼播放器状态。</summary>
    public string DebugUnitRotor(int unitId) =>
        _unitAnimators.TryGetValue(unitId, out UnitAnimator a) ? a.DebugRotor() : "none";

    /// <summary>调试：单位当前实际播放的动画组。</summary>
    public string DebugUnitAnimState(int unitId) =>
        _unitAnimators.TryGetValue(unitId, out UnitAnimator a) ? a.DebugPlaying() : "none";

    /// <summary>调试：单位炮塔的相对角度（没有炮塔返回 NaN）。</summary>
    public float DebugTurretYaw(int unitId)
    {
        if (!_unitAnimators.TryGetValue(unitId, out UnitAnimator a) || !a.HasTurret)
            return float.NaN;
        return a.TurretYaw;
    }

    /// <summary>单位血条的世界锚点（模型头顶）。</summary>
    public Vector3 UnitBarAnchor(UnitInst u)
    {
        if (u == null || !_unitViews.TryGetValue(u.Id, out Node3D view))
            return HexLayout.ToWorld(u?.Coord ?? default, TileTop + 1.6f);

        return view.Position + Vector3.Up * (u.IsAir ? 1.35f : 1.75f);
    }

    /// <summary>建筑血条的世界锚点（屋顶上方）。</summary>
    public Vector3 BuildingBarAnchor(BuildingInst b)
    {
        float top = 1.2f;
        if (b != null && _buildingTops.TryGetValue(b.Id, out float t) && t > 0.01f)
            top = t;

        Vector3 cell = HexLayout.ToWorld(b?.Coord ?? default, TileTop);
        return new Vector3(cell.X, TileTop + top + 0.32f, cell.Z);
    }

    /// <summary>
    /// 升级预览：把已有建筑临时换成目标等级的模型（鼠标移开按钮后恢复）。
    /// </summary>
    public void PreviewBuildingLevel(BuildingInst b, int level)
    {
        if (b == null)
            return;

        ClearBuildingPreview();
        if (!_buildingViews.TryGetValue(b.Id, out Node3D view))
            return;

        view.QueueFree();
        _buildingViews.Remove(b.Id);
        _buildingHeights.Remove(b.Id);
        _buildingTops.Remove(b.Id);

        _previewBuildingId = b.Id;
        _previewSavedLevel = b.Level;
        _buildingViews[b.Id] = CreateBuildingView(b, level);
    }

    /// <summary>建造预览：在空地格子上临时摆一个目标建筑（鼠标移开按钮后移除）。</summary>
    public void PreviewBuildingAt(string defId, HexCoord coord, PlayerId owner, int level = 1)
    {
        ClearBuildingPreview();

        BuildingDef def = GameDatabase.Building(defId);
        if (def == null)
            return;

        var fake = new BuildingInst
        {
            Id = -9999,
            DefId = defId,
            Def = def,
            Owner = owner,
            Coord = coord,
            Level = level,
            Hp = def.HpAt(level),
        };

        _previewBuildDefId = defId;
        _previewBuildCoord = coord;
        _previewView = CreateBuildingView(fake, level);
    }

    /// <summary>清除所有预览。</summary>
    public void ClearBuildingPreview()
    {
        if (_previewBuildingId >= 0)
        {
            int id = _previewBuildingId;
            _previewBuildingId = -1;

            if (_buildingViews.TryGetValue(id, out Node3D view))
            {
                view.QueueFree();
                _buildingViews.Remove(id);
                _buildingHeights.Remove(id);
                _buildingTops.Remove(id);
            }

            // 用**当前真实等级**重建（不能用心存的那个等级：
            // 预览期间如果建筑真的升级了，存的是升级前的等级，会造成「升完级又退回上一级」）
            BuildingInst inst = _state?.Buildings.FirstOrDefault(x => x.Id == id);
            if (inst != null)
                _buildingViews[id] = CreateBuildingView(inst);
        }

        if (_previewView != null)
        {
            _previewView.QueueFree();
            _previewView = null;
            _previewBuildDefId = null;
        }
    }

    /// <summary>调试：建筑当前可见高度。</summary>
    public float DebugBuildingTop(int id) => _buildingTops.GetValueOrDefault(id, -1f);

    /// <summary>设置当前选中单位（-1 = 取消）：选中的那个用黄色描边，其余恢复阵营色。</summary>
    public void SetSelectedUnit(int unitId)
    {
        if (_selectedUnitId == unitId)
            return;

        if (_selectedUnitId >= 0)
            ApplyUnitOutline(_selectedUnitId, selected: false);

        _selectedUnitId = unitId;

        if (unitId >= 0)
            ApplyUnitOutline(unitId, selected: true);
    }

    private void ApplyUnitOutline(int unitId, bool selected)
    {
        if (!_unitViews.TryGetValue(unitId, out Node3D view))
            return;
        if (!_unitMaterials.TryGetValue(unitId, out (string Path, float Scale, PlayerId Owner) info))
            return;

        Color? color = selected
            ? ToonMaterial.SelectedOutline
            : ToonMaterial.OutlineFor(info.Owner);

        ToonMaterial.Apply(view, info.Path, info.Scale, color);
    }

    /// <summary>调试：单位是否正在播放攻击动作。</summary>
    public bool DebugUnitAttacking(int unitId) =>
        _unitAnimators.TryGetValue(unitId, out UnitAnimator a) && a.IsAttacking;

    /// <summary>播放一次攻击动作。</summary>
    public void PlayUnitAttack(int unitId)
    {
        if (_unitAnimators.TryGetValue(unitId, out UnitAnimator animator))
            animator.PlayAttack();
    }

    // ─────────────────────────── 网格构造 ───────────────────────────

    public static Mesh BuildRingMesh(float innerT, float outerT)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        Vector3[] inner = HexLayout.Corners(HexLayout.Size * innerT);
        Vector3[] outer = HexLayout.Corners(HexLayout.Size * outerT);

        for (int i = 0; i < 6; i++)
        {
            Vector3 a = inner[i];
            Vector3 b = inner[(i + 1) % 6];
            Vector3 c = outer[(i + 1) % 6];
            Vector3 d = outer[i];
            st.AddVertex(a);
            st.AddVertex(b);
            st.AddVertex(c);
            st.AddVertex(a);
            st.AddVertex(c);
            st.AddVertex(d);
        }

        st.GenerateNormals();
        return st.Commit();
    }

    private static Mesh BuildFillMesh(float t)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3[] pts = HexLayout.Corners(HexLayout.Size * t);
        for (int i = 1; i < 5; i++)
        {
            st.AddVertex(pts[0]);
            st.AddVertex(pts[i]);
            st.AddVertex(pts[i + 1]);
        }

        st.GenerateNormals();
        return st.Commit();
    }
}
