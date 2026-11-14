using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>
/// 牌堆查看器：卡牌网格铺满面板（杀戮尖塔式），打开/关闭带透明度淡入淡出。
/// </summary>
public partial class PileViewer : Control
{
    private const int MaxColumns = 5;
    private const int Gap = 14;

    // 面板内边距 + 顶栏占用（用来算卡牌缩放）
    private const float PanelWidth = 940f;
    private const float PanelHeight = 600f;
    private const int ContentMargin = 16;
    private const int HeaderHeight = 46;
    private const int HeaderGap = 12;

    private PanelContainer _panel;
    private CenterContainer _center;
    private GridContainer _grid;
    private Label _title;
    private readonly List<Control> _cells = new();

    private Tween _fade;
    private bool _closing;

    public PileViewer()
    {
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visible = false;
        Modulate = new Color(1, 1, 1, 0);

        var dim = new ColorRect
        {
            Color = new Color(0.03f, 0.02f, 0.02f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop,
        };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        dim.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed)
                Close();
        };

        _panel = new PanelContainer { Name = "Panel" };
        _panel.SetAnchorsPreset(LayoutPreset.Center);
        _panel.OffsetLeft = -PanelWidth * 0.5f;
        _panel.OffsetRight = PanelWidth * 0.5f;
        _panel.OffsetTop = -PanelHeight * 0.5f;
        _panel.OffsetBottom = PanelHeight * 0.5f;

        var style = new StyleBoxFlat
        {
            BgColor = new Color("1C1A17"),
            BorderColor = new Color("6B5B3E"),
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
        };
        style.SetCornerRadiusAll(10);
        style.SetContentMarginAll(ContentMargin);
        _panel.AddThemeStyleboxOverride("panel", style);
        AddChild(_panel);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", HeaderGap);
        _panel.AddChild(root);

        // 顶栏：标题 + 关闭
        var header = new HBoxContainer { CustomMinimumSize = new Vector2(0, HeaderHeight) };
        header.AddThemeConstantOverride("separation", 12);
        root.AddChild(header);

        _title = new Label { Text = "牌堆" };
        _title.AddThemeFontSizeOverride("font_size", 24);
        _title.AddThemeColorOverride("font_color", new Color("E8D9B5"));
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_title);

        Button close = new() { Text = "关闭" };
        UiSound.Attach(close);
        close.CustomMinimumSize = new Vector2(96, 40);
        close.Pressed += Close;
        header.AddChild(close);

        // 卡牌网格：居中铺满（不滚动，靠缩放自适应数量）
        _center = new CenterContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        root.AddChild(_center);

        _grid = new GridContainer { Columns = MaxColumns };
        _grid.AddThemeConstantOverride("h_separation", Gap);
        _grid.AddThemeConstantOverride("v_separation", Gap);
        _center.AddChild(_grid);
    }

    /// <summary>可用的内容区域（用来算卡牌缩放）。</summary>
    private Vector2 ContentArea =>
        new(PanelWidth - ContentMargin * 2f, PanelHeight - ContentMargin * 2f - HeaderHeight - HeaderGap);

    public void Open(string title, IEnumerable<string> cardIds, IReadOnlyList<int> etherealIndices = null)
    {
        List<string> list = cardIds.ToList();
        _title.Text = $"{title}（{list.Count}）";

        var ethereal = new HashSet<int>(etherealIndices ?? (IReadOnlyList<int>)System.Array.Empty<int>());

        foreach (Control cell in _cells)
            cell.QueueFree();
        _cells.Clear();

        if (list.Count == 0)
        {
            var empty = new Label { Text = "（空）" };
            empty.AddThemeColorOverride("font_color", new Color("E8D9B5"));
            _grid.AddChild(empty);
            _cells.Add(empty);
        }

        // 按数量算格子大小，让卡牌尽量铺满
        int columns = Mathf.Clamp(list.Count, 1, MaxColumns);
        int rows = Mathf.Max(1, Mathf.CeilToInt(list.Count / (float)columns));
        _grid.Columns = columns;

        // 先建卡，量出「一张卡实际需要多大」（描述行数会让它比 128x196 高）
        var pending = new List<(CardDef Def, CardView Card, bool Ethereal)>();
        Vector2 need = new(CardView.CardWidth, CardView.CardHeight);

        for (int i = 0; i < list.Count; i++)
        {
            CardDef def = GameDatabase.Card(list[i]);
            if (def == null)
                continue;

            var card = new CardView
            {
                MouseFilter = MouseFilterEnum.Ignore,
                PivotOffset = Vector2.Zero,     // 从左上角缩放，避免内容被推到格子外
            };
            card.Theme = MsdfTheme();      // 放大后文字不糊
            card.Setup(def, 0, affordable: true);
            card.SetEthereal(ethereal.Contains(i));
            need = need.Max(card.GetCombinedMinimumSize());
            pending.Add((def, card, ethereal.Contains(i)));
        }

        Vector2 area = ContentArea;
        float fitW = (area.X - (columns - 1) * Gap) / (columns * need.X);
        float fitH = (area.Y - (rows - 1) * Gap) / (rows * need.Y);
        // 放大上限压到 1.3：再往上字体/卡图会被拉糊（卡牌是按 128×196 排版的）
        float scale = Mathf.Clamp(Mathf.Min(fitW, fitH), 0.35f, 1.3f);

        foreach ((CardDef def, CardView card, bool _) in pending)
        {
            var cell = new Control
            {
                CustomMinimumSize = need * scale,
                MouseFilter = MouseFilterEnum.Ignore,
                ClipContents = true,
            };

            card.Position = Vector2.Zero;
            card.Size = need;
            card.Scale = Vector2.One * scale;
            cell.AddChild(card);

            _grid.AddChild(cell);
            _cells.Add(cell);
        }

        FadeTo(1f, 0.18f);
    }

    public void Close()
    {
        if (!Visible || _closing)
            return;

        _closing = true;
        FadeTo(0f, 0.15f, () =>
        {
            _closing = false;
            Visible = false;
        });
    }

    private void FadeTo(float alpha, float duration, System.Action onDone = null)
    {
        _fade?.Kill();
        Visible = true;
        _fade = CreateTween();
        _fade.TweenProperty(this, "modulate:a", alpha, duration)
             .SetTrans(Tween.TransitionType.Sine)
             .SetEase(Tween.EaseType.Out);

        if (onDone != null)
            _fade.TweenCallback(Callable.From(onDone));
    }

    /// <summary>
    /// 给查看器里的卡牌一套 MSDF 字体主题：字体按距离场渲染，放大后依然锐利，
    /// 不会像普通动态字体那样被拉糊。
    /// </summary>
    private static Theme _msdfTheme;

    private static Theme MsdfTheme()
    {
        if (_msdfTheme != null)
            return _msdfTheme;

        var font = new SystemFont
        {
            FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" },
            MultichannelSignedDistanceField = true,
        };

        _msdfTheme = new Theme { DefaultFont = font, DefaultFontSize = 18 };
        return _msdfTheme;
    }

    /// <summary>一张按比例缩放的卡（外面包一层固定尺寸的壳，方便网格排版）。</summary>
    private Control BuildCardCell(CardDef def, float scale)
    {
        var holder = new Control
        {
            CustomMinimumSize = new Vector2(CardView.CardWidth, CardView.CardHeight) * scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };

        var card = new CardView
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Scale = Vector2.One * scale,
        };
        card.Setup(def, 0, affordable: true);
        card.Refresh(affordable: true);
        holder.AddChild(card);

        return holder;
    }
}

/// <summary>卡牌关键字的显示名。</summary>
public static class UiText
{
    public static string KeywordsOf(CardDef def)
    {
        if (def == null)
            return "";

        var parts = new List<string>();
        if (def.Retain)
            parts.Add("保留");
        if (def.Exhaust)
            parts.Add("消耗");
        return string.Join(" · ", parts);
    }
}
