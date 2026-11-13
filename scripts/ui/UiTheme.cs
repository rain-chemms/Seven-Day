using Godot;

namespace SevenDay.UI;

/// <summary>
/// 用 gameAsset/textures/ui 里的九宫格素材在运行时拼出 UI 主题。
/// 配色与 3D 卡通渲染保持一致：深棕描边 + 米色/沙色/军绿。
/// </summary>
public static class UiTheme
{
    public const string Btn = "res://gameAsset/textures/ui/button/";

    /// <summary>
    /// 按下/选中时的文字色：偏灰的暖黄（不是刺眼的白），在暗色底图上够亮又不扎眼。
    /// </summary>
    public static readonly Color OnPressedText = new("D8CDA6");
    public const string PanelDir = "res://gameAsset/textures/ui/panel/";
    public const string BarDir = "res://gameAsset/textures/ui/progressBar/";
    public const string IconDir = "res://gameAsset/textures/ui/icon/";

    public static readonly Color Ink = new("3B2E16");
    public static readonly Color Beige = new("EEE7D8");
    public static readonly Color Sand = new("C9B98A");
    public static readonly Color SandLight = new("D6C99C");
    public static readonly Color SandDark = new("A89566");
    public static readonly Color Red = new("C2543E");
    public static readonly Color Green = new("6C7C4A");
    public static readonly Color Gold = new("D9A63A");
    public static readonly Color Grey = new("9A9A92");
    public static readonly Color Paper = new("F5EFE2");

    private static Theme _theme;

    public static Theme Get()
    {
        return _theme ??= Build();
    }

    public static StyleBoxTexture Box(string path, int margin, int l = 0, int r = 0, int t = 0, int b = 0)
    {
        var sb = new StyleBoxTexture
        {
            Texture = GD.Load<Texture2D>(path),
        };
        sb.SetTextureMarginAll(margin);
        if (l + r + t + b > 0)
        {
            sb.ContentMarginLeft = l;
            sb.ContentMarginRight = r;
            sb.ContentMarginTop = t;
            sb.ContentMarginBottom = b;
        }

        return sb;
    }

    public static Texture2D Icon(string name) =>
        GD.Load<Texture2D>(IconDir + "icon_" + name + ".png");

    public static Texture2D CardFace(string unitId) =>
        GD.Load<Texture2D>("res://gameAsset/textures/cardface/cardface_" + unitId + ".png");

    private static Theme Build()
    {
        var theme = new Theme();
        var font = new SystemFont
        {
            FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" },
            AllowSystemFallback = true,
        };
        theme.DefaultFont = font;
        theme.DefaultFontSize = 18;

        theme.SetStylebox("normal", "Button", Box(Btn + "btn_normal.png", 14, 18, 18, 6, 10));
        theme.SetStylebox("hover", "Button", Box(Btn + "btn_hover.png", 14, 18, 18, 6, 10));
        theme.SetStylebox("pressed", "Button", Box(Btn + "btn_pressed.png", 14, 18, 18, 6, 10));
        theme.SetStylebox("disabled", "Button", Box(Btn + "btn_disabled.png", 14, 18, 18, 6, 10));
        theme.SetStylebox("focus", "Button", new StyleBoxEmpty());
        theme.SetColor("font_color", "Button", Ink);
        theme.SetColor("font_hover_color", "Button", Ink);
        theme.SetColor("font_pressed_color", "Button", OnPressedText);
        theme.SetColor("font_disabled_color", "Button", Grey);

        theme.SetStylebox("panel", "PanelContainer", Box(PanelDir + "panel.png", 18, 16, 16, 16, 16));
        theme.SetStylebox("panel", "Panel", Box(PanelDir + "panel.png", 18, 16, 16, 16, 16));
        theme.SetStylebox("panel", "PopupPanel", Box(PanelDir + "panel_light.png", 18, 10, 10, 10, 10));
        theme.SetStylebox("panel", "TooltipPanel", Box("res://gameAsset/textures/ui/tooltip/tooltip.png", 14, 12, 12, 12, 12));

        theme.SetColor("font_color", "Label", Ink);
        theme.SetColor("font_color", "RichTextLabel", Ink);
        theme.SetColor("default_color", "RichTextLabel", Ink);

        theme.SetStylebox("background", "ProgressBar", Box(BarDir + "bar_bg.png", 10));
        theme.SetStylebox("fill", "ProgressBar", Box(BarDir + "bar_fill_green.png", 10));
        theme.SetColor("font_color", "ProgressBar", Ink);

        theme.SetStylebox("tab_selected", "TabContainer", Box("res://gameAsset/textures/ui/tab/tab_selected.png", 12, 14, 14, 8, 6));
        theme.SetStylebox("tab_unselected", "TabContainer", Box("res://gameAsset/textures/ui/tab/tab_normal.png", 12, 14, 14, 8, 6));
        theme.SetStylebox("tab_hovered", "TabContainer", HoveredTabBox());
        theme.SetStylebox("tab_disabled", "TabContainer", Box("res://gameAsset/textures/ui/tab/tab_normal.png", 12, 14, 14, 8, 6));
        theme.SetStylebox("tab_focus", "TabContainer", new StyleBoxEmpty());
        theme.SetStylebox("tabbar_background", "TabContainer", new StyleBoxEmpty());
        theme.SetStylebox("panel", "TabContainer", Box(PanelDir + "panel.png", 18, 16, 16, 16, 16));
        theme.SetColor("font_selected_color", "TabContainer", Ink);
        theme.SetColor("font_unselected_color", "TabContainer", new Color("6B5836"));
        theme.SetColor("font_hovered_color", "TabContainer", Ink);
        theme.SetColor("font_disabled_color", "TabContainer", Grey);

        theme.SetStylebox("scroll", "VScrollBar", Box("res://gameAsset/textures/ui/scrollBar/scroll_track.png", 10));
        theme.SetStylebox("grabber", "VScrollBar", Box("res://gameAsset/textures/ui/scrollBar/scroll_grabber.png", 10));

        theme.SetStylebox("panel", "PopupMenu", Box(PanelDir + "panel_light.png", 18, 8, 8, 8, 8));
        theme.SetColor("font_color", "PopupMenu", Ink);

        theme.SetIcon("checked", "CheckBox", GD.Load<Texture2D>("res://gameAsset/textures/ui/checkBox/check_on.png"));
        theme.SetIcon("unchecked", "CheckBox", GD.Load<Texture2D>("res://gameAsset/textures/ui/checkBox/check_off.png"));
        theme.SetIcon("checked", "CheckButton", GD.Load<Texture2D>("res://gameAsset/textures/ui/checkBox/check_on.png"));
        theme.SetIcon("unchecked", "CheckButton", GD.Load<Texture2D>("res://gameAsset/textures/ui/checkBox/check_off.png"));

        // 开关不要外围包裹框：只留方框图标本身
        foreach (string type in new[] { "CheckBox", "CheckButton" })
        {
            theme.SetStylebox("normal", type, new StyleBoxEmpty());
            theme.SetStylebox("hover", type, new StyleBoxEmpty());
            theme.SetStylebox("pressed", type, new StyleBoxEmpty());
            theme.SetStylebox("hover_pressed", type, new StyleBoxEmpty());
            theme.SetStylebox("disabled", type, new StyleBoxEmpty());
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
        }

        theme.SetStylebox("normal", "OptionButton", Box(Btn + "btn_normal.png", 14, 18, 18, 6, 10));
        theme.SetStylebox("hover", "OptionButton", Box(Btn + "btn_hover.png", 14, 18, 18, 6, 10));
        theme.SetStylebox("pressed", "OptionButton", Box(Btn + "btn_pressed.png", 14, 18, 18, 6, 10));
        theme.SetStylebox("disabled", "OptionButton", Box(Btn + "btn_disabled.png", 14, 18, 18, 6, 10));
        theme.SetStylebox("focus", "OptionButton", new StyleBoxEmpty());
        theme.SetColor("font_color", "OptionButton", Ink);
        theme.SetColor("font_hover_color", "OptionButton", Ink);
        theme.SetColor("font_pressed_color", "OptionButton", OnPressedText);

        return theme;
    }

    /// <summary>标签页悬停：用未选中的贴图提亮一点，和默认灰色框区分开。</summary>
    private static StyleBoxTexture HoveredTabBox()
    {
        StyleBoxTexture sb = Box("res://gameAsset/textures/ui/tab/tab_normal.png", 12, 14, 14, 8, 6);
        sb.ModulateColor = new Color(1.16f, 1.12f, 1.02f);
        return sb;
    }
}
