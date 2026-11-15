using System.Collections.Generic;
using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>地图与难度选择。选定后进入战斗场景。</summary>
public partial class MapSelect : Control
{
    private OptionButton _mapOption;
    private readonly List<MapDef> _maps = new();
    private readonly List<(Button Button, AIDifficulty Diff)> _diffButtons = new();
    private Label _mapInfo;
    private Label _mapDesc;


    public override void _Ready()
    {
        Loc.EnsureLoaded();
        GameDatabase.EnsureLoaded();
        DisplaySettings.ApplyFromSettings();   // 直接跑地图选择（调试）时也应用窗口设置

        Theme = UiTheme.Get();
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var bg = new ColorRect { Color = UiTheme.Paper };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.OffsetLeft = -420;
        panel.OffsetRight = 420;
        panel.OffsetTop = -215;
        panel.OffsetBottom = 215;
        AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        panel.AddChild(box);

        _titleLabel = new Label { Text = Loc.T("mapselect.title") };
        var title = _titleLabel;
        title.AddThemeFontSizeOverride("font_size", 30);
        box.AddChild(title);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        box.AddChild(row);

        // 左：地图预览 + 说明
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(left);

        // 初始基地等级：三张 HQ 图各作一个按钮（悬停/按下动画与音效由 ButtonJuice / UiSound 统一处理）
        _hqLevelLabel = new Label { Text = Loc.T("mapselect.hq_level") };
        var hqLabel = _hqLevelLabel;
        hqLabel.AddThemeFontSizeOverride("font_size", 16);
        left.AddChild(hqLabel);

        var hqRow = new HBoxContainer();
        hqRow.AddThemeConstantOverride("separation", 10);
        left.AddChild(hqRow);

        var hqSheet = GD.Load<Texture2D>("res://gameAsset/textures/buildingsPreview/preview_hq.png");
        int sheetW = hqSheet != null ? hqSheet.GetWidth() / 3 : 0;

        for (int lv = 1; lv <= 3; lv++)
        {
            int level = lv;
            var btn = new Button
            {
                CustomMinimumSize = new Vector2(120, 104),
                ToggleMode = true,
                TextureFilter = TextureFilterEnum.Linear,
            };

            var art = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
                Texture = new AtlasTexture
                {
                    Atlas = hqSheet,
                    Region = new Rect2((lv - 1) * sheetW, 0, sheetW, hqSheet?.GetHeight() ?? 0),
                },
            };
            art.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            art.OffsetTop = 6;
            art.OffsetBottom = -26;
            art.OffsetLeft = 6;
            art.OffsetRight = -6;
            btn.AddChild(art);

            var name = new Label
            {
                Text = $"Lv{lv}",
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            name.SetAnchorsPreset(LayoutPreset.BottomWide);
            name.OffsetTop = -24;
            name.OffsetBottom = -2;
            name.AddThemeFontSizeOverride("font_size", 15);
            btn.AddChild(name);

            btn.Pressed += () => SelectHqLevel(level, _hqButtons);
            hqRow.AddChild(btn);
            _hqButtons.Add(btn);
        }

        _hqHint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _hqHint.AddThemeFontSizeOverride("font_size", 14);
        _hqHint.AddThemeColorOverride("font_color", UiTheme.SandDark);
        left.AddChild(_hqHint);

        SelectHqLevel(Session.StartHqLevel, _hqButtons);

        _mapInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _mapInfo.AddThemeFontSizeOverride("font_size", 15);
        left.AddChild(_mapInfo);

        // 地图简介（占满左侧剩余空间，随语言切换）
        _mapDesc = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _mapDesc.AddThemeFontSizeOverride("font_size", 14);
        _mapDesc.AddThemeColorOverride("font_color", UiTheme.SandDark);
        _mapDesc.CustomMinimumSize = new Vector2(0, 104);
        _mapDesc.VerticalAlignment = VerticalAlignment.Top;
        left.AddChild(_mapDesc);

        // 右：地图下拉 + 难度
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(340, 0) };
        right.AddThemeConstantOverride("separation", 14);
        row.AddChild(right);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--map-list") >= 0)
            GD.Print("[Maps] " + string.Join(",", GameDatabase.Maps.Keys));

        _mapOption = new OptionButton();
        foreach (MapDef m in GameDatabase.Maps.Values)
        {
            _maps.Add(m);
            _mapOption.AddItem(Loc.Name(m.NameZh, m.NameEn), _maps.Count - 1);
        }

        if (_maps.Count == 0)
            _mapOption.AddItem("map_01", 0);

        _mapOption.ItemSelected += OnMapSelected;
        _mapOption.CustomMinimumSize = new Vector2(0, 42);
        right.AddChild(_mapOption);

        right.AddChild(SectionLine());

        _diffLabel = new Label { Text = Loc.T("mapselect.difficulty") };
        right.AddChild(_diffLabel);

        var diffGrid = new GridContainer { Columns = 2 };
        diffGrid.AddThemeConstantOverride("h_separation", 8);
        diffGrid.AddThemeConstantOverride("v_separation", 8);
        right.AddChild(diffGrid);

        AddDiff(diffGrid, AIDifficulty.Easy, "diff.easy");
        AddDiff(diffGrid, AIDifficulty.Normal, "diff.normal");
        AddDiff(diffGrid, AIDifficulty.Hard, "diff.hard");
        AddDiff(diffGrid, AIDifficulty.Lunatic, "diff.lunatic");
        SelectDiff(Session.Difficulty);

        right.AddChild(SectionLine());

        _startButton = new Button { Text = Loc.T("mapselect.start"), CustomMinimumSize = new Vector2(0, 54) };
        Button start = _startButton;
        start.AddThemeFontSizeOverride("font_size", 22);
        start.Pressed += StartBattle;
        right.AddChild(start);

        _backButton = new Button { Text = Loc.T("mapselect.back"), CustomMinimumSize = new Vector2(0, 44) };
        Button back = _backButton;
        back.Pressed += () =>
        {
            if (_leaving)
                return;

            _leaving = true;
            GetTree().ChangeSceneToFile("res://scenes/mainMenu.tscn");
        };
        right.AddChild(back);

        OnMapSelected(0);
        Loc.Changed += RefreshTexts;
        TreeExiting += () => Loc.Changed -= RefreshTexts;

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--demo-mapselect") >= 0)
        {
            var t2 = GetTree().CreateTimer(0.5);
            t2.Timeout += () =>
            {
                if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--mapselect-second") >= 0 && _maps.Count > 1)
                {
                    _mapOption.Selected = 1;
                    OnMapSelected(1);
                    GD.Print($"[MapSelect] 切到第二张图：{Loc.Name(_maps[1].NameZh, _maps[1].NameEn)}");
                }

                GD.Print($"[MapSelect] 基地等级={Session.StartHqLevel} 按钮数={_hqButtons.Count}");

                Vector2 center = _hqButtons[2].GlobalPosition + _hqButtons[2].Size * 0.5f;
                Input.WarpMouse(center);
                Input.ParseInputEvent(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    Pressed = true,
                    Position = center,
                    GlobalPosition = center,
                });
                Input.ParseInputEvent(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    Pressed = false,
                    Position = center,
                    GlobalPosition = center,
                });
                var t3 = GetTree().CreateTimer(0.25);
                t3.Timeout += () => GD.Print($"[MapSelect] 点击 Lv3 后 等级={Session.StartHqLevel}");
            };
        }

        ButtonJuice.AttachAll(this);
        UiSound.AttachAll(this);
    }

    private readonly List<Button> _hqButtons = new();
    private Label _hqHint;
    private Label _titleLabel;
    private Label _hqLevelLabel;
    private Label _diffLabel;
    private Button _startButton;
    private Button _backButton;

    /// <summary>选择初始基地等级：选中态靠 ToggleMode 的按下样式 + 半透明未选中项。</summary>
    private void SelectHqLevel(int level, List<Button> buttons)
    {
        Session.StartHqLevel = level;

        for (int i = 0; i < buttons.Count; i++)
        {
            Button b = buttons[i];
            if (!IsInstanceValid(b))
                continue;

            b.ButtonPressed = i + 1 == level;
            b.Modulate = i + 1 == level ? Colors.White : new Color(1f, 1f, 1f, 0.72f);
        }

        Data.BuildingDef hq = Data.GameDatabase.Building("hq");
        int hp = hq?.HpAt(level) ?? 0;
        int draw = level >= 2 ? 2 : 1;

        if (_hqHint != null)
            _hqHint.Text = Loc.T("mapselect.hq_desc", level, hp, draw);
    }
    /// <summary>右侧分区的细横线。</summary>
    private static Control SectionLine()
    {
        var wrap = new MarginContainer();
        wrap.AddThemeConstantOverride("margin_top", 2);
        wrap.AddThemeConstantOverride("margin_bottom", 2);

        var line = new ColorRect
        {
            Color = new Color(0.72f, 0.62f, 0.38f, 0.55f),
            CustomMinimumSize = new Vector2(0, 2),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        wrap.AddChild(line);
        return wrap;
    }
    private MapDef _currentMap;

    /// <summary>地图信息（尺寸 / 起始金币 / 两基地距离），跟随语言。</summary>
    private void UpdateMapInfo()
    {
        if (_currentMap == null || _mapInfo == null)
            return;

        MapDef m = _currentMap;
        int dist = HexCoord.Distance(new HexCoord(m.PlayerHq[0], m.PlayerHq[1]),
                                     new HexCoord(m.EnemyHq[0], m.EnemyHq[1]));

        _mapInfo.Text = Loc.T("mapselect.size", m.Width, m.Height) + "　" +
                        Loc.T("mapselect.gold", m.StartGold) + "\n" +
                        Loc.T("mapselect.hq_dist", dist);

        if (_mapDesc != null)
            _mapDesc.Text = Loc.Name(m.DescZh, m.DescEn);
    }

    /// <summary>切换语言后刷新本界面所有文本。</summary>
    private void RefreshTexts()
    {
        if (_titleLabel != null)
            _titleLabel.Text = Loc.T("mapselect.title");
        if (_hqLevelLabel != null)
            _hqLevelLabel.Text = Loc.T("mapselect.hq_level");
        if (_diffLabel != null)
            _diffLabel.Text = Loc.T("mapselect.difficulty");
        if (_startButton != null)
            _startButton.Text = Loc.T("mapselect.start");
        if (_backButton != null)
            _backButton.Text = Loc.T("mapselect.back");

        for (int i = 0; i < _diffButtons.Count; i++)
        {
            (Button b, AIDifficulty d) = _diffButtons[i];
            if (IsInstanceValid(b))
                b.Text = Loc.T(DiffKey(d));
        }

        UpdateMapInfo();
        SelectHqLevel(Session.StartHqLevel, _hqButtons);
    }

    private static string DiffKey(AIDifficulty d) => d switch
    {
        AIDifficulty.Easy => "diff.easy",
        AIDifficulty.Hard => "diff.hard",
        AIDifficulty.Lunatic => "diff.lunatic",
        _ => "diff.normal",
    };
    private void AddDiff(GridContainer grid, AIDifficulty diff, string key)
    {
        var b = new Button { Text = Loc.T(key), ToggleMode = true, CustomMinimumSize = new Vector2(148, 44) };
        b.Pressed += () => SelectDiff(diff);
        grid.AddChild(b);
        _diffButtons.Add((b, diff));
    }

    private void SelectDiff(AIDifficulty diff)
    {
        Session.Difficulty = diff;
        foreach ((Button b, AIDifficulty d) in _diffButtons)
            b.ButtonPressed = d == diff;
    }

    private void OnMapSelected(long index)
    {
        if (_maps.Count == 0)
            return;
        MapDef m = _maps[Mathf.Clamp((int)index, 0, _maps.Count - 1)];
        Session.MapId = m.Id;
        _currentMap = m;
        UpdateMapInfo();
    }

    /// <summary>已经在切场景了：防止同一次点击触发两次跳转。</summary>
    private bool _leaving;

    private void StartBattle()
    {
        if (_leaving)
            return;

        _leaving = true;
        GetTree().ChangeSceneToFile("res://scenes/battleScene.tscn");
    }
}
