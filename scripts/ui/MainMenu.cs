using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Core;

namespace SevenDay.UI;

/// <summary>
/// 主菜单：3D 步入场景（镜头缓慢移动）+ 左上标题卡 + 右侧竖排按钮。
/// 版式来自 设计草案/战斗UI参考-1，设置面板来自 战斗UI参考-2。
/// </summary>
public partial class MainMenu : Node3D
{
    private CanvasLayer _ui;
    private Control _root;
    private Label _toast;
    private PanelContainer _settingsPanel;
    private PanelContainer _galleryPanel;
    private TabContainer _tabs;
    private float _toastTimer;

    private readonly List<(string Key, Action OnChanged)> _locBindings = new();

    public override void _Ready()
    {
        Loc.EnsureLoaded();
        AudioBuses.Ensure();

        AddChild(new MainMenuBackdrop { Name = "Backdrop" });
        BuildUi();
        ApplyAudioFromSettings();
        AudioManager.PlayBgm("res://gameAsset/audios/bgm/menu_theme.ogg", 1.2f);
        Loc.Changed += RebuildTexts;

        // 开发调试：--demo-button 模拟悬停第一个按钮、按下第二个按钮，检查动效
        if (OS.GetCmdlineUserArgs().Contains("--demo-button"))
            CallDeferred(nameof(DemoButtonFlow));

        // 开发调试：--demo-settings 打开设置面板并切到摄像机页
        if (OS.GetCmdlineUserArgs().Contains("--demo-settings"))
            CallDeferred(nameof(DemoSettings));
    }

    private void DemoSettings()
    {
        _settingsPanel.Visible = true;
        _tabs.CurrentTab = 2;
    }

    private async void DemoButtonFlow()
    {
        List<Button> buttons = _root.FindChildren("*", "Button", true, false).Cast<Button>().ToList();
        if (buttons.Count < 2)
            return;

        buttons[0].EmitSignal(Control.SignalName.MouseEntered);
        buttons[1].EmitSignal(BaseButton.SignalName.ButtonDown);

        var timer = GetTree().CreateTimer(1.0);
        timer.Timeout += () => GD.Print(
            $"[ButtonJuice] hover scale={ButtonJuice.DebugScale(buttons[0])} " +
            $"press scale={ButtonJuice.DebugScale(buttons[1])} " +
            $"hoverZ={buttons[0].ZIndex} rot0={Mathf.RadToDeg(buttons[0].Rotation):F1}deg");

        // 松手后连续采样旋转，确认倾斜是平滑走完再回正，而不是被半路打断
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        buttons[1].EmitSignal(BaseButton.SignalName.ButtonUp);

        for (int i = 0; i < 8; i++)
        {
            await ToSignal(GetTree().CreateTimer(0.04), SceneTreeTimer.SignalName.Timeout);
            GD.Print($"[ButtonJuice] t={i * 0.04 + 0.04:F2}s rot={Mathf.RadToDeg(buttons[1].Rotation):F2}deg " +
                     $"scale={ButtonJuice.DebugScale(buttons[1]).X:F3}");
        }
    }

    public override void _ExitTree()
    {
        Loc.Changed -= RebuildTexts;
    }

    // ─────────────────────────── UI ───────────────────────────

    private void BuildUi()
    {
        _ui = new CanvasLayer { Name = "MenuUi" };
        AddChild(_ui);

        _root = new Control
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Pass,
            Theme = UiTheme.Get(),
        };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _ui.AddChild(_root);

        BuildTitleCard();
        BuildMenuButtons();
        BuildToast();
        BuildSettingsPanel();
        BuildGalleryPanel();
        ButtonJuice.AttachAll(_root);
        RebuildTexts();
    }

    private void BuildTitleCard()
    {
        var panel = new PanelContainer { Name = "TitleCard" };
        panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        panel.OffsetLeft = 48;
        panel.OffsetTop = 40;
        panel.OffsetRight = 448;
        panel.OffsetBottom = 200;
        _root.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        panel.AddChild(box);

        var title = new Label { Name = "Title", Text = Loc.T("game.title") };
        title.AddThemeFontSizeOverride("font_size", 46);
        title.AddThemeColorOverride("font_color", UiTheme.Ink);
        box.AddChild(title);
        _locBindings.Add(("game.title", () => title.Text = Loc.T("game.title")));

        var tagline = new Label { Name = "Tagline", Text = Loc.T("game.tagline") };
        tagline.AddThemeFontSizeOverride("font_size", 20);
        tagline.AddThemeColorOverride("font_color", UiTheme.SandDark);
        box.AddChild(tagline);
        _locBindings.Add(("game.tagline", () => tagline.Text = Loc.T("game.tagline")));

        var author = new Label { Name = "Author", Text = Loc.T("game.author") };
        author.AddThemeFontSizeOverride("font_size", 15);
        author.AddThemeColorOverride("font_color", UiTheme.Grey);
        box.AddChild(author);
        _locBindings.Add(("game.author", () => author.Text = Loc.T("game.author")));

        var hint = new Label { Name = "Hint", Text = Loc.T("menu.hint") };
        hint.AddThemeFontSizeOverride("font_size", 13);
        hint.AddThemeColorOverride("font_color", new Color(UiTheme.Grey, 0.8f));
        box.AddChild(hint);
        _locBindings.Add(("menu.hint", () => hint.Text = Loc.T("menu.hint")));
    }

    private void BuildMenuButtons()
    {
        var col = new VBoxContainer { Name = "MenuButtons" };
        col.AddThemeConstantOverride("separation", 14);

        // 显式锚点：贴右边、垂直居中
        col.AnchorLeft = 1f;
        col.AnchorRight = 1f;
        col.AnchorTop = 0.5f;
        col.AnchorBottom = 0.5f;
        col.OffsetLeft = -400f;
        col.OffsetRight = -72f;
        col.OffsetTop = -190f;
        col.OffsetBottom = 190f;
        _root.AddChild(col);

        AddMenuButton(col, "menu.vs_ai", OnVsAi);
        AddMenuButton(col, "menu.lan", () => ShowToast(Loc.T("common.locked")), accent: false);
        AddMenuButton(col, "menu.settings", () => _settingsPanel.Visible = true);
        AddMenuButton(col, "menu.gallery", () =>
        {
            RefreshHistory();
            _galleryPanel.Visible = true;
        });
        AddMenuButton(col, "menu.quit", () => GetTree().Quit());
    }

    private void AddMenuButton(Container parent, string key, Action onPressed, bool accent = true)
    {
        var btn = new Button
        {
            Name = key.Replace(".", "_"),
            CustomMinimumSize = new Vector2(300, 62),
            Text = Loc.T(key),
        };
        btn.AddThemeFontSizeOverride("font_size", 24);
        btn.Pressed += onPressed;
        parent.AddChild(btn);
        _locBindings.Add((key, () => btn.Text = Loc.T(key)));
    }

    private void BuildToast()
    {
        var panel = new PanelContainer { Name = "ToastPanel", Modulate = new Color(1, 1, 1, 0) };
        panel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        panel.OffsetLeft = -240;
        panel.OffsetRight = 240;
        panel.OffsetTop = -150;
        panel.OffsetBottom = -100;
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.PanelDir + "panel_light.png", 18, 12, 12, 6, 6));
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _root.AddChild(panel);

        _toast = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _toast.AddThemeFontSizeOverride("font_size", 18);
        panel.AddChild(_toast);
        _toastPanel = panel;
    }

    private PanelContainer _toastPanel;

    private void ShowToast(string msg)
    {
        _toast.Text = msg;
        _toastTimer = 2.4f;
        _toastPanel.Modulate = new Color(1, 1, 1, 1);
    }

    // ─────────────────────────── 设置面板 ───────────────────────────

    private void BuildSettingsPanel()
    {
        _settingsPanel = new PanelContainer { Name = "Settings", Visible = false };
        _settingsPanel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _settingsPanel.OffsetLeft = -360;
        _settingsPanel.OffsetRight = 360;
        _settingsPanel.OffsetTop = -250;
        _settingsPanel.OffsetBottom = 250;
        _root.AddChild(_settingsPanel);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 10);
        _settingsPanel.AddChild(outer);

        var title = new Label { Text = Loc.T("settings.title") };
        title.AddThemeFontSizeOverride("font_size", 26);
        outer.AddChild(title);
        _locBindings.Add(("settings.title", () => title.Text = Loc.T("settings.title")));

        var tabs = new TabContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 300),
        };
        outer.AddChild(tabs);

        tabs.AddChild(BuildVideoTab());
        tabs.AddChild(BuildAudioTab());
        tabs.AddChild(BuildCameraTab());
        _tabs = tabs;

        var close = new Button { Text = Loc.T("settings.close"), CustomMinimumSize = new Vector2(0, 46) };
        close.Pressed += () => _settingsPanel.Visible = false;
        outer.AddChild(close);
        _locBindings.Add(("settings.close", () => close.Text = Loc.T("settings.close")));
    }

    private Control BuildVideoTab()
    {
        var tab = new VBoxContainer { Name = Loc.T("settings.video") };
        tab.AddThemeConstantOverride("separation", 12);

        tab.AddChild(NewRowLabel("settings.resolution"));
        var res = new OptionButton();
        res.AddItem("1280 × 720", 0);
        res.AddItem("1600 × 900", 1);
        res.AddItem("1920 × 1080", 2);
        res.Selected = Settings.GetInt("video", "resolution", 0);
        res.ItemSelected += i =>
        {
            Settings.SetInt("video", "resolution", (int)i);
            Settings.Save();
            ApplyResolution((int)i);
        };
        tab.AddChild(res);

        tab.AddChild(NewRowLabel("settings.vsync"));
        var vsync = new CheckButton { ButtonPressed = Settings.GetBool("video", "vsync", true) };
        vsync.Toggled += on =>
        {
            Settings.SetBool("video", "vsync", on);
            Settings.Save();
            DisplayServer.WindowSetVsyncMode(on ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        };
        tab.AddChild(vsync);

        tab.AddChild(NewRowLabel("settings.fullscreen"));
        var full = new CheckButton { ButtonPressed = Settings.GetBool("video", "fullscreen", false) };
        full.Toggled += on =>
        {
            Settings.SetBool("video", "fullscreen", on);
            Settings.Save();
            DisplayServer.WindowSetMode(on ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        };
        tab.AddChild(full);

        tab.AddChild(NewRowLabel("settings.language"));
        var lang = new OptionButton();
        lang.AddItem("中文", 0);
        lang.AddItem("English", 1);
        lang.Selected = Loc.IsChinese ? 0 : 1;
        lang.ItemSelected += i => Loc.SetLanguage(i == 0 ? "zh" : "en");
        tab.AddChild(lang);

        return tab;
    }

    private Control BuildAudioTab()
    {
        var tab = new VBoxContainer { Name = Loc.T("settings.audio") };
        tab.AddThemeConstantOverride("separation", 14);

        tab.AddChild(NewRowLabel("settings.master"));
        tab.AddChild(VolumeSlider("Master", 0.8f));

        tab.AddChild(NewRowLabel("settings.music"));
        tab.AddChild(VolumeSlider("Music", 0.7f));

        tab.AddChild(NewRowLabel("settings.sfx"));
        tab.AddChild(VolumeSlider("SFX", 0.9f));

        return tab;
    }

    /// <summary>战斗摄像机手感：平移 / 旋转 / 缩放三个倍率。</summary>
    private Control BuildCameraTab()
    {
        var tab = new VBoxContainer { Name = Loc.T("settings.camera") };
        tab.AddThemeConstantOverride("separation", 14);

        AddSpeedRow(tab, "settings.pan_speed", "pan");
        AddSpeedRow(tab, "settings.rotate_speed", "rotate");
        AddSpeedRow(tab, "settings.zoom_speed", "zoom");

        return tab;
    }

    private void AddSpeedRow(VBoxContainer parent, string labelKey, string settingKey)
    {
        var row = new VBoxContainer();
        row.AddThemeConstantOverride("separation", 2);

        var label = new Label();
        label.AddThemeFontSizeOverride("font_size", 17);
        row.AddChild(label);

        var slider = new HSlider
        {
            MinValue = 0.3,
            MaxValue = 3.0,
            Step = 0.05,
            CustomMinimumSize = new Vector2(0, 28),
            Value = Settings.GetFloat("camera", settingKey, 1f),
        };
        row.AddChild(slider);

        void Sync() => label.Text = $"{Loc.T(labelKey)}   {slider.Value:0.00}×";
        Sync();
        _locBindings.Add((labelKey, Sync));

        slider.ValueChanged += v =>
        {
            Settings.SetFloat("camera", settingKey, (float)v);
            Settings.Save();
            Sync();
        };

        parent.AddChild(row);
    }

    private Control VolumeSlider(string bus, float def)
    {
        var slider = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.01, CustomMinimumSize = new Vector2(0, 28) };
        slider.Value = Settings.GetFloat("audio", bus, def);
        slider.ValueChanged += v =>
        {
            Settings.SetFloat("audio", bus, (float)v);
            Settings.Save();
            AudioBuses.SetVolume(bus, (float)v);
        };
        return slider;
    }

    private Label NewRowLabel(string key)
    {
        var l = new Label { Text = Loc.T(key) };
        l.AddThemeFontSizeOverride("font_size", 17);
        _locBindings.Add((key, () => l.Text = Loc.T(key)));
        return l;
    }

    private static void ApplyResolution(int index)
    {
        Vector2I size = index switch
        {
            1 => new Vector2I(1600, 900),
            2 => new Vector2I(1920, 1080),
            _ => new Vector2I(1280, 720),
        };
        DisplayServer.WindowSetSize(size);
        Vector2I screen = DisplayServer.ScreenGetSize();
        DisplayServer.WindowSetPosition((screen - size) / 2);
    }

    private void ApplyAudioFromSettings()
    {
        AudioBuses.SetVolume("Master", Settings.GetFloat("audio", "Master", 0.8f));
        AudioBuses.SetVolume("Music", Settings.GetFloat("audio", "Music", 0.7f));
        AudioBuses.SetVolume("SFX", Settings.GetFloat("audio", "SFX", 0.9f));
    }

    // ─────────────────────────── 总览 ───────────────────────────

    private void BuildGalleryPanel()
    {
        _galleryPanel = new PanelContainer { Name = "Gallery", Visible = false };
        _galleryPanel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _galleryPanel.OffsetLeft = -520;
        _galleryPanel.OffsetRight = 520;
        _galleryPanel.OffsetTop = -330;
        _galleryPanel.OffsetBottom = 330;
        _root.AddChild(_galleryPanel);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 10);
        _galleryPanel.AddChild(outer);

        var title = new Label { Text = Loc.T("gallery.title") };
        title.AddThemeFontSizeOverride("font_size", 26);
        outer.AddChild(title);
        _locBindings.Add(("gallery.title", () => title.Text = Loc.T("gallery.title")));

        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        outer.AddChild(grid);

        string[] ids = { "hq", "city", "barracks", "gunTurret", "aaGun", "missileSilo", "helipad" };
        foreach (string id in ids)
        {
            var cell = new VBoxContainer();
            var tex = new TextureRect
            {
                CustomMinimumSize = new Vector2(220, 120),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Texture = GD.Load<Texture2D>("res://gameAsset/textures/buildingsPreview/preview_" + id + ".png"),
            };
            cell.AddChild(tex);

            Data.BuildingDef def = Data.GameDatabase.Building(id);
            var label = new Label
            {
                Text = def != null ? Loc.Name(def.NameZh, def.NameEn) : id,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            cell.AddChild(label);
            grid.AddChild(cell);
        }

        var hint = new Label { Text = Loc.T("gallery.hint") };
        hint.AddThemeFontSizeOverride("font_size", 14);
        hint.AddThemeColorOverride("font_color", UiTheme.Grey);
        outer.AddChild(hint);
        _locBindings.Add(("gallery.hint", () => hint.Text = Loc.T("gallery.hint")));

        // 历史战绩
        var histTitle = new Label { Text = Loc.T("gallery.history") };
        histTitle.AddThemeFontSizeOverride("font_size", 20);
        outer.AddChild(histTitle);
        _locBindings.Add(("gallery.history", () => histTitle.Text = Loc.T("gallery.history")));

        _historyBox = new VBoxContainer();
        _historyBox.AddThemeConstantOverride("separation", 3);
        outer.AddChild(_historyBox);

        var back = new Button { Text = Loc.T("common.back"), CustomMinimumSize = new Vector2(0, 46) };
        back.Pressed += () => _galleryPanel.Visible = false;
        outer.AddChild(back);
        _locBindings.Add(("common.back", () => back.Text = Loc.T("common.back")));
    }

    private VBoxContainer _historyBox;

    private void RefreshHistory()
    {
        if (_historyBox == null)
            return;
        foreach (Node child in _historyBox.GetChildren())
            child.QueueFree();

        MatchHistory.EnsureLoaded();
        int count = 0;
        foreach (MatchRecord r in MatchHistory.Recent(4))
        {
            var l = new Label { Text = MatchHistory.Summarize(r) };
            l.AddThemeFontSizeOverride("font_size", 14);
            l.AddThemeColorOverride("font_color", r.Win ? UiTheme.Ink : UiTheme.Grey);
            _historyBox.AddChild(l);
            count++;
        }

        if (count == 0)
        {
            var l = new Label { Text = Loc.T("result.nohistory") };
            l.AddThemeFontSizeOverride("font_size", 14);
            l.AddThemeColorOverride("font_color", UiTheme.Grey);
            _historyBox.AddChild(l);
        }
    }

    // ─────────────────────────── 通用 ───────────────────────────

    private void OnVsAi()
    {
        GetTree().ChangeSceneToFile("res://scenes/mapSelect.tscn");
    }

    private void RebuildTexts()
    {
        foreach ((string _, Action onChanged) in _locBindings)
            onChanged();

        if (_tabs != null && _tabs.GetTabCount() >= 3)
        {
            _tabs.SetTabTitle(0, Loc.T("settings.video"));
            _tabs.SetTabTitle(1, Loc.T("settings.audio"));
            _tabs.SetTabTitle(2, Loc.T("settings.camera"));
            if (OS.GetCmdlineUserArgs().Contains("--demo-settings"))
                GD.Print($"[Tabs] 0='{_tabs.GetTabTitle(0)}' 1='{_tabs.GetTabTitle(1)}' 2='{_tabs.GetTabTitle(2)}'");
        }
    }

    public override void _Process(double delta)
    {
        if (_toastTimer > 0)
        {
            _toastTimer -= (float)delta;
            _toastPanel.Modulate = _toastTimer <= 0
                ? new Color(1, 1, 1, 0)
                : new Color(1, 1, 1, Mathf.Min(1f, _toastTimer / 0.6f));
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            if (_settingsPanel.Visible)
                _settingsPanel.Visible = false;
            else if (_galleryPanel.Visible)
                _galleryPanel.Visible = false;
        }
    }
}
