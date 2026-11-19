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
    private const int MaxColumns = 4;
    private const int Gap = 14;
    private const float ScrollScale = 1.0f;   // 竖向滚动模式下的固定缩放

    // 面板内边距 + 顶栏占用（用来算卡牌缩放）
    private const float PanelWidth = 940f;
    private const float PanelHeight = 600f;
    private const int ContentMargin = 16;
    private const int HeaderHeight = 46;
    private const int HeaderGap = 12;

    private PanelContainer _panel;
    private GridContainer _grid;
    private ScrollContainer _scroll;
    private CenterContainer _center;
    private Label _title;
    private Button _close;
    private Button _confirm;
    private Label _confirmLabel;
    private bool _picking;
    private int _maxPicks;
    private int _requiredPicks;
    private System.Action<List<int>> _multiConfirm;
    private readonly List<int> _selected = new();
    private readonly List<CardView> _pickCards = new();
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
            // 选择模式下不允许点背景关闭，避免没选就退出
            if (!_picking && e is InputEventMouseButton mb && mb.Pressed)
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

        _confirm = new Button
        {
            Text = "",
            Visible = false,
            CustomMinimumSize = new Vector2(120, 40),
        };
        _confirm.Pressed += () =>
        {
            if (_selected.Count > 0)
                _multiConfirm?.Invoke(new List<int>(_selected));
        };

        _confirmLabel = new Label
        {
            Text = "确定（0）",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _confirmLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _confirmLabel.AddThemeFontSizeOverride("font_size", 17);
        _confirmLabel.AddThemeColorOverride("font_color", new Color("241D12"));
        _confirm.AddChild(_confirmLabel);
        header.AddChild(_confirm);

        _close = new Button { Text = "关闭" };
        UiSound.Attach(_close);
        _close.CustomMinimumSize = new Vector2(96, 40);
        _close.Pressed += Close;
        header.AddChild(_close);

        // 卡牌网格：竖向滚动（滚动条沿用主题里的 VScrollBar 样式）
        _scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowAlways,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = false,
        };
        root.AddChild(_scroll);

        _center = new CenterContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _scroll.AddChild(_center);

        _grid = new GridContainer
        {
            Columns = MaxColumns,
        };
        _grid.AddThemeConstantOverride("h_separation", Gap);
        _grid.AddThemeConstantOverride("v_separation", Gap);
        _center.AddChild(_grid);
    }

    /// <summary>让内容在面板里纵向居中：容器高度取 max(视口, 内容高)，CenterContainer 负责居中。</summary>
    private void UpdateCenterHeight(int rows, float cardHeight, int count = -1)
    {
        if (_center == null || _scroll == null)
            return;

        int actualRows = rows > 0 ? rows : 1;
        float contentH = actualRows * cardHeight + (actualRows - 1) * Gap;
        _center.CustomMinimumSize = new Vector2(0, Mathf.Max(0, contentH));
    }

    /// <summary>布局变化后按当前网格尺寸重算居中容器高度。</summary>
    private void UpdateCenterHeight()
    {
        if (_center == null || _scroll == null)
            return;

        float contentH = _center.GetCombinedMinimumSize().Y;
        if (_grid != null)
            contentH = Mathf.Max(contentH, _grid.GetCombinedMinimumSize().Y);

        _center.CustomMinimumSize = new Vector2(0, Mathf.Max(_scroll.Size.Y, contentH));
    }
    /// <summary>可用的内容区域（用来算卡牌缩放）。</summary>
    private Vector2 ContentArea =>
        new(PanelWidth - ContentMargin * 2f, PanelHeight - ContentMargin * 2f - HeaderHeight - HeaderGap);

    public void Open(string title, IEnumerable<string> cardIds, IReadOnlyList<int> etherealIndices = null,
        IReadOnlyList<int> producedExhaustIndices = null, System.Action<int> onPick = null,
        System.Action<List<int>> onMultiPick = null, int requiredPicks = 0, int maxPicks = 0)
    {
        List<string> list = cardIds.ToList();
        _picking = onPick != null || onMultiPick != null;
        _multiConfirm = onMultiPick;
        _requiredPicks = requiredPicks;
        _maxPicks = maxPicks;
        _selected.Clear();
        _pickCards.Clear();

        _title.Text = onMultiPick != null
            ? (requiredPicks > 0
                ? $"{title}（{list.Count}）· 选择 {requiredPicks} 张"
                : $"{title}（{list.Count}）· 可选择多张")
            : onPick != null ? $"{title}（{list.Count}）· 点击选择一张" : $"{title}（{list.Count}）";

        if (_close != null)
            _close.Visible = !_picking;
        if (_confirm != null)
        {
            _confirm.Visible = onMultiPick != null;
            _confirm.Disabled = true;
            _confirmLabel.Text = requiredPicks > 0
                ? $"确定（0/{requiredPicks}）"
                : "确定（0）";
        }

        var ethereal = new HashSet<int>(etherealIndices ?? (IReadOnlyList<int>)System.Array.Empty<int>());
        var pexhaust = new HashSet<int>(producedExhaustIndices ?? (IReadOnlyList<int>)System.Array.Empty<int>());

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

        // 竖向滚动：列数取 min(卡片数, 上限)，再按面板宽度把卡片放大到填满
        int columns = Mathf.Clamp(Mathf.Min(list.Count, MaxColumns), 1, MaxColumns);
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
            card.Theme = UiTheme.MsdfTheme();   // 放大后文字不糊
            card.Setup(def, 0, affordable: true);
            card.SetEthereal(ethereal.Contains(i));
            card.SetProducedExhaust(pexhaust.Contains(i));
            need = need.Max(card.GetCombinedMinimumSize());
            pending.Add((def, card, ethereal.Contains(i)));
        }

        // 卡片尽量大：先用宽度算缩放；行数少（<=2 行）时再按高度收一点，保证整块能居中显示
        float usableW = ContentArea.X - (columns - 1) * Gap;
        float widthFit = usableW / (columns * Mathf.Max(1f, need.X));
        int previewRows = Mathf.Max(1, Mathf.CeilToInt(pending.Count / (float)columns));
        float usableH = ContentArea.Y - (previewRows - 1) * Gap;
        float heightFit = usableH / (previewRows * Mathf.Max(1f, need.Y));

        float scale = previewRows <= 2
            ? Mathf.Clamp(Mathf.Min(widthFit, heightFit), 0.85f, 1.85f)
            : Mathf.Clamp(widthFit, 0.9f, 1.85f);

        int rows = Mathf.Max(1, Mathf.CeilToInt(pending.Count / (float)columns));
        UpdateCenterHeight(rows, need.Y * scale);

        int order = 0;
        foreach ((CardDef def, CardView card, bool isEthereal) in pending)
        {
            var cell = new Control
            {
                CustomMinimumSize = need * scale,
                MouseFilter = MouseFilterEnum.Stop,
                ClipContents = true,
            };

            int pickedIndex = order++;

            // 右键放大检视；选择模式下左键直接选中
            cell.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { Pressed: true } mb)
                    return;

                if (mb.ButtonIndex == MouseButton.Right)
                {
                    CardInspectOverlay.Show(def, isEthereal);
                    cell.AcceptEvent();
                    return;
                }

                if (mb.ButtonIndex == MouseButton.Left && onMultiPick != null)
                {
                    if (_selected.Contains(pickedIndex))
                    {
                        _selected.Remove(pickedIndex);
                        card.SetChosen(false);
                    }
                    else
                    {
                        if (_maxPicks > 0 && _selected.Count >= _maxPicks)
                        {
                            cell.AcceptEvent();
                            return;
                        }
                        _selected.Add(pickedIndex);
                        card.SetChosen(true);
                    }

                    _confirm.Disabled = _requiredPicks > 0
                        ? _selected.Count != _requiredPicks
                        : _selected.Count == 0;
                    _confirmLabel.Text = _requiredPicks > 0
                        ? $"确定（{_selected.Count}/{_requiredPicks}）"
                        : $"确定（{_selected.Count}）";
                    cell.AcceptEvent();
                    return;
                }

                if (mb.ButtonIndex == MouseButton.Left && onPick != null)
                {
                    onPick(pickedIndex);
                    cell.AcceptEvent();
                }
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
            parts.Add(Core.Loc.T("keyword.retain"));
        if (def.Exhaust)
            parts.Add(Core.Loc.T("keyword.exhaust"));
        return string.Join(" · ", parts);
    }
}
