using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Core;

namespace SevenDay.UI;

/// <summary>
/// 主菜单背景：一座六边形小岛 + 建筑与兵种模型，镜头做长镜头缓慢环绕推进，
/// 远处带景深虚化（参考-1 的要求：3D 步入场景、Camera 长镜头移动、景深动画）。
/// </summary>
public partial class MainMenuBackdrop : Node3D
{
    // 小岛模型抬高一点，避免描边壳与地砖顶面共面导致移动镜头时闪烁
    private const float MenuModelLift = 0.47f;
    private const float MissileSiloDrop = 0.35f;
    private Camera3D _camera;
    private float _time;
    private float _orbit = 0.6f;
    private Mesh _hexMesh;
    private readonly Dictionary<TerrainType, Material> _mats = new();

    private static readonly (HexCoord Coord, string Model, float Width)[] Props =
    {
        (new HexCoord(0, 0), "res://gameAsset/models/buildings/hq/hq.glb", 1.62f),
        (new HexCoord(2, -1), "res://gameAsset/models/buildings/city/city.glb", 1.62f),
        (new HexCoord(-2, 0), "res://gameAsset/models/buildings/barracks/barracks.glb", 1.62f),
        (new HexCoord(1, 1), "res://gameAsset/models/buildings/gunTurret/gunTurret.glb", 1.5f),
        (new HexCoord(-1, 2), "res://gameAsset/models/buildings/helipad/helipad.glb", 1.62f),
        (new HexCoord(2, 1), "res://gameAsset/models/buildings/missileSilo/missileSilo.glb", 1.55f),
        (new HexCoord(-2, -1), "res://gameAsset/models/buildings/aaGun/aaGun.glb", 1.5f),
        (new HexCoord(2, -2), "res://gameAsset/models/militaries/tank/tank.glb", 1.0f),
        (new HexCoord(-1, -1), "res://gameAsset/models/militaries/infantry/infantry.glb", 0.85f),
    };

    /// <summary>按模型路径找到建筑定义，取它配置的隐藏部件名（导弹、尾焰等）。</summary>
    private static string[] HiddenPartsOf(string modelPath)
    {
        foreach (Data.BuildingDef def in Data.GameDatabase.Buildings.Values)
        {
            if (def.Model == modelPath)
                return def.HiddenParts;
        }

        return null;
    }
    private static readonly string[] Decorations =
    {
        "res://gameAsset/models/decorates/pine/pine.glb",
        "res://gameAsset/models/decorates/rock/rock.glb",
        "res://gameAsset/models/decorates/bush/bush.glb",
        "res://gameAsset/models/decorates/grassTuft/grassTuft.glb",
        "res://gameAsset/models/decorates/barrel/barrel.glb",
        "res://gameAsset/models/decorates/crate/crate.glb",
    };

    public override void _Ready()
    {
        LoadHexMesh();
        BuildMaterials();
        BuildEnvironment();
        BuildIsland();
        BuildCamera();
    }

    private void LoadHexMesh()
    {
        var scene = GD.Load<PackedScene>("res://gameAsset/models/hexTile/hexTile.gltf");
        Node inst = scene.Instantiate();
        MeshInstance3D mi = inst.GetChildren().OfType<MeshInstance3D>().FirstOrDefault();
        _hexMesh = mi?.Mesh ?? new PlaneMesh { Size = new Vector2(1.732f, 2f) };
        inst.QueueFree();
    }

    private void BuildMaterials()
    {
        (TerrainType t, string f)[] list =
        {
            (TerrainType.Grass, "hexTile_grass.png"),
            (TerrainType.Sand, "hexTile_sand.png"),
            (TerrainType.Dirt, "hexTile_dirt.png"),
            (TerrainType.Rock, "hexTile_rock.png"),
            (TerrainType.Snow, "hexTile_snow.png"),
            (TerrainType.Water, "hexTile_water.png"),
        };

        foreach ((TerrainType t, string f) in list)
        {
            _mats[t] = new StandardMaterial3D
            {
                AlbedoTexture = GD.Load<Texture2D>("res://gameAsset/models/hexTile/" + f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
        }
    }

    private void BuildEnvironment()
    {
        var sun = new DirectionalLight3D
        {
            LightEnergy = 0.95f,
            LightColor = new Color(1f, 0.95f, 0.86f),
            ShadowEnabled = true,
            RotationDegrees = new Vector3(-48, -35, 0),
        };
        AddChild(sun);

        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color("EDE1C2"),
            SkyHorizonColor = new Color("FAF3E2"),
            GroundBottomColor = new Color("E6D8B8"),
            GroundHorizonColor = new Color("F4EAD3"),
            SunAngleMax = 20f,
        };
        var sky = new Sky { SkyMaterial = skyMat };

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 1.1f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            TonemapExposure = 1.0f,
            FogEnabled = true,
            FogLightColor = new Color("F4EBD8"),
            FogDensity = 0.006f,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    private void BuildIsland()
    {
        var tiles = new Node3D { Name = "Tiles" };
        AddChild(tiles);

        Mesh borderMesh = Battle.HexMapView.BuildRingMesh(0.92f, 1.0f);
        var borderMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.231f, 0.180f, 0.086f, 1f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var hexOutlineMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.231f, 0.180f, 0.086f, 1f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
        };

        // 中心到半径 3 的小岛；边缘用沙地做海岸
        foreach (HexCoord c in HexCoord.Spiral(HexCoord.Zero, 3))
        {
            int dist = HexCoord.Distance(c, HexCoord.Zero);
            TerrainType terrain = dist >= 3 ? TerrainType.Sand : TerrainType.Grass;

            var mi = new MeshInstance3D
            {
                Mesh = _hexMesh,
                Position = HexLayout.ToWorld(c),
            };
            bool bakedOutline = false;
            for (int s = 0; s < _hexMesh.GetSurfaceCount(); s++)
            {
                Material source = _hexMesh.SurfaceGetMaterial(s);
                bool isOutline = source?.ResourceName.Contains("Outline", System.StringComparison.OrdinalIgnoreCase) == true;
                bakedOutline |= isOutline;
                mi.SetSurfaceOverrideMaterial(s, isOutline ? hexOutlineMat : _mats[terrain]);
            }
            if (!bakedOutline)
                mi.MaterialOverride = _mats[terrain];
            tiles.AddChild(mi);

            var border = new MeshInstance3D
            {
                Mesh = borderMesh,
                Position = HexLayout.ToWorld(c, 0.454f),
                MaterialOverride = borderMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            tiles.AddChild(border);
        }

        foreach ((HexCoord coord, string model, float width) in Props)
        {
            var holder = new Node3D { Position = HexLayout.ToWorld(coord, MenuModelLift) };
            // 发射井单独微调：用 model 路径判断，holder.Name 默认不是模型名；
            // 10 个世界单位约等于 10 米，会把模型埋进小岛，这里用 0.05 做微调。
            if (model.Contains("missileSilo", System.StringComparison.Ordinal))
                holder.Position -= new Vector3(0, MissileSiloDrop, 0);
            AddChild(holder);
            // 菜单小岛上直接展示三级建筑（最高形态最好看），兵种传 0 表示不做等级筛选
            bool isBuilding = model.Contains("/buildings/");

            // 导弹井这类建筑要跳过「隐藏部件」（导弹、尾焰）参与落地计算，
            // 否则那些部件的最低点会把整栋楼顶起来悬空
            string[] hidden = isBuilding ? HiddenPartsOf(model) : null;

            Node3D m = ModelUtil.SpawnModel(model, holder, width, isBuilding ? 3 : 0,
                out float _, hiddenParts: hidden);
            if (m != null)
                holder.RotationDegrees = new Vector3(0, coord.R % 2 == 0 ? 28f : -18f, 0);
        }

        // 装饰物：随机撒在外圈
        var rng = new RandomNumberGenerator { Seed = 20261112 };
        var used = Props.Select(p => p.Coord).ToHashSet();
        used.Add(HexCoord.Zero);
        foreach (HexCoord c in HexCoord.Spiral(HexCoord.Zero, 3))
        {
            if (used.Contains(c) || HexCoord.Distance(c, HexCoord.Zero) < 2)
                continue;
            if (rng.Randf() > 0.5f)
                continue;

            string decor = Decorations[rng.RandiRange(0, Decorations.Length - 1)];
            var holder = new Node3D
            {
                Position = HexLayout.ToWorld(c, MenuModelLift) +
                           new Vector3(rng.RandfRange(-0.4f, 0.4f), 0, rng.RandfRange(-0.4f, 0.4f)),
                RotationDegrees = new Vector3(0, rng.RandfRange(0, 360), 0),
            };
            AddChild(holder);
            ModelUtil.SpawnModel(decor, holder, rng.RandfRange(0.5f, 0.9f));
        }
    }

    private void BuildCamera()
    {
        _camera = new Camera3D
        {
            Name = "MenuCamera",
            Current = true,
            Fov = 42f,
            Near = 0.5f,
            Far = 500f,
        };

        var attrs = new CameraAttributesPractical
        {
            DofBlurFarEnabled = true,
            DofBlurFarDistance = 26f,
            DofBlurFarTransition = 26f,
            DofBlurNearEnabled = true,
            DofBlurNearDistance = 8f,
            DofBlurNearTransition = 6f,
            DofBlurAmount = 0.08f,
        };
        _camera.Attributes = attrs;
        AddChild(_camera);

        UpdateCamera(0f);
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        UpdateCamera((float)delta);
    }

    private void UpdateCamera(float delta)
    {
        // 长镜头：极慢的环绕 + 高度/距离的呼吸感
        _orbit += delta * 0.035f;
        float radius = 17.5f + Mathf.Sin(_time * 0.11f) * 2.5f;
        float height = 8.2f + Mathf.Sin(_time * 0.07f + 1.2f) * 1.2f;

        Vector3 target = new(0f, 1.1f, 0f);
        Vector3 pos = new(
            Mathf.Sin(_orbit) * radius,
            height,
            Mathf.Cos(_orbit) * radius);

        _camera.Position = pos;
        _camera.LookAt(target, Vector3.Up);

        if (_camera.Attributes is CameraAttributesPractical attrs)
            attrs.DofBlurFarDistance = pos.DistanceTo(target) + 6f;
    }
}
