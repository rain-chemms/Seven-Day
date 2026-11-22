using Godot;
using SevenDay.Battle;
using SevenDay.Core;

namespace SevenDay.UI;

/// <summary>横向天气图标筛选器：点击开关，外框表示状态，悬停用跟随鼠标的提示框显示描述。</summary>
public static class WeatherFilterUi
{
    public static PopupPanel Create(Node parent)
    {
        var popup = new PopupPanel
        {
            Title = Loc.T("settings.weather_filter"),
            Transient = true,
            Exclusive = true,
        };
        parent.AddChild(popup);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        popup.AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        margin.AddChild(column);

        var title = new Label
        {
            Text = Loc.T("settings.weather_filter"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", UiTheme.Ink);
        column.AddChild(title);

        var separator = new ColorRect
        {
            Color = new Color(UiTheme.Ink, 0.35f),
            CustomMinimumSize = new Vector2(0, 2),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        column.AddChild(separator);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        column.AddChild(row);

        foreach (WeatherDef weather in WeatherCatalog.All)
        {
            WeatherDef captured = weather;
            var button = new Button
            {
                CustomMinimumSize = new Vector2(54, 54),
                ToggleMode = true,
                ButtonPressed = Session.WeatherTypeEnabled(weather.Type),
                TooltipText = $"{weather.Name}\n{weather.Desc}",
                FocusMode = Control.FocusModeEnum.None,
            };
            button.AddThemeStyleboxOverride("normal", Frame(new Color("D8CBB1"), new Color("6A573D")));
            button.AddThemeStyleboxOverride("hover", Frame(new Color("F1E4C8"), new Color("D9A733")));
            button.AddThemeStyleboxOverride("pressed", Frame(new Color("F7E7B8"), new Color("C58B16")));
            button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

            var icon = new TextureRect
            {
                Texture = GD.Load<Texture2D>(weather.Icon),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = Session.WeatherTypeEnabled(weather.Type)
                    ? Colors.White
                    : new Color(1, 1, 1, 0.42f),
            };
            icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            icon.OffsetLeft = 4;
            icon.OffsetTop = 4;
            icon.OffsetRight = -4;
            icon.OffsetBottom = -4;
            button.AddChild(icon);

            button.Toggled += on =>
            {
                Session.SetWeatherTypeEnabled(captured.Type, on);
                Settings.Save();
                icon.Modulate = on ? Colors.White : new Color(1, 1, 1, 0.42f);
            };

            ButtonJuice.Attach(button);
            row.AddChild(button);
        }

        popup.WindowInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
                popup.Hide();
        };
        popup.PopupHide += popup.QueueFree;

        popup.PopupCentered(new Vector2I(580, 130));
        return popup;
    }

    private static StyleBoxFlat Frame(Color fill, Color border) => new()
    {
        BgColor = fill,
        BorderColor = border,
        BorderWidthLeft = 2,
        BorderWidthRight = 2,
        BorderWidthTop = 2,
        BorderWidthBottom = 2,
        CornerRadiusTopLeft = 8,
        CornerRadiusTopRight = 8,
        CornerRadiusBottomLeft = 8,
        CornerRadiusBottomRight = 8,
    };
}
