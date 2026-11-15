using System;
using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>
/// 一张手牌。版式参考《杀戮尖塔》：左上角费用圆点、标题条、卡图、描述区。
/// 自己负责画自己，位置/旋转/缩放由 <see cref="CardHandView"/> 的扇形排布驱动。
/// </summary>
public partial class CardView : Control
{
    private HBoxContainer _stats;
    private Label _statsHpValue;
    private Label _statsAtkValue;
    private Label _statsRangeValue;
    private Label _statsMoveValue;
    private Label _targets;

    /// <summary>一行数值：「标签 + 红色数字」。</summary>
    private static Label AddStatRow(VBoxContainer box, string label, Color valueColor, bool alignEnd = false)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 4);

        if (alignEnd)
            row.Alignment = BoxContainer.AlignmentMode.End;
        box.AddChild(row);

        var name = new Label
        {
            Text = label,
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.AddThemeFontSizeOverride("font_size", ScaledFont(8.5f));
        name.AddThemeColorOverride("font_color", new Color("4A3A20"));
        row.AddChild(name);

        var value = new Label
        {
            Text = "",
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
        };
        value.AddThemeFontSizeOverride("font_size", ScaledFont(9.5f));
        value.AddThemeColorOverride("font_color", valueColor);
        row.AddChild(value);

        return value;
    }

    /// <summary>
    /// 卡牌字号缩放（设置里「卡牌字体大小」调整）。首次访问时从 settings 读取，
    /// 之后由设置界面即时写入。
    /// </summary>
    public static float FontScale
    {
        get
        {
            if (_fontScale < 0f)
            {
                _fontScale = Mathf.Clamp(
                    Settings.GetFloat("video", "card_font_scale", 1f), 0.8f, 1.35f);
            }

            return _fontScale;
        }
        set
        {
            _fontScale = Mathf.Clamp(value, 0.8f, 1.35f);
            Settings.SetFloat("video", "card_font_scale", _fontScale);
            Settings.Save();
        }
    }

    private static float _fontScale = -1f;

    private static int ScaledFont(float baseSize) =>
        Mathf.Max(6, Mathf.RoundToInt(baseSize * FontScale));

    public const float CardWidth = 128f;
    public const float CardHeight = 196f;

    public int Index { get; private set; }
    public CardDef Def { get; private set; }

    /// <summary>点击（按下并抬起且几乎没移动）。</summary>
    public Action<int> Clicked;

    /// <summary>右键：请求放大检视这张卡。</summary>
    public Action<int> InspectRequested;
    /// <summary>双击。</summary>
    public Action<int> DoubleClicked;
    /// <summary>按住在卡上开始拖动。</summary>
    public Action<int, Vector2> DragStarted;
    /// <summary>拖动中。</summary>
    public Action<int, Vector2> Dragging;
    /// <summary>松手（屏幕坐标）。</summary>
    public Action<int, Vector2, Vector2> DragEnded; // index, releasePos, startPos

    private Panel _body;
    private Label _cost;
    private Label _title;
    private Label _desc;
    private Label _keyword;
    private PanelContainer _keywordBadge;
    private TextureRect _art;
    private Panel _artPlaceholder;
    private StyleBoxFlat _frame;
    private StyleBoxFlat _frameHighlight;

    private bool _pressed;
    private Vector2 _pressPos;
    private bool _dragging;
    private ulong _lastClickMs;

    public CardView()
    {
        CustomMinimumSize = new Vector2(CardWidth, CardHeight);
        Size = new Vector2(CardWidth, CardHeight);
        PivotOffset = new Vector2(CardWidth * 0.5f, CardHeight);
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = false;
        SetProcessInput(true);
    }

    public void Setup(CardDef def, int index, bool affordable)
    {
        Def = def;
        Index = index;
        Build();
        Refresh(affordable);
    }

    private void Build()
    {
        if (_body != null)
            return;

        _body = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        _body.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_body);

        var content = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        content.OffsetLeft = 8;
        content.OffsetRight = -8;
        content.OffsetTop = 8;
        content.OffsetBottom = -8;
        content.AddThemeConstantOverride("separation", 4);
        _body.AddChild(content);

        // 顶部：费用 + 名称
        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        top.AddThemeConstantOverride("separation", 4);
        content.AddChild(top);

        var orb = new Panel { CustomMinimumSize = new Vector2(36, 22), MouseFilter = MouseFilterEnum.Ignore };
        var orbStyle = new StyleBoxFlat
        {
            BgColor = new Color("3B2E16"),
            CornerRadiusTopLeft = 11,
            CornerRadiusTopRight = 11,
            CornerRadiusBottomLeft = 11,
            CornerRadiusBottomRight = 11,
        };
        orb.AddThemeStyleboxOverride("panel", orbStyle);
        top.AddChild(orb);

        _cost = new Label
        {
            Text = "0",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _cost.AddThemeFontSizeOverride("font_size", ScaledFont(12f));
        _cost.AddThemeColorOverride("font_color", new Color("F5EFE2"));
        _cost.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        orb.AddChild(_cost);

        _title = new Label
        {
            Text = "",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
        };
        _title.AddThemeFontSizeOverride("font_size", ScaledFont(14f));
        _title.AddThemeColorOverride("font_color", UiTheme.Ink);
        top.AddChild(_title);

        // 关键字角标（保留 / 消耗 / 虚无）：浮在卡图右上角，长卡名也不会把它挤掉
        _keywordBadge = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var badgeStyle = new StyleBoxFlat
        {
            BgColor = new Color("2B2317"),
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            ContentMarginLeft = 5,
            ContentMarginRight = 5,
            ContentMarginTop = 1,
            ContentMarginBottom = 1,
        };
        _keywordBadge.AddThemeStyleboxOverride("panel", badgeStyle);

        _keyword = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _keyword.AddThemeFontSizeOverride("font_size", ScaledFont(10f));
        _keyword.AddThemeColorOverride("font_color", new Color("F0D98A"));
        _keywordBadge.AddChild(_keyword);

        // 浮在卡图右上角
        _keywordBadge.SetAnchorsPreset(LayoutPreset.TopRight);
        _keywordBadge.OffsetLeft = -78;
        _keywordBadge.OffsetRight = -8;
        _keywordBadge.OffsetTop = 30;
        _keywordBadge.OffsetBottom = 48;
        _body.AddChild(_keywordBadge);

        // 卡图
        var artHolder = new Panel { CustomMinimumSize = new Vector2(0, 66), MouseFilter = MouseFilterEnum.Ignore };
        var artStyle = new StyleBoxFlat
        {
            BgColor = new Color("2B2317"),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        artHolder.AddThemeStyleboxOverride("panel", artStyle);
        content.AddChild(artHolder);

        _art = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _art.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _art.OffsetLeft = 2;
        _art.OffsetRight = -2;
        _art.OffsetTop = 2;
        _art.OffsetBottom = -2;
        artHolder.AddChild(_art);

        _artPlaceholder = new Panel { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _artPlaceholder.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        artHolder.AddChild(_artPlaceholder);

        // 单位数值：生命 / 攻击，各占一行，数字用红字
        // 描述
        _desc = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Center,   // 水平居中
            MaxLinesVisible = 3,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        _desc.AddThemeFontSizeOverride("font_size", ScaledFont(10f));
        _desc.AddThemeColorOverride("font_color", UiTheme.Ink);
        content.AddChild(_desc);

        // 可打击模态（可对陆 / 可对空）：黄字，居中，排在数值上方
        _targets = new Label
        {
            Text = "",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false,
        };
        _targets.AddThemeFontSizeOverride("font_size", ScaledFont(9f));
        _targets.AddThemeColorOverride("font_color", new Color("E8B62A"));
        _targets.AddThemeConstantOverride("outline_size", 2);
        _targets.AddThemeColorOverride("font_outline_color", new Color("3B2E16"));
        content.AddChild(_targets);

        // 数值行跟在描述后面（不压卡图）：生命 / 攻击各一行，数字红字
        _stats = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _stats.AddThemeConstantOverride("separation", 6);
        content.AddChild(_stats);

        // 左列：生命 / 攻击；右列：射程 / 移动，行与左侧一一对应
        var statsLeft = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        statsLeft.AddThemeConstantOverride("separation", -1);
        _stats.AddChild(statsLeft);

        _statsHpValue = AddStatRow(statsLeft, "生命", new Color("C0392B"));   // 血量红字
        _statsAtkValue = AddStatRow(statsLeft, "攻击", new Color("2F6FB5"));  // 攻击蓝字

        var statsRight = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        statsRight.AddThemeConstantOverride("separation", -1);
        _stats.AddChild(statsRight);

        _statsRangeValue = AddStatRow(statsRight, "射程", new Color("7B4FB5"), alignEnd: true);   // 射程紫字
        _statsMoveValue = AddStatRow(statsRight, "移动", new Color("3E8E4A"), alignEnd: true);    // 移动绿字
    }

    /// <summary>这张牌是「发射井产出、打出即消耗」的实例（卡牌定义本身没有消耗关键字）。</summary>
    public void SetProducedExhaust(bool on)
    {
        if (_producedExhaust == on)
            return;

        _producedExhaust = on;
        ApplyKeyword();
    }

    public void SetEthereal(bool on)
    {
        if (_ethereal == on)
            return;

        _ethereal = on;
        if (_keyword != null)
            ApplyKeyword();
    }

    private bool _ethereal;
    private bool _producedExhaust;

    /// <summary>
    /// 卡名放不下时自动缩小字号（例如「武装直升机」这种 5 字长名），
    /// 布局完成后再量宽度。
    /// </summary>
    private void FitTitle()
    {
        if (!IsInstanceValid(_title))
            return;

        Font font = _title.GetThemeFont("font") ?? ThemeDB.FallbackFont;
        if (font == null)
            return;

        // 卡宽 - 左右内边距(16) - 费用球(40) - 间距
        float budget = CardWidth - 16f - 40f - 4f;

        string text = _title.Text;
        int size = ScaledFont(14f);
        int min = Mathf.Max(8, ScaledFont(9f));

        while (size > min &&
               font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X > budget)
        {
            size--;
        }

        _title.AddThemeFontSizeOverride("font_size", size);


    }
    private void ApplyKeyword()
    {
        if (_keyword == null)
            return;

        string text = KeywordText();
        _keyword.Text = text;
        if (_keywordBadge != null)
            _keywordBadge.Visible = !string.IsNullOrEmpty(text);
    }

    private string KeywordText()
    {
        string baseText = UiText.KeywordsOf(Def);
        if (_producedExhaust)
            baseText = string.IsNullOrEmpty(baseText) ? "消耗" : baseText + " · 消耗";

        if (!_ethereal)
            return baseText;

        return string.IsNullOrEmpty(baseText) ? "虚无" : baseText + " · 虚无";
    }

    public void Refresh(bool affordable)
    {
        if (Def == null)
            return;

        bool unit = Def.KindEnum == CardKind.Unit;
        bool strike = Def.KindEnum == CardKind.Strike;

        Color bg = unit ? new Color("E3D7B4") : strike ? new Color("E9C4B8") : new Color("C7DCEA");
        _frame ??= new StyleBoxFlat
        {
            BorderWidthLeft = 3,
            BorderWidthRight = 3,
            BorderWidthTop = 3,
            BorderWidthBottom = 3,
        };
        _frame.BgColor = bg;
        _frame.BorderColor = UiTheme.Ink;
        _frame.SetCornerRadiusAll(8);
        _body.AddThemeStyleboxOverride("panel", _frame);

        _cost.Text = Def.Cost.ToString();
        _title.Text = Loc.Name(Def.NameZh, Def.NameEn);
        CallDeferred(nameof(FitTitle));

        // 单位卡：显示生命 / 攻击（数字红字）
        UnitDef unitDef = string.IsNullOrEmpty(Def.Unit) ? null : GameDatabase.Unit(Def.Unit);
        bool showStats = unit && unitDef != null;
        if (_stats != null)
        {
            _stats.Visible = showStats;
            if (showStats)
            {
                _statsHpValue.Text = unitDef.Hp.ToString();
                _statsAtkValue.Text = unitDef.Damage.ToString();
                _statsRangeValue.Text = unitDef.Range.ToString();
                _statsMoveValue.Text = unitDef.Move.ToString();

                var modes = new System.Collections.Generic.List<string>();
                if (unitDef.CanHitGround)
                    modes.Add("可对陆");
                if (unitDef.CanHitAir)
                    modes.Add("可对空");

                if (_targets != null)
                {
                    _targets.Text = string.Join(" · ", modes);
                    _targets.Visible = modes.Count > 0;
                }
            }
        }

        // 有数值行时：描述限 2 行，并给足 2 行的高度
        // （高度不够时 Label 会把两行压在一起，看起来字都糊成一团）
        if (_desc != null)
        {
            _desc.MaxLinesVisible = 3;
            _desc.CustomMinimumSize = Vector2.Zero;
        }
        _desc.Text = Loc.IsChinese ? Def.DescriptionZh : Def.DescriptionEn;
        ApplyKeyword();

        string unitId = Def.Unit;
        if (!string.IsNullOrEmpty(unitId) &&
            ResourceLoader.Exists("res://gameAsset/textures/cardface/cardface_" + unitId + ".png"))
        {
            _art.Texture = UiTheme.CardFace(unitId);
            _art.Visible = true;
            _artPlaceholder.Visible = false;
        }
        else
        {
            _art.Visible = false;
            _artPlaceholder.Visible = true;
            var ps = new StyleBoxFlat
            {
                BgColor = strike ? new Color("C2543E") : new Color("6C7C4A"),
            };
            _artPlaceholder.AddThemeStyleboxOverride("panel", ps);
        }

        Modulate = affordable ? Colors.White : new Color(1, 1, 1, 0.55f);
    }

    /// <summary>选中态：加一圈金色描边。</summary>
    public void SetChosen(bool chosen)
    {
        if (_frame == null)
            return;
        _frame.BorderColor = chosen ? UiTheme.Gold : UiTheme.Ink;
        _frame.BorderWidthLeft = chosen ? 5 : 3;
        _frame.BorderWidthRight = chosen ? 5 : 3;
        _frame.BorderWidthTop = chosen ? 5 : 3;
        _frame.BorderWidthBottom = chosen ? 5 : 3;
    }

    public override void _GuiInput(InputEvent @event)
    {
        // 按住后拖动：卡片会拿到鼠标焦点，motion 也会送到这里
        if (@event is InputEventMouseMotion mm && _pressed)
        {
            UpdateDrag(mm.GlobalPosition);
            AcceptEvent();
            return;
        }

        // 右键：放大检视
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
        {
            InspectRequested?.Invoke(Index);
            AcceptEvent();
            return;
        }

        if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left)
            return;

        if (mb.Pressed)
        {
            _pressed = true;
            _dragging = false;
            _pressPos = mb.GlobalPosition;

            ulong now = Time.GetTicksMsec();
            if (now - _lastClickMs < 320)
            {
                _lastClickMs = 0;
                DoubleClicked?.Invoke(Index);
                AcceptEvent();
                return;
            }

            _lastClickMs = now;
            AcceptEvent();
            return;
        }

        // 抬起
        if (!_pressed)
            return;
        _pressed = false;

        Vector2 release = mb.GlobalPosition;
        float moved = (release - _pressPos).Length();

        if (_dragging)
        {
            DragEnded?.Invoke(Index, release, _pressPos);
            _dragging = false;
        }
        else if (moved < 10f)
        {
            Clicked?.Invoke(Index);
        }

        AcceptEvent();
    }

    public override void _Notification(int what)
    {
        // 拖动时卡片跟随鼠标
    }

    public void UpdateDrag(Vector2 globalMouse)
    {
        if (!_pressed)
            return;

        Vector2 delta = globalMouse - _pressPos;
        if (!_dragging)
        {
            if (delta.Length() < 12f)
                return;
            _dragging = true;
            DragStarted?.Invoke(Index, _pressPos);
        }

        Dragging?.Invoke(Index, delta);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mm && _pressed)
            UpdateDrag(mm.GlobalPosition);
    }
}
