using System;
using Godot;

namespace SevenDay.Battle;

/// <summary>
/// 战斗特效：爆炸、子弹命中、曳光、导弹落地、回血等。
///
/// 风格要求（和整体 3渲2 手绘风统一）：
///   · 全部用 Unshaded 材质 + BillBoard 四边形，像手绘贴纸一样永远朝向镜头；
///   · 主色是暖橙黄（#FFD24A / #FF8A3C）+ 白心，烟雾用半透明深棕；
///   · 不做真实光照/阴影，靠缩放 + 淡出 + 少量粒子表现，生命周期都很短（0.15~0.6 秒）。
/// </summary>
public partial class Vfx : Node3D
{
    private static Texture2D _starTex;
    private static Texture2D _softTex;
    private static Texture2D _ringTex;

    /// <summary>屏幕震动（可选，由战斗场景提供）。</summary>
    public Action<float, float> Shake;

    public static Vfx Create(Node parent)
    {
        var vfx = new Vfx { Name = "Vfx" };
        parent.AddChild(vfx);
        return vfx;
    }

    // ─────────────────────────── 贴图（程序化生成，不依赖美术资源） ───────────────────────────

    /// <summary>尖刺星形：手绘爆炸的主要形状。</summary>
    private static Texture2D StarTex()
    {
        if (_starTex != null)
            return _starTex;

        const int size = 128;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));

        const int spikes = 8;
        float inner = 0.30f;
        var center = new Vector2(size * 0.5f, size * 0.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - center.X) / center.X;
                float dy = (y - center.Y) / center.Y;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Atan2(dy, dx);

                // 8 瓣的三角波轮廓：尖处 1.0，凹处 inner
                float t = Mathf.Abs(Mathf.Sin(a * spikes * 0.5f));
                float limit = Mathf.Lerp(inner, 1.0f, t);

                if (r > limit)
                    continue;

                // 边缘只留一点抗锯齿，尖角要够硬才像手绘
                float edge = Mathf.Clamp((limit - r) / 0.045f, 0f, 1f);
                img.SetPixel(x, y, new Color(1, 1, 1, Mathf.Min(1f, edge * 1.1f)));
            }
        }

        _starTex = ImageTexture.CreateFromImage(img);
        return _starTex;
    }

    /// <summary>柔和的圆点（烟 / 火光）。</summary>
    private static Texture2D SoftTex()
    {
        if (_softTex != null)
            return _softTex;

        const int size = 64;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var center = new Vector2(size * 0.5f, size * 0.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2(x - center.X, y - center.Y).Length() / center.X;
                float a = Mathf.Clamp(1f - d, 0f, 1f);
                img.SetPixel(x, y, new Color(1, 1, 1, a * a));
            }
        }

        _softTex = ImageTexture.CreateFromImage(img);
        return _softTex;
    }

    /// <summary>细环（爆炸冲击波）。</summary>
    private static Texture2D RingTex()
    {
        if (_ringTex != null)
            return _ringTex;

        const int size = 128;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var center = new Vector2(size * 0.5f, size * 0.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2(x - center.X, y - center.Y).Length() / center.X;
                float a = Mathf.Clamp(1f - Mathf.Abs(d - 0.86f) / 0.12f, 0f, 1f);
                img.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        }

        _ringTex = ImageTexture.CreateFromImage(img);
        return _ringTex;
    }

    // ─────────────────────────── 材质 ───────────────────────────

    private static StandardMaterial3D BillboardMat(Texture2D tex, Color color, bool additive,
        bool vertexColor = false)
    {
        return new StandardMaterial3D
        {
            AlbedoTexture = tex,
            AlbedoColor = color,
            VertexColorUseAsAlbedo = vertexColor,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DisableReceiveShadows = true,
        };
    }

    private static MeshInstance3D Quad(Texture2D tex, Color color, bool additive, float size)
    {
        return new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(1f, 1f) },
            Scale = Vector3.One * size,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = BillboardMat(tex, color, additive),
        };
    }

    // ─────────────────────────── 爆炸 ───────────────────────────

    /// <summary>地面爆炸：星形闪光 + 冲击波环 + 烟雾 + 火花粒子。</summary>
    public void Explosion(Vector3 pos, float radius = 1.0f, float delay = 0f)
    {
        if (delay > 0f)
        {
            var timer = GetTree().CreateTimer(delay);
            timer.Timeout += () => Explosion(pos, radius);
            return;
        }

        var root = new Node3D { Position = pos };
        AddChild(root);

        // 白心闪光（小一点、退得快，别把橙色星芒盖住）
        MeshInstance3D flash = Quad(StarTex(), new Color(1f, 0.96f, 0.82f, 0.9f), true, radius * 0.7f);
        root.AddChild(flash);

        // 橙黄外圈：爆炸的主体形状（用实色，别加色，才能像手绘那样不透明）
        MeshInstance3D star = Quad(StarTex(), new Color("FF8A22", 0.95f), false, radius * 1.9f);
        star.RotationDegrees = new Vector3(0f, 0f, 18f);
        root.AddChild(star);

        // 地面闪光（贴地的一层暖光）
        MeshInstance3D ground = Quad(SoftTex(), new Color(1f, 0.75f, 0.35f, 0.85f), true, radius * 1.6f);
        ground.Position = new Vector3(0f, 0.05f, 0f);
        ground.RotationDegrees = new Vector3(-90f, 0f, 0f);
        root.AddChild(ground);

        // 冲击波环
        MeshInstance3D ring = Quad(RingTex(), new Color(1f, 0.85f, 0.55f, 0.9f), true, radius * 1.2f);
        root.AddChild(ring);

        // 第二圈冲击波（延迟一点、更大更淡）
        MeshInstance3D ring2 = Quad(RingTex(), new Color(1f, 0.95f, 0.75f, 0.75f), true, radius * 1.4f);
        root.AddChild(ring2);
        Tween r2 = CreateTween().SetParallel(true);
        r2.TweenProperty(ring2, "scale", Vector3.One * radius * 5.0f, 0.55f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        if (ring2.MaterialOverride is StandardMaterial3D ring2Mat)
            r2.TweenProperty(ring2Mat, "albedo_color", new Color(1f, 0.95f, 0.75f, 0f), 0.6f).SetDelay(0.1f);

        // 四散的碎块（先抛起再落下）
        for (int i = 0; i < 6; i++)
        {
            float ang = i * Mathf.Tau / 6f + (float)GD.RandRange(-0.4, 0.4);
            var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
            MeshInstance3D chunk = Quad(StarTex(), new Color(0.28f, 0.2f, 0.16f, 0.95f), false, radius * 0.22f);
            chunk.Position = dir * radius * 0.3f + Vector3.Up * 0.2f;
            root.AddChild(chunk);

            Vector3 peak = chunk.Position + dir * radius * 0.9f + Vector3.Up * radius * 1.1f;
            Vector3 land = dir * radius * 1.5f + Vector3.Up * 0.05f;

            Tween ct = CreateTween();
            ct.TweenProperty(chunk, "position", peak, 0.22f)
                .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
            ct.TweenProperty(chunk, "position", land, 0.3f)
                .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            ct.Parallel().TweenProperty(chunk, "rotation_degrees:z", (float)GD.RandRange(-360, 360), 0.5f);
            if (chunk.MaterialOverride is StandardMaterial3D chunkMat)
                CreateTween().TweenProperty(chunkMat, "albedo_color", new Color(0.28f, 0.2f, 0.16f, 0f), 0.3f)
                    .SetDelay(0.2f);
        }

        // 烟雾
        for (int i = 0; i < 4; i++)
        {
            Vector3 offset = new((float)GD.RandRange(-0.35, 0.35) * radius, 0.15f + GD.Randf() * 0.3f,
                (float)GD.RandRange(-0.35, 0.35) * radius);
            MeshInstance3D puff = Quad(SoftTex(), new Color(0.32f, 0.26f, 0.22f, 0.75f), false,
                radius * 1.1f);
            puff.Position = offset;
            root.AddChild(puff);

            Tween pt = CreateTween().SetParallel(true);
            pt.TweenProperty(puff, "scale", Vector3.One * radius * 2.4f, 0.55f)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            pt.TweenProperty(puff, "position", offset + new Vector3(0f, 0.6f * radius, 0f), 0.55f)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

            if (puff.MaterialOverride is StandardMaterial3D puffMat)
                pt.TweenProperty(puffMat, "albedo_color", new Color(0.32f, 0.26f, 0.22f, 0f), 0.55f);
        }

        // 火花粒子：往斜上方溅
        var sparks = new CpuParticles3D
        {
            Amount = 26,
            Lifetime = 0.55,
            OneShot = true,
            Explosiveness = 0.95f,
            Direction = new Vector3(0, 1, 0),
            Spread = 80f,
            InitialVelocityMin = 2f * radius,
            InitialVelocityMax = 5.5f * radius,
            Gravity = new Vector3(0, -9f, 0),
            ScaleAmountMin = 0.5f,
            ScaleAmountMax = 1.2f,
            Mesh = new QuadMesh { Size = new Vector2(0.16f, 0.16f) },
        };
        sparks.MaterialOverride = BillboardMat(SoftTex(), new Color("FFD24A"), true);
        root.AddChild(sparks);
        sparks.Emitting = true;

        // 主闪光：猛地放大再收
        Tween t = CreateTween().SetParallel(true);
        t.TweenProperty(flash, "scale", Vector3.One * radius * 1.7f, 0.11f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        t.TweenProperty(star, "scale", Vector3.One * radius * 2.9f, 0.30f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        t.TweenProperty(ring, "scale", Vector3.One * radius * 3.4f, 0.36f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        if (flash.MaterialOverride is StandardMaterial3D flashMat)
            t.TweenProperty(flashMat, "albedo_color", new Color(1f, 0.96f, 0.82f, 0f), 0.12f);
        if (star.MaterialOverride is StandardMaterial3D starMat)
            t.TweenProperty(starMat, "albedo_color", new Color(0.85f, 0.35f, 0.08f, 0f), 0.34f);
        if (ring.MaterialOverride is StandardMaterial3D ringMat)
            t.TweenProperty(ringMat, "albedo_color", new Color(1f, 0.85f, 0.55f, 0f), 0.38f);
        if (ground.MaterialOverride is StandardMaterial3D groundMat)
            t.TweenProperty(groundMat, "albedo_color", new Color(1f, 0.75f, 0.35f, 0f), 0.3f);
        t.TweenProperty(ground, "scale", Vector3.One * radius * 3.0f, 0.3f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        Shake?.Invoke(0.16f * radius, 0.22f);
        // 爆炸音效：半径越大越响（0.55~1.0）
        SevenDay.Core.AudioManager.PlayExplosion(Mathf.Clamp(0.55f + radius * 0.35f, 0.5f, 1.0f));

        KillAfter(root, 0.75f);
    }

    // ─────────────────────────── 轻武器 / 命中 ───────────────────────────

    /// <summary>子弹命中：小星芒 + 亮点。</summary>
    public void BulletHit(Vector3 pos, float size = 0.5f, Color? tint = null)
    {
        var root = new Node3D { Position = pos };
        AddChild(root);

        Color color = tint ?? new Color("FFE08A");

        MeshInstance3D star = Quad(StarTex(), color, true, size * 0.9f);
        star.RotationDegrees = new Vector3(0f, 0f, (float)GD.RandRange(0, 90));
        root.AddChild(star);

        MeshInstance3D dot = Quad(SoftTex(), Colors.White, true, size * 0.6f);
        root.AddChild(dot);

        Tween t = CreateTween().SetParallel(true);
        t.TweenProperty(star, "scale", Vector3.One * size * 1.7f, 0.16f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        if (star.MaterialOverride is StandardMaterial3D starMat)
            t.TweenProperty(starMat, "albedo_color", new Color(color, 0f), 0.18f);
        if (dot.MaterialOverride is StandardMaterial3D dotMat)
            t.TweenProperty(dotMat, "albedo_color", new Color(1, 1, 1, 0f), 0.12f);

        KillAfter(root, 0.25f);
    }

    /// <summary>曳光弹：从 from 到 to 的一条细光带，很快消失。</summary>
    public void Tracer(Vector3 from, Vector3 to, Color? tint = null, float thickness = 0.035f)
    {
        float length = from.DistanceTo(to);
        if (length < 0.05f)
            return;

        Color color = tint ?? new Color("FFD98A");

        var line = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(thickness, thickness, length) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = (from + to) * 0.5f,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                DisableReceiveShadows = true,
            },
        };
        AddChild(line);
        line.LookAt(to, Vector3.Up);

        Tween t = CreateTween().SetParallel(true);
        if (line.MaterialOverride is StandardMaterial3D mat)
            t.TweenProperty(mat, "albedo_color", new Color(color, 0f), 0.1f);
        t.TweenProperty(line, "scale", new Vector3(0.4f, 0.4f, 1f), 0.1f);
        KillAfter(line, 0.15f);
    }

    /// <summary>炮口闪光。</summary>
    public void MuzzleFlash(Vector3 pos, float size = 0.45f)
    {
        var root = new Node3D { Position = pos };
        AddChild(root);

        MeshInstance3D flash = Quad(StarTex(), new Color(1f, 0.92f, 0.6f), true, size);
        root.AddChild(flash);

        Tween t = CreateTween().SetParallel(true);
        t.TweenProperty(flash, "scale", Vector3.One * size * 1.8f, 0.12f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        if (flash.MaterialOverride is StandardMaterial3D flashMat)
            t.TweenProperty(flashMat, "albedo_color", new Color(1f, 0.92f, 0.6f, 0f), 0.14f);

        KillAfter(root, 0.2f);
    }

    // ─────────────────────────── 导弹 ───────────────────────────

    /// <summary>
    /// 造一枚导弹（本地 -Z 是弹头方向，原点在弹体中心）。
    /// </summary>
    private static Node3D BuildMissile(float scale)
    {
        var missile = new Node3D { Scale = Vector3.One * scale };

        var body = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = 0.10f,
                BottomRadius = 0.14f,
                Height = 0.72f,
                RadialSegments = 6,
            },
            // 圆柱默认沿 Y 轴，转到 Z 轴（弹头朝 -Z）
            RotationDegrees = new Vector3(90f, 0f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                // 天空很亮，弹体用深色才看得见
                AlbedoColor = new Color("4A4750"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        missile.AddChild(body);

        // 弹头（黄色小锥）
        var nose = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.10f, Height = 0.24f, RadialSegments = 6 },
            Position = new Vector3(0f, 0f, -0.47f),
            RotationDegrees = new Vector3(-90f, 0f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("FFC24A"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        missile.AddChild(nose);

        // 尾焰（弹体后方 +Z）
        MeshInstance3D flame = Quad(SoftTex(), new Color(1f, 0.68f, 0.22f, 0.95f), true, 0.8f);
        flame.Position = new Vector3(0f, 0f, 0.5f);
        missile.AddChild(flame);

        return missile;
    }

    /// <summary>导弹从高空**笔直**落下砸到目标点，落地爆炸（空袭卡用）。</summary>
    public void MissileStrike(Vector3 target, float radius = 1.1f, float fallHeight = 5f,
        float duration = 0.55f, float delay = 0f)
    {
        if (delay > 0f)
        {
            var timer = GetTree().CreateTimer(delay);
            timer.Timeout += () => MissileStrike(target, radius, fallHeight, duration);
            return;
        }

        var root = new Node3D();
        AddChild(root);

        Vector3 start = target + new Vector3((float)GD.RandRange(-0.3, 0.3), fallHeight,
            (float)GD.RandRange(-0.3, 0.3));

        Node3D missile = BuildMissile(1f);
        missile.Position = start;
        // 笔直朝下：弹头（-Z）转到 -Y
        missile.RotationDegrees = new Vector3(-90f, 0f, 0f);
        root.AddChild(missile);

        // 落点警示圈
        MeshInstance3D mark = Quad(RingTex(), new Color(1f, 0.35f, 0.28f, 0.85f), true, radius * 2.2f);
        mark.Position = new Vector3(target.X, 0.06f, target.Z);
        mark.RotationDegrees = new Vector3(-90f, 0f, 0f);
        root.AddChild(mark);

        Tween fall = CreateTween();
        fall.TweenProperty(missile, "position", target + new Vector3(0f, 0.12f, 0f), duration)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.In);

        CreateTween()
            .TweenProperty(mark, "scale", Vector3.One * radius * 2.6f, duration)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        fall.Finished += () =>
        {
            Explosion(target + new Vector3(0f, 0.25f, 0f), radius);
            if (IsInstanceValid(missile))
                missile.QueueFree();
            if (IsInstanceValid(mark))
                mark.QueueFree();
            if (IsInstanceValid(root))
                root.QueueFree();
        };

        KillAfter(root, duration + 1.2f);
    }

    // ─────────────────────────── 治疗 ───────────────────────────

    /// <summary>
    /// 导弹井发射：从井口升起一枚导弹（用 decorates/missile 模型），
    /// 沿弧线飞向目标格上方，最后从高空砸下来爆炸。
    /// </summary>
    public void MissileLaunchFrom(Vector3 from, Vector3 target, float radius = 1.35f,
        float duration = 1.0f, float delay = 0f)
    {
        if (delay > 0f)
        {
            var timer = GetTree().CreateTimer(delay);
            timer.Timeout += () => MissileLaunchFrom(from, target, radius, duration);
            return;
        }

        var root = new Node3D();
        AddChild(root);

        var pivot = new Node3D { Position = from + Vector3.Up * 0.6f };
        root.AddChild(pivot);

        // 自转层：绕飞行轴（-Z）转，roll 效果
        var spinner = new Node3D();
        pivot.AddChild(spinner);

        Node3D model = SevenDay.Core.ModelUtil.SpawnModel(
            "res://gameAsset/models/decorates/missile/missile.glb", spinner, 0.66f, 0, out _);

        if (model == null)
        {
            root.QueueFree();
            MissileStrike(target, radius);
            return;
        }

        // 模型本身是「竖着」的（长轴沿 Y），先转到长轴朝 -Z（LookAt 的前方），
        // 否则会出现「横着飞」的情况
        model.RotationDegrees = new Vector3(-90f, 0f, 0f);

        // 旋转后模型重心不在原点上：把它挪回 pivot 原点，
        // 否则弹体会飘在飞行轨迹上方（尾迹在原点、弹体偏出一截）
        Aabb? body = SevenDay.Core.ModelUtil.CombinedAabb(model);
        if (body != null)
        {
            Transform3D mt = model.Transform;
            Vector3 center = (mt * body.Value).GetCenter();
            model.Position -= center;
        }

        // 自转：飞行途中绕自身轴转两圈
        CreateTween()
            .TweenProperty(spinner, "rotation_degrees:z", 720f, duration)
            .SetTrans(Tween.TransitionType.Linear);

        // ── 尾焰：外红内黄两层粒子（粒子在 3D 里更可靠，不会被遮挡判掉）
        CpuParticles3D MakeFlame(Color color, float size, float speed)
        {
            var grad = new Gradient();
            grad.SetColor(0, new Color(color.R, color.G, color.B, 0.95f));
            grad.SetColor(1, new Color(color.R, color.G * 0.6f, color.B * 0.4f, 0f));

            var p = new CpuParticles3D
            {
                Amount = 30,
                Lifetime = 0.22,
                Emitting = true,
                LocalCoords = true,
                Direction = new Vector3(0, 0, 1),
                Spread = 6f,
                InitialVelocityMin = speed * 0.9f,
                InitialVelocityMax = speed * 1.5f,
                Gravity = Vector3.Zero,
                ScaleAmountMin = size * 0.7f,
                ScaleAmountMax = size * 1.15f,
                ColorRamp = grad,
                Mesh = new QuadMesh { Size = new Vector2(0.4f, 0.4f) },
                Position = new Vector3(0f, 0f, 0.55f),
            };
            // 粒子颜色由 ColorRamp 决定，材质必须启用顶点色
            // 用 Mix 而不是加色：天空很亮，加色会被冲淡
            p.MaterialOverride = BillboardMat(SoftTex(), Colors.White, false, vertexColor: true);
            return p;
        }

        pivot.AddChild(MakeFlame(new Color(1f, 0.25f, 0.05f), 2.0f, 1.9f));   // 外层红
        pivot.AddChild(MakeFlame(new Color(1f, 0.95f, 0.55f), 1.2f, 2.6f));   // 内层黄

        // 尾迹：烟雾粒子挂在弹体上，随飞行拉出一条烟
        var smokeCurve = new Curve();
        smokeCurve.AddPoint(new Vector2(0f, 0.5f));
        smokeCurve.AddPoint(new Vector2(0.4f, 1.6f));
        smokeCurve.AddPoint(new Vector2(1f, 2.4f));

        var smokeGrad = new Gradient();
        smokeGrad.SetColor(0, new Color(0.95f, 0.92f, 0.88f, 0.75f));
        smokeGrad.SetColor(1, new Color(0.55f, 0.52f, 0.5f, 0f));

        var trail = new CpuParticles3D
        {
            Amount = 64,
            Lifetime = 0.95,
            Emitting = true,
            Direction = new Vector3(0, 0, 1),
            Spread = 18f,
            InitialVelocityMin = 0.15f,
            InitialVelocityMax = 0.55f,
            Gravity = new Vector3(0, 0.35f, 0),
            ScaleAmountMin = 0.5f,
            ScaleAmountMax = 0.75f,
            ScaleAmountCurve = smokeCurve,
            ColorRamp = smokeGrad,
            Mesh = new QuadMesh { Size = new Vector2(0.3f, 0.3f) },
        };
        trail.MaterialOverride = BillboardMat(SoftTex(), Colors.White, false, vertexColor: true);
        pivot.AddChild(trail);

        // 飞行音效
        SevenDay.Core.AudioManager.PlayMissileFlight(0.95f);

        // 发射闪光
        MuzzleFlash(from + Vector3.Up * 0.8f, 0.9f);

        // 落点警示圈
        MeshInstance3D mark = Quad(RingTex(), new Color(1f, 0.35f, 0.28f, 0.85f), true, radius * 2.2f);
        mark.Position = new Vector3(target.X, 0.06f, target.Z);
        mark.RotationDegrees = new Vector3(-90f, 0f, 0f);
        root.AddChild(mark);
        CreateTween()
            .TweenProperty(mark, "scale", Vector3.One * radius * 2.6f, duration)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        Vector3 p0 = pivot.Position;
        Vector3 c1 = p0 + Vector3.Up * 2.0f;              // 先垂直升空（别飞太高，会出画面）
        Vector3 c2 = target + Vector3.Up * 2.6f;          // 掠过
        Vector3 p3 = target + Vector3.Up * 0.2f;          // 砸向目标

        Tween fly = CreateTween();
        fly.TweenMethod(Callable.From<float>(p =>
        {
            if (!IsInstanceValid(pivot))
                return;

            Vector3 pos = Bezier3(p0, c1, c2, p3, p);
            pivot.Position = pos;

            Vector3 ahead = Bezier3(p0, c1, c2, p3, Mathf.Min(p + 0.015f, 1f));
            Vector3 dir = ahead - pos;
            if (dir.LengthSquared() > 0.0001f)
                pivot.LookAt(pos + dir, Vector3.Up);
        }), 0f, 1f, duration).SetTrans(Tween.TransitionType.Linear);

        fly.Finished += () =>
        {
            Explosion(target + Vector3.Up * 0.25f, radius);
            if (IsInstanceValid(root))
                root.QueueFree();
        };

        KillAfter(root, duration + 1.5f);
    }

    private static Vector3 Bezier3(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
    {
        float u = 1f - t;
        return u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d;
    }

    /// <summary>
    /// 小型导弹：从 from 飞向 to，命中后爆炸（战斗机用直线，导弹车给 arc 走抛物线）。
    /// </summary>
    public void MissileFromTo(Vector3 from, Vector3 to, float radius = 0.85f, float scale = 0.45f,
        float duration = 0.3f, float arc = 0f)
    {
        var root = new Node3D();
        AddChild(root);

        Node3D missile = BuildMissile(scale);
        missile.Position = from;
        root.AddChild(missile);
        missile.LookAt(to, Vector3.Up);

        if (arc <= 0.01f)
        {
            Tween fly = CreateTween();
            fly.TweenProperty(missile, "position", to, duration)
                .SetTrans(Tween.TransitionType.Quad)
                .SetEase(Tween.EaseType.In);
            fly.Finished += Impact;
        }
        else
        {
            // 抛物线弹道：控制点在两点中间上方
            Vector3 ctrl = (from + to) * 0.5f + Vector3.Up * arc;
            Tween fly = CreateTween();
            fly.TweenMethod(Callable.From<float>(p =>
            {
                if (!IsInstanceValid(missile))
                    return;

                Vector3 pos = Bezier(from, ctrl, to, p);
                missile.Position = pos;

                Vector3 ahead = Bezier(from, ctrl, to, Mathf.Min(p + 0.03f, 1f));
                Vector3 dir = ahead - pos;
                if (dir.LengthSquared() > 0.0001f)
                    missile.LookAt(pos + dir, Vector3.Up);
            }), 0f, 1f, duration).SetTrans(Tween.TransitionType.Linear);
            fly.Finished += Impact;
        }

        // 命中：爆炸 + 清理
        void Impact()
        {
            Explosion(to + Vector3.Up * 0.2f, radius);
            if (IsInstanceValid(missile))
                missile.QueueFree();
            if (IsInstanceValid(root))
                root.QueueFree();
        }

        KillAfter(root, duration + 1f);
    }

    private static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, float t)
    {
        float u = 1f - t;
        return u * u * a + 2f * u * t * b + t * t * c;
    }

    /// <summary>回血：绿色星芒往上飘、渐隐。</summary>
    public void Heal(Vector3 pos, float radius = 0.7f)
    {
        var root = new Node3D { Position = pos };
        AddChild(root);

        SevenDay.Core.AudioManager.PlayHeal();
        var green = new Color("8FE36B");

        for (int i = 0; i < 6; i++)
        {
            float ang = i * Mathf.Tau / 6f;
            Vector3 offset = new(Mathf.Cos(ang) * radius * 0.55f, 0f, Mathf.Sin(ang) * radius * 0.55f);

            MeshInstance3D cross = Quad(StarTex(), green, true, 0.42f);
            cross.Position = offset + new Vector3(0f, 0.15f, 0f);
            root.AddChild(cross);

            Tween t = CreateTween().SetParallel(true);
            t.TweenProperty(cross, "position", offset + new Vector3(0f, 1.15f, 0f), 0.75f)
                .SetDelay(i * 0.04f)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
            if (cross.MaterialOverride is StandardMaterial3D mat)
                t.TweenProperty(mat, "albedo_color", new Color(green, 0f), 0.7f).SetDelay(i * 0.04f);
        }

        // 地面绿光
        MeshInstance3D glow = Quad(SoftTex(), new Color(green, 0.55f), true, radius * 2.2f);
        glow.Position = new Vector3(0f, 0.05f, 0f);
        glow.RotationDegrees = new Vector3(-90f, 0f, 0f);
        root.AddChild(glow);

        if (glow.MaterialOverride is StandardMaterial3D glowMat)
            CreateTween().TweenProperty(glowMat, "albedo_color", new Color(green, 0f), 0.6f);

        KillAfter(root, 0.95f);
    }

    // ─────────────────────────── 阵亡 ───────────────────────────

    /// <summary>
    /// 单位阵亡：一小团扬尘 + 几片碎屑 + 一抹阵营色魂光（不刺眼，和爆炸区分开）。
    /// </summary>
    public void UnitDeath(Vector3 pos, Color accent, float scale = 1f)
    {
        var root = new Node3D { Position = pos };
        AddChild(root);

        // 扬尘：两团米灰色软雾，往上飘散
        for (int i = 0; i < 2; i++)
        {
            Vector3 off = new((float)GD.RandRange(-0.16, 0.16) * scale, 0.12f * scale,
                              (float)GD.RandRange(-0.16, 0.16) * scale);
            MeshInstance3D puff = Quad(SoftTex(), new Color("C9B18C", 0.55f), false, 0.5f * scale);
            puff.Position = off;
            root.AddChild(puff);

            Tween pt = CreateTween().SetParallel(true);
            pt.TweenProperty(puff, "scale", Vector3.One * 1.05f * scale, 0.5f)
                .SetDelay(i * 0.06f)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            pt.TweenProperty(puff, "position", off + new Vector3(0f, 0.55f * scale, 0f), 0.5f)
                .SetDelay(i * 0.06f)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

            if (puff.MaterialOverride is StandardMaterial3D puffMat)
                pt.TweenProperty(puffMat, "albedo_color", new Color(0.79f, 0.69f, 0.55f, 0f), 0.5f)
                    .SetDelay(i * 0.06f);
        }

        // 魂光：阵营色小星芒，一闪即灭（比爆炸柔和）
        MeshInstance3D soul = Quad(StarTex(), new Color("FFE7BE", 0.5f), true, 0.5f * scale);
        soul.Position = new Vector3(0f, 0.35f * scale, 0f);
        root.AddChild(soul);

        Tween st = CreateTween().SetParallel(true);
        st.TweenProperty(soul, "scale", Vector3.One * 1.5f * scale, 0.3f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        st.TweenProperty(soul, "position", new Vector3(0f, 0.75f * scale, 0f), 0.3f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        if (soul.MaterialOverride is StandardMaterial3D soulMat)
            st.TweenProperty(soulMat, "albedo_color", new Color(1f, 0.9f, 0.72f, 0f), 0.28f);

        // 碎屑：深褐小方块，往外弹开再落下
        var debris = new CpuParticles3D
        {
            Amount = 10,
            Lifetime = 0.5,
            OneShot = true,
            Explosiveness = 0.95f,
            Direction = new Vector3(0, 1, 0),
            Spread = 75f,
            InitialVelocityMin = 1.4f * scale,
            InitialVelocityMax = 3.0f * scale,
            Gravity = new Vector3(0, -8f, 0),
            ScaleAmountMin = 0.4f,
            ScaleAmountMax = 0.9f,
            Mesh = new QuadMesh { Size = new Vector2(0.1f, 0.1f) * scale },
        };
        debris.MaterialOverride = BillboardMat(SoftTex(), new Color("5A4632"), false);
        root.AddChild(debris);
        debris.Emitting = true;

        KillAfter(root, 0.7f);
    }

    /// <summary>建筑被摧毁：大团尘土、扩散气浪环、更多碎块，还有一点火星。</summary>
    public void BuildingDeath(Vector3 pos, Color accent, float scale = 1.3f)
    {
        var root = new Node3D { Position = pos };
        AddChild(root);

        // 尘土：四团米灰雾，错开上升
        for (int i = 0; i < 4; i++)
        {
            float ang = i * Mathf.Tau / 4f + 0.4f;
            Vector3 off = new(Mathf.Cos(ang) * 0.45f * scale, 0.2f * scale, Mathf.Sin(ang) * 0.45f * scale);

            MeshInstance3D puff = Quad(SoftTex(), new Color("C3AB86", 0.6f), false, 0.9f * scale);
            puff.Position = off;
            root.AddChild(puff);

            Tween pt = CreateTween().SetParallel(true);
            pt.TweenProperty(puff, "scale", Vector3.One * 2.1f * scale, 0.8f)
                .SetDelay(i * 0.05f)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            pt.TweenProperty(puff, "position", off + new Vector3(0f, 1.1f * scale, 0f), 0.8f)
                .SetDelay(i * 0.05f)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

            if (puff.MaterialOverride is StandardMaterial3D puffMat)
                pt.TweenProperty(puffMat, "albedo_color", new Color(0.75f, 0.65f, 0.5f, 0f), 0.8f)
                    .SetDelay(i * 0.05f);
        }

        // 气浪环：贴地扩散
        MeshInstance3D ring = Quad(RingTex(), new Color(1f, 0.86f, 0.6f, 0.7f), true, 0.9f * scale);
        ring.Position = new Vector3(0f, 0.08f, 0f);
        ring.RotationDegrees = new Vector3(-90f, 0f, 0f);
        root.AddChild(ring);

        Tween rt = CreateTween().SetParallel(true);
        rt.TweenProperty(ring, "scale", Vector3.One * 2.6f * scale, 0.55f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        if (ring.MaterialOverride is StandardMaterial3D ringMat)
            rt.TweenProperty(ringMat, "albedo_color", new Color(1f, 0.86f, 0.6f, 0f), 0.55f);

        // 阵营色闪光
        MeshInstance3D flash = Quad(StarTex(), new Color("FFD9A0", 0.55f), true, 0.9f * scale);
        flash.Position = new Vector3(0f, 0.5f * scale, 0f);
        root.AddChild(flash);

        Tween ft = CreateTween().SetParallel(true);
        ft.TweenProperty(flash, "scale", Vector3.One * 2.0f * scale, 0.35f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        if (flash.MaterialOverride is StandardMaterial3D flashMat)
            ft.TweenProperty(flashMat, "albedo_color", new Color(1f, 0.85f, 0.63f, 0f), 0.3f);

        // 碎块：比单位多，飞得更远
        var debris = new CpuParticles3D
        {
            Amount = 22,
            Lifetime = 0.75,
            OneShot = true,
            Explosiveness = 0.9f,
            Direction = new Vector3(0, 1, 0),
            Spread = 85f,
            InitialVelocityMin = 2.2f * scale,
            InitialVelocityMax = 4.6f * scale,
            Gravity = new Vector3(0, -9f, 0),
            ScaleAmountMin = 0.4f,
            ScaleAmountMax = 1.0f,
            Mesh = new QuadMesh { Size = new Vector2(0.14f, 0.14f) * scale },
        };
        debris.MaterialOverride = BillboardMat(SoftTex(), new Color("4E3B2A"), false);
        root.AddChild(debris);
        debris.Emitting = true;

        // 少量火星点缀
        var embers = new CpuParticles3D
        {
            Amount = 8,
            Lifetime = 0.5,
            OneShot = true,
            Explosiveness = 0.9f,
            Direction = new Vector3(0, 1, 0),
            Spread = 70f,
            InitialVelocityMin = 2.0f * scale,
            InitialVelocityMax = 3.8f * scale,
            Gravity = new Vector3(0, -7f, 0),
            ScaleAmountMin = 0.3f,
            ScaleAmountMax = 0.7f,
            Mesh = new QuadMesh { Size = new Vector2(0.1f, 0.1f) * scale },
        };
        embers.MaterialOverride = BillboardMat(SoftTex(), new Color("FFC24A"), true);
        root.AddChild(embers);
        embers.Emitting = true;

        Shake?.Invoke(0.08f * scale, 0.18f);
        KillAfter(root, 1.0f);
    }

    // ─────────────────────────── 工具 ───────────────────────────

    private void KillAfter(Node node, float seconds)
    {
        if (!IsInsideTree())
        {
            node.QueueFree();
            return;
        }

        var timer = GetTree().CreateTimer(seconds);
        timer.Timeout += () =>
        {
            if (IsInstanceValid(node))
                node.QueueFree();
        };
    }
}
