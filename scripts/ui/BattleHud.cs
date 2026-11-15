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
    public event Action ProduceToggled;
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
    private CanvasLayer _spawnLayer;
    private PanelContainer _spawnPanel;
    private Label _spawnTitle;
    private GridContainer _spawnList;
    private string _spawnSignature = "";
    private PanelContainer _terrainPanel;
    private TextureRect _terrainImage;
    private Label _terrainTitle;
    private Label _terrainBody;
    private Button _produceBtn;
    private Label _produceName;
    private Label _produceState;
    private Button _demolishBtn;
    private Label _demolishName;
    private Label _demolishGold;
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

        // 弃牌阶段的场景遮罩：压在 3D 地图上、所有 UI 之下（第一个子节点=画在最底层）
        _pickDim = new ColorRect
        {
            Name = "PickDim",
            Color = new Color(0f, 0f, 0f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _pickDim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_pickDim);

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
        BuildTerrainPanel();
        BuildSpawnPanel();

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

        // 「拆除」按钮：文字拆成两个 Label —— 名称保持墨色，金额用亮金色
        // 产卡开关：只有会产卡的建筑才显示；「产卡：」保持墨色，只有「开/关」上色
        _produceBtn = NewButton("");
        _produceBtn.CustomMinimumSize = new Vector2(140, 44);   // 给「产卡：开/关」留足宽度
        _produceBtn.ToggleMode = true;
        _produceBtn.Pressed += () => ProduceToggled?.Invoke();

        var produceRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        produceRow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        produceRow.Alignment = BoxContainer.AlignmentMode.Center;
        produceRow.AddThemeConstantOverride("separation", 2);
        _produceBtn.AddChild(produceRow);

        _produceName = new Label
        {
            Text = Loc.T("info.produce_name"),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
        };
        produceRow.AddChild(_produceName);

        _produceState = new Label
        {
            Text = "",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
        };
        produceRow.AddChild(_produceState);

        infoButtons.AddChild(_produceBtn);

        _demolishBtn = NewButton("");
        _demolishBtn.CustomMinimumSize = new Vector2(170, 44);   // 给「拆除 +金额」留足宽度
        _demolishBtn.Pressed += () => DemolishClicked?.Invoke();

        var demolishRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        demolishRow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        demolishRow.Alignment = BoxContainer.AlignmentMode.Center;
        demolishRow.AddThemeConstantOverride("separation", 0);
        _demolishBtn.AddChild(demolishRow);

        _demolishName = new Label
        {
            Text = "拆除",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
        };
        demolishRow.AddChild(_demolishName);

        _demolishGold = new Label
        {
            Text = "",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _demolishGold.AddThemeColorOverride("font_color", new Color("FFE45C"));
        _demolishGold.AddThemeFontSizeOverride("font_size", 19);
        _demolishGold.AddThemeConstantOverride("outline_size", 3);
        _demolishGold.AddThemeColorOverride("font_outline_color", new Color("3B2E16"));
        demolishRow.AddChild(_demolishGold);

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

        // 弃牌阶段的大号提示：持续显示在画面中上方
        _pickBanner = new Label
        {
            Name = "PickBanner",
            Text = Loc.T("battle.pick_discard"),
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _pickBanner.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _pickBanner.OffsetLeft = -420;
        _pickBanner.OffsetRight = 420;
        _pickBanner.OffsetTop = 92;
        _pickBanner.OffsetBottom = 156;
        _pickBanner.AddThemeFontSizeOverride("font_size", 34);
        _pickBanner.AddThemeColorOverride("font_color", new Color("FFF6DC"));
        _pickBanner.AddThemeConstantOverride("outline_size", 10);
        _pickBanner.AddThemeColorOverride("font_outline_color", new Color("241C0E"));
        _root.AddChild(_pickBanner);

        ButtonJuice.AttachAll(_root);
        UiSound.AttachAll(_root);
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
        UiSound.Attach(b);
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

        // 手牌顺序编号（设置里的开关，默认开）
        if (_hand != null)
            _hand.SetOrderHintEnabled(Settings.GetBool("video", "hand_order", true));

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
                         System.Collections.Generic.IReadOnlyList<int> etherealIndices = null,
                         System.Collections.Generic.IReadOnlyList<int> producedExhaustIndices = null,
                         System.Action<int> onPick = null) =>
        _pileViewer.Open(title, cards, etherealIndices, producedExhaustIndices, onPick);

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
                             System.Action onDone = null, float startScale = 0.55f)
    {
        var fx = new CardTossEffect();
        _effects.AddChild(fx);
        fx.Play(def, from, to, duration, endScale, arc, faceDown, onDone, startScale);
    }

    /// <summary>抽牌动画：从抽牌堆飞到手上。</summary>
    public void PlayDrawAnimation(int count) => PlayDrawAnimation(null, count);

    /// <summary>按手牌下标播抽牌动画（只动画真正从牌库抽到的那些）。</summary>
    public void PlayDrawAnimation(System.Collections.Generic.IReadOnlyList<int> indices, int fallbackCount = 0)
    {
        Vector2 from = PileCenter(PileView.PileKind.Deck);
        int total = _hand.Cards.Count;

        var list = new System.Collections.Generic.List<int>();
        if (indices != null)
            list.AddRange(indices);
        else
        {
            int first = Mathf.Max(0, total - fallbackCount);
            for (int i = first; i < total; i++)
                list.Add(i);
        }

        if (list.Count == 0)
            return;

        int drawn = 0;
        foreach (int i in list)
        {
            int index = i;
            if (index < 0 || index >= total)
                continue;

            if (!_hand.TryGetCardTransform(index, out Rect2 rect, out _))
                continue;

            _hand.SetCardVisible(index, false);

            float delay = drawn++ * 0.07f;
            var timer = GetTree().CreateTimer(delay);
            timer.Timeout += () =>
            {
                // 每张牌起飞的瞬间各播一次抽牌音效
                AudioManager.PlayCardDrawn();

                string handId = _state.Player.Hand.Count > index ? _state.Player.Hand[index] : null;
                CardDef def = string.IsNullOrEmpty(handId) ? null : GameDatabase.Card(handId);
                PlayCardToss(def, from, rect.Position + rect.Size * 0.5f, 0.34f, 0.72f, 90f,
                    faceDown: true, onDone: () => _hand.SetCardVisible(index, true));
            };
        }
    }
    /// <summary>
    /// 建筑产卡动画：这张手牌本身从建筑位置「长大」飞到自己现在的位置。
    /// 直接用真实卡牌移动，飞行途中不会出现第二张同名牌。
    /// </summary>
    /// <summary>隐藏手牌编号（抽牌/产卡动画开始时）。</summary>
    public void HideOrderHints() => _hand.SetOrderHintReady(false);

    /// <summary>等入场动画播完再显示手牌编号。</summary>
    public void ShowOrderHintsAfter(float seconds)
    {
        if (seconds <= 0f)
        {
            _hand.SetOrderHintReady(true);
            return;
        }

        SceneTreeTimer timer = GetTree().CreateTimer(seconds);
        timer.Timeout += () =>
        {
            _hand.SetOrderHintReady(true);
            if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--anim-log") >= 0)
                GD.Print($"[Speed] 编号出现 t={Time.GetTicksMsec()}");
        };
    }

    public void PlayProduceAnimation(CardDef def, Vector2 fromScreen, int handIndex, float delay = 0f)
    {
        if (handIndex < 0 || handIndex >= _hand.Cards.Count)
            return;

        CardView card = _hand.Cards[handIndex];

        SceneTreeTimer timer = GetTree().CreateTimer(Mathf.Max(0f, delay));
        timer.Timeout += () =>
        {
            if (!IsInstanceValid(card))
                return;

            Vector2 to = card.Position + card.Size * 0.5f;   // 手牌里的最终位置（中心）
            float finalRotation = card.Rotation;
            Vector2 half = card.Size * 0.5f;

            card.PivotOffset = half;
            card.Position = fromScreen - half;
            card.Rotation = 0f;
            card.Scale = Vector2.Zero;
            card.Modulate = new Color(1f, 1f, 1f, 0f);   // 透明度 0 → 1
            card.Visible = true;

            Vector2 from = fromScreen;
            Vector2 dir = (to - from).Normalized();
            Vector2 ctrl = (from + to) * 0.5f + new Vector2(-dir.Y, dir.X) * 120f;

            Tween tw = CreateTween().SetParallel(true);
            tw.TweenMethod(Callable.From<float>(k =>
            {
                if (!IsInstanceValid(card))
                    return;

                float u = 1f - k;
                card.Position = u * u * from + 2f * u * k * ctrl + k * k * to - half;
                card.Scale = Vector2.One * Mathf.Lerp(0f, 1f, Mathf.Min(1f, k * 1.15f));
                card.Modulate = new Color(1f, 1f, 1f, Mathf.Min(1f, k * 1.8f));   // 前 55% 就完全不透明
            }), 0f, 1f, 0.5f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

            tw.TweenProperty(card, "rotation", finalRotation, 0.5f);
        };
    }
    /// <summary>打出的卡飞向弃牌堆/消耗堆。</summary>
    public void PlayToPileAnimation(CardDef def, Rect2 cardRect, bool exhaust, bool playSound = true)
    {
        if (playSound)
            AudioManager.PlayCardDiscarded();

        Vector2 from = cardRect.Position + cardRect.Size * 0.5f;
        Vector2 to = PileCenter(exhaust ? PileView.PileKind.Exhaust : PileView.PileKind.Discard);
        PlayCardToss(def, from, to, 0.4f, 0.22f, 60f,
            onDone: () => BumpPile(exhaust ? PileView.PileKind.Exhaust : PileView.PileKind.Discard));
    }

    /// <summary>洗回牌库：几张卡从弃牌堆飞向抽牌堆。</summary>
    public void PlayReshuffleAnimation(int count = 4)
    {
        AudioManager.PlayCardShuffled();

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
        _endTurnButton.Disabled = _state.Phase != BattlePhase.PlayerTurn || _handPickMode;
        _endTurnButton.Text = _handPickMode
            ? Loc.T("battle.discard_first")
            : _state.Phase == BattlePhase.PlayerTurn
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

    // ─────────────────────────── 建筑出兵面板 ───────────────────────────

    public event Action<string> SpawnClicked;

    /// <summary>
    /// 显示「出兵」面板（右下角）：每个可用兵种一张带预览图的按钮。
    /// options = (卡牌 id, 名称, 价格, 预览图路径, 是否可选/不可选原因)。
    /// </summary>
    public void ShowSpawnPanel(string title,
                               System.Collections.Generic.List<(string CardId, string Name, int Cost, string Icon, bool Ready, string Tip)> options)
    {
        if (_spawnPanel == null)
            return;

        _spawnTitle.Text = title;

        // 选项没变化就只显示，不重建（避免一次点击里刷新两次、按钮叠在一起）
        string signature = title + "|" +
                           string.Join(",", options.ConvertAll(o => $"{o.CardId}:{o.Cost}:{o.Ready}"));
        if (signature == _spawnSignature && _spawnList.GetChildCount() > 0)
        {
            _spawnPanel.Visible = true;
            Callable.From(PlaceSpawnPanel).CallDeferred();
            return;
        }

        _spawnSignature = signature;

        // 立刻把旧按钮从树上摘掉：QueueFree 是延迟的，不摘掉会在同一帧里叠在
        // 新按钮上面吞掉点击 —— 表现就是「出兵选项点不动」
        foreach (Node child in _spawnList.GetChildren())
        {
            _spawnList.RemoveChild(child);
            child.QueueFree();
        }

        _spawnButtonTweens.Clear();

        foreach ((string cardId, string name, int cost, string icon, bool ready, string tip) in options)
        {
            var button = new Button
            {
                CustomMinimumSize = new Vector2(92, 108),
                Disabled = !ready,
                TooltipText = tip,
            };
            button.SetMeta("juice", true);

            var box = new VBoxContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Alignment = BoxContainer.AlignmentMode.Center,
            };
            box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            box.OffsetTop = 12;      // 预览图离按钮上沿留点空隙
            box.OffsetBottom = -6;
            button.AddChild(box);

            var art = new TextureRect
            {
                CustomMinimumSize = new Vector2(0, 52),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Texture = ResourceLoader.Exists(icon) ? GD.Load<Texture2D>(icon) : null,
            };
            box.AddChild(art);

            var label = new Label
            {
                Text = name,
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            label.AddThemeFontSizeOverride("font_size", 14);
            label.AddThemeColorOverride("font_color", UiTheme.Ink);
            box.AddChild(label);

            var price = new Label
            {
                Text = $"{cost}$",
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            price.AddThemeFontSizeOverride("font_size", 14);
            price.AddThemeColorOverride("font_color", new Color("C08A10"));   // 价格始终金黄，禁用状态交给按钮本身
            box.AddChild(price);

            // 悬停放大 / 按下回缩 / 松开回弹（ScrollContainer 会裁剪，幅度控制小一点）
            button.MouseEntered += () => AnimateSpawnButton(button, 1.03f, 0.10f, Tween.TransitionType.Back);
            button.MouseExited += () => AnimateSpawnButton(button, 1f, 0.12f, Tween.TransitionType.Cubic);
            button.ButtonDown += () => AnimateSpawnButton(button, 0.94f, 0.06f, Tween.TransitionType.Sine);
            button.ButtonUp += () => AnimateSpawnButton(button, 1.03f, 0.14f, Tween.TransitionType.Back);

            // 音效：悬停轻响 + 点击确认（与其他 UI 按钮一致）
            button.MouseEntered += () =>
                AudioManager.PlaySfx("res://gameAsset/audios/ui/button_hover.ogg", -7f);

            string id = cardId;
            button.Pressed += () =>
            {
                AudioManager.PlaySfx("res://gameAsset/audios/ui/button_click_enterOrEnable.ogg", -3f);
                SpawnClicked?.Invoke(id);
            };
            _spawnList.AddChild(button);
        }

        _spawnPanel.Visible = true;
        SetSpawnBlocking(true);
        Callable.From(PlaceSpawnPanel).CallDeferred();   // 等一帧布局后再贴位置
    }

    public bool SpawnVisible => _spawnPanel != null && _spawnPanel.Visible;

    /// <summary>当前出兵按钮数量（调试用）。</summary>
    public int SpawnButtonCount() => _spawnList?.GetChildCount() ?? 0;

    /// <summary>第 index 个出兵按钮的屏幕矩形（调试用）。</summary>
    public Rect2 SpawnButtonRect(int index)
    {
        if (_spawnList == null || index < 0 || index >= _spawnList.GetChildCount())
            return new Rect2();

        return (_spawnList.GetChild(index) as Control)?.GetGlobalRect() ?? new Rect2();
    }

    /// <summary>出兵面板的屏幕矩形（调试用）。</summary>
    public Rect2 SpawnPanelRect() => _spawnPanel == null ? new Rect2() : _spawnPanel.GetGlobalRect();

    /// <summary>出兵面板在屏幕上的中心点（调试用）。</summary>
    public Vector2 SpawnPanelGlobalCenter() =>
        _spawnPanel == null ? Vector2.Zero : _spawnPanel.GlobalPosition + _spawnPanel.Size * 0.5f;

    /// <summary>把出兵面板贴到信息面板正下方（两者挨在一起）。</summary>
    private void PlaceSpawnPanel()
    {
        if (_spawnPanel == null || _infoPanel == null)
            return;

        const float SpawnPanelHeight = 192f;
        // 信息面板是「顶部右对齐」锚点，Size.Y 会被拉伸，用内容最小高度更准
        float top = _infoPanel.Position.Y + _infoPanel.GetCombinedMinimumSize().Y + 8f;

        // 屏幕放不下就贴到底部上方，别跑到屏幕外
        float maxTop = GetViewport().GetVisibleRect().Size.Y - SpawnPanelHeight - 16f;
        top = Mathf.Clamp(top, 72f, Mathf.Max(72f, maxTop));

        _spawnPanel.OffsetTop = top;
        _spawnPanel.OffsetBottom = top + SpawnPanelHeight;
    }

    public void HideSpawnPanel()
    {
        if (_spawnPanel != null)
            _spawnPanel.Visible = false;

        SetSpawnBlocking(false);
    }

    /// <summary>出兵面板打开时，把下面的手牌 / 牌堆控件设为不吃鼠标，避免点击穿透。</summary>
    private void SetSpawnBlocking(bool blocked)
    {
        var filter = blocked ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Pass;

        if (_hand != null)
            SetTreeMouseFilter(_hand, filter);

        if (_deckPile != null)
            SetTreeMouseFilter(_deckPile, filter);

        if (_discardPile != null)
            SetTreeMouseFilter(_discardPile, filter);

        if (_exhaustPile != null)
            SetTreeMouseFilter(_exhaustPile, filter);
    }

    private readonly System.Collections.Generic.Dictionary<Control, Tween> _spawnButtonTweens = new();

    /// <summary>出兵按钮的缩放反馈（轴心在按钮中心）。</summary>
    private void AnimateSpawnButton(Control button, float scale, float duration, Tween.TransitionType trans)
    {
        if (button == null || !IsInstanceValid(button) || button.Size.X <= 1f)
            return;

        if (_spawnButtonTweens.TryGetValue(button, out Tween old) && old != null && old.IsValid())
            old.Kill();

        button.PivotOffset = button.Size * 0.5f;

        Tween tween = CreateTween();
        _spawnButtonTweens[button] = tween;
        tween.TweenProperty(button, "scale", Vector2.One * scale, duration)
            .SetTrans(trans).SetEase(Tween.EaseType.Out);
    }

    private static void SetTreeMouseFilter(Node node, Control.MouseFilterEnum filter)
    {
        if (node is Control c)
            c.MouseFilter = filter;

        foreach (Node child in node.GetChildren())
            SetTreeMouseFilter(child, filter);
    }

    private void BuildSpawnPanel()
    {
        _spawnPanel = new PanelContainer
        {
            Name = "SpawnPanel",
            Visible = false,
            CustomMinimumSize = new Vector2(320, 0),
            ZIndex = 40,          // 盖住右下角的牌堆图标 / 手牌
            MouseFilter = Control.MouseFilterEnum.Stop,   // 挡住下面的地图/手牌点击
        };
        _spawnPanel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _spawnPanel.GuiInput += e =>
        {
            if (e is InputEventMouseButton)
                _spawnPanel.AcceptEvent();
        };
        // 挂在信息面板正下方（位置在 ShowSpawnPanel 里按信息面板高度动态计算）
        _spawnPanel.OffsetLeft = -346;
        _spawnPanel.OffsetTop = 320;
        _spawnPanel.OffsetRight = -16;
        _spawnPanel.OffsetBottom = 496;
        _root.AddChild(_spawnPanel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        _spawnPanel.AddChild(box);

        _spawnTitle = new Label { Text = Loc.T("spawn.title") };
        _spawnTitle.AddThemeFontSizeOverride("font_size", 18);
        _spawnTitle.AddThemeColorOverride("font_color", UiTheme.Ink);
        box.AddChild(_spawnTitle);

        // 一行 3 个，多了纵向滚动
        var scroll = new ScrollContainer
        {
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        box.AddChild(scroll);

        _spawnList = new GridContainer
        {
            Columns = 3,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _spawnList.AddThemeConstantOverride("h_separation", 4);
        _spawnList.AddThemeConstantOverride("v_separation", 4);
        scroll.AddChild(_spawnList);
    }
    // ─────────────────────────── 收入飘字 ───────────────────────────

    /// <summary>在某建筑上方弹出「+N$」金色飘字：缩放 0→1 同时淡入，停留后 1→0 淡出。</summary>
    public void ShowIncomePopup(Vector2 screenPos, int amount, float delay = 0f, float hold = 0.9f)
    {
        if (_root == null || amount <= 0)
            return;

        var holder = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Size = new Vector2(190, 48),
            PivotOffset = new Vector2(95, 24),
            Position = screenPos - new Vector2(95, 24),
        };
        _root.AddChild(holder);

        var label = new Label
        {
            Text = $"+{amount}",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeFontSizeOverride("font_size", 24);
        label.AddThemeColorOverride("font_color", new Color("FFD54A"));
        label.AddThemeConstantOverride("outline_size", 5);
        label.AddThemeColorOverride("font_outline_color", new Color("3B2E16"));
        holder.AddChild(label);

        holder.Scale = Vector2.Zero;
        holder.Modulate = new Color(1f, 1f, 1f, 0f);

        Tween tween = CreateTween();
        if (delay > 0f)
            tween.TweenInterval(delay);

        // 冒出：缩放 0→1 与淡入同时进行
        tween.TweenProperty(holder, "scale", Vector2.One, 0.18f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tween.Parallel().TweenProperty(holder, "modulate:a", 1f, 0.16f);

        // 停留
        tween.TweenInterval(hold);

        // 消失：缩放 1→0 与淡出同时进行
        tween.TweenProperty(holder, "scale", Vector2.Zero, 0.35f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        tween.Parallel().TweenProperty(holder, "modulate:a", 0f, 0.35f);

        tween.TweenCallback(Callable.From(() =>
        {
            if (IsInstanceValid(holder))
                holder.QueueFree();
        }));
    }
    // ─────────────────────────── 地形检视 ───────────────────────────

    /// <summary>左键点中任意格子时显示：格子预览图 + 地形效果描述（左上角）。</summary>
    public void ShowTerrain(TerrainType terrain)
    {
        if (_terrainPanel == null)
            return;

        _terrainPanel.Visible = true;
        _terrainTitle.Text = TerrainName(terrain);
        _terrainBody.Text = TerrainDesc(terrain);

        string path = "res://gameAsset/textures/hexTilePreview/preview_" + TerrainKey(terrain) + ".png";
        _terrainImage.Texture = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    public void HideTerrain()
    {
        if (_terrainPanel != null)
            _terrainPanel.Visible = false;
    }

    public bool TerrainVisible => _terrainPanel != null && _terrainPanel.Visible;

    private static string TerrainKey(TerrainType t) => t switch
    {
        TerrainType.Grass => "grass",
        TerrainType.Sand => "sand",
        TerrainType.Rock => "rock",
        TerrainType.Snow => "snow",
        TerrainType.Water => "water",
        _ => "dirt",
    };

    private static string TerrainName(TerrainType t) => Loc.T("terrain." + TerrainKey(t));

    private static string TerrainDesc(TerrainType t) => Loc.T("terrain." + TerrainKey(t) + ".desc");

    private void BuildTerrainPanel()
    {
        _terrainPanel = new PanelContainer
        {
            Name = "TerrainPanel",
            Visible = false,
            CustomMinimumSize = new Vector2(238, 0),
        };
        _terrainPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        _terrainPanel.OffsetLeft = 16;
        _terrainPanel.OffsetTop = 78;
        _terrainPanel.OffsetRight = 254;
        _root.AddChild(_terrainPanel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        _terrainPanel.AddChild(box);

        _terrainTitle = new Label { Text = "" };
        _terrainTitle.AddThemeFontSizeOverride("font_size", 20);
        _terrainTitle.AddThemeColorOverride("font_color", UiTheme.Ink);
        box.AddChild(_terrainTitle);

        _terrainImage = new TextureRect
        {
            CustomMinimumSize = new Vector2(0, 150),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        box.AddChild(_terrainImage);

        _terrainBody = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _terrainBody.AddThemeFontSizeOverride("font_size", 15);
        _terrainBody.AddThemeColorOverride("font_color", UiTheme.Ink);
        box.AddChild(_terrainBody);

        var hint = new Label
        {
            Text = Loc.T("terrain.hint"),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hint.AddThemeFontSizeOverride("font_size", 13);
        hint.AddThemeColorOverride("font_color", new Color(UiTheme.Grey, 0.9f));
        box.AddChild(hint);
    }
    /// <summary>进场遮罩：挡住从上一场景带过来的按下/松开，0.36s 内淡出。</summary>
    public void ShowTransitionMask()
    {
        if (_root == null)
            return;

        var mask = new ColorRect
        {
            Name = "TransitionMask",
            Color = new Color(0.05f, 0.04f, 0.02f, 0.5f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 90,
        };
        mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(mask);

        Tween tween = CreateTween();
        tween.TweenInterval(0.12f);
        tween.TweenProperty(mask, "color:a", 0f, 0.24f);
        tween.TweenCallback(Callable.From(() =>
        {
            if (IsInstanceValid(mask))
                mask.QueueFree();
        }));
    }
    public void SetSelectedCard(int index)
    {
        _selectedCard = index;
        _hand.SetSelected(index);
    }

    /// <summary>
    /// 「选一张手牌弃掉」阶段：整排手牌泛绿并轻轻起伏，
    /// 同时压暗 3D 场景（不遮 UI）并显示一条持续的大号提示。
    /// </summary>
    public void SetHandPickMode(bool on)
    {
        _handPickMode = on;
        _hand.Modulate = on ? new Color(0.82f, 1f, 0.86f) : Colors.White;

        if (on)
        {
            _hand.PulseHint();
            ShowPickHint();
        }
        else
        {
            HidePickHint();
        }

        // 选牌期间「回合结束」不可点
        if (_state != null)
            RefreshTop();
    }

    private void ShowPickHint()
    {
        if (_pickDim != null)
        {
            _pickDim.Visible = true;
            Tween tween = CreateTween();
            tween.TweenProperty(_pickDim, "color:a", 0.45f, 0.18f);
        }

        if (_pickBanner == null)
            return;

        _pickBanner.Text = Loc.T("battle.pick_discard");
        _pickBanner.Visible = true;
        _pickBanner.Modulate = new Color(1f, 1f, 1f, 1f);
        _pickBanner.Scale = Vector2.One;
        _pickBanner.PivotOffset = _pickBanner.Size * 0.5f;
    }

    private void HidePickHint()
    {
        if (_pickDim != null && _pickDim.Visible)
        {
            Tween tween = CreateTween();
            tween.TweenProperty(_pickDim, "color:a", 0f, 0.15f);
            tween.TweenCallback(Callable.From(() =>
            {
                if (IsInstanceValid(_pickDim))
                    _pickDim.Visible = false;
            }));
        }

        if (_pickBanner != null)
            _pickBanner.Visible = false;
    }

    private bool _handPickMode;
    private ColorRect _pickDim;
    private Label _pickBanner;

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

    /// <summary>调试：手牌节点数与可见数。</summary>
    public int HandCardCount() => _hand.Cards.Count;

    public int VisibleHandCards()
    {
        int n = 0;
        foreach (CardView c in _hand.Cards)
            if (c.Visible)
                n++;
        return n;
    }
    /// <summary>调试：当前产卡开关按钮的文案。</summary>
    public string DebugProduceText() => (_produceName?.Text ?? "") + (_produceState?.Text ?? "");

    public void ShowInfo(string title, string body, string imagePath = null, bool showButtons = false,
        int extraFireCost = 0, bool extraFireReady = false, bool showDemolish = true,
        bool canUpgrade = true, int repairCost = 0, bool repairAffordable = true,
        int demolishRefund = 0, bool showProduce = false, bool produceOn = true)
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

        if (_produceBtn != null)
        {
            _produceBtn.Visible = showButtons && showProduce;
            _produceBtn.ButtonPressed = produceOn;

            if (_produceName != null)
                _produceName.Text = Loc.T("info.produce_name");

            if (_produceState != null)
            {
                _produceState.Text = produceOn ? Loc.T("info.produce_on_word") : Loc.T("info.produce_off_word");
                _produceState.AddThemeColorOverride("font_color",
                    produceOn ? new Color("2E7D32") : new Color("C0392B"));
            }
        }

        if (_demolishBtn != null)
        {
            _demolishBtn.Visible = showButtons && showDemolish;

            // 名称保持墨色，金额单独用亮金色
            if (_demolishGold != null)
            {
                _demolishName.Text = "拆除";
                _demolishGold.Text = demolishRefund > 0 ? $"  +{demolishRefund}$" : "";
            }
        }

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
