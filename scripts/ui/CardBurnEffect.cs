using Godot;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>
/// 卡牌被「烧掉」的特效：
///   卡面渲染进 SubViewport，用 card_dissolve 材质做噪声消融（从底部烧穿，边缘泛橙红），
///   同时配三组跟游戏风格统一的粒子：向上飘的火星、四散的火花、往下落的灰烬。
/// </summary>
public partial class CardBurnEffect : Control
{
    private SubViewport _viewport;
    private TextureRect _view;
    private ShaderMaterial _material;
    private float _t;
    private float _duration = 0.9f;

    private static Texture2D _softDot;
    private static Texture2D _star;

    public void Play(CardDef def, Rect2 from, float rotationDeg = 0f)
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Position = from.Position;
        Size = from.Size;
        PivotOffset = new Vector2(from.Size.X * 0.5f, from.Size.Y);
        RotationDegrees = rotationDeg;
        ZIndex = 80;

        // 卡面先渲染到离屏视口，才能整体套一个 canvas_item 消融材质
        _viewport = new SubViewport
        {
            Size = new Vector2I((int)from.Size.X, (int)from.Size.Y),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            Disable3D = true,
        };
        AddChild(_viewport);

        var card = new CardView { MouseFilter = MouseFilterEnum.Ignore };
        card.Setup(def, -1, true);
        card.Position = Vector2.Zero;
        _viewport.AddChild(card);

        _material = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://gameAsset/shaders/card_dissolve.gdshader"),
        };
        _material.SetShaderParameter("dissolve", 0f);

        _view = new TextureRect
        {
            Texture = _viewport.GetTexture(),
            Size = from.Size,
            Position = Vector2.Zero,
            MouseFilter = MouseFilterEnum.Ignore,
            Material = _material,
            StretchMode = TextureRect.StretchModeEnum.Scale,
        };
        AddChild(_view);

        // 底部的一层暖光，随燃烧往上升
        AddGlow(from);

        // 三组粒子
        AddEmbers(from);
        AddSparks(from);
        AddAsh(from);

        SetProcess(true);
    }

    // ─────────────────────────── 粒子 ───────────────────────────

    private void AddGlow(Rect2 from)
    {
        var glow = new TextureRect
        {
            Texture = SoftDot(),
            Size = new Vector2(from.Size.X * 1.35f, from.Size.Y * 0.75f),
            Position = new Vector2(-from.Size.X * 0.3f, from.Size.Y * 0.45f),
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = new Color(1f, 0.58f, 0.24f, 0.25f),
            StretchMode = TextureRect.StretchModeEnum.Scale,
        };
        AddChild(glow);

        Tween tw = CreateTween().SetParallel(true);
        tw.TweenProperty(glow, "modulate:a", 0f, 0.85f).SetEase(Tween.EaseType.In);
        tw.TweenProperty(glow, "position:y", -from.Size.Y * 0.2f, 0.85f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        tw.TweenProperty(glow, "scale", new Vector2(1.15f, 0.7f), 0.85f);
    }

    /// <summary>向上飘的火星：暖黄到橙红，越飞越小。</summary>
    private void AddEmbers(Rect2 from)
    {
        var embers = new CpuParticles2D
        {
            Amount = 26,
            Lifetime = 0.95,
            OneShot = true,
            Emitting = true,
            Explosiveness = 0.75f,
            Direction = new Vector2(0, -1),
            Spread = 38f,
            Gravity = new Vector2(0, -95f),
            InitialVelocityMin = 35f,
            InitialVelocityMax = 105f,
            ScaleAmountMin = 0.10f,
            ScaleAmountMax = 0.26f,
            ScaleAmountCurve = ShrinkCurve(),
            ColorRamp = EmberGradient(),
            Texture = SoftDot(),
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
            EmissionRectExtents = new Vector2(from.Size.X * 0.42f, from.Size.Y * 0.28f),
            Position = new Vector2(from.Size.X * 0.5f, from.Size.Y * 0.72f),
            DampingMin = 6f,
            DampingMax = 20f,
        };
        AddChild(embers);
    }

    /// <summary>四散的火花：小小四角星，往外弹一下就熄。</summary>
    private void AddSparks(Rect2 from)
    {
        var sparks = new CpuParticles2D
        {
            Amount = 14,
            Lifetime = 0.55,
            OneShot = true,
            Emitting = true,
            Explosiveness = 0.9f,
            Direction = new Vector2(0, -1),
            Spread = 120f,
            Gravity = new Vector2(0, 180f),
            InitialVelocityMin = 110f,
            InitialVelocityMax = 230f,
            ScaleAmountMin = 0.10f,
            ScaleAmountMax = 0.26f,
            ScaleAmountCurve = ShrinkCurve(),
            ColorRamp = SparkGradient(),
            Texture = StarDot(),
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
            EmissionRectExtents = new Vector2(from.Size.X * 0.3f, from.Size.Y * 0.2f),
            Position = new Vector2(from.Size.X * 0.5f, from.Size.Y * 0.6f),
        };
        AddChild(sparks);
    }

    /// <summary>往下掉的灰烬：深褐小碎片，慢慢飘落。</summary>
    private void AddAsh(Rect2 from)
    {
        var ash = new CpuParticles2D
        {
            Amount = 16,
            Lifetime = 1.1,
            OneShot = true,
            Emitting = true,
            Explosiveness = 0.35f,
            Direction = new Vector2(0, 1),
            Spread = 55f,
            Gravity = new Vector2(0, 90f),
            InitialVelocityMin = 12f,
            InitialVelocityMax = 45f,
            ScaleAmountMin = 0.07f,
            ScaleAmountMax = 0.18f,
            ScaleAmountCurve = ShrinkCurve(),
            ColorRamp = AshGradient(),
            Texture = SoftDot(),
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
            EmissionRectExtents = new Vector2(from.Size.X * 0.4f, from.Size.Y * 0.3f),
            Position = new Vector2(from.Size.X * 0.5f, from.Size.Y * 0.5f),
            AngularVelocityMin = -120f,
            AngularVelocityMax = 120f,
        };
        AddChild(ash);
    }

    // ─────────────────────────── 曲线 / 渐变 ───────────────────────────

    private static Curve ShrinkCurve()
    {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0f, 1f));
        curve.AddPoint(new Vector2(0.6f, 0.7f));
        curve.AddPoint(new Vector2(1f, 0f));
        return curve;
    }

    private static Gradient EmberGradient()
    {
        var g = new Gradient();
        g.SetOffset(0, 0f);
        g.SetColor(0, new Color("FFF1B8"));
        g.SetOffset(1, 1f);
        g.SetColor(1, new Color(0.85f, 0.22f, 0.12f, 0f));

        g.AddPoint(0.45f, new Color("FFB53C"));
        g.AddPoint(0.75f, new Color("F2703A"));
        return g;
    }

    private static Gradient SparkGradient()
    {
        var g = new Gradient();
        g.SetOffset(0, 0f);
        g.SetColor(0, new Color("FFF6D2"));
        g.SetOffset(1, 1f);
        g.SetColor(1, new Color(1f, 0.55f, 0.2f, 0f));
        return g;
    }

    private static Gradient AshGradient()
    {
        var g = new Gradient();
        g.SetOffset(0, 0f);
        g.SetColor(0, new Color(0.35f, 0.28f, 0.22f, 0.85f));
        g.SetOffset(1, 1f);
        g.SetColor(1, new Color(0.2f, 0.16f, 0.12f, 0f));
        return g;
    }

    // ─────────────────────────── 贴图 ───────────────────────────

    /// <summary>柔和圆点（暖光/火星/灰烬共用）。</summary>
    private static Texture2D SoftDot()
    {
        if (_softDot != null)
            return _softDot;

        const int size = 64;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f;
                float dy = (y + 0.5f) / size * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp(1f - d, 0f, 1f);
                a = a * a;
                img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }

        _softDot = ImageTexture.CreateFromImage(img);
        return _softDot;
    }

    /// <summary>四角星（和战斗里的火花、治疗星一致）。</summary>
    private static Texture2D StarDot()
    {
        if (_star != null)
            return _star;

        const int size = 64;
        float c = size * 0.5f;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Abs((x + 0.5f) - c) / c;
                float dy = Mathf.Abs((y + 0.5f) - c) / c;
                // 十字光晕 + 中心亮点
                float cross = Mathf.Max(0f, 1f - Mathf.Min(dx, dy) * 6f) * Mathf.Clamp(1f - Mathf.Max(dx, dy), 0f, 1f);
                float core = Mathf.Clamp(1f - Mathf.Sqrt(dx * dx + dy * dy) * 2.2f, 0f, 1f);
                float a = Mathf.Clamp(cross * 0.9f + core, 0f, 1f);
                img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }

        _star = ImageTexture.CreateFromImage(img);
        return _star;
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        float k = Mathf.Clamp(_t / _duration, 0f, 1f);

        // 消融进度：先慢后快，最后 15% 已经完全消失
        float dissolve = Mathf.Clamp(k / 0.85f, 0f, 1f);
        dissolve = Mathf.Pow(dissolve, 0.85f);
        _material?.SetShaderParameter("dissolve", dissolve);

        // 稍微缩小并淡出，收得自然一点
        float fade = Mathf.Clamp((k - 0.7f) / 0.3f, 0f, 1f);
        Scale = Vector2.One * (1f - 0.06f * k);
        if (_view != null)
            _view.Modulate = new Color(1f, 1f, 1f, 1f - fade * 0.35f);

        if (k >= 1f)
            QueueFree();
    }
}
