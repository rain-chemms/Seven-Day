using Godot;
using SevenDay.Core;
using SevenDay.UI;

namespace SevenDay.UI;

/// <summary>
/// 启动动画：黑底、Logo 淡入 + 高光扫过 + 淡出，然后进入主菜单。
/// 任意按键/点击可跳过（立刻进入主菜单）。
/// </summary>
public partial class BootSplash : Control
{
    private const float LogoWidth = 560f;

    private TextureRect _logo;
    private ShaderMaterial _shine;
    private Tween _anim;
    private bool _finishing;

    public override void _Ready()
    {
        DisplaySettings.ApplyFromSettings();

        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        // 引擎默认清屏灰，和原生启动图一致
        var bg = new ColorRect { Color = new Color(0.3f, 0.3f, 0.3f, 1f) };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        bool pixelStyle = Settings.GetInt("video", "boot_logo", 0) == 1;
        _logo = new TextureRect
        {
            Texture = GD.Load<Texture2D>(pixelStyle
                ? "res://gameAsset/textures/logo/logo_pixel.png"
                : "res://gameAsset/textures/logo/logo.png"),
            CustomMinimumSize = new Vector2(LogoWidth, LogoWidth * 344f / 358f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            PivotOffset = new Vector2(LogoWidth * 0.5f, LogoWidth * 344f / 358f * 0.5f),
        };
        _logo.SetAnchorsPreset(LayoutPreset.Center);
        if (pixelStyle)
            _logo.TextureFilter = TextureFilterEnum.Nearest;   // 像素风保持硬边

        _shine = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://gameAsset/shaders/bootShine.gdshader"),
        };
        _shine.SetShaderParameter("sweep", -0.4f);
        // 流光调暗：只做一层很轻的扫光，不抢 Logo
        _shine.SetShaderParameter("strength", 0.38f);
        _shine.SetShaderParameter("outline_color", new Color(0.66f, 0.84f, 1f, 0.85f));   // 淡蓝描边
        _shine.SetShaderParameter("outline_width", 1.2f);
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--boot-sweep") >= 0)
            _shine.SetShaderParameter("sweep", 0.55f);
        _logo.Material = _shine;

        center.AddChild(_logo);

        _logo.Modulate = new Color(1, 1, 1, 0);
        _logo.Scale = Vector2.One * 0.94f;

        PlayIntro();
    }

    private void PlayIntro()
    {
        GD.Print($"[Boot] 启动动画开始 t={Time.GetTicksMsec()}");
        _anim?.Kill();

        // 1) 淡入 + 轻微放大
        Tween fade = CreateTween().SetParallel(true);
        fade.TweenProperty(_logo, "modulate:a", 1f, 1.0f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        fade.TweenProperty(_logo, "scale", Vector2.One, 1.2f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        // 2) 高光从左上扫到右下（--boot-sweep 时固定在中段，便于截图）
        bool fixedSweep = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--boot-sweep") >= 0;
        Tween shine = CreateTween();
        if (fixedSweep)
        {
            _shine.SetShaderParameter("sweep", 0.55f);
            shine.Kill();
        }
        else
        {
        shine.TweenInterval(0.4f);
        bool slowShine = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--boot-slow") >= 0;
        shine.TweenMethod(Callable.From<float>(v =>
        {
            _shine.SetShaderParameter("sweep", v);
            if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--boot-debug") >= 0)
                GD.Print($"[Boot] sweep={v:F2}");
        }), -0.4f, 1.4f,
                slowShine ? 5.0f : 2.2f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        }

        // 3) 停留后整体淡出，进入主菜单（--boot-slow 放慢，便于截图/录屏）
        bool slow = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--boot-slow") >= 0;
        GetTree().CreateTimer(slow ? 12.0 : 3.4).Timeout += Finish;

        // 调试：--demo-boot-hold 停在动画中段方便截图
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--demo-boot-hold") >= 0)
        {
            GetTree().CreateTimer(0.0).Timeout += () => SetProcess(false);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_finishing)
            return;

        if (@event is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true } ||
            @event is InputEventJoypadButton { Pressed: true })
        {
            GetViewport().SetInputAsHandled();
            Finish();
        }
    }

    private void Finish()
    {
        if (_finishing)
            return;

        _finishing = true;
        GD.Print($"[Boot] 启动动画结束，进入主菜单 t={Time.GetTicksMsec()}");
        _anim?.Kill();

        Tween outTween = CreateTween().SetParallel(true);
        outTween.TweenProperty(_logo, "modulate:a", 0f, 0.8f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        outTween.TweenProperty(_logo, "scale", Vector2.One * 1.03f, 0.8f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);

        // Logo 完全消失后，主菜单从全黑慢慢淡入，避免画面跳变
        outTween.Chain().TweenCallback(Callable.From(() =>
        {
            Session.FadeInFromBlack = true;
            GetTree().ChangeSceneToFile("res://scenes/mainMenu.tscn");
        }));
    }
}
