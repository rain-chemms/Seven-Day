using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SevenDay.Core;

/// <summary>
/// 3渲2 材质工厂：把 gameAsset/shaders/toon_type 里的 toon_cel + toon_outline 套到每个模型上。
///
/// 关键点：描边 shader 是在**模型空间**把顶点沿法线外推的，而模型被我们等比缩小到
/// 一格大小（缩放系数可能是 0.05 量级），所以描边宽度必须按 scale 反向补偿，
/// 否则缩完就看不见了（README_Godot.md 里专门提醒过这一点）。
/// </summary>
public static class ToonMaterial
{
    /// <summary>玩家方描边色（蓝）</summary>
    public static readonly Color PlayerOutline = new("2F6FD0");

    /// <summary>敌方描边色（红）</summary>
    public static readonly Color EnemyOutline = new("C43B31");

    /// <summary>按阵营取描边色；中立/未指定时用默认深棕。</summary>
    public static Color? OutlineFor(PlayerId owner) => owner switch
    {
        PlayerId.Player => PlayerOutline,
        PlayerId.Enemy => EnemyOutline,
        _ => null,
    };

    /// <summary>世界里想要的描边粗细（米），配合 screen_uniform 时会按镜头距离换算，
    /// 做到「不管推远拉近，屏幕上的粗细基本一致」。</summary>
    public const float WorldOutlineWidth = 0.09f;

    /// <summary>screen_uniform 的参考距离：默认战斗镜头大约 21 米。</summary>
    private const float OutlineReferenceDistance = 21f;

    private const string CelShader = "res://gameAsset/shaders/toon_type/toon_cel.gdshader";
    private const string OutlineShader = "res://gameAsset/shaders/toon_type/toon_outline.gdshader";

    /// <summary>与 Blender 里 ColorRamp 对齐的默认值，和 fighter 的 toon_cel.tres 一致。</summary>
    private const float BandLow = 0.42f;
    private const float BandHigh = 0.72f;
    private const float LevelDark = 0.62f;
    private const float LevelMid = 0.82f;
    private const float LevelLit = 1.0f;
    private const float AmbientBoost = 0.18f;

    /// <summary>默认描边色（Blender 里 ColorRamp 对齐的深棕）</summary>
    private static readonly Color DefaultOutline = new(0.231f, 0.180f, 0.086f);

    private static readonly Dictionary<string, ShaderMaterial> Cache = new();

    /// <summary>按「图集 + 缩放 + 描边色」缓存材质，避免每个单位都新建一份。</summary>
    public static ShaderMaterial Get(string atlasPath, float modelScale, Color? outlineColor = null)
    {
        // 缩放量化到 3 位小数，避免缓存爆炸
        float rounded = Mathf.Round(Mathf.Max(modelScale, 0.0001f) * 1000f) / 1000f;
        Color outlineColorValue = outlineColor ?? DefaultOutline;
        string key = atlasPath + "|" + rounded + "|" + outlineColorValue.ToHtml(false);
        if (Cache.TryGetValue(key, out ShaderMaterial cached))
            return cached;

        var cel = new ShaderMaterial { Shader = GD.Load<Shader>(CelShader) };
        if (!string.IsNullOrEmpty(atlasPath) && ResourceLoader.Exists(atlasPath))
            cel.SetShaderParameter("albedo_tex", GD.Load<Texture2D>(atlasPath));
        cel.SetShaderParameter("albedo_tint", new Vector3(1, 1, 1));
        cel.SetShaderParameter("band_low", BandLow);
        cel.SetShaderParameter("band_high", BandHigh);
        cel.SetShaderParameter("level_dark", LevelDark);
        cel.SetShaderParameter("level_mid", LevelMid);
        cel.SetShaderParameter("level_lit", LevelLit);
        cel.SetShaderParameter("ambient_boost", AmbientBoost);

        var outline = new ShaderMaterial { Shader = GD.Load<Shader>(OutlineShader) };
        outline.SetShaderParameter("outline_color", new Vector3(
            outlineColorValue.R, outlineColorValue.G, outlineColorValue.B));
        // 模型空间宽度 = 世界宽度 / 节点缩放；再开 screen_uniform 让屏幕上的粗细不随远近变化
        outline.SetShaderParameter("width", Mathf.Clamp(WorldOutlineWidth / rounded, 0.0005f, 0.6f));
        outline.SetShaderParameter("screen_uniform", true);
        outline.SetShaderParameter("reference_distance", OutlineReferenceDistance);

        cel.NextPass = outline;

        Cache[key] = cel;
        return cel;
    }

    /// <summary>按模型路径自动找同目录下的 *_atlas.png。</summary>
    public static string FindAtlas(string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath))
            return null;

        string dir = modelPath.GetBaseDir();
        string[] files = DirAccess.GetFilesAt(dir);
        foreach (string f in files)
        {
            if (f.EndsWith("_atlas.png", System.StringComparison.OrdinalIgnoreCase))
                return dir + "/" + f;
        }

        return null;
    }

    /// <summary>开发调试：--nomats 时跳过 3渲2 覆盖，用 glb 自带材质。</summary>
    public static bool Disabled;

    /// <summary>把整棵模型里的所有 MeshInstance3D 换成 3渲2 材质。</summary>
    public static void Apply(Node3D model, string modelPath, float modelScale, Color? outlineColor = null)
    {
        if (Disabled || model == null)
            return;

        string atlas = FindAtlas(modelPath);
        ShaderMaterial mat = Get(atlas, modelScale, outlineColor);

        int applied = 0;
        foreach (MeshInstance3D mi in model.FindChildren("*", "MeshInstance3D", true, false)
                     .Cast<MeshInstance3D>())
        {
            if (mi.Mesh == null)
                continue;

            mi.MaterialOverride = mat;
            // 描边壳是反向外壳，不参与阴影会更干净
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
            applied++;
        }

        if (applied == 0 && model is MeshInstance3D single && single.Mesh != null)
            single.MaterialOverride = mat;
    }
}
