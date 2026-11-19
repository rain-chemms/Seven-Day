using System;
using System.Collections.Generic;
using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>
/// 选牌界面（参考杀戮尖塔的选牌表现）：
/// 上下有边界、左右延伸到屏幕边缘的暗色横向面板 + 放大的卡面。
/// 支持单选/多选（多选时显示「确定」按钮）、选中提示、眼睛按钮临时隐藏。
/// </summary>
public partial class CardChoiceOverlay : Control
{
    private Action<List<int>> _onPick;
    private int _pickCount = 1;
    private bool _closed;
    private bool _hidden;

    private ColorRect _blocker;
    private PanelContainer _band;
    private HBoxContainer _row;
    private Button _confirm;
    private TextureButton _eye;
    private Label _hiddenHint;

    private readonly List<CardView> _cards = new();
    private readonly List<Control> _slots = new();
    private readonly List<int> _selected = new();

    private static readonly Color BandColor = new("241D12");
    private static readonly Color Gold = new("C9A45A");

    public CardChoiceOverlay()
    {
        Name = "CardChoiceOverlay";
        ZIndex = 1000;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public void Open(string title, string subtitle, IReadOnlyList<string> cardIds,
                     Action<List<int>> onPick, int pickCount = 1, bool zeroCostDisplay = false)
    {
        _onPick = onPick;
        _pickCount = Mathf.Max(1, pickCount);

        _blocker = new ColorRect
        {
            Color = new Color(0.06f, 0.05f, 0.03f, 0.78f),
            MouseFilter = MouseFilterEnum.Stop,
        };
        _blocker.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_blocker);

        BuildBand(title, subtitle, cardIds, zeroCostDisplay);
        BuildEyeButton();

        _hiddenHint = new Label
        {
            Text = Loc.T("choice.hint_hidden"),
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _hiddenHint.SetAnchorsPreset(LayoutPreset.TopWide);
        _hiddenHint.OffsetTop = 22;
        _hiddenHint.OffsetBottom = 52;
        _hiddenHint.AddThemeFontSizeOverride("font_size", 16);
        _hiddenHint.AddThemeColorOverride("font_color", new Color("F0D98A"));
        _hiddenHint.AddThemeConstantOverride("outline_size", 4);
        _hiddenHint.AddThemeColorOverride("font_outline_color", new Color("241D12"));
        AddChild(_hiddenHint);

        // 入场：整体淡入 + 卡面依次弹出
        Modulate = new Color(1, 1, 1, 0);
        var fade = CreateTween();
        fade.TweenProperty(this, "modulate", Colors.White, 0.18f);
        for (int i = 0; i < _cards.Count; i++)
        {
            CardView card = _cards[i];
            card.Scale = Vector2.One * (CardScale() * 0.9f);
            var pop = card.CreateTween();
            pop.TweenInterval(i * 0.05f);
            pop.TweenProperty(card, "scale", Vector2.One * CardScale(), 0.2f)
                .SetTrans(Tween.TransitionType.Back)
                .SetEase(Tween.EaseType.Out);
        }
    }

    private void BuildBand(string title, string subtitle, IReadOnlyList<string> cardIds,
                           bool zeroCostDisplay)
    {
        _band = new PanelContainer
        {
            Name = "ChoiceBand",
            MouseFilter = MouseFilterEnum.Stop,
        };
        _band.AnchorLeft = 0f;
        _band.AnchorRight = 1f;
        _band.AnchorTop = 0.5f;
        _band.AnchorBottom = 0.5f;
        _band.OffsetLeft = 0f;
        _band.OffsetRight = 0f;
        _band.OffsetTop = -272f;
        _band.OffsetBottom = 272f;

        var style = new StyleBoxFlat
        {
            BgColor = new Color(BandColor, 0.97f),
            BorderColor = Gold,
            BorderWidthTop = 3,
            BorderWidthBottom = 3,
            BorderWidthLeft = 0,
            BorderWidthRight = 0,
            ContentMarginLeft = 32,
            ContentMarginRight = 32,
            ContentMarginTop = 16,
            ContentMarginBottom = 20,
        };
        _band.AddThemeStyleboxOverride("panel", style);
        AddChild(_band);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _band.AddChild(box);

        var titleLabel = new Label
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        titleLabel.AddThemeFontSizeOverride("font_size", 30);
        titleLabel.AddThemeColorOverride("font_color", new Color("F0D98A"));
        box.AddChild(titleLabel);

        var subLabel = new Label
        {
            Text = subtitle,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subLabel.AddThemeFontSizeOverride("font_size", 15);
        subLabel.AddThemeColorOverride("font_color", new Color("CFC3A5"));
        box.AddChild(subLabel);

        _row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _row.AddThemeConstantOverride("separation", 14);
        box.AddChild(_row);

        float scale = CardScaleFor(cardIds.Count);
        for (int i = 0; i < cardIds.Count; i++)
        {
            string id = cardIds[i];
            CardDef def = GameDatabase.Card(id);
            if (def == null)
                continue;

            var slot = new Control
            {
                CustomMinimumSize = new Vector2(CardView.CardWidth * scale + 14f,
                                                CardView.CardHeight * scale + 10f),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            _row.AddChild(slot);
            _slots.Add(slot);

            var card = new CardView
            {
                Position = Vector2.Zero,
                PivotOffset = Vector2.Zero,
                Scale = Vector2.One * scale,
            };
            card.Setup(def, i, affordable: true, costOverride: zeroCostDisplay ? 0 : -1);
            slot.AddChild(card);
            _cards.Add(card);

            int captured = i;
            CardView c = card;
            card.Clicked += _ => OnCardClicked(captured, c);
            card.MouseEntered += () => Hover(c, true);
            card.MouseExited += () => Hover(c, false);
        }

        // 卡牌与确定按钮之间留出距离
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 14) });

        // 多选时才显示确定按钮；单选隐藏
        _confirm = new Button
        {
            Text = Loc.T("choice.confirm"),
            Visible = _pickCount > 1,
            CustomMinimumSize = new Vector2(160, 44),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _confirm.Theme = UiTheme.Get();
        _confirm.AddThemeFontSizeOverride("font_size", 18);
        _confirm.AddThemeColorOverride("font_color", new Color("241D12"));
        _confirm.AddThemeColorOverride("font_disabled_color", new Color(0.85f, 0.75f, 0.45f, 0.75f));
        _confirm.Pressed += Confirm;
        _confirm.Disabled = true;
        box.AddChild(_confirm);
        ButtonJuice.Attach(_confirm);
        UpdateConfirmText();
    }

    private void BuildEyeButton()
    {
        _eye = new TextureButton
        {
            Name = "EyeButton",
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(52, 52),
        };
        _eye.SetAnchorsPreset(LayoutPreset.TopRight);
        _eye.OffsetLeft = -76;
        _eye.OffsetRight = -20;
        _eye.OffsetTop = 16;
        _eye.OffsetBottom = 72;
        _eye.TextureNormal = GD.Load<Texture2D>("res://gameAsset/textures/ui/button/btn_eye_normal.png");
        _eye.TextureHover = GD.Load<Texture2D>("res://gameAsset/textures/ui/button/btn_eye_hover.png");
        _eye.TexturePressed = GD.Load<Texture2D>("res://gameAsset/textures/ui/button/btn_eye_pressed.png");
        _eye.MouseEntered += () => TweenEye(1.12f);
        _eye.MouseExited += () => TweenEye(1f);
        _eye.Pressed += ToggleHidden;
        AddChild(_eye);
        _eye.MoveToFront();
    }

    private void TweenEye(float scale)
    {
        if (_eye == null)
            return;
        _eye.PivotOffset = _eye.Size * 0.5f;
        var tween = _eye.CreateTween();
        tween.TweenProperty(_eye, "scale", Vector2.One * scale, 0.12f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    private float CardScaleFor(int count) =>
        count <= 4 ? 1.55f : count <= 6 ? 1.28f : 1.02f;

    private float CardScale() => _cards.Count <= 4 ? 1.55f : _cards.Count <= 6 ? 1.28f : 1.02f;

    private void OnCardClicked(int index, CardView card)
    {
        if (_pickCount <= 1)
        {
            _selected.Clear();
            _selected.Add(index);
            RefreshSelection();
            CloseAndPick();
            return;
        }

        if (_selected.Contains(index))
        {
            _selected.Remove(index);
        }
        else
        {
            if (_selected.Count >= _pickCount)
                _selected.RemoveAt(0);
            _selected.Add(index);
        }

        RefreshSelection();
    }

    private void RefreshSelection()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            bool on = _selected.Contains(i);
            _cards[i].SetChosen(on);

            var tween = _cards[i].CreateTween();
            tween.TweenProperty(_cards[i], "scale",
                    Vector2.One * (CardScale() * (on ? 1.06f : 1f)), 0.12f)
                .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        }

        UpdateConfirmText();
    }

    private void UpdateConfirmText()
    {
        if (_confirm == null)
            return;

        _confirm.Text = _pickCount > 1
            ? $"{Loc.T("choice.confirm")}（{_selected.Count}/{_pickCount}）"
            : Loc.T("choice.confirm");
        _confirm.Disabled = _pickCount > 1 && _selected.Count != _pickCount;
    }

    /// <summary>调试：直接选中若干张（多选验证）。</summary>
    public void DebugSelect(params int[] indices)
    {
        _selected.Clear();
        foreach (int i in indices)
        {
            if (i >= 0 && i < _cards.Count && !_selected.Contains(i))
                _selected.Add(i);
        }
        RefreshSelection();
    }

    /// <summary>调试：切换隐藏状态。</summary>
    public void DebugToggleHidden() => ToggleHidden();

    private void Confirm()
    {
        if (_selected.Count != _pickCount)
            return;
        CloseAndPick();
    }

    private void ToggleHidden()
    {
        _hidden = !_hidden;

        var tween = CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(_band, "modulate", _hidden ? new Color(1, 1, 1, 0) : Colors.White, 0.18f);
        tween.TweenProperty(_blocker, "color",
            _hidden ? new Color(0.06f, 0.05f, 0.03f, 0f) : new Color(0.06f, 0.05f, 0.03f, 0.78f), 0.18f);
        _hiddenHint.Visible = _hidden;

        // 隐藏态仍保留全屏拦截，玩家可以看手牌但不能操作
        _eye.TextureNormal = GD.Load<Texture2D>(_hidden
            ? "res://gameAsset/textures/ui/button/btn_eye_off_normal.png"
            : "res://gameAsset/textures/ui/button/btn_eye_normal.png");
        _eye.TextureHover = GD.Load<Texture2D>(_hidden
            ? "res://gameAsset/textures/ui/button/btn_eye_off_hover.png"
            : "res://gameAsset/textures/ui/button/btn_eye_hover.png");
        _eye.TexturePressed = _eye.TextureNormal;
    }

    private void CloseAndPick()
    {
        if (_closed)
            return;

        _closed = true;
        Action<List<int>> cb = _onPick;
        _onPick = null;
        var picked = new List<int>(_selected);

        _blocker.MouseFilter = MouseFilterEnum.Ignore;
        var tween = CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(this, "modulate", new Color(1, 1, 1, 0), 0.16f);
        tween.TweenProperty(_band, "scale", new Vector2(0.96f, 0.9f), 0.16f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            QueueFree();
            cb?.Invoke(picked);
        }));
    }

    private void Hover(CardView card, bool on)
    {
        if (card == null || !IsInstanceValid(card))
            return;

        bool selected = _selected.Contains(card.Index);
        float target = CardScale() * (selected ? 1.06f : 1f) * (on ? 1.035f : 1f);
        var tween = card.CreateTween();
        tween.TweenProperty(card, "scale", Vector2.One * target, on ? 0.10f : 0.14f)
            .SetTrans(Tween.TransitionType.Back)
            .SetEase(on ? Tween.EaseType.Out : Tween.EaseType.InOut);
    }
}
