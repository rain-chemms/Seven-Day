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
    private TextureRect _preview;

    public override void _Ready()
    {
        Loc.EnsureLoaded();
        GameDatabase.EnsureLoaded();

        Theme = UiTheme.Get();
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var bg = new ColorRect { Color = UiTheme.Paper };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.OffsetLeft = -420;
        panel.OffsetRight = 420;
        panel.OffsetTop = -260;
        panel.OffsetBottom = 260;
        AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        panel.AddChild(box);

        var title = new Label { Text = Loc.T("mapselect.title") };
        title.AddThemeFontSizeOverride("font_size", 30);
        box.AddChild(title);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        box.AddChild(row);

        // 左：地图预览 + 说明
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(left);

        _preview = new TextureRect
        {
            CustomMinimumSize = new Vector2(380, 240),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = GD.Load<Texture2D>("res://gameAsset/textures/buildingsPreview/preview_hq.png"),
        };
        left.AddChild(_preview);

        _mapInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _mapInfo.AddThemeFontSizeOverride("font_size", 15);
        left.AddChild(_mapInfo);

        // 右：地图下拉 + 难度
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        row.AddChild(right);

        _mapOption = new OptionButton();
        foreach (MapDef m in GameDatabase.Maps.Values)
        {
            _maps.Add(m);
            _mapOption.AddItem(Loc.Name(m.NameZh, m.NameEn), _maps.Count - 1);
        }

        if (_maps.Count == 0)
            _mapOption.AddItem("map_01", 0);

        _mapOption.ItemSelected += OnMapSelected;
        right.AddChild(_mapOption);

        right.AddChild(new Label { Text = Loc.T("mapselect.difficulty") });

        var diffGrid = new GridContainer { Columns = 2 };
        diffGrid.AddThemeConstantOverride("h_separation", 8);
        diffGrid.AddThemeConstantOverride("v_separation", 8);
        right.AddChild(diffGrid);

        AddDiff(diffGrid, AIDifficulty.Easy, "diff.easy");
        AddDiff(diffGrid, AIDifficulty.Normal, "diff.normal");
        AddDiff(diffGrid, AIDifficulty.Hard, "diff.hard");
        AddDiff(diffGrid, AIDifficulty.Lunatic, "diff.lunatic");
        SelectDiff(Session.Difficulty);

        right.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        var start = new Button { Text = Loc.T("mapselect.start"), CustomMinimumSize = new Vector2(0, 54) };
        start.AddThemeFontSizeOverride("font_size", 22);
        start.Pressed += StartBattle;
        right.AddChild(start);

        var back = new Button { Text = Loc.T("mapselect.back"), CustomMinimumSize = new Vector2(0, 44) };
        back.Pressed += () =>
        {
            if (_leaving)
                return;

            _leaving = true;
            GetTree().ChangeSceneToFile("res://scenes/mainMenu.tscn");
        };
        right.AddChild(back);

        OnMapSelected(0);
        ButtonJuice.AttachAll(this);
        UiSound.AttachAll(this);
    }

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
        _mapInfo.Text = $"{m.Width} × {m.Height} 格　起始金币 {m.StartGold}$\n" +
                        $"双方指挥部距离 {HexCoord.Distance(new HexCoord(m.PlayerHq[0], m.PlayerHq[1]), new HexCoord(m.EnemyHq[0], m.EnemyHq[1]))} 格";
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
