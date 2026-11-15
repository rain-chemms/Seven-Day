using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>
/// 卡牌放大检视层：右键任意卡牌时弹出，压暗背景、把卡面放大到屏幕中央，
/// 点任意处 / 右键 / Esc 关闭。全游戏共用一个实例（静态入口）。
/// </summary>
public partial class CardInspectOverlay : CanvasLayer
{
    private static CardInspectOverlay _instance;

    public static bool IsOpen => _instance != null && IsInstanceValid(_instance);

    /// <summary>打开查看器；再次调用会替换当前展示的卡牌。</summary>
    public static void Show(CardDef def, bool ethereal = false)
    {
        if (def == null)
            return;

        ulong t0 = Time.GetTicksMsec();



        if (!IsOpen)
        {
            _instance = new CardInspectOverlay { Name = "CardInspect", Layer = 200 };
            SceneTree tree = (SceneTree)Engine.GetMainLoop();
            tree.Root.AddChild(_instance);
            _instance.Build();
        }
        else
        {
            _instance.GetParent()?.MoveChild(_instance, -1);
        }

        _instance.ShowCard(def, ethereal);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--anim-log") >= 0)
            GD.Print($"[Inspect] 打开耗时 {Time.GetTicksMsec() - t0}ms");
    }

    public static void Close()
    {
        if (!IsOpen)
            return;

        _instance.CloseAnimated();
    }

    /// <summary>打开动画：暗底淡入 + 卡面缩放淡入（延后一帧开始，避免首帧跳变）。</summary>
    private void PlayOpenAnimation()
    {
        if (_closing || !IsInstanceValid(_dim) || !IsInstanceValid(_holder))
            return;

        const float DimAlpha = 0.75f;
        Tween tween = CreateTween().SetParallel(true);
        tween.TweenProperty(_dim, "color:a", DimAlpha, 0.16f);
        tween.TweenProperty(_holder, "modulate:a", 1f, 0.16f);
        tween.TweenProperty(_holder, "scale", Vector2.One, 0.22f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    /// <summary>
    /// 预热：提前建一次检视层（不可见），把 MSDF 字体、卡面贴图、布局都跑一遍，
    /// 这样玩家第一次右键时不会卡一下。
    /// </summary>
    public static void Prewarm()
    {
        if (IsOpen || System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-warm") >= 0)
            return;

        CardDef first = null;
        foreach (CardDef def in GameDatabase.Cards.Values)
        {
            if (def.KindEnum == CardKind.Unit)
            {
                first = def;
                break;
            }
        }

        if (first == null)
            return;

        var overlay = new CardInspectOverlay { Name = "CardInspectWarm", Layer = 200 };
        Node root = ((SceneTree)Engine.GetMainLoop()).Root;
        if (root.IsNodeReady() == false)
        {
            overlay.QueueFree();
            return;
        }

        root.AddChild(overlay);
        overlay.Build(false);   // 预热不走动画，保持完全透明
        overlay.ShowCard(first, false);
        overlay.SetDimAlpha(0f);   // 预热期间完全透明

        SceneTreeTimer timer = overlay.GetTree().CreateTimer(0.15);
        timer.Timeout += () =>
        {
            if (IsInstanceValid(overlay))
                overlay.QueueFree();
        };
    }
    /// <summary>预热/调试用：直接设置暗底透明度。</summary>
    public void SetDimAlpha(float alpha)
    {
        if (_dim != null && IsInstanceValid(_dim))
            _dim.Color = new Color(_dim.Color, alpha);
    }

    private bool _closing;

    /// <summary>关闭：暗底与卡面一起淡出，动画结束后再销毁。</summary>
    private void CloseAnimated()
    {
        if (_closing)
            return;

        _closing = true;
        _instance = null;      // 立刻标记为已关闭，避免重复触发

        Tween tween = CreateTween().SetParallel(true);
        tween.TweenProperty(_dim, "color:a", 0f, 0.16f);
        tween.TweenProperty(_holder, "modulate:a", 0f, 0.14f);
        tween.TweenProperty(_holder, "scale", Vector2.One * 0.94f, 0.16f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);

        tween.SetParallel(false);
        tween.TweenCallback(Callable.From(() =>
        {
            if (IsInstanceValid(this))
                QueueFree();
        }));
    }

    private ColorRect _dim;
    private Control _holder;
    private CardView _card;
    private Label _title;
    private Label _body;
    private Label _hint;

    private const float CardScale = 2.6f;

    private void Build(bool animate = true)
    {
        _dim = new ColorRect
        {
            Name = "Dim",
            Color = new Color(0.04f, 0.03f, 0.02f, 0f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var dim = _dim;
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        dim.GuiInput += OnBackgroundInput;
        AddChild(dim);

        // 卡面本体：缩放到 2.6 倍居中
        _holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _holder.SetAnchorsPreset(Control.LayoutPreset.Center);
        AddChild(_holder);

        var cardSize = new Vector2(CardView.CardWidth, CardView.CardHeight) * CardScale;
        _holder.OffsetLeft = -cardSize.X * 0.5f;
        _holder.OffsetRight = cardSize.X * 0.5f;
        _holder.OffsetTop = -cardSize.Y * 0.5f;
        _holder.OffsetBottom = cardSize.Y * 0.5f;

        _card = new CardView
        {
            Name = "BigCard",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            PivotOffset = Vector2.Zero,
            Scale = Vector2.One * CardScale,
        };
        _card.Theme = UiTheme.MsdfTheme();   // 放大 2.6 倍用 MSDF，文字保持锐利
        _holder.AddChild(_card);

        // 卡名（放在卡面上方）
        _title = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _title.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _title.OffsetLeft = -420;
        _title.OffsetRight = 420;
        _title.OffsetTop = 18;
        _title.OffsetBottom = 62;
        _title.AddThemeFontSizeOverride("font_size", 26);
        _title.AddThemeColorOverride("font_color", new Color("FFF3D6"));
        _title.AddThemeConstantOverride("outline_size", 8);
        _title.AddThemeColorOverride("font_outline_color", new Color("241C0E"));
        AddChild(_title);

        // 底部说明：关键字 / 关闭提示
        _body = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _body.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _body.OffsetLeft = -460;
        _body.OffsetRight = 460;
        _body.OffsetTop = -96;
        _body.OffsetBottom = -54;
        _body.AddThemeFontSizeOverride("font_size", 19);
        _body.AddThemeColorOverride("font_color", new Color("E8DCC0"));
        _body.AddThemeConstantOverride("outline_size", 7);
        _body.AddThemeColorOverride("font_outline_color", new Color("241C0E"));
        AddChild(_body);

        _hint = new Label
        {
            Text = Loc.T("inspect.hint"),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _hint.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _hint.OffsetLeft = -460;
        _hint.OffsetRight = 460;
        _hint.OffsetTop = -50;
        _hint.OffsetBottom = -14;
        _hint.AddThemeFontSizeOverride("font_size", 16);
        _hint.AddThemeColorOverride("font_color", new Color("B9A981"));
        AddChild(_hint);

        // 弹出动画：暗底淡入 + 卡面缩放淡入
        _holder.Modulate = new Color(1f, 1f, 1f, 0f);
        _holder.Scale = Vector2.One * 0.92f;
        _holder.PivotOffset = cardSize * 0.5f;

        // 等一帧布局 + 字形/贴图准备完成再起动画，否则第一次打开会先闪一下再补间
        if (animate)
            Callable.From(PlayOpenAnimation).CallDeferred();
    }

    private void ShowCard(CardDef def, bool ethereal)
    {
        _card.Setup(def, -1, true);
        _card.SetEthereal(ethereal);
        _title.Text = Loc.Name(def.NameZh, def.NameEn);

        string kind = def.KindEnum switch
        {
            CardKind.Unit => "单位",
            CardKind.Strike => "打击",
            _ => "全局",
        };

        string keywords = "";
        if (ethereal)
            keywords = Loc.T("keyword.ethereal");
        if (def.Retain)
            keywords = string.IsNullOrEmpty(keywords) ? Loc.T("keyword.retain") : keywords + " · " + Loc.T("keyword.retain");
        if (def.Exhaust)
            keywords = string.IsNullOrEmpty(keywords) ? Loc.T("keyword.exhaust") : keywords + " · " + Loc.T("keyword.exhaust");

        _body.Text = string.IsNullOrEmpty(keywords)
            ? $"{kind} · {def.Cost}$"
            : $"{kind} · {def.Cost}$ · {keywords}";
    }

    private void OnBackgroundInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true })
            Close();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
