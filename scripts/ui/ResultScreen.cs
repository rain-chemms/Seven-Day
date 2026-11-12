using System;
using System.Collections.Generic;
using Godot;
using SevenDay.Core;

namespace SevenDay.UI;

/// <summary>结算画面：胜负、回合数、双方指挥部血量、历史战绩。</summary>
public partial class ResultScreen : CanvasLayer
{
    public event Action RematchClicked;
    public event Action MenuClicked;

    private Control _root;
    private PanelContainer _panel;
    private Label _title;
    private Label _subtitle;
    private VBoxContainer _historyBox;

    public override void _Ready()
    {
        Layer = 20;

        _root = new Control { MouseFilter = Control.MouseFilterEnum.Stop, Theme = UiTheme.Get() };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        var dim = new ColorRect { Color = new Color(0.05f, 0.04f, 0.02f, 0.55f) };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(dim);

        _panel = new PanelContainer();
        _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panel.OffsetLeft = -330;
        _panel.OffsetRight = 330;
        _panel.OffsetTop = -220;
        _panel.OffsetBottom = 220;
        _root.AddChild(_panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        _panel.AddChild(box);

        _title = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 44);
        box.AddChild(_title);

        _subtitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _subtitle.AddThemeFontSizeOverride("font_size", 18);
        _subtitle.AddThemeColorOverride("font_color", UiTheme.SandDark);
        box.AddChild(_subtitle);

        box.AddChild(new HSeparator());

        var historyTitle = new Label { Text = Loc.T("gallery.history") };
        historyTitle.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(historyTitle);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 180), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        box.AddChild(scroll);

        _historyBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _historyBox.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_historyBox);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        box.AddChild(row);

        var rematch = new Button { Text = Loc.T("result.rematch"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 52) };
        rematch.Pressed += () => RematchClicked?.Invoke();
        row.AddChild(rematch);

        var menu = new Button { Text = Loc.T("result.menu"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 52) };
        menu.Pressed += () => MenuClicked?.Invoke();
        row.AddChild(menu);

        ButtonJuice.AttachAll(_root);
    }

    public void Show(bool win, int turns, string mapName, int difficulty, int playerHqHp, int enemyHqHp)
    {
        _title.Text = win ? Loc.T("battle.victory") : Loc.T("battle.defeat");
        _title.AddThemeColorOverride("font_color", win ? UiTheme.Green : UiTheme.Red);

        _subtitle.Text = $"{mapName}　{MatchHistory.DifficultyName(difficulty)}　{turns} 回合\n" +
                         $"我方指挥部 {Mathf.Max(0, playerHqHp)}　敌方指挥部 {Mathf.Max(0, enemyHqHp)}";

        foreach (Node child in _historyBox.GetChildren())
            child.QueueFree();

        MatchHistory.EnsureLoaded();
        int shown = 0;
        foreach (MatchRecord r in MatchHistory.Recent(6))
        {
            var l = new Label { Text = MatchHistory.Summarize(r) };
            l.AddThemeFontSizeOverride("font_size", 15);
            l.AddThemeColorOverride("font_color", r.Win ? UiTheme.Ink : UiTheme.Grey);
            _historyBox.AddChild(l);
            shown++;
        }

        if (shown == 0)
            _historyBox.AddChild(new Label { Text = Loc.T("result.nohistory") });
    }
}
