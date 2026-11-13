using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Battle;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>
/// 战斗 HUD：顶部回合/金币、右侧格子信息、底部扇形手牌、建造菜单、回合结束。
/// 版式参考 设计草案/战斗UI参考-4、-5、-6。
/// </summary>
public partial class BattleHud : CanvasLayer
{
    public event Action<int> CardClicked;
    public event Action<int> CardDoubleClicked;
    public event Action<int> CardDragPlayed;
    public event Action<int, Vector2> CardDropped;
    public event Action<int, int> CardReordered;
    public event Action EndTurnClicked;
    public event Action<string> BuildClicked;
    public event Action UpgradeClicked;
    public event Action RepairClicked;
    public event Action DemolishClicked;
    public event Action ExtraFireClicked;

    /// <summary>鼠标悬停建造项 / 升级按钮时触发，用于预览下一级外观。</summary>
    public event Action<string> BuildHovered;
    public event Action PreviewUnhovered;
    public event Action UpgradeHovered;
    public event Action CancelClicked;

    /// <summary>点了底部某个牌堆（0=抽牌堆 1=弃牌堆 2=消耗堆）。</summary>
    public event Action<int> PileClicked;

    private PileView _deckPile;
    private PileView _discardPile;
    private PileView _exhaustPile;
    private PileViewer _pileViewer;
    private Control _effects;

    private Label _turnLabel;
    private Label _goldLabel;
    private Label _enemyGoldLabel;
    private Label _toastLabel;
    private PanelContainer _toastPanel;
    private Label _infoTitle;
    private RichTextLabel _infoBody;
    private TextureRect _infoImage;
    private CardHandView _hand;
    private TargetArrow _arrow;
    private HealthBars _bars;
    private Button _endTurnButton;
    private PanelContainer _infoPanel;
    private Button _extraFireBtn;
    private Button _demolishBtn;
    private Button _upgradeBtn;
    private Button _repairBtn;
    private PanelContainer _buildMenu;

    /// <summary>调试：建造菜单是否可见。</summary>
    public bool DebugBuildMenuVisible => _buildMenu is { Visible: true };
    private VBoxContainer _buildList;
    private Label _buildTitle;
    private Control _root;
    private float _toastTimer;

    private BattleState _state;
    private int _selectedCard = -1;

    public override void _Ready()
    {
        Layer = 10;
        BuildUi();
    }

    private void BuildUi()
    {
        _root = new Control
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Theme = UiTheme.Get(),
        };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // 出牌指示箭头：放在最底层，被手牌和面板盖住
        _arrow = new TargetArrow { Name = "TargetArrow" };
        _root.AddChild(_arrow);

        // 血条：在地图之上、手牌之下
        _bars = new HealthBars { Name = "HealthBars" };
        _root.AddChild(_bars);

        // ── 顶部信息条 ──
        var top = new PanelContainer
        {
            Name = "TopBar",
            MouseFilter = Control.MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(0, 56),
        };
        top.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        top.OffsetBottom = 56;
        _root.AddChild(top);

        var topRow = new HBoxContainer { Name = "TopRow" };
        topRow.AddThemeConstantOverride("separation", 22);
        topRow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        top.AddChild(topRow);

        _turnLabel = NewLabel("第 1 回合", 20);
        topRow.AddChild(_turnLabel);

        var goldBox = new HBoxContainer();
        goldBox.AddChild(NewIcon("gold"));
        _goldLabel = NewLabel("0", 20);
        goldBox.AddChild(_goldLabel);
        topRow.AddChild(goldBox);

        topRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var enemyBox = new HBoxContainer();
        enemyBox.AddChild(NewIcon("defense"));
        _enemyGoldLabel = NewLabel("敌方 0", 18);
        enemyBox.AddChild(_enemyGoldLabel);
        topRow.AddChild(enemyBox);

        // ── 右侧信息面板 ──
        _infoPanel = new PanelContainer
        {
            Name = "InfoPanel",
            Visible = false,
            CustomMinimumSize = new Vector2(330, 0),
        };
        _infoPanel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _infoPanel.Position = new Vector2(-346, 72);
        _infoPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        _infoPanel.OffsetLeft = -346;
        _infoPanel.OffsetTop = 72;
        _infoPanel.OffsetRight = -16;
        _root.AddChild(_infoPanel);

        var infoBox = new VBoxContainer();
        infoBox.AddThemeConstantOverride("separation", 8);
        _infoPanel.AddChild(infoBox);

        _infoTitle = NewLabel("", 20);
        infoBox.AddChild(_infoTitle);

        _infoImage = new TextureRect
        {
            Name = "InfoImage",
            CustomMinimumSize = new Vector2(0, 96),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        infoBox.AddChild(_infoImage);

        _infoBody = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(256, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _infoBody.AddThemeFontSizeOverride("normal_font_size", 16);
        _infoBody.AddThemeColorOverride("default_color", UiTheme.Ink);
        infoBox.AddChild(_infoBody);

        // 用流式容器：按钮多了会自动换行，不会顶出面板
        var infoButtons = new HFlowContainer();
        infoButtons.AddThemeConstantOverride("h_separation", 8);
        infoButtons.AddThemeConstantOverride("v_separation", 6);
        infoButtons.CustomMinimumSize = new Vector2(256, 0);
        infoBox.AddChild(infoButtons);

        _upgradeBtn = NewButton("升级");
        _upgradeBtn.Pressed += () => UpgradeClicked?.Invoke();
        _upgradeBtn.MouseEntered += () => UpgradeHovered?.Invoke();
        _upgradeBtn.MouseExited += () => PreviewUnhovered?.Invoke();
        infoButtons.AddChild(_upgradeBtn);

        // 受伤后可以花钱修理（按损失血量计价）
        _repairBtn = NewButton("维修");
        _repairBtn.AddThemeColorOverride("font_disabled_color", new Color(0.23f, 0.18f, 0.09f, 0.55f));
        _repairBtn.Pressed += () => RepairClicked?.Invoke();
        _repairBtn.Visible = false;
        infoButtons.AddChild(_repairBtn);

        _demolishBtn = NewButton("拆除");
        _demolishBtn.Pressed += () => DemolishClicked?.Invoke();
        infoButtons.AddChild(_demolishBtn);

        // 炮台等建筑可以花钱多打一炮
        _extraFireBtn = NewButton("额外开炮");
        _extraFireBtn.AddThemeColorOverride("font_disabled_color", new Color(0.23f, 0.18f, 0.09f, 0.55f));
        _extraFireBtn.Pressed += () => ExtraFireClicked?.Invoke();
        _extraFireBtn.Visible = false;
        infoButtons.AddChild(_extraFireBtn);

    // ── 底部手牌（扇形） ──
    _hand = new CardHandView { Name = "Hand" };
    _hand.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
    _hand.OffsetLeft = 230;
    _hand.OffsetRight = -230;
    _hand.OffsetTop = -272;
    _hand.OffsetBottom = 0;
    _root.AddChild(_hand);

    _hand.Selected += i => CardClicked?.Invoke(i);
    _hand.DoubleClicked += i => CardDoubleClicked?.Invoke(i);
    _hand.DragPlayed += i => CardDragPlayed?.Invoke(i);
    _hand.DroppedOnMap += (i, pos) => CardDropped?.Invoke(i, pos);
    _hand.Reordered += (a, b) => CardReordered?.Invoke(a, b);

    // ── 底部牌堆（左：抽牌堆；右：弃牌堆 + 消耗堆） ──
    _deckPile = new PileView();
    _deckPile.Setup(PileView.PileKind.Deck, "抽牌堆");
    _deckPile.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
    _deckPile.OffsetLeft = 14;
    _deckPile.OffsetTop = -218;
    _deckPile.OffsetRight = 98;
    _deckPile.OffsetBottom = -132;
    _deckPile.PileClicked += kind => PileClicked?.Invoke((int)kind);
    _root.AddChild(_deckPile);

    _exhaustPile = new PileView();
    _exhaustPile.Setup(PileView.PileKind.Exhaust, "消耗堆");
    _exhaustPile.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
    _exhaustPile.OffsetLeft = -94;
    _exhaustPile.OffsetTop = -218;
    _exhaustPile.OffsetRight = -10;
    _exhaustPile.OffsetBottom = -132;
    _exhaustPile.PileClicked += kind => PileClicked?.Invoke((int)kind);
    _root.AddChild(_exhaustPile);

    _discardPile = new PileView();
    _discardPile.Setup(PileView.PileKind.Discard, "弃牌堆");
    _discardPile.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
    _discardPile.OffsetLeft = -186;
    _discardPile.OffsetTop = -218;
    _discardPile.OffsetRight = -102;
    _discardPile.OffsetBottom = -132;
    _discardPile.PileClicked += kind => PileClicked?.Invoke((int)kind);
    _root.AddChild(_discardPile);

    // 飞牌动画层
    _effects = new Control { Name = "Effects", MouseFilter = Control.MouseFilterEnum.Ignore };
    _effects.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    _root.AddChild(_effects);

    // 牌堆查看器（盖在最上层，连提示条也压住）
    _pileViewer = new PileViewer { ZIndex = 120 };
    _root.AddChild(_pileViewer);

        // ── 回合结束 ──
        _endTurnButton = NewButton("回合结束");
        _endTurnButton.Name = "EndTurn";
        _endTurnButton.CustomMinimumSize = new Vector2(150, 46);
        _endTurnButton.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        _endTurnButton.OffsetLeft = -172;
        _endTurnButton.OffsetTop = -58;
        _endTurnButton.OffsetRight = -18;
        _endTurnButton.OffsetBottom = -14;
        _endTurnButton.Pressed += () => EndTurnClicked?.Invoke();
        _root.AddChild(_endTurnButton);

        // ── 提示文字 ──
        var toastPanel = new PanelContainer
        {
            Name = "ToastPanel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0),
        };
        toastPanel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        toastPanel.OffsetLeft = 300;
        toastPanel.OffsetRight = -360;
        toastPanel.OffsetTop = 64;
        toastPanel.OffsetBottom = 104;
        toastPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.PanelDir + "panel_light.png", 18, 12, 12, 6, 6));
        _root.AddChild(toastPanel);
        _toastPanel = toastPanel;

        _toastLabel = NewLabel("", 18);
        _toastLabel.Name = "Toast";
        _toastLabel.HorizontalAlignment = HorizontalAlignment.Center;
        toastPanel.AddChild(_toastLabel);

        // ── 建造菜单 ──
        _buildMenu = new PanelContainer
        {
            Name = "BuildMenu",
            Visible = false,
            CustomMinimumSize = new Vector2(320, 0),
        };
        _buildMenu.SetAnchorsPreset(Control.LayoutPreset.Center);
        _buildMenu.OffsetLeft = -160;
        _buildMenu.OffsetTop = -250;
        _buildMenu.OffsetRight = 160;
        _root.AddChild(_buildMenu);

        var buildBox = new VBoxContainer();
        buildBox.AddThemeConstantOverride("separation", 6);
        _buildMenu.AddChild(buildBox);

        _buildTitle = NewLabel("建造", 20);
        buildBox.AddChild(_buildTitle);

        _buildList = new VBoxContainer();
        _buildList.AddThemeConstantOverride("separation", 4);
        buildBox.AddChild(_buildList);

        var cancelBtn = NewButton("取消");
        cancelBtn.Pressed += () =>
        {
            HideBuildMenu();
            CancelClicked?.Invoke();
        };
        buildBox.AddChild(cancelBtn);

        ButtonJuice.AttachAll(_root);
    }

    private static Label NewLabel(string text, int size)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", UiTheme.Ink);
        return l;
    }

    private static Button NewButton(string text)
    {
        var b = new Button { Text = text };
        b.AddThemeFontSizeOverride("font_size", 18);
        // 禁用态也要能看清文字（默认禁用色在沙色底上几乎隐形）
        b.AddThemeColorOverride("font_disabled_color", new Color(0.23f, 0.18f, 0.09f, 0.55f));
        return b;
    }

    private static TextureRect NewIcon(string name)
    {
        return new TextureRect
        {
            Texture = UiTheme.Icon(name),
            CustomMinimumSize = new Vector2(24, 24),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        };
    }

    public void Setup(BattleState state)
    {
        _state = state;
        RefreshAll();
    }

    public void RefreshAll()
    {
        if (_state == null)
            return;
        RefreshTop();
        RefreshHand();
        RefreshPiles();
    }

    public void RefreshPiles()
    {
        if (_state == null)
            return;

        _deckPile.SetCount(_state.Player.Deck.Count);
        _discardPile.SetCount(_state.Player.Discard.Count);
        _exhaustPile.SetCount(Mathf.Max(0, _state.Player.Exhaust.Count - _pendingExhaust));
    }

    /// <summary>牌堆位置（给飞牌动画当目标点）。</summary>
    public Vector2 PileCenter(PileView.PileKind kind) => kind switch
    {
        PileView.PileKind.Discard => _discardPile.StackCenter,
        PileView.PileKind.Exhaust => _exhaustPile.StackCenter,
        _ => _deckPile.StackCenter,
    };

    public void BumpPile(PileView.PileKind kind)
    {
        switch (kind)
        {
            case PileView.PileKind.Discard: _discardPile.Bump(); break;
            case PileView.PileKind.Exhaust: _exhaustPile.Bump(); break;
            default: _deckPile.Bump(); break;
        }
    }

    /// <summary>打开某个牌堆的查看器。</summary>
    public void ShowPile(string title, System.Collections.Generic.IEnumerable<string> cards,
                         System.Collections.Generic.IReadOnlyList<int> etherealIndices = null) =>
        _pileViewer.Open(title, cards, etherealIndices);

    public void ClosePile() => _pileViewer.Close();

    /// <summary>调试：抽牌堆当前缩放（验证悬停动画）。</summary>
    public float DebugPileScale() => _deckPile.Scale.X;

    /// <summary>调试：当前实际可见的手牌数量。</summary>
    public int DebugVisibleCardCount() =>
        _hand.Cards.Count(c => c.Visible);

    /// <summary>取某张手牌的屏幕矩形（给飞牌动画用）。</summary>
    public bool TryGetHandCardRect(int index, out Rect2 rect) =>
        _hand.TryGetCardRect(index, out rect);

    /// <summary>放一个飞牌动画。</summary>
    public void PlayCardToss(CardDef def, Vector2 from, Vector2 to,
                             float duration = 0.42f, float endScale = 0.3f,
                             float arc = 120f, bool faceDown = false,
                             System.Action onDone = null)
    {
        var fx = new CardTossEffect();
        _effects.AddChild(fx);
        fx.Play(def, from, to, duration, endScale, arc, faceDown, onDone);
    }

    /// <summary>抽牌动画：从抽牌堆飞到手上。</summary>
    public void PlayDrawAnimation(int count)
    {
        if (count <= 0)
            return;

        Vector2 from = PileCenter(PileView.PileKind.Deck);
        int total = _hand.Cards.Count;
        int first = Mathf.Max(0, total - count);

        for (int i = first; i < total; i++)
        {
            int index = i;
            if (!_hand.TryGetCardTransform(index, out Rect2 rect, out _))
                continue;

            _hand.SetCardVisible(index, false);

            float delay = (i - first) * 0.07f;
            var timer = GetTree().CreateTimer(delay);
            timer.Timeout += () =>
            {
                CardDef def = GameDatabase.Card(_state.Player.Hand.Count > index
                    ? _state.Player.Hand[index] : null);
                PlayCardToss(def, from, rect.Position + rect.Size * 0.5f, 0.34f, 0.72f, 90f,
                    faceDown: true, onDone: () => _hand.SetCardVisible(index, true));
            };
        }
    }

    /// <summary>打出的卡飞向弃牌堆/消耗堆。</summary>
    public void PlayToPileAnimation(CardDef def, Rect2 cardRect, bool exhaust)
    {
        Vector2 from = cardRect.Position + cardRect.Size * 0.5f;
        Vector2 to = PileCenter(exhaust ? PileView.PileKind.Exhaust : PileView.PileKind.Discard);
        PlayCardToss(def, from, to, 0.4f, 0.22f, 60f,
            onDone: () => BumpPile(exhaust ? PileView.PileKind.Exhaust : PileView.PileKind.Discard));
    }

    /// <summary>洗回牌库：几张卡从弃牌堆飞向抽牌堆。</summary>
    public void PlayReshuffleAnimation(int count = 4)
    {
        Vector2 from = PileCenter(PileView.PileKind.Discard);
        Vector2 to = PileCenter(PileView.PileKind.Deck);

        for (int i = 0; i < count; i++)
        {
            float delay = i * 0.08f;
            var timer = GetTree().CreateTimer(delay);
            timer.Timeout += () => PlayCardToss(null, from, to, 0.5f, 0.5f, 130f,
                faceDown: true, onDone: () => BumpPile(PileView.PileKind.Deck));
        }
    }

    public void RefreshTop()
    {
        _turnLabel.Text = Loc.T("battle.turn", _state.TurnNumber);
        _goldLabel.Text = $"{_state.Player.Gold}";
        _enemyGoldLabel.Text = Loc.T("battle.enemy", _state.Enemy.Gold);
        _endTurnButton.Disabled = _state.Phase != BattlePhase.PlayerTurn;
        _endTurnButton.Text = _state.Phase == BattlePhase.PlayerTurn
            ? Loc.T("battle.end_turn")
            : Loc.T("battle.enemy_turn");
    }

    public void RefreshHand()
    {
        if (_state == null)
            return;

        _hand.SetState(_state);
        _hand.Refresh();
        _hand.SetSelected(_selectedCard);
    }

    public void SetSelectedCard(int index)
    {
        _selectedCard = index;
        _hand.SetSelected(index);
    }

    /// <summary>调试用：取第 index 张卡的屏幕矩形。</summary>
    public bool TryGetCardRect(int index, out Rect2 rect) => _hand.TryGetCardRect(index, out rect);

    /// <summary>
    /// 出牌指示箭头：从第 cardIndex 张卡的顶端指向屏幕坐标 tip。
    /// valid 决定箭头颜色（绿=可以打，红=不行）。
    /// </summary>
    public void AimArrow(int cardIndex, Vector2 tip, bool valid)
    {
        if (_arrow == null)
            return;

        if (!_hand.TryGetCardRect(cardIndex, out Rect2 rect))
        {
            _arrow.HideArrow();
            return;
        }

        Vector2 from = new(rect.Position.X + rect.Size.X * 0.5f, rect.Position.Y + 8f);
        _arrow.Aim(from, tip, valid);
    }

    public void HideArrow() => _arrow?.HideArrow();

    /// <summary>出牌飞行期间隐藏手牌里对应的那张卡。</summary>
    public void SetHandCardVisible(int index, bool visible) => _hand.SetCardVisible(index, visible);

    /// <summary>
    /// 出牌飞行动画：复制一张卡从手牌位置沿曲线飞到目标格，落地后回调（再真正生效）。
    /// </summary>
    public void FlyCard(int cardIndex, CardDef card, Vector2 targetScreen, Action onArrive)
    {
        if (card == null || !_hand.TryGetCardRect(cardIndex, out Rect2 from))
        {
            onArrive?.Invoke();
            return;
        }

        var fly = new CardFlyEffect { Name = "CardFly" };
        _root.AddChild(fly);
        fly.OnArrive = onArrive;
        fly.Play(card, from, targetScreen);
    }

    /// <summary>
    /// 烧卡特效：卡牌被移除时在手牌原位置「烧掉」。
    /// 必须在刷新（重建手牌）之前调用，否则拿不到卡面位置。
    /// </summary>
    public void BurnCards(IReadOnlyList<string> cardIds)
    {
        if (cardIds == null || cardIds.Count == 0)
            return;

        var used = new HashSet<int>();

        foreach (string id in cardIds)
        {
            int index = _hand.IndexOfCard(id, used);
            if (index < 0)
                continue;
            if (!_hand.TryGetCardTransform(index, out Rect2 rect, out float rot))
                continue;

            CardDef def = GameDatabase.Card(id);
            if (def == null)
                continue;

            used.Add(index);

            var burn = new CardBurnEffect { Name = "CardBurn" };
            _root.AddChild(burn);
            burn.Play(def, rect, rot);

            // 先藏起原卡，避免和燃烧层重叠
            _hand.SetCardVisible(index, false);
        }
    }

    /// <summary>正在播放「烧卡」的消耗卡数量：烧完之前先不计入消耗堆显示。</summary>
    private int _pendingExhaust;

    /// <summary>在指定位置播「烧卡」特效用（消耗卡打出时用）。</summary>
    public void BurnCardAt(CardDef def, Rect2 rect, float rotationDeg = 0f, System.Action onDone = null)
    {
        if (def == null)
        {
            onDone?.Invoke();
            return;
        }

        _pendingExhaust++;
        RefreshPiles();

        var burn = new CardBurnEffect { Name = "CardBurn" };
        _root.AddChild(burn);
        burn.Play(def, rect, rotationDeg);

        var timer = GetTree().CreateTimer(0.95);
        timer.Timeout += () =>
        {
            _pendingExhaust = Mathf.Max(0, _pendingExhaust - 1);
            RefreshPiles();
            BumpPile(PileView.PileKind.Exhaust);
            onDone?.Invoke();
        };
    }

    /// <summary>血条层：设置相机与每帧的数据。</summary>
    public void SetHealthCamera(Camera3D camera) => _bars?.SetCamera(camera);

    public void SetHealthBars(List<HealthBars.Bar> bars) => _bars?.SetBars(bars);

    /// <summary>调试：血条当前显示值 / 残影值。</summary>
    public string DebugHealth(bool building, int id) => _bars?.DebugShown(building, id) ?? "n/a";

    public void ShowInfo(string title, string body, string imagePath = null, bool showButtons = false,
        int extraFireCost = 0, bool extraFireReady = false, bool showDemolish = true,
        bool canUpgrade = true, int repairCost = 0, bool repairAffordable = true)
    {
        _infoPanel.Visible = true;
        _infoTitle.Text = title;
        _infoBody.Text = body;
        if (!string.IsNullOrEmpty(imagePath) && ResourceLoader.Exists(imagePath))
        {
            _infoImage.Visible = true;
            _infoImage.Texture = GD.Load<Texture2D>(imagePath);
        }
        else
        {
            _infoImage.Visible = false;
        }

        foreach (Node child in _infoPanel.GetChildren())
        {
            if (child is VBoxContainer v)
            {
                foreach (Node sub in v.GetChildren())
                {
                    if (sub is HFlowContainer hf)
                        hf.Visible = showButtons;
                    else if (sub is HBoxContainer h)
                        h.Visible = showButtons;
                }
            }
        }

        if (_extraFireBtn != null)
        {
            _extraFireBtn.Visible = showButtons && extraFireCost > 0;
            if (extraFireCost > 0)
            {
                _extraFireBtn.Text = extraFireReady
                    ? $"{Loc.T("info.extra_fire")} {extraFireCost}$"
                    : Loc.T("info.extra_fire_used");
                _extraFireBtn.Disabled = !extraFireReady;
            }
        }

        if (_repairBtn != null)
        {
            _repairBtn.Visible = showButtons && repairCost > 0;
            if (repairCost > 0)
            {
                _repairBtn.Text = $"维修 {repairCost}$";
                _repairBtn.Disabled = !repairAffordable;
            }
        }

        if (_demolishBtn != null)
            _demolishBtn.Visible = showButtons && showDemolish;

        if (_upgradeBtn != null)
        {
            _upgradeBtn.Visible = showButtons;
            _upgradeBtn.Disabled = !canUpgrade;
        }
    }

    public void HideInfo() => _infoPanel.Visible = false;

    private readonly Dictionary<string, Button> _buildButtons = new();

    /// <summary>调试：模拟悬停某个建造选项。</summary>
    public void DebugHoverBuild(string defId)
    {
        if (_buildButtons.TryGetValue(defId, out Button b))
            b.EmitSignal(Control.SignalName.MouseEntered);
    }

    /// <summary>调试：模拟悬停升级按钮。</summary>
    public void DebugHoverUpgrade() => _upgradeBtn?.EmitSignal(Control.SignalName.MouseEntered);

    public void ShowBuildMenu(IReadOnlyList<BuildingDef> defs, int gold)
    {
        foreach (Node child in _buildList.GetChildren())
            child.QueueFree();

        _buildTitle.Text = "建造（在此格）";
        foreach (BuildingDef def in defs)
        {
            int cost = def.UpgradeCostAt(0);
            string label = $"{def.NameZh}    {cost}$";
            var b = NewButton(label);
            b.Disabled = gold < cost;
            string id = def.Id;
            b.Pressed += () => BuildClicked?.Invoke(id);
            b.MouseEntered += () => BuildHovered?.Invoke(id);
            b.MouseExited += () => PreviewUnhovered?.Invoke();
            _buildList.AddChild(b);
            _buildButtons[id] = b;
            ButtonJuice.Attach(b);
        }

        _buildMenu.Visible = true;
    }

    public void HideBuildMenu() => _buildMenu.Visible = false;

    public void Toast(string msg)
    {
        if (_toastLabel == null)
            return;
        _toastLabel.Text = msg;
        _toastTimer = 2.8f;
        _toastPanel.Modulate = new Color(1, 1, 1, 1);
    }

    public override void _Process(double delta)
    {
        if (_toastTimer > 0)
        {
            _toastTimer -= (float)delta;
            if (_toastTimer <= 0)
                _toastPanel.Modulate = new Color(1, 1, 1, 0);
            else if (_toastTimer < 0.6f)
                _toastPanel.Modulate = new Color(1, 1, 1, _toastTimer / 0.6f);
        }
    }

    public bool IsMouseOverUi()
    {
        Vector2 mouse = GetViewport().GetMousePosition();
        if (_hand != null && _hand.GetGlobalRect().HasPoint(mouse))
            return true;
        return (_endTurnButton != null && _endTurnButton.GetGlobalRect().HasPoint(mouse)) ||
               (_infoPanel != null && _infoPanel.Visible && _infoPanel.GetGlobalRect().HasPoint(mouse)) ||
               (_buildMenu != null && _buildMenu.Visible && _buildMenu.GetGlobalRect().HasPoint(mouse));
    }
}
