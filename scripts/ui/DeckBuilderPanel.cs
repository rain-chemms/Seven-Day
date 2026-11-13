using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>
/// 出战卡组编辑器：
///   左边是牌库（可按类别筛选、拖到右边加入），中间一条分割线，
///   右边是出战卡牌（可切换「合并显示 / 逐张显示」），底部带 5 组预设的保存 / 载入。
/// </summary>
public partial class DeckBuilderPanel : PanelContainer
{
    public event Action<List<string>> Saved;
    public event Action Closed;

    private enum DeckViewMode
    {
        Stacked,   // 同种只显示一张 + 右下角数量
        Individual, // 每张单独显示，按加入顺序
    }

    private const int PoolColumns = 4;
    private const int DeckColumns = 4;
    private const float CardGap = 8f;
    private const float MiniScale = 0.72f;
    private const int PresetCount = 5;

    private const string PresetSection = "deck";

    private DeckViewMode _mode = DeckViewMode.Stacked;
    private readonly List<string> _deck = new();
    private string _filter = "all";

    private GridContainer _poolGrid;
    private readonly Dictionary<string, Control> _poolCells = new();
    private readonly Dictionary<string, Control> _deckCells = new();

    private Control _deckCanvas;
    private Label _deckEmptyHint;
    private MarginContainer _deckMargin;
    private readonly Dictionary<string, Vector2> _deckOldPos = new();
    private int _deckCanvasColumnCache = 4;
    private ScrollContainer _deckScroll;
    private Control _deckArea;
    private Label _countLabel;
    private Button _modeButton;
    private OptionButton _filterButton;
    private OptionButton _presetButton;
    private Button _saveButton;
    private Label _toast;

    // 拖拽
    private const float DragThreshold = 8f;

    private string _dragCardId;
    private DragSource _dragSource;
    private int _dragDeckIndex = -1;
    private bool _dragMoved;
    private bool _dragDeckLifted;
    private int _dragGapIndex = -1;
    private Control _dragGhost;
    private CardView _dragGhostCard;
    private Label _dragHint;
    private Vector2 _dragStart;

    public DeckBuilderPanel()
    {
        Name = "DeckBuilder";
        Visible = false;
        SetAnchorsPreset(LayoutPreset.Center);
        OffsetLeft = -600;
        OffsetRight = 600;
        OffsetTop = -334;
        OffsetBottom = 334;
        AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.PanelDir + "panel.png", 18, 18, 18, 18, 18));
        SetProcessInput(true);
    }

    public void Open()
    {
        List<string> seed = Session.PlayerDeck.Count >= Session.DeckMin
            ? Session.PlayerDeck
            : Session.DefaultDeck();

        _deck.Clear();
        _deck.AddRange(seed);

        EnsureBuilt();

        // 先显示再重建：否则第一次打开时容器还没布局，宽度是 0，列数会算错
        Visible = true;

        RebuildPool();
        RebuildDeck();
        UpdateCount();

        // 布局落定后再算一次（Container 的排版是延迟的）
        CallDeferred(nameof(DeferredRebuildDeck));
        PlayOpenAnimation();
        var settle = GetTree().CreateTimer(0.05);
        settle.Timeout += DeferredRebuildDeck;
    }

    /// <summary>宽度变化时重算列数（只在真的需要时才重建，避免反复触发）。</summary>
    private void OnDeckAreaResized()
    {
        if (_deckCanvas == null || _deckScroll == null || _deckScroll.Size.X < CardView.CardWidth * MiniScale)
            return;

        int columns = ColumnsForWidth(_deckScroll.Size.X - 12f);
        if (columns != _deckCanvasColumnCache)
            RebuildDeck();
    }

    private static int ColumnsForWidth(float available)
    {
        float cardW = CardView.CardWidth * MiniScale;
        int columns = Mathf.FloorToInt((available + CardGap) / (cardW + CardGap));
        return Mathf.Clamp(columns, 1, 12);
    }

    // ─────────────────────────── 构建 ───────────────────────────

    private void EnsureBuilt()
    {
        if (_poolGrid != null)
            return;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        AddChild(root);

        // 标题行
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        root.AddChild(header);

        var title = new Label { Text = Loc.T("deck.title") };
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_color", UiTheme.Ink);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);

        _countLabel = new Label();
        _countLabel.AddThemeFontSizeOverride("font_size", 17);
        _countLabel.AddThemeColorOverride("font_color", UiTheme.SandDark);
        header.AddChild(_countLabel);

        // 主体：牌库 | 分割线 | 出战卡牌
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 14);
        root.AddChild(body);

        body.AddChild(BuildPoolColumn());
        body.AddChild(BuildDivider());
        body.AddChild(BuildDeckColumn());

        root.AddChild(BuildFooter());
        BuildToast();

        _dragGhost = new Control { Visible = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 400 };
        // 必须挂在非 Container 的父节点下：PanelContainer 会强制摆放子节点，跟手卡会跑偏
        (GetParent() as Control ?? this).AddChild(_dragGhost);

        WireButtons(this);
    }

    /// <summary>弹窗类控件：缩一下 + 淡出，结束后才隐藏。</summary>
    private void PopOut(Control control, float duration = 0.13f)
    {
        if (control == null || !IsInstanceValid(control))
            return;

        control.PivotOffset = control.Size * 0.5f;

        Tween tween = CreateTween().SetParallel(true);
        tween.TweenProperty(control, "scale", Vector2.One * 0.92f, duration)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        tween.TweenProperty(control, "modulate:a", 0f, duration);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            if (!IsInstanceValid(control))
                return;

            control.Visible = false;
            control.Scale = Vector2.One;
            control.Modulate = Colors.White;
        }));
    }
    /// <summary>弹窗类控件：从略小、透明 → 正常大小，淡入。</summary>
    private void PopIn(Control control, float from, float duration)
    {
        if (control == null || !IsInstanceValid(control))
            return;

        control.PivotOffset = control.Size * 0.5f;
        control.Scale = Vector2.One * from;
        control.Modulate = new Color(1f, 1f, 1f, 0f);

        Tween tween = CreateTween().SetParallel(true);
        tween.TweenProperty(control, "scale", Vector2.One, duration)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(control, "modulate:a", 1f, duration * 0.8f);
    }
    // ─────────────────────────── 动画 ───────────────────────────

    private Tween _openTween;
    private int _lastCount = -1;

    /// <summary>给树里所有按钮挂上「悬停微放大 / 按下回弹」。</summary>
    private void WireButtons(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is BaseButton button)
            {
                button.MouseEntered += () => TweenScale(button, 1.045f, 0.12f);
                button.MouseExited += () => TweenScale(button, 1f, 0.12f);
                button.ButtonDown += () => TweenScale(button, 0.955f, 0.06f);
                button.ButtonUp += () => TweenScale(button, 1.02f, 0.1f);
            }

            WireButtons(child);
        }
    }

    /// <summary>把控件缩放到 k（以中心为轴心），用来做悬停/按下反馈。</summary>
    private void TweenScale(Control control, float k, float duration)
    {
        if (control == null || !IsInstanceValid(control))
            return;

        control.PivotOffset = control.Size * 0.5f;

        Tween tween = CreateTween();
        tween.TweenProperty(control, "scale", Vector2.One * k, duration)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    /// <summary>面板打开：整体淡入 + 轻微放大，卡牌依次浮现。</summary>
    private void PlayOpenAnimation()
    {
        if (_openTween != null && _openTween.IsValid())
            _openTween.Kill();

        Modulate = new Color(1f, 1f, 1f, 0f);
        PivotOffset = Size * 0.5f;
        Scale = Vector2.One * 0.955f;

        _openTween = CreateTween().SetParallel(true);
        _openTween.TweenProperty(this, "modulate:a", 1f, 0.16f);
        _openTween.TweenProperty(this, "scale", Vector2.One, 0.22f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);

        StaggerChildren(_poolGrid, 0.012f, slide: false);
        StaggerChildren(_deckCanvas, 0.012f, slide: true);
    }

    /// <summary>子节点逐个淡入（出战区还能从下往上滑一点）。</summary>
    private void StaggerChildren(Node parent, float step, bool slide)
    {
        if (parent == null)
            return;

        int i = 0;
        foreach (Node child in parent.GetChildren())
        {
            if (child is not Control control || !IsInstanceValid(control))
                continue;

            float delay = Mathf.Min(i * step, 0.22f);
            i++;

            control.Modulate = new Color(1f, 1f, 1f, 0f);

            if (slide)
            {
                Vector2 target = control.Position;
                control.Position = target + new Vector2(0f, 12f);

                Tween move = CreateTween();
                move.TweenProperty(control, "position", target, 0.24f)
                    .SetDelay(delay)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            }

            Tween fade = CreateTween();
            fade.TweenProperty(control, "modulate:a", 1f, 0.2f).SetDelay(delay);
        }
    }

    /// <summary>中间的分割线。</summary>
    private Control BuildDivider()
    {
        var holder = new Control
        {
            CustomMinimumSize = new Vector2(2, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };

        var line = new ColorRect
        {
            Color = new Color(UiTheme.Ink, 0.35f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        line.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        line.OffsetLeft = 0.5f;
        line.OffsetRight = 1.5f;
        holder.AddChild(line);

        return holder;
    }

    private Control BuildPoolColumn()
    {
        var col = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(430, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        col.AddThemeConstantOverride("separation", 6);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        col.AddChild(head);

        var label = new Label { Text = Loc.T("deck.pool"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("font_size", 17);
        label.AddThemeColorOverride("font_color", UiTheme.SandDark);
        head.AddChild(label);

        _filterButton = new OptionButton { CustomMinimumSize = new Vector2(140, 32) };
        _filterButton.AddItem(Loc.T("deck.filter_all"), 0);
        _filterButton.AddItem(Loc.T("deck.filter_unit"), 1);
        _filterButton.AddItem(Loc.T("deck.filter_strike"), 2);
        _filterButton.AddItem(Loc.T("deck.filter_global"), 3);
        _filterButton.Selected = 0;
        _filterButton.ItemSelected += i =>
        {
            _filter = i switch { 1 => "unit", 2 => "strike", 3 => "global", _ => "all" };
            RebuildPool();
        };
        head.AddChild(_filterButton);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
        };
        col.AddChild(scroll);
        HideScrollbar(scroll);

        _poolGrid = new GridContainer { Columns = PoolColumns, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _poolGrid.AddThemeConstantOverride("h_separation", 8);
        _poolGrid.AddThemeConstantOverride("v_separation", 8);
        scroll.AddChild(_poolGrid);

        return col;
    }

    private Control BuildDeckColumn()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 6);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        col.AddChild(head);

        var label = new Label { Text = Loc.T("deck.current"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("font_size", 17);
        label.AddThemeColorOverride("font_color", UiTheme.SandDark);
        head.AddChild(label);

        _modeButton = new Button { CustomMinimumSize = new Vector2(150, 32) };
        _modeButton.Text = Loc.T("deck.mode_stacked");
        _modeButton.Pressed += ToggleMode;
        head.AddChild(_modeButton);

        // 预设
        var presetRow = new HBoxContainer();
        presetRow.AddThemeConstantOverride("separation", 8);
        col.AddChild(presetRow);

        _presetButton = new OptionButton { CustomMinimumSize = new Vector2(200, 32), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _presetButton.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left })
                return;

            // 双击预设 → 重命名（顺便把弹出的下拉收起来）
            _presetButton.GetPopup()?.Hide();
            OpenRenamePreset(_presetButton.Selected);
            AcceptEvent();
        };
        presetRow.AddChild(_presetButton);
        RefreshPresetItems();

        var savePreset = new Button { Text = Loc.T("deck.preset_save"), CustomMinimumSize = new Vector2(120, 32) };
        savePreset.Pressed += OnSavePreset;
        presetRow.AddChild(savePreset);

        var loadPreset = new Button { Text = Loc.T("deck.preset_load"), CustomMinimumSize = new Vector2(120, 32) };
        loadPreset.Pressed += OnLoadPreset;
        presetRow.AddChild(loadPreset);

        var renamePreset = new Button { Text = Loc.T("deck.preset_rename"), CustomMinimumSize = new Vector2(96, 32) };
        renamePreset.Pressed += () => OpenRenamePreset(_presetButton.Selected);
        presetRow.AddChild(renamePreset);

        _deckArea = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _deckArea.AddThemeStyleboxOverride("panel",
            UiTheme.Box(UiTheme.PanelDir + "panel_light.png", 18, 8, 8, 8, 8));
        col.AddChild(_deckArea);

        _deckScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
        };
        _deckArea.AddChild(_deckScroll);
        HideScrollbar(_deckScroll);
        _deckScroll.Resized += OnDeckAreaResized;

        // 用 margin 把整片卡牌纵向居中：卡少时上下留白均分，卡多时正常滚动
        _deckMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _deckScroll.AddChild(_deckMargin);

        // 自己排版（不用 GridContainer）：容器每帧强制摆放子节点，位置没法做补间
        _deckCanvas = new Control
        {
            Name = "DeckCanvas",
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _deckMargin.AddChild(_deckCanvas);

        // 空卡组提示：直接贴在出战区中央，蓝字
        _deckEmptyHint = new Label
        {
            Name = "EmptyHint",
            Text = Loc.T("deck.empty_hint"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        _deckEmptyHint.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _deckEmptyHint.AddThemeFontSizeOverride("font_size", 24);
        _deckEmptyHint.AddThemeColorOverride("font_color", new Color("3E7BD1"));
        _deckArea.AddChild(_deckEmptyHint);

        return col;
    }

    /// <summary>当前排版参数（宽、列数、左边距、卡尺寸）。</summary>
    private (float Available, int Columns, float X0, float CardW, float CardH) DeckGeometry()
    {
        float available = Mathf.Max(0f, _deckScroll != null ? _deckScroll.Size.X - 12f : 0f);
        float cardW = CardView.CardWidth * MiniScale;
        float cardH = CardView.CardHeight * MiniScale;
        int columns = Mathf.Max(1, ColumnsForWidth(available));
        float totalW = columns * cardW + (columns - 1) * CardGap;
        float x0 = Mathf.Max(0f, (available - totalW) * 0.5f);
        return (available, columns, x0, cardW, cardH);
    }

    /// <summary>
    /// 自己排卡（替掉 GridContainer）：列数按宽度、整体横向居中、纵向按 margin 居中，
    /// 位置变了就补间过去，而不是瞬移。
    /// gapIndex ≥ 0 时，从这一位开始整体后移一格（拖拽插入前的空位预览）。
    /// </summary>
    private void LayoutDeck(bool animate)
    {
        if (_deckCanvas == null || _deckScroll == null)
            return;

        int count = _deckCanvas.GetChildCount();
        (float available, int columns, float x0, float cardW, float cardH) = DeckGeometry();
        _deckCanvasColumnCache = columns;

        int gap = _dragGapIndex >= 0 && _dragSource == DragSource.Pool ? _dragGapIndex : -1;
        int slots = count + (gap >= 0 ? 1 : 0);

        int rows = Mathf.Max(1, Mathf.CeilToInt(slots / (float)columns));
        float contentH = rows * cardH + Mathf.Max(0, rows - 1) * CardGap;

        _deckCanvas.CustomMinimumSize = new Vector2(available, contentH);
        CenterDeckVertically(rows);

        for (int i = 0; i < count; i++)
        {
            if (_deckCanvas.GetChild(i) is not Control cell)
                continue;

            cell.Size = new Vector2(cardW, cardH);

            int slot = gap >= 0 && i >= gap ? i + 1 : i;
            int col = slot % columns;
            int row = slot / columns;
            Vector2 target = new(x0 + col * (cardW + CardGap), row * (cardH + CardGap));

            string key = cell.GetMeta("key", "").AsString();
            Vector2 old = default;
            bool hasOld = animate && !string.IsNullOrEmpty(key) &&
                          _deckOldPos.TryGetValue(key, out old);

            if (hasOld)
            {
                cell.Position = old;
                Tween tween = CreateTween();
                tween.TweenProperty(cell, "position", target, 0.22f)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            }
            else
            {
                cell.Position = target;
            }
        }
    }

    /// <summary>
    /// 逐张模式下：按光标位置算这张卡应该插到第几位（离哪个格子最近就插它前面，
    /// 落在格子右半边则插它后面）。只在出战区内有效，返回 -1 表示不算。
    /// </summary>
    private int ComputeInsertIndex(Vector2 globalPos)
    {
        if (_deckCanvas == null || _deckArea == null)
            return -1;

        if (!_deckArea.GetGlobalRect().HasPoint(globalPos))
            return -1;

        (_, int columns, float x0, float cardW, float cardH) = DeckGeometry();
        Vector2 local = globalPos - _deckCanvas.GlobalPosition;

        int count = _deck.Count;
        int best = count;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            Rect2 cellRect = new(
                new Vector2(x0 + col * (cardW + CardGap), row * (cardH + CardGap)),
                new Vector2(cardW, cardH));

            Vector2 center = cellRect.Position + cellRect.Size * 0.5f;
            float d = local.DistanceTo(center);
            if (d >= bestDistance)
                continue;

            bestDistance = d;
            // 光标在这张卡的右半边 → 插到它后面
            best = local.X > center.X ? i + 1 : i;
        }

        return Mathf.Clamp(best, 0, count);
    }

    /// <summary>卡牌总数不足以撑满时，算一下要往下推多少才能纵向居中。</summary>
    private void CenterDeckVertically(int rows)
    {
        if (_deckMargin == null || _deckScroll == null)
            return;

        float cardH = CardView.CardHeight * MiniScale;
        float contentH = rows * cardH + Mathf.Max(0, rows - 1) * 8f;
        float extra = Mathf.Max(0f, _deckScroll.Size.Y - contentH);

        _deckMargin.AddThemeConstantOverride("margin_top", Mathf.RoundToInt(extra * 0.5f));
        _deckMargin.AddThemeConstantOverride("margin_bottom", 0);
    }

    private Control BuildFooter()
    {
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 10);

        var def = new Button { Text = Loc.T("deck.default"), CustomMinimumSize = new Vector2(150, 44) };
        def.Pressed += OnResetDefault;
        footer.AddChild(def);

        var clear = new Button { Text = Loc.T("deck.clear"), CustomMinimumSize = new Vector2(110, 44) };
        clear.Pressed += OnClear;
        footer.AddChild(clear);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        footer.AddChild(spacer);

        var close = new Button { Text = Loc.T("deck.close"), CustomMinimumSize = new Vector2(120, 44) };
        close.Pressed += Close;
        footer.AddChild(close);

        _saveButton = new Button { Text = Loc.T("deck.save"), CustomMinimumSize = new Vector2(190, 44) };
        _saveButton.Pressed += OnSave;
        footer.AddChild(_saveButton);

        return footer;
    }

    private void BuildToast()
    {
        // 变暗的遮罩：铺满整个游戏画面，**连面板一起压暗**，只有提示文字浮在上面
        Control root = GetParent() as Control;
        _toastDim = new ColorRect
        {
            Name = "ToastDim",
            Visible = false,
            Color = new Color(0f, 0f, 0f, 0f),
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 20,
        };
        _toastDim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _toast = new Label
        {
            Name = "Toast",
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 21,          // 唯一的“亮”层，浮在遮罩之上
        };
        _toast.SetAnchorsPreset(LayoutPreset.Center);
        _toast.OffsetLeft = -320;
        _toast.OffsetRight = 320;
        _toast.OffsetTop = -44;
        _toast.OffsetBottom = 44;
        _toast.AddThemeFontSizeOverride("font_size", 30);
        _toast.AddThemeColorOverride("font_color", new Color("FFF6E2"));
        _toast.AddThemeConstantOverride("outline_size", 8);
        _toast.AddThemeColorOverride("font_outline_color", new Color("2B2317"));

        if (root != null)
        {
            root.AddChild(_toastDim);
            root.AddChild(_toast);
        }
        else
        {
            AddChild(_toastDim);
            AddChild(_toast);
        }
    }

    public override void _ExitTree()
    {
        if (_toastDim != null && IsInstanceValid(_toastDim))
            _toastDim.QueueFree();
    }

    // ─────────────────────────── 牌库（左） ───────────────────────────

    private void RebuildPool()
    {
        // 立刻从树上摘掉：QueueFree 是延迟的，紧接着 GetChildCount 会把旧卡也算进去
        foreach (Node child in _poolGrid.GetChildren().ToList())
        {
            _poolGrid.RemoveChild(child);
            child.QueueFree();
        }

        _poolCells.Clear();

        foreach (CardDef def in AllCards())
        {
            if (_filter != "all" && KindKey(def) != _filter)
                continue;

            Control cell = BuildPoolCell(def);
            _poolGrid.AddChild(cell);
            _poolCells[def.Id] = cell;
        }
    }

    private static string KindKey(CardDef def) => def.KindEnum switch
    {
        CardKind.Unit => "unit",
        CardKind.Strike => "strike",
        _ => "global",
    };

    private static IEnumerable<CardDef> AllCards()
    {
        return GameDatabase.Cards.Values
            .OrderBy(d => d.KindEnum switch
            {
                CardKind.Unit => 0,
                CardKind.Strike => 1,
                _ => 2,
            })
            .ThenBy(d => d.Cost)
            .ThenBy(d => d.NameZh);
    }

    private Control BuildPoolCell(CardDef def)
    {
        var holder = new Control
        {
            CustomMinimumSize = new Vector2(CardView.CardWidth, CardView.CardHeight) * MiniScale,
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = def.NameZh,
        };

        var card = new CardView
        {
            MouseFilter = MouseFilterEnum.Ignore,
            PivotOffset = Vector2.Zero,
            Scale = Vector2.One * MiniScale,
        };
        card.Setup(def, -1, true);
        holder.AddChild(card);

        holder.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
            {
                StartDrag(def.Id, mb.GlobalPosition);
                AcceptEvent();
            }
        };

        holder.MouseEntered += () =>
        {
            TweenScale(holder, 1.05f, 0.12f);
            holder.Modulate = new Color(1.05f, 1.05f, 1.05f);
        };
        holder.MouseExited += () =>
        {
            TweenScale(holder, 1f, 0.12f);
            holder.Modulate = Colors.White;
        };

        return holder;
    }

    // ─────────────────────────── 出战卡牌（右） ───────────────────────────

    private void RebuildDeck()
    {
        // 先记下旧位置：按「卡牌 id + 第几次出现」做键，重建后从旧位置滑到新位置
        _deckOldPos.Clear();
        foreach (Node child in _deckCanvas.GetChildren().ToList())
        {
            if (child is Control oldCell)
            {
                string oldKey = oldCell.GetMeta("key", "").AsString();
                if (!string.IsNullOrEmpty(oldKey))
                    _deckOldPos[oldKey] = oldCell.Position;
            }

            _deckCanvas.RemoveChild(child);
            child.QueueFree();
        }

        // 每行按可用宽度铺满（布局还没算完时等一帧再来）
        float available = _deckScroll != null ? _deckScroll.Size.X - 12f : 0f;
        if (available < CardView.CardWidth * MiniScale)
        {
            CallDeferred(nameof(DeferredRebuildDeck));
            return;
        }

        if (_deck.Count == 0)
        {
            // 空卡组：显示一句蓝色提示（贴在出战区中央），不留一条莫名其妙的小横线
            _deckCanvas.CustomMinimumSize = new Vector2(0f, 0f);
            _deckEmptyHint.Visible = true;
            return;
        }

        _deckEmptyHint.Visible = false;

        if (_mode == DeckViewMode.Individual)
        {
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < _deck.Count; i++)
            {
                string id = _deck[i];
                int occ = seen.TryGetValue(id, out int n) ? n : 0;
                seen[id] = occ + 1;

                Control cell = BuildDeckCard(id, 1, i);
                cell.SetMeta("key", $"{id}#{occ}");
                _deckCanvas.AddChild(cell);
            }

            LayoutDeck(true);
            return;
        }

        // 合并显示：同种一张卡 + 数量，按首次出现顺序
        var order = new List<string>();
        var counts = new Dictionary<string, int>();
        foreach (string id in _deck)
        {
            if (!counts.ContainsKey(id))
                order.Add(id);

            counts[id] = counts.TryGetValue(id, out int c) ? c + 1 : 1;
        }

        _deckCells.Clear();
        foreach (string id in order)
        {
            Control cell = BuildDeckCard(id, counts[id], -1);
            cell.SetMeta("key", id);
            _deckCanvas.AddChild(cell);
            _deckCells[id] = cell;
        }

        LayoutDeck(true);

        CenterDeckVertically(Mathf.CeilToInt(order.Count / (float)_deckCanvasColumnCache));
    }

    private Control BuildDeckCard(string cardId, int count, int index)
    {
        CardDef def = GameDatabase.Card(cardId);
        var holder = new Control
        {
            CustomMinimumSize = new Vector2(CardView.CardWidth, CardView.CardHeight) * MiniScale,
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = "",
        };

        if (def != null)
        {
            var card = new CardView
            {
                MouseFilter = MouseFilterEnum.Ignore,
                PivotOffset = Vector2.Zero,
                Scale = Vector2.One * MiniScale,
            };
            card.Setup(def, -1, true);
            holder.AddChild(card);
        }

        if (count > 1)
        {
            var badge = new Label
            {
                Name = "Count",
                Text = $"×{count}",
                MouseFilter = MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            badge.SetAnchorsPreset(LayoutPreset.BottomRight);
            badge.OffsetLeft = -52;
            badge.OffsetRight = -4;
            badge.OffsetTop = -26;
            badge.OffsetBottom = -4;
            badge.AddThemeFontSizeOverride("font_size", 16);
            badge.AddThemeColorOverride("font_color", new Color("FFD98A"));
            badge.AddThemeConstantOverride("outline_size", 5);
            badge.AddThemeColorOverride("font_outline_color", new Color("2B2317"));
            holder.AddChild(badge);
        }

        holder.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
                return;

            // 按下先待定：拖动到左侧（出牌区外）移除，原地点击也移除一张
            StartDrag(cardId, mb.GlobalPosition, DragSource.Deck, index);
            AcceptEvent();
        };

        holder.MouseEntered += () =>
        {
            TweenScale(holder, 1.05f, 0.12f);
            holder.Modulate = new Color(1.05f, 1.05f, 1.05f);
        };
        holder.MouseExited += () =>
        {
            TweenScale(holder, 1f, 0.12f);
            holder.Modulate = Colors.White;
        };

        return holder;
    }

    // ─────────────────────────── 拖拽 ───────────────────────────

    private enum DragSource
    {
        None,
        Pool,   // 牌库 → 拖到右侧加入
        Deck,   // 出战卡牌 → 拖到左侧移除
    }

    /// <summary>按下时只是「待定」：移动超过阈值才算拖动，否则抬手时算点击。</summary>
    private void StartDrag(string cardId, Vector2 pos, DragSource source = DragSource.Pool,
                           int deckIndex = -1)
    {
        _dragCardId = cardId;
        _dragSource = source;
        _dragDeckIndex = deckIndex;
        _dragStart = pos;
        _dragMoved = false;

        // 先不显示跟手卡，等真的动了再出现，避免误触时闪一下
        HideGhost();
    }

    private void ShowGhost(Vector2 pos)
    {
        if (_dragGhost == null)
            return;

        CardDef def = GameDatabase.Card(_dragCardId);
        if (def == null)
            return;

        if (_dragGhost.GetChildCount() == 0)
        {
            var card = new CardView
            {
                MouseFilter = MouseFilterEnum.Ignore,
                PivotOffset = Vector2.Zero,
                Scale = Vector2.One * MiniScale,
                Modulate = new Color(1, 1, 1, 0.85f),
            };
            card.Setup(def, -1, true);
            _dragGhost.AddChild(card);
            _dragGhostCard = card;

            _dragHint = new Label
            {
                Name = "Hint",
                Visible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            _dragHint.SetAnchorsPreset(LayoutPreset.BottomWide);
            _dragHint.OffsetTop = -24;
            _dragHint.OffsetBottom = -2;
            _dragHint.AddThemeFontSizeOverride("font_size", 15);
            _dragHint.AddThemeConstantOverride("outline_size", 6);
            _dragHint.AddThemeColorOverride("font_outline_color", new Color("2B2317"));
            _dragGhost.AddChild(_dragHint);

            _dragGhost.Size = new Vector2(CardView.CardWidth, CardView.CardHeight) * MiniScale;
            _dragGhost.PivotOffset = _dragGhost.Size * 0.5f;
        }

        _dragGhost.GlobalPosition = pos - _dragGhost.Size * 0.5f;
        _dragGhost.Modulate = Colors.White;
        _dragGhost.Visible = true;
        UpdateDragHint(pos);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--drag-log") >= 0)
        {
            GD.Print($"[Ghost] pos={pos} size={_dragGhost.Size} local={_dragGhost.Position} " +
                     $"global={_dragGhost.GlobalPosition} parent={_dragGhost.GetParent()?.Name}");
        }
    }

    /// <summary>拖拽提示：拖出出战区要删卡时整张卡泛红并显示「移除」，拖到出战区是「加入」。</summary>
    private void UpdateDragHint(Vector2 pos)
    {
        if (_dragGhostCard == null || _dragHint == null)
            return;

        bool overDeck = _deckArea != null && _deckArea.GetGlobalRect().HasPoint(pos);

        bool removing = _dragSource == DragSource.Deck && !overDeck;
        bool adding = _dragSource == DragSource.Pool && overDeck;

        // 会删除 → 整张泛红；会加入 → 整张泛绿；其它位置保持正常
        _dragGhostCard.Modulate = removing
            ? new Color(1f, 0.42f, 0.38f, 0.95f)
            : adding
                ? new Color(0.55f, 1f, 0.6f, 0.95f)
                : new Color(1f, 1f, 1f, 0.85f);

        _dragHint.Visible = removing || adding;
        if (!_dragHint.Visible)
            return;

        _dragHint.Text = removing ? Loc.T("deck.drop_remove") : Loc.T("deck.drop_add");
        _dragHint.AddThemeColorOverride("font_color",
            removing ? new Color("FF7A6A") : new Color("9CE07A"));
    }

    private void HideGhost()
    {
        if (_dragGhost == null)
            return;

        _dragGhost.Visible = false;
        _dragGhostCard = null;
        _dragHint = null;
        foreach (Node child in _dragGhost.GetChildren().ToList())
        {
            _dragGhost.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void EndDrag()
    {
        bool hadGap = _dragGapIndex >= 0;

        _dragCardId = null;
        _dragSource = DragSource.None;
        _dragDeckIndex = -1;
        _dragMoved = false;
        _dragDeckLifted = false;
        _dragGapIndex = -1;
        HideGhost();

        if (hadGap)
            LayoutDeck(true);
    }

    public override void _Input(InputEvent @event)
    {
        // 重命名小窗开着时，Esc 先关小窗（不要连整个面板一起关掉）
        if (_renamePanel is { Visible: true } && @event.IsActionPressed("ui_cancel"))
        {
            CloseRenamePanel();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_dragCardId == null)
            return;

        // 鼠标 / 触屏都在这里跟手
        if (@event is InputEventMouseMotion motion)
        {
            UpdateDrag(motion.GlobalPosition);
            return;
        }

        if (@event is InputEventScreenDrag drag)
        {
            UpdateDrag(drag.Position);
            return;
        }

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } mb)
        {
            FinishDrag(mb.GlobalPosition);
            return;
        }

        if (@event is InputEventScreenTouch { Pressed: false } touch)
            FinishDrag(touch.Position);
    }

    private void UpdateDrag(Vector2 pos)
    {
        // 优先判定拖动：移动超过阈值就进入拖动状态
        if (!_dragMoved && pos.DistanceTo(_dragStart) > DragThreshold)
        {
            _dragMoved = true;

            // 从卡组里拖出来的瞬间就把这张「拿」走：看上去就是拖着它、而牌组里已经少了一张
            if (_dragSource == DragSource.Deck && !_dragDeckLifted)
            {
                _dragDeckLifted = true;
                RemoveDeckCard(_dragCardId, _dragDeckIndex, fly: false);
            }
        }

        if (_dragMoved)
            ShowGhost(pos);

        // 逐张模式：把牌库的卡拖到出战区上时，提前把那一位让出来
        UpdateInsertGap(pos);
    }

    /// <summary>逐张模式下的插入空位预览：光标移到哪，哪就空出一格。</summary>
    private void UpdateInsertGap(Vector2 pos)
    {
        if (_dragSource != DragSource.Pool || _mode != DeckViewMode.Individual)
        {
            if (_dragGapIndex >= 0)
            {
                _dragGapIndex = -1;
                LayoutDeck(true);
            }

            return;
        }

        int index = ComputeInsertIndex(pos);
        if (index == _dragGapIndex)
            return;

        _dragGapIndex = index;
        LayoutDeck(true);
    }

    private void FinishDrag(Vector2 pos)
    {
        string id = _dragCardId;
        if (id == null)
            return;

        bool wasDragging = _dragMoved;
        DragSource source = _dragSource;
        int deckIndex = _dragDeckIndex;
        bool lifted = _dragDeckLifted;
        int insertIndex = _dragGapIndex;
        bool overDeck = _deckArea != null && _deckArea.GetGlobalRect().HasPoint(pos);

        if (source == DragSource.Deck)
        {
            if (!wasDragging)
            {
                // 原地点击：正常移除（带飞回动画）
                EndDrag();
                RemoveDeckCard(id, deckIndex, fly: true);
                return;
            }

            // 拖动过程中已经拿走了；拖回区域内 = 放回去
            if (overDeck && lifted)
            {
                EndDrag();
                InsertDeckCard(id, deckIndex);
                return;
            }

            // 拖到区域外：卡已经在手上，让它淡出消失（而不是瞬间不见）
            FadeGhostOut();
            return;
        }

        // 牌库：拖到右侧区域 = 加入（不播转移动画）；原地点击 = 加入（带动画）
        if (wasDragging)
        {
            bool adding = overDeck;
            EndDrag();

            if (adding)
                AddCardAt(id, insertIndex);

            return;
        }

        EndDrag();
        AddCard(id);
    }

    /// <summary>把正在拖的卡淡出（用于「拖出去删除」的收尾）。</summary>
    private void FadeGhostOut()
    {
        if (_dragGhost == null || !_dragGhost.Visible)
        {
            EndDrag();
            return;
        }

        // 立即停止接收拖拽输入，但保留卡片继续播淡出
        _dragCardId = null;
        _dragSource = DragSource.None;
        _dragDeckIndex = -1;
        _dragMoved = false;
        _dragDeckLifted = false;
        _dragGapIndex = -1;

        Control ghost = _dragGhost;
        Tween tween = CreateTween();
        tween.TweenProperty(ghost, "modulate:a", 0f, 0.22f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        tween.TweenCallback(Callable.From(HideGhost));
    }

    /// <summary>拖回卡组：放回原来的位置（越界就放到末尾）。</summary>
    private void InsertDeckCard(string id, int index)
    {
        int at = index >= 0 && index <= _deck.Count ? index : _deck.Count;
        _deck.Insert(at, id);

        RebuildDeck();
        UpdateCount();
    }

    private void RemoveDeckCard(string id, int index, bool fly = true)
    {
        // 先记下它现在的位置，重建之后就没了
        Vector2 from = DeckCellCenter(id, index);
        Vector2 to = PoolCellCenter(id);

        if (index >= 0 && index < _deck.Count && _deck[index] == id)
            _deck.RemoveAt(index);
        else
            _deck.Remove(id);

        RebuildDeck();
        UpdateCount();

        // 点击移除时才播「飞回牌库」；拖动移除时卡已经在手上，不用再飞
        if (fly)
            FlyCard(id, from, to, new Color(1f, 0.78f, 0.74f), null, fadeOut: true);
    }

    private Vector2 DeckCellCenter(string id, int index)
    {
        if (_mode == DeckViewMode.Stacked &&
            _deckCells.TryGetValue(id, out Control stacked) && IsInstanceValid(stacked))
        {
            return CellCenter(stacked);
        }

        if (index >= 0 && index < _deckCanvas.GetChildCount() &&
            _deckCanvas.GetChild(index) is Control cell)
        {
            return CellCenter(cell);
        }

        return _deckArea != null
            ? _deckArea.GlobalPosition + _deckArea.Size * 0.5f
            : new Vector2(800f, 400f);
    }

    // ─────────────────────────── 增删 ───────────────────────────

    private int Total => _deck.Count;

    private async void AddCard(string id, bool animate = true)
    {
        if (Total >= Session.DeckMax)
        {
            ShowToast(Loc.T("deck.tooMany"));
            return;
        }

        // 拖动加入：直接生效，不播转运动画（卡已经在手上）
        if (!animate)
        {
            _deck.Add(id);
            RebuildDeck();
            UpdateCount();

            Control target = FindDeckCell(id, _deck.Count - 1);
            if (target != null)
                CallDeferred(nameof(FlashCellDeferred), target);
            return;
        }

        Vector2 from = PoolCellCenter(id);
        bool stacked = _mode == DeckViewMode.Stacked;
        int oldCount = _deck.Count(c => c == id);

        _deck.Add(id);
        RebuildDeck();
        UpdateCount();

        // 刚重建完，Container 还没排版，格子位置是旧的；先藏好再去取目标点
        Control cell = FindDeckCell(id, _deck.Count - 1);
        Label badge = null;
        if (cell != null)
        {
            if (stacked)
            {
                badge = cell.GetNodeOrNull<Label>("Count");
                if (oldCount > 0 && badge != null)
                    badge.Text = $"×{oldCount}";     // 先退回旧数量
                else
                    HideCell(cell);                  // 新种类：整格先藏
            }
            else
            {
                HideCell(cell);                      // 逐张：新卡等落地再出现
            }
        }

        // 等排版（一般一帧就够，多等一帧保险）
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        if (!IsInstanceValid(this) || !Visible)
            return;

        cell ??= FindDeckCell(id, _deck.Count - 1);
        if (cell == null || !IsInstanceValid(cell))
            return;

        badge ??= cell.GetNodeOrNull<Label>("Count");
        Vector2 to = CellCenter(cell);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--fly-log") >= 0)
        {
            GD.Print($"[Fly] mode={_mode} id={id} from={from} to={to} cell={cell.GlobalPosition} " +
                     $"cellSize={cell.Size} canvas={_deckCanvas.GlobalPosition}");
        }

        if (stacked && oldCount > 0)
        {
            FlyCard(id, from, to, new Color(1, 1, 1, 0.95f), () =>
            {
                if (IsInstanceValid(cell))
                {
                    if (badge != null)
                        badge.Text = $"×{oldCount + 1}";
                    PlayAddFlash(cell);
                }
            });
            return;
        }

        FlyCard(id, from, to, new Color(1, 1, 1, 0.95f), () =>
        {
            if (!IsInstanceValid(cell))
                return;

            cell.Visible = true;
            cell.Modulate = Colors.White;
            if (badge != null)
                badge.Text = $"×{oldCount + 1}";
            PlayAddFlash(cell);
        });
    }

    /// <summary>
    /// 暂时藏起一格：**不能**用 Visible=false —— 容器会把隐藏的子节点移出排版，
    /// 那一格的位置就失效了（飞行目标会算错）。这里用全透明代替。
    /// </summary>
    private static void HideCell(Control cell)
    {
        if (cell == null || !IsInstanceValid(cell))
            return;

        cell.Modulate = new Color(1f, 1f, 1f, 0f);
    }

    /// <summary>把牌库的卡拖进卡组：直接插入到指定位置（逐张模式），不播转移动画。</summary>
    private void AddCardAt(string id, int index)
    {
        if (Total >= Session.DeckMax)
        {
            ShowToast(Loc.T("deck.tooMany"));
            return;
        }

        int at = index >= 0 && index <= _deck.Count ? index : _deck.Count;
        _deck.Insert(at, id);

        RebuildDeck();
        UpdateCount();

        Control target = FindDeckCell(id, at);
        if (target != null)
            CallDeferred(nameof(FlashCellDeferred), target);
    }

    /// <summary>延后一帧再播绿色闪光（布局算完才有正确尺寸做轴心）。</summary>
    private void FlashCellDeferred(Control cell)
    {
        if (cell == null || !IsInstanceValid(cell))
            return;

        PlayAddFlash(cell);
    }

    /// <summary>合并模式按 id 找格子，逐张模式按下标找。</summary>
    private Control FindDeckCell(string id, int index)
    {
        if (_mode == DeckViewMode.Stacked)
            return _deckCells.TryGetValue(id, out Control stacked) && IsInstanceValid(stacked) ? stacked : null;

        if (index < 0 || index >= _deckCanvas.GetChildCount())
            return null;

        return _deckCanvas.GetChild(index) as Control;
    }

    /// <summary>牌库里这张卡的位置（找不到就给左侧区域中心）。</summary>
    private Vector2 PoolCellCenter(string id)
    {
        if (_poolCells.TryGetValue(id, out Control cell) && IsInstanceValid(cell))
            return CellCenter(cell);

        return _poolGrid != null
            ? _poolGrid.GlobalPosition + new Vector2(_poolGrid.Size.X * 0.5f, 120f)
            : new Vector2(300f, 300f);
    }

    private static Vector2 CellCenter(Control cell) =>
        cell.GlobalPosition + cell.Size * 0.5f;

    /// <summary>一张缩略卡从 A 飞到 B，落地后回调（移除时结束时淡出）。</summary>
    private void FlyCard(string cardId, Vector2 from, Vector2 to, Color tint,
                         Action onArrive, bool fadeOut = false)
    {
        CardDef def = GameDatabase.Card(cardId);
        if (def == null)
        {
            onArrive?.Invoke();
            return;
        }

        var host = new Control
        {
            Name = "FlyingCard",
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 500,
        };
        (GetParent() as Control ?? this).AddChild(host);

        var card = new CardView
        {
            MouseFilter = MouseFilterEnum.Ignore,
            PivotOffset = Vector2.Zero,
            Scale = Vector2.One * MiniScale,
        };
        card.Setup(def, -1, true);
        host.AddChild(card);

        Vector2 size = new Vector2(CardView.CardWidth, CardView.CardHeight) * MiniScale;
        host.Size = size;
        host.PivotOffset = size * 0.5f;
        host.GlobalPosition = from - size * 0.5f;
        host.Modulate = tint;

        Tween tween = CreateTween().SetParallel(true);
        tween.TweenProperty(host, "global_position", to - size * 0.5f, 0.34f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        tween.TweenProperty(host, "scale", Vector2.One * 0.94f, 0.34f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

        if (fadeOut)
            tween.TweenProperty(host, "modulate:a", 0f, 0.22f).SetDelay(0.12f);

        tween.Chain().TweenCallback(Callable.From(() =>
        {
            onArrive?.Invoke();
            if (IsInstanceValid(host))
                host.QueueFree();
        }));
    }

    /// <summary>刚加入的那张卡：绿色描边扩散 + 绿光一闪 + 轻微弹一下。</summary>
    private void PlayAddFlash(Control holder)
    {
        holder.PivotOffset = holder.Size * 0.5f;
        holder.Modulate = new Color(0.55f, 1f, 0.58f);
        holder.Scale = Vector2.One * 1.08f;

        Tween tint = CreateTween().SetParallel(true);
        tint.TweenProperty(holder, "modulate", Colors.White, 0.5f)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        tint.TweenProperty(holder, "scale", Vector2.One, 0.32f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);

        // 绿色描边：从卡片轮廓扩散出去再消失
        var ring = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        ring.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var ringStyle = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = new Color("7CF08C"),
            BorderWidthLeft = 4,
            BorderWidthRight = 4,
            BorderWidthTop = 4,
            BorderWidthBottom = 4,
        };
        ringStyle.SetCornerRadiusAll(9);
        ring.AddThemeStyleboxOverride("panel", ringStyle);
        holder.AddChild(ring);

        ring.PivotOffset = holder.Size * 0.5f;
        Tween ringTween = CreateTween().SetParallel(true);
        ringTween.TweenProperty(ring, "scale", Vector2.One * 1.3f, 0.42f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        ringTween.TweenProperty(ring, "modulate:a", 0f, 0.4f);
        ringTween.Chain().TweenCallback(Callable.From(() => ring.QueueFree()));
    }

    private void UpdateCount()
    {
        int total = Total;
        _countLabel.Text = Loc.T("deck.count", total);
        _countLabel.AddThemeColorOverride("font_color",
            total < Session.DeckMin ? new Color("C4392F") : UiTheme.SandDark);

        if (_lastCount >= 0 && total != _lastCount)
            PulseCount(total > _lastCount);

        _lastCount = total;

        if (_saveButton != null)
            _saveButton.Disabled = total < Session.DeckMin || total > Session.DeckMax;
    }

    /// <summary>总数变化时弹一下：加卡闪绿、减卡闪红。</summary>
    private void PulseCount(bool added)
    {
        if (_countLabel == null || !IsInstanceValid(_countLabel))
            return;

        _countLabel.PivotOffset = _countLabel.Size * 0.5f;
        _countLabel.Scale = Vector2.One * 1.16f;
        _countLabel.Modulate = added ? new Color(0.6f, 1f, 0.66f) : new Color(1f, 0.7f, 0.66f);

        Tween tween = CreateTween().SetParallel(true);
        tween.TweenProperty(_countLabel, "scale", Vector2.One, 0.24f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(_countLabel, "modulate", Colors.White, 0.4f);
    }
    // ─────────────────────────── 预设 ───────────────────────────

    private void RefreshPresetItems()
    {
        int selected = _presetButton.Selected;

        _presetButton.Clear();
        for (int i = 0; i < PresetCount; i++)
        {
            List<string> preset = LoadPreset(i);
            string label = PresetLabel(i);

            string text = preset.Count > 0
                ? $"{label}（{preset.Count}）"
                : $"{label}（{Loc.T("deck.empty_word")}）";

            _presetButton.AddItem(text, i);
        }

        _presetButton.Selected = Mathf.Clamp(selected, 0, PresetCount - 1);
        _presetButton.TooltipText = Loc.T("deck.preset_rename_hint");
    }

    /// <summary>预设显示名：自定义过就用自定义名，否则「预设 N」。</summary>
    private static string PresetLabel(int index)
    {
        string custom = Settings.GetString(PresetSection, "preset" + (index + 1) + "_name", "");
        return string.IsNullOrWhiteSpace(custom) ? Loc.T("deck.preset_name_default", index + 1) : custom;
    }

    private static List<string> LoadPreset(int index)
    {
        string raw = Settings.GetString(PresetSection, "preset" + (index + 1), "");
        var list = new List<string>();

        if (string.IsNullOrWhiteSpace(raw))
            return list;

        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string id = part.Trim();
            if (GameDatabase.Card(id) != null)
                list.Add(id);
        }

        return list;
    }

    private void OnSavePreset()
    {
        if (Total < Session.DeckMin)
        {
            ShowToast(Loc.T("deck.tooFew"));
            return;
        }

        int slot = _presetButton.Selected;
        Settings.SetString(PresetSection, "preset" + (slot + 1), string.Join(",", _deck));
        Settings.Save();

        RefreshPresetItems();
        _presetButton.Selected = slot;
        ShowToast(Loc.T("deck.preset_saved", slot + 1));
    }

    private void OnLoadPreset()
    {
        int slot = _presetButton.Selected;
        List<string> preset = LoadPreset(slot);

        if (preset.Count == 0)
        {
            ShowToast(Loc.T("deck.preset_empty", slot + 1));
            return;
        }

        _deck.Clear();
        _deck.AddRange(preset);
        RebuildDeck();
        UpdateCount();
        ShowToast(Loc.T("deck.preset_loaded", slot + 1));
    }

    // ─────────────────────────── 预设重命名 ───────────────────────────

    private PanelContainer _renamePanel;
    private ColorRect _renameDim;
    private LineEdit _renameEdit;
    private Label _renameTitle;
    private int _renameSlot = -1;

    /// <summary>双击预设时弹出的小输入框。</summary>
    private void OpenRenamePreset(int slot)
    {
        if (_renamePanel == null)
            BuildRenamePanel();

        _renameSlot = Mathf.Clamp(slot, 0, PresetCount - 1);

        string custom = Settings.GetString(PresetSection, "preset" + (_renameSlot + 1) + "_name", "");
        _renameTitle.Text = Loc.T("deck.preset_rename_title", _renameSlot + 1);
        _renameEdit.Text = string.IsNullOrWhiteSpace(custom) ? PresetLabel(_renameSlot) : custom;

        _renamePanel.Visible = true;
        PopIn(_renamePanel, 0.9f, 0.16f);
        ShowRenameDim();
        _renameEdit.GrabFocus();
        _renameEdit.SelectAll();
    }

    private void BuildRenamePanel()
    {
        Control root = GetParent() as Control;

        // 全屏遮罩：和提示条一样压暗背后（点了就取消）
        _renameDim = new ColorRect
        {
            Name = "RenameDim",
            Visible = false,
            Color = new Color(0f, 0f, 0f, 0f),
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 25,
        };
        _renameDim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _renameDim.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                CloseRenamePanel();
        };

        _renamePanel = new PanelContainer { Name = "PresetRename", Visible = false, ZIndex = 30 };
        _renamePanel.SetAnchorsPreset(LayoutPreset.Center);
        _renamePanel.OffsetLeft = -220;
        _renamePanel.OffsetRight = 220;
        _renamePanel.OffsetTop = -90;
        _renamePanel.OffsetBottom = 90;
        _renamePanel.AddThemeStyleboxOverride("panel",
            UiTheme.Box(UiTheme.PanelDir + "panel.png", 18, 16, 16, 16, 16));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        _renamePanel.AddChild(box);

        _renameTitle = new Label { Text = Loc.T("deck.preset_rename_title", 1) };
        _renameTitle.AddThemeFontSizeOverride("font_size", 19);
        _renameTitle.AddThemeColorOverride("font_color", UiTheme.Ink);
        box.AddChild(_renameTitle);

        _renameEdit = new LineEdit { CustomMinimumSize = new Vector2(0, 40), MaxLength = 16 };
        _renameEdit.TextSubmitted += _ => ConfirmRename();
        box.AddChild(_renameEdit);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        box.AddChild(row);

        var cancel = new Button { Text = Loc.T("common.cancel"), CustomMinimumSize = new Vector2(0, 38), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cancel.Pressed += () => CloseRenamePanel();
        row.AddChild(cancel);

        var ok = new Button { Text = Loc.T("common.confirm"), CustomMinimumSize = new Vector2(0, 38), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ok.Pressed += ConfirmRename;
        row.AddChild(ok);

        if (root != null)
        {
            root.AddChild(_renameDim);
            root.AddChild(_renamePanel);
        }
        else
        {
            AddChild(_renameDim);
            AddChild(_renamePanel);
        }

        WireButtons(_renamePanel);
    }

    private void ConfirmRename()
    {
        if (_renamePanel == null)
            return;

        string name = _renameEdit.Text.Trim();
        Settings.SetString(PresetSection, "preset" + (_renameSlot + 1) + "_name", name);
        Settings.Save();

        int selected = _presetButton.Selected;
        RefreshPresetItems();
        _presetButton.Selected = selected;

        CloseRenamePanel();
    }

    /// <summary>关掉重命名小窗：缩一下 + 淡出，而不是瞬间消失。</summary>
    private void CloseRenamePanel()
    {
        if (_renamePanel == null || !_renamePanel.Visible)
            return;

        PopOut(_renamePanel);
        HideRenameDim();
    }

    /// <summary>重命名的全屏遮罩：和提示一样压暗背后的画面。</summary>
    private void ShowRenameDim()
    {
        if (_renameDim == null || !IsInstanceValid(_renameDim))
            return;

        _renameDim.Visible = true;
        Tween tween = CreateTween();
        tween.TweenProperty(_renameDim, "color:a", 0.42f, 0.16f);
    }

    private void HideRenameDim()
    {
        if (_renameDim == null || !IsInstanceValid(_renameDim) || !_renameDim.Visible)
            return;

        Tween tween = CreateTween();
        tween.TweenProperty(_renameDim, "color:a", 0f, 0.13f);
        tween.TweenCallback(Callable.From(() =>
        {
            if (IsInstanceValid(_renameDim))
                _renameDim.Visible = false;
        }));
    }

    // ─────────────────────────── 按钮 ───────────────────────────

    private void OnResetDefault()
    {
        _deck.Clear();
        _deck.AddRange(Session.DefaultDeck());
        RebuildDeck();
        UpdateCount();
    }

    private void OnClear()
    {
        _deck.Clear();
        RebuildDeck();
        UpdateCount();
    }

    private void OnSave()
    {
        int total = Total;
        if (total < Session.DeckMin)
        {
            ShowToast(Loc.T("deck.tooFew"));
            return;
        }

        if (total > Session.DeckMax)
        {
            ShowToast(Loc.T("deck.tooMany"));
            return;
        }

        Session.SaveDeck(_deck);
        Saved?.Invoke(new List<string>(_deck));
        ShowToast(Loc.T("deck.saved"));
        Close();
    }

    public void Close()
    {
        EndDrag();

        // 关面板时把提示和全屏遮罩一起收掉，别留一层灰
        if (_toast != null)
            _toast.Visible = false;

        if (_toastDim != null)
        {
            _toastDim.Visible = false;
            _toastDim.Color = new Color(0f, 0f, 0f, 0f);
        }

        _toastAge = 0f;

        if (_renameDim != null && IsInstanceValid(_renameDim))
        {
            _renameDim.Visible = false;
            _renameDim.Color = new Color(0f, 0f, 0f, 0f);
        }

        // 淡出 + 微缩后再真正隐藏
        if (_openTween != null && _openTween.IsValid())
            _openTween.Kill();

        _openTween = CreateTween().SetParallel(true);
        _openTween.TweenProperty(this, "modulate:a", 0f, 0.13f);
        _openTween.TweenProperty(this, "scale", Vector2.One * 0.965f, 0.13f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        _openTween.Chain().TweenCallback(Callable.From(FinishClose));
    }

    private void FinishClose()
    {
        Visible = false;
        Scale = Vector2.One;
        Modulate = Colors.White;
        Closed?.Invoke();
    }

    // ─────────────────────────── 工具 ───────────────────────────

    private static void HideScrollbar(ScrollContainer scroll)
    {
        VScrollBar bar = scroll.GetVScrollBar();
        if (bar == null)
            return;

        bar.AddThemeStyleboxOverride("scroll", new StyleBoxEmpty());
        bar.AddThemeStyleboxOverride("grabber", new StyleBoxEmpty());
        bar.AddThemeStyleboxOverride("grabber_highlight", new StyleBoxEmpty());
        bar.AddThemeStyleboxOverride("grabber_pressed", new StyleBoxEmpty());
    }

    private ColorRect _toastDim;
    private float _toastAge;

    private void ShowToast(string text)
    {
        if (_toast == null)
            return;

        _toast.Text = text;
        _toast.Visible = true;
        _toast.Modulate = new Color(1f, 1f, 1f, 0f);
        _toast.Scale = Vector2.One * 0.92f;

        if (_toastDim != null)
        {
            _toastDim.Visible = true;
            _toastDim.Color = new Color(0f, 0f, 0f, 0f);
        }

        _toastAge = 0f;

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--toast-log") >= 0 && _toastDim != null)
        {
            GD.Print($"[Toast] dim size={_toastDim.Size} vis={_toastDim.Visible} z={_toastDim.ZIndex} " +
                     $"parent={_toastDim.GetParent()?.Name} panelZ={ZIndex}");
        }
    }

    // 提示节奏：淡入 → 停留 → 随背景一起淡出
    private const float ToastIn = 0.14f;
    private const float ToastHold = 1.15f;
    private const float ToastOut = 0.38f;

    public override void _Process(double delta)
    {
        if (_toast == null || !_toast.Visible)
            return;

        _toastAge += (float)delta;

        float total = ToastIn + ToastHold + ToastOut;
        if (_toastAge >= total)
        {
            _toast.Visible = false;
            if (_toastDim != null)
                _toastDim.Visible = false;

            return;
        }

        float inK = Mathf.Clamp(_toastAge / ToastIn, 0f, 1f);
        float outK = Mathf.Clamp((_toastAge - ToastIn - ToastHold) / ToastOut, 0f, 1f);
        float alpha = inK * (1f - outK);

        _toast.Modulate = new Color(1f, 1f, 1f, alpha);
        _toast.PivotOffset = _toast.Size * 0.5f;
        _toast.Scale = Vector2.One * Mathf.Lerp(0.92f, 1f, inK);

        if (_toastDim != null)
            _toastDim.Color = new Color(0f, 0f, 0f, 0.45f * alpha);
    }

    // ─────────────────────────── 调试 ───────────────────────────

    public int DebugTotal() => Total;

    public int DebugPoolCount() => _poolGrid?.GetChildCount() ?? 0;

    public string DebugDeckIds() => string.Join(",", _deck);

    public Control DebugPoolCell(int index) =>
        _poolGrid != null && index >= 0 && index < _poolGrid.GetChildCount()
            ? _poolGrid.GetChild(index) as Control
            : null;

    public void DebugAdd(string id) => AddCard(id);

    public void DebugRemoveAt(int index)
    {
        if (index < 0 || index >= _deck.Count)
            return;

        RemoveDeckCard(_deck[index], index);
    }

    public void DebugSetFilter(string filter)
    {
        _filter = filter;
        RebuildPool();
    }

    public void OnDebugClear() => OnClear();

    public void DebugSavePreset() => OnSavePreset();

    public void DebugLoadPreset() => OnLoadPreset();

    public Rect2 DebugDeckRect() => _deckArea?.GetGlobalRect() ?? new Rect2();

    public bool DebugGhostVisible() => _dragGhost != null && _dragGhost.Visible;

    public Vector2 DebugGhostPos() => _dragGhost?.GlobalPosition ?? Vector2.Zero;

    public bool DebugDragging() => _dragMoved;

    public void DebugToggleMode() => ToggleMode();

    public int DebugDeckColumns() => _deckCanvasColumnCache;

    public float DebugScrollWidth() => _deckScroll?.Size.X ?? 0f;

    public float DebugRowOffset() => 0f;

    public int DebugDeckCount() => _deckCanvas?.GetChildCount() ?? 0;

    public int DebugGapIndex() => _dragGapIndex;

    public string DebugPresetLabel(int slot) => PresetLabel(slot);

    /// <summary>按文字找按钮中心（截图/测试用）。</summary>
    public Vector2 DebugButtonCenter(string text)
    {
        foreach (Node node in FindChildren("*", nameof(Button), true, false))
        {
            if (node is Button b && b.Text == text && b.IsVisibleInTree())
                return b.GlobalPosition + b.Size * 0.5f;
        }

        return Vector2.Zero;
    }

    public void DebugOpenRename(int slot) => OpenRenamePreset(slot);

    public void DebugCloseRename() => CloseRenamePanel();

    /// <summary>调试：直接给某个预设改名（不经过输入框）。</summary>
    public void DebugRenamePreset(int slot, string name)
    {
        Settings.SetString(PresetSection, "preset" + (slot + 1) + "_name", name);
        Settings.Save();

        int selected = _presetButton.Selected;
        RefreshPresetItems();
        _presetButton.Selected = selected;
    }

    /// <summary>调试：直接把空位预览设到某一位（截图用，不经过鼠标）。</summary>
    public void DebugPreviewGap(int index)
    {
        _dragSource = DragSource.Pool;
        _dragGapIndex = index;
        LayoutDeck(true);
    }

    /// <summary>当前有几个「正在飞的卡」（验证拖拽操作不播转移动画）。</summary>
    public int DebugFlyingCount()
    {
        Node parent = GetParent();
        if (parent == null)
            return 0;

        int n = 0;
        foreach (Node child in parent.GetChildren())
        {
            if (child.Name.ToString().StartsWith("FlyingCard"))
                n++;
        }

        return n;
    }

    public Vector2 DebugDeckCellPos(int index) =>
        _deckCanvas != null && index >= 0 && index < _deckCanvas.GetChildCount()
            ? (_deckCanvas.GetChild(index) as Control)?.Position ?? Vector2.Zero
            : Vector2.Zero;

    public Control DebugDeckCell(int index) =>
        _deckCanvas != null && index >= 0 && index < _deckCanvas.GetChildCount()
            ? _deckCanvas.GetChild(index) as Control
            : null;

    private void DeferredRebuildDeck() => RebuildDeck();

    private void ToggleMode()
    {
        _mode = _mode == DeckViewMode.Stacked ? DeckViewMode.Individual : DeckViewMode.Stacked;
        if (_modeButton != null)
            _modeButton.Text = _mode == DeckViewMode.Stacked
                ? Loc.T("deck.mode_stacked")
                : Loc.T("deck.mode_individual");

        RebuildDeck();
    }
}
