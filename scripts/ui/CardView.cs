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
    public const float CardWidth = 128f;
    public const float CardHeight = 196f;

    public int Index { get; private set; }
    public CardDef Def { get; private set; }

    /// <summary>点击（按下并抬起且几乎没移动）。</summary>
    public Action<int> Clicked;
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
        _cost.AddThemeFontSizeOverride("font_size", 12);
        _cost.AddThemeColorOverride("font_color", new Color("F5EFE2"));
        _cost.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        orb.AddChild(_cost);

        _title = new Label
        {
            Text = "",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _title.AddThemeFontSizeOverride("font_size", 14);
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
        _keyword.AddThemeFontSizeOverride("font_size", 10);
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

        // 描述
        _desc = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MaxLinesVisible = 3,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        _desc.AddThemeFontSizeOverride("font_size", 9);
        _desc.AddThemeColorOverride("font_color", UiTheme.Ink);
        content.AddChild(_desc);
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
