using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SevenDay.Core;

/// <summary>
/// 3渲2 材质工厂：把 gameAsset/shaders/toon_type 里的 toon_cel + toon_outline 套到每个模型上。
/// 描边在模型空间沿法线外推，宽度按节点缩放反补偿。
/// </summary>
public static class ToonMaterial
{
    public static readonly Color PlayerOutline = new("2F6FD0");
    public static readonly Color EnemyOutline = new("C43B31");
    public static readonly Color SelectedOutline = new("F2C230");

    public static Color? OutlineFor(PlayerId owner) => owner switch
    {
        PlayerId.Player => PlayerOutline,
        PlayerId.Enemy => EnemyOutline,
        _ => null,
    };

    public const float WorldOutlineWidth = 0.09f;
    private const float OutlineReferenceDistance = 21f;
    private const string CelShader = "res://gameAsset/shaders/toon_type/toon_cel.gdshader";
    private const string OutlineShader = "res://gameAsset/shaders/toon_type/toon_outline.gdshader";

    private const float BandLow = 0.42f;
    private const float BandHigh = 0.72f;
    private const float LevelDark = 0.62f;
    private const float LevelMid = 0.82f;
    private const float LevelLit = 1.0f;
    private const float AmbientBoost = 0.18f;

    private static readonly Color DefaultOutline = new(0.231f, 0.180f, 0.086f);
    private static readonly Dictionary<string, ShaderMaterial> Cache = new();
    private static readonly Dictionary<string, StandardMaterial3D> FlatCache = new();

    public static ShaderMaterial Get(string atlasPath, float modelScale, Color? outlineColor = null)
    {
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
        outline.SetShaderParameter("width", Mathf.Clamp(WorldOutlineWidth / rounded, 0.0005f, 0.6f));
        outline.SetShaderParameter("screen_uniform", true);
        outline.SetShaderParameter("reference_distance", OutlineReferenceDistance);
        cel.NextPass = outline;

        Cache[key] = cel;
        return cel;
    }

    /// <summary>烘焙描边面的材质：纯色、无光照、参与深度测试。</summary>
    private static StandardMaterial3D OutlineFlat(Color color)
    {
        string key = color.ToHtml(false);
        if (FlatCache.TryGetValue(key, out StandardMaterial3D cached))
            return cached;

        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = color,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
            DisableReceiveShadows = true,
        };
        FlatCache[key] = mat;
        return mat;
    }

    public static string FindAtlas(string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath))
            return null;

        string dir = modelPath.GetBaseDir();
        string baseName = modelPath.GetFile().GetBaseName();

        string[] candidates =
        {
            $"{dir}/{baseName}_{baseName}_atlas.png",
            $"{dir}/{baseName}_atlas.png",
            $"{dir}/{baseName}_Atlas.png",
        };

        foreach (string path in candidates)
        {
            if (ResourceLoader.Exists(path))
                return path;
        }

        if (!OS.HasFeature("template"))
        {
            foreach (string f in DirAccess.GetFilesAt(dir))
            {
                if (f.EndsWith("_atlas.png", System.StringComparison.OrdinalIgnoreCase))
                    return dir + "/" + f;
            }
        }

        return null;
    }

    public static bool Disabled;

    public static void Apply(Node3D model, string modelPath, float modelScale,
        Color? outlineColor = null, bool smoothOutline = false)
    {
        if (Disabled || model == null)
            return;

        string atlas = FindAtlas(modelPath);
        ShaderMaterial mat = Get(atlas, modelScale, outlineColor);
        StandardMaterial3D outlineFlat = OutlineFlat(outlineColor ?? DefaultOutline);

        int applied = 0;
        foreach (MeshInstance3D mi in model.FindChildren("*", "MeshInstance3D", true, false)
                     .Cast<MeshInstance3D>())
        {
            if (mi.Mesh == null)
                continue;

            // 烘进 GLB 的描边是独立材质面：按面名区分，避免整块 MaterialOverride 把描边也盖成卡通材质。
            bool hasBakedOutline = false;
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                Material source = mi.Mesh.SurfaceGetMaterial(s);
                string sourceName = source?.ResourceName ?? "";
                bool isOutline = sourceName.Contains("Outline", System.StringComparison.OrdinalIgnoreCase);
                hasBakedOutline |= isOutline;
                mi.SetSurfaceOverrideMaterial(s, isOutline ? outlineFlat : mat);
            }

            if (!hasBakedOutline)
                mi.MaterialOverride = mat;
            else
                mi.MaterialOverride = null;

            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
            applied++;
        }

        if (applied == 0 && model is MeshInstance3D single && single.Mesh != null)
            single.MaterialOverride = mat;
    }
}
