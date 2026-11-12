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
    public event Action DemolishClicked;
    public event Action ExtraFireClicked;

    /// <summary>鼠标悬停建造项 / 升级按钮时触发，用于预览下一级外观。</summary>
    public event Action<string> BuildHovered;
    public event Action PreviewUnhovered;
    public event Action UpgradeHovered;
    public event Action CancelClicked;

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
    private PanelContainer _buildMenu;
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

        var infoButtons = new HBoxContainer();
        infoButtons.AddThemeConstantOverride("separation", 8);
        infoBox.AddChild(infoButtons);

        _upgradeBtn = NewButton("升级");
        _upgradeBtn.Pressed += () => UpgradeClicked?.Invoke();
        _upgradeBtn.MouseEntered += () => UpgradeHovered?.Invoke();
        _upgradeBtn.MouseExited += () => PreviewUnhovered?.Invoke();
        infoButtons.AddChild(_upgradeBtn);

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

        // ── 回合结束 ──
        _endTurnButton = NewButton("回合结束");
        _endTurnButton.Name = "EndTurn";
        _endTurnButton.CustomMinimumSize = new Vector2(196, 56);
        _endTurnButton.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        _endTurnButton.OffsetLeft = -220;
        _endTurnButton.OffsetTop = -76;
        _endTurnButton.OffsetRight = -24;
        _endTurnButton.OffsetBottom = -20;
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

    /// <summary>血条层：设置相机与每帧的数据。</summary>
    public void SetHealthCamera(Camera3D camera) => _bars?.SetCamera(camera);

    public void SetHealthBars(List<HealthBars.Bar> bars) => _bars?.SetBars(bars);

    /// <summary>调试：血条当前显示值 / 残影值。</summary>
    public string DebugHealth(bool building, int id) => _bars?.DebugShown(building, id) ?? "n/a";

    public void ShowInfo(string title, string body, string imagePath = null, bool showButtons = false,
        int extraFireCost = 0, bool extraFireReady = false, bool showDemolish = true,
        bool canUpgrade = true)
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
                    if (sub is HBoxContainer h)
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
