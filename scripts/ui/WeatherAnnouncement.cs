using Godot;
using SevenDay.Battle;
using SevenDay.Core;

namespace SevenDay.UI;

/// <summary>天气来临播报：每种天气有独立背景，显示大号天气名与描述。</summary>
public partial class WeatherAnnouncement : Control
{
    private TextureRect _background;
    private WeatherRingFx _rings;
    private Tween _ringTween;
    private Label _name;
    private Label _description;
    private Tween _tween;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visible = false;
        ZIndex = 400;

        _background = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0),
        };
        _background.SetAnchorsPreset(LayoutPreset.Center);
        _background.OffsetLeft = -450;
        _background.OffsetTop = -260;
        _background.OffsetRight = 450;
        _background.OffsetBottom = 260;
        _background.PivotOffset = new Vector2(450, 260);
        AddChild(_background);

        _rings = new WeatherRingFx
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        _rings.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_rings);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 2 };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 4);
        center.AddChild(box);

        _name = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _name.AddThemeFontSizeOverride("font_size", 78);
        _name.AddThemeColorOverride("font_color", new Color(1f, 0.97f, 0.88f));
        _name.AddThemeColorOverride("font_outline_color", new Color(0.08f, 0.05f, 0.04f));
        _name.AddThemeConstantOverride("outline_size", 10);
        _name.PivotOffset = new Vector2(200, 50);
        _name.Resized += () => _name.PivotOffset = _name.Size * 0.5f;
        box.AddChild(_name);

        _description = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(720, 0),
        };
        _description.AddThemeFontSizeOverride("font_size", 21);
        _description.AddThemeColorOverride("font_color", new Color(1f, 0.94f, 0.82f));
        _description.AddThemeColorOverride("font_outline_color", new Color(0.08f, 0.05f, 0.04f));
        _description.AddThemeConstantOverride("outline_size", 7);
        box.AddChild(_description);
    }

    public void Show(WeatherType type, string weatherName, string description)
    {
        if (_tween != null && _tween.IsValid())
            _tween.Kill();

        _background.Texture = GD.Load<Texture2D>(BackgroundPath(type));
        _rings.Play(RingColor(type));
        _rings.ZIndex = 1;
        _rings.Size = GetViewportRect().Size;
        _rings.Position = Vector2.Zero;

        _background.Modulate = new Color(1, 1, 1, 0);
        _background.Scale = new Vector2(0.78f, 0.78f);
        _background.Rotation = -0.035f;

        _name.Text = weatherName;
        _name.AddThemeColorOverride("font_color", NameColor(type));
        _description.Text = description;
        _name.Scale = new Vector2(1.30f, 1.30f);
        _description.Modulate = new Color(1, 1, 1, 0);

        Visible = true;
        Modulate = new Color(1, 1, 1, 0);
        AudioManager.PlaySfx("res://gameAsset/audios/weather/weather_announce.ogg", -4f);

        _tween = CreateTween();
        _tween.SetParallel(true);
        _tween.TweenProperty(this, "modulate:a", 1f, 0.18f);
        _tween.TweenProperty(_background, "modulate:a", 0.96f, 0.24f);
        _tween.TweenProperty(_background, "scale", Vector2.One, 0.48f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        _tween.TweenProperty(_background, "rotation", 0f, 0.48f);
        _tween.TweenProperty(_name, "scale", Vector2.One, 0.36f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        _tween.TweenProperty(_description, "modulate:a", 1f, 0.28f).SetDelay(0.18f);

        _tween.Chain().TweenInterval(1.55f);
        _tween.Chain().TweenProperty(this, "modulate:a", 0f, 0.45f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        _tween.Chain().TweenCallback(Callable.From(() => Visible = false));
    }

    private static string BackgroundPath(WeatherType type) => type switch
    {
        WeatherType.Thunderstorm => "res://gameAsset/textures/ui/weather/announce/announce_thunderstorm.png",
        WeatherType.Tornado => "res://gameAsset/textures/ui/weather/announce/announce_tornado.png",
        WeatherType.Sandstorm => "res://gameAsset/textures/ui/weather/announce/announce_sandstorm.png",
        WeatherType.Earthquake => "res://gameAsset/textures/ui/weather/announce/announce_earthquake.png",
        WeatherType.ClearSky => "res://gameAsset/textures/ui/weather/announce/announce_clear_sky.png",
        WeatherType.Moonbow => "res://gameAsset/textures/ui/weather/announce/announce_moonbow.png",
        WeatherType.Aurora => "res://gameAsset/textures/ui/weather/announce/announce_aurora.png",
        WeatherType.GiftRain => "res://gameAsset/textures/ui/weather/announce/announce_gift_rain.png",
        _ => "res://gameAsset/textures/ui/weather/announce/announce_clear_sky.png",
    };

    private static Color RingColor(WeatherType type) => type switch
    {
        WeatherType.Thunderstorm => new Color(1f, 0.82f, 0.20f),
        WeatherType.Tornado => new Color(0.86f, 0.86f, 0.84f),
        WeatherType.Sandstorm => new Color(1f, 0.66f, 0.26f),
        WeatherType.Earthquake => new Color(0.82f, 0.58f, 0.34f),
        WeatherType.ClearSky => new Color(1f, 0.88f, 0.35f),
        WeatherType.Moonbow => new Color(0.70f, 0.78f, 1f),
        WeatherType.Aurora => new Color(0.36f, 1f, 0.72f),
        WeatherType.GiftRain => new Color(1f, 0.42f, 0.36f),
        _ => Colors.White,
    };

    private static Color NameColor(WeatherType type) => type switch
    {
        WeatherType.Thunderstorm => new Color(1f, 0.95f, 0.62f),
        WeatherType.Tornado => new Color(0.92f, 0.92f, 0.90f),
        WeatherType.Sandstorm => new Color(1f, 0.88f, 0.58f),
        WeatherType.Earthquake => new Color(0.96f, 0.82f, 0.62f),
        WeatherType.ClearSky => new Color(1f, 0.98f, 0.82f),
        WeatherType.Moonbow => new Color(0.82f, 0.90f, 1f),
        WeatherType.Aurora => new Color(0.72f, 1f, 0.88f),
        WeatherType.GiftRain => new Color(1f, 0.88f, 0.82f),
        _ => new Color(1f, 0.97f, 0.88f),
    };
}
