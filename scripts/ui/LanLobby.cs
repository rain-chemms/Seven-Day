using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Battle;
using SevenDay.Core;
using SevenDay.Data;
using SevenDay.Net;

namespace SevenDay.UI;

/// <summary>局域网联机大厅：创建房间 / 加入房间。主机用当前 Session 配置开局。</summary>
public partial class LanLobby : Control
{
    private Label _status;
    private LineEdit _ip;
    private LineEdit _port;
    private Button _hostBtn;
    private Button _joinBtn;
    private OptionButton _mapOption;
    private TextureRect _mapPreview;
    private readonly List<Button> _hqButtons = new();
    private int _hqLevel = 1;
    private Button _backBtn;
    private Button _cancelBtn;
    private PanelContainer _restorePanel;
    private PanelContainer _panel;
    private ColorRect _backdrop;
    private Tween _anim;
    private bool _closing;
    private bool _starting;
    private readonly System.Collections.Generic.List<string> _mapIds = new();

    private const int DefaultPort = LanSession.DefaultPort;

    public override void _Ready()
    {
        Loc.EnsureLoaded();
        GameDatabase.EnsureLoaded();
        Theme = UiTheme.Get();
        BuildUi();
        ButtonJuice.AttachAll(this);
        SelectHq(Session.StartHqLevel);
        UpdatePreviewFromSelection();
        ValidateInputs();
        PlayOpen();

        if (OS.GetCmdlineUserArgs().Contains("--demo-lan-close"))
            GetTree().CreateTimer(0.9).Timeout += () => _backBtn?.EmitSignal(BaseButton.SignalName.Pressed);

        // 有未完成的联机对局：先让玩家决定是否载入
        bool joinMode = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--lan-join") >= 0;
        if (!joinMode && LanSaveStore.Exists())
        {
            if (OS.GetCmdlineUserArgs().Contains("--demo-lan-restore"))
                CallDeferred(nameof(DemoRestoreNow));
            else
                CallDeferred(nameof(ShowRestorePrompt));
        }

        NetBridge bridge = NetBridge.Instance;
        if (bridge != null)
        {
            bridge.ClientHello += OnClientHello;
            bridge.PeerConnected += OnPeerConnectedForOffer;
            bridge.StartReceived += OnStartReceived;
            bridge.OfferReceived += OnOfferReceived;
            bridge.PeerLeft += id => SetStatus($"对手已断开（{id}）");
        }

        // 调试：--demo-lan-hosting 停留在“已建房等待对手”状态（用于截图）
        if (OS.GetCmdlineUserArgs().Contains("--demo-lan-hosting"))
            CallDeferred(nameof(OnHostClicked));

        // 调试：--demo-lan-cancel 验证创建房间后取消联机
        if (OS.GetCmdlineUserArgs().Contains("--demo-lan-cancel"))
            CallDeferred(nameof(DemoCancel));

        // 调试：--demo-lan-invalid-ui 停留在无效输入状态（用于截图）
        if (OS.GetCmdlineUserArgs().Contains("--demo-lan-invalid-ui"))
            CallDeferred(nameof(DemoInvalidUi));

        // 调试：--demo-lan-validate 验证 IP / 端口校验
        if (OS.GetCmdlineUserArgs().Contains("--demo-lan-validate"))
            CallDeferred(nameof(DemoValidate));

        // 调试：--demo-lan-back 验证关闭动画后返回主菜单
        if (OS.GetCmdlineUserArgs().Contains("--demo-lan-back"))
            CallDeferred(nameof(DemoClose));

        // 调试：--lan-map map_02 / --lan-hq 3 / --lan-diff 2
        string[] dbgArgs = OS.GetCmdlineUserArgs();
        int mapIdx = System.Array.IndexOf(dbgArgs, "--lan-map");
        if (mapIdx >= 0 && mapIdx + 1 < dbgArgs.Length)
        {
            int found = _mapIds.IndexOf(dbgArgs[mapIdx + 1]);
            if (found >= 0)
            {
                _mapOption.Selected = found;
                Session.MapId = dbgArgs[mapIdx + 1];
            }
        }

        int hqIdx = System.Array.IndexOf(dbgArgs, "--lan-hq");
        if (hqIdx >= 0 && hqIdx + 1 < dbgArgs.Length && int.TryParse(dbgArgs[hqIdx + 1], out int hq))
            SelectHq(Mathf.Clamp(hq, 1, 3));


        // 调试：--lan-host / --lan-join <ip>
        string[] args = OS.GetCmdlineUserArgs();
        if (args.Contains("--lan-host"))
        {
            CallDeferred(nameof(DebugHost));
        }
        else if (args.Contains("--lan-join"))
        {
            int i = System.Array.IndexOf(args, "--lan-join");
            string ip = i >= 0 && i + 1 < args.Length ? args[i + 1] : "127.0.0.1";
            _ip.Text = ip;
            CallDeferred(nameof(DebugJoin));
        }
    }

    public override void _ExitTree()
    {
        if (NetBridge.Instance != null)
        {
            NetBridge.Instance.ClientHello -= OnClientHello;
            NetBridge.Instance.PeerConnected -= OnPeerConnectedForOffer;
            NetBridge.Instance.StartReceived -= OnStartReceived;
            NetBridge.Instance.OfferReceived -= OnOfferReceived;
        }
    }

    private void BuildUi()
    {
        _backdrop = new ColorRect { Color = new Color("F6EEDC") };
        _backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_backdrop);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        _panel = new PanelContainer { CustomMinimumSize = new Vector2(820, 0) };
        center.AddChild(_panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 9);
        _panel.AddChild(box);

        var title = new Label
        {
            Text = Loc.T("menu.lan"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_color", UiTheme.Ink);
        box.AddChild(title);

        var hint = new Label
        {
            Text = Loc.T("lan.hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hint.AddThemeFontSizeOverride("font_size", 14);
        hint.AddThemeColorOverride("font_color", UiTheme.SandDark);
        box.AddChild(hint);

        box.AddChild(new HSeparator());

        // 地图
        var mapRow = new HBoxContainer();
        mapRow.AddThemeConstantOverride("separation", 10);
        mapRow.AddChild(RowLabel(Loc.T("mapselect.title")));
        _mapOption = new OptionButton { CustomMinimumSize = new Vector2(0, 36) };
        _mapOption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        int idx = 0;
        foreach (MapDef map in GameDatabase.Maps.Values.OrderBy(m => m.Id))
        {
            _mapIds.Add(map.Id);
            _mapOption.AddItem(Loc.Name(map.NameZh, map.NameEn), idx++);
        }

        int currentMap = Mathf.Max(0, _mapIds.IndexOf(Session.MapId));
        _mapOption.Selected = currentMap;
        mapRow.AddChild(_mapOption);
        _mapOption.ItemSelected += _ => { UpdatePreviewFromSelection(); ApplyChoicesToSession(applyMap: false); };
        box.AddChild(mapRow);

        _mapPreview = new TextureRect
        {
            CustomMinimumSize = new Vector2(0, 150),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddChild(_mapPreview);

        // 初始基地等级：三张 HQ 预览图按钮（与单机地图选择一致）
        var hqRow = new HBoxContainer();
        hqRow.AddThemeConstantOverride("separation", 10);
        hqRow.AddChild(RowLabel(Loc.T("mapselect.hq_level")));

        var hqSheet = GD.Load<Texture2D>("res://gameAsset/textures/buildingsPreview/preview_hq.png");
        int sheetW = hqSheet != null ? hqSheet.GetWidth() / 3 : 0;

        for (int lv = 1; lv <= 3; lv++)
        {
            int level = lv;
            var btn = new Button
            {
                CustomMinimumSize = new Vector2(88, 74),
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
            art.OffsetTop = 4;
            art.OffsetBottom = -22;
            art.OffsetLeft = 4;
            art.OffsetRight = -4;
            btn.AddChild(art);

            var name = new Label
            {
                Text = $"Lv{lv}",
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            name.SetAnchorsPreset(LayoutPreset.BottomWide);
            name.OffsetTop = -20;
            name.OffsetBottom = -2;
            name.AddThemeFontSizeOverride("font_size", 13);
            btn.AddChild(name);

            btn.Pressed += () => SelectHq(level);
            hqRow.AddChild(btn);
            _hqButtons.Add(btn);
        }

        box.AddChild(hqRow);


        // 主机
        var hostRow = new HBoxContainer();
        hostRow.AddThemeConstantOverride("separation", 10);
        hostRow.AddChild(RowLabel(Loc.T("lan.port")));
        _port = new LineEdit
        {
            Text = DefaultPort.ToString(),
            CustomMinimumSize = new Vector2(170, 36),   // 与 IP 输入框同宽，保证下面的按钮对齐
            Alignment = HorizontalAlignment.Center,     // 输入内容居中
        };
        _port.TextChanged += _ => ValidateInputs();
        hostRow.AddChild(_port);
        _hostBtn = new Button { Text = Loc.T("lan.host"), CustomMinimumSize = new Vector2(0, 40) };
        _hostBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _hostBtn.Pressed += OnHostClicked;
        hostRow.AddChild(_hostBtn);
        box.AddChild(hostRow);

        // 加入
        var joinRow = new HBoxContainer();
        joinRow.AddThemeConstantOverride("separation", 10);
        joinRow.AddChild(RowLabel(Loc.T("lan.ip")));
        _ip = new LineEdit
        {
            Text = "127.0.0.1",
            CustomMinimumSize = new Vector2(170, 36),
            Alignment = HorizontalAlignment.Center,
        };
        _ip.TextChanged += _ => ValidateInputs();
        joinRow.AddChild(_ip);
        _joinBtn = new Button { Text = Loc.T("lan.join"), CustomMinimumSize = new Vector2(0, 40) };
        _joinBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _joinBtn.Pressed += OnJoinClicked;
        joinRow.AddChild(_joinBtn);
        box.AddChild(joinRow);

        _status = new Label
        {
            Text = Loc.T("lan.idle"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _status.AddThemeFontSizeOverride("font_size", 14);
        _status.AddThemeColorOverride("font_color", UiTheme.SandDark);
        box.AddChild(_status);

        _cancelBtn = new Button
        {
            Text = Loc.T("lan.cancel"),
            CustomMinimumSize = new Vector2(0, 36),
            Visible = false,
        };
        _cancelBtn.AddThemeColorOverride("font_color", new Color("C0392B"));
        _cancelBtn.Pressed += CancelSession;
        box.AddChild(_cancelBtn);

        _backBtn = new Button { Text = Loc.T("lan.back"), CustomMinimumSize = new Vector2(0, 38) };
        _backBtn.Pressed += () =>
        {
            LanSession.Leave();
            Session.LanBattle = false;
            PlayClose(() => GetTree().ChangeSceneToFile("res://scenes/mainMenu.tscn"));
        };
        box.AddChild(_backBtn);
    }

    // ── 输入校验：IP / 端口 ──
    private static readonly Color ValidText = Colors.White;
    private static readonly Color InvalidText = new("C0392B");

    /// <summary>端口必须 1..65535 的整数。</summary>
    public static bool IsValidPort(string text, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!int.TryParse(text.Trim(), out port))
            return false;

        return port is >= 1 and <= 65535;
    }

    /// <summary>IPv4（a.b.c.d，每段 0..255）；也放行 localhost。</summary>
    public static bool IsValidIp(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string ip = text.Trim();
        if (ip.Equals("localhost", System.StringComparison.OrdinalIgnoreCase))
            return true;

        string[] parts = ip.Split('.');
        if (parts.Length != 4)
            return false;

        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 3)
                return false;
            foreach (char c in part)
            {
                if (c < '0' || c > '9')
                    return false;
            }

            if (!int.TryParse(part, out int value) || value > 255)
                return false;
        }

        return true;
    }

    /// <summary>根据输入有效性刷新字体颜色与两个按钮的可用状态。</summary>
    private void ValidateInputs()
    {
        bool portOk = IsValidPort(_port?.Text, out _);
        bool ipOk = IsValidIp(_ip?.Text);

        if (_port != null)
            _port.AddThemeColorOverride("font_color", portOk ? ValidText : InvalidText);
        if (_ip != null)
            _ip.AddThemeColorOverride("font_color", ipOk ? ValidText : InvalidText);

        if (_hostBtn != null)
        {
            _hostBtn.Disabled = !portOk;           // 建房只需要端口
            ApplyButtonError(_hostBtn, !portOk);   // 无效时按钮文字也变红
        }

        if (_joinBtn != null)
        {
            _joinBtn.Disabled = !(portOk && ipOk); // 加入需要端口 + IP
            ApplyButtonError(_joinBtn, !(portOk && ipOk));
        }

        // 联机进行中：建房/加入按钮保持禁用（输入框变化不要把它们重新打开）
        bool networking = _cancelBtn is { Visible: true };
        if (networking)
        {
            if (_hostBtn != null)
            {
                _hostBtn.Disabled = true;
                SetButtonDisabledLook(_hostBtn, true);
            }

            if (_joinBtn != null)
            {
                _joinBtn.Disabled = true;
                SetButtonDisabledLook(_joinBtn, true);
            }
        }
        else
        {
            SetButtonDisabledLook(_hostBtn, false);
            SetButtonDisabledLook(_joinBtn, false);
        }
    }

    /// <summary>无效输入时把按钮文字改成红色（同时覆盖禁用态字体颜色）。</summary>
    private static void ApplyButtonError(Button button, bool invalid)
    {
        if (button == null)
            return;

        if (invalid)
        {
            button.AddThemeColorOverride("font_color", InvalidText);
            button.AddThemeColorOverride("font_disabled_color", InvalidText);
            button.AddThemeColorOverride("font_hover_color", InvalidText);
            button.AddThemeColorOverride("font_pressed_color", InvalidText);
        }
        else
        {
            button.RemoveThemeColorOverride("font_color");
            button.RemoveThemeColorOverride("font_disabled_color");
            button.RemoveThemeColorOverride("font_hover_color");
            button.RemoveThemeColorOverride("font_pressed_color");
        }
    }
    // ── 打开 / 关闭动画（与设置面板同一套观感）──
    private void PlayOpen()
    {
        if (_panel == null || _backdrop == null)
            return;

        _panel.PivotOffset = _panel.Size * 0.5f;
        _backdrop.Color = new Color(0.965f, 0.933f, 0.863f, 0f);
        _panel.Modulate = new Color(1f, 1f, 1f, 0f);
        _panel.Scale = Vector2.One * 0.955f;

        KillAnim();
        _anim = CreateTween().SetParallel(true);
        _anim.TweenProperty(_backdrop, "color:a", 1f, 0.18f);
        _anim.TweenProperty(_panel, "modulate:a", 1f, 0.20f);
        _anim.TweenProperty(_panel, "scale", Vector2.One, 0.24f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    private void PlayClose(System.Action onDone)
    {
        if (_closing)
            return;

        _closing = true;
        if (_panel == null || _backdrop == null)
        {
            onDone?.Invoke();
            return;
        }

        _panel.PivotOffset = _panel.Size * 0.5f;
        KillAnim();
        _anim = CreateTween().SetParallel(true);
        // 底色保持不透明（淡掉的话会露出引擎默认灰色背景），只淡出面板
        _anim.TweenProperty(_panel, "modulate:a", 0f, 0.12f);
        _anim.TweenProperty(_panel, "scale", Vector2.One * 0.965f, 0.13f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        _anim.Chain().TweenCallback(Callable.From(() => onDone?.Invoke()));
    }

    private void KillAnim()
    {
        if (_anim != null && _anim.IsValid())
            _anim.Kill();
    }
    private static Label RowLabel(string text)
    {
        var label = new Label { Text = text, CustomMinimumSize = new Vector2(96, 0), VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 16);
        label.AddThemeColorOverride("font_color", UiTheme.Ink);
        return label;
    }

    /// <summary>选择初始基地等级（三个预览按钮）。</summary>
    /// <summary>取消当前联机操作：关闭房间 / 断开连接，并把大厅恢复到可操作状态。</summary>
    private void CancelSession()
    {
        if (_starting)
            return;

        NetBridge.Instance?.Abort();
        _mapOption.Disabled = false;
        _hostBtn.Disabled = false;
        _joinBtn.Disabled = false;
        SetButtonDisabledLook(_hostBtn, false);
        SetButtonDisabledLook(_joinBtn, false);
        _cancelBtn.Visible = false;
        SetStatus(Loc.T("lan.cancelled"));
        ValidateInputs();
    }

    /// <summary>进入联机中状态：地图选择器锁住，显示取消按钮。</summary>
    private void EnterNetworkingState()
    {
        // 禁用后仍保持文字可读（默认禁用色在这个主题下几乎看不见）
        _mapOption.AddThemeColorOverride("font_disabled_color", UiTheme.Ink);
        _mapOption.Disabled = true;
        _hostBtn.Disabled = true;
        _joinBtn.Disabled = true;
        SetButtonDisabledLook(_hostBtn, true);
        SetButtonDisabledLook(_joinBtn, true);
        _cancelBtn.Visible = true;
    }

    /// <summary>禁用按钮时把文字压成灰褐色；恢复时移除覆盖。</summary>
    private static void SetButtonDisabledLook(Button button, bool disabled)
    {
        if (button == null)
            return;

        if (disabled)
        {
            var muted = new Color("9A8F78");
            button.AddThemeColorOverride("font_disabled_color", muted);
            button.AddThemeColorOverride("font_color", muted);
        }
        else
        {
            button.RemoveThemeColorOverride("font_disabled_color");
            button.RemoveThemeColorOverride("font_color");
        }
    }
    private void SelectHq(int level)
    {
        _hqLevel = Mathf.Clamp(level, 1, 3);
        Session.StartHqLevel = _hqLevel;

        for (int i = 0; i < _hqButtons.Count; i++)
        {
            Button b = _hqButtons[i];
            bool on = i + 1 == _hqLevel;
            b.ButtonPressed = on;
            b.Modulate = on ? Colors.White : new Color(0.82f, 0.78f, 0.68f);
        }
    }

    /// <summary>根据当前地图下拉项刷新预览图。</summary>
    private void UpdatePreviewFromSelection()
    {
        if (_mapPreview == null || _mapOption == null)
            return;

        int idx = _mapOption.Selected;
        if (idx < 0 || idx >= _mapIds.Count)
            return;

        MapDef map = GameDatabase.Map(_mapIds[idx]);
        _mapPreview.Texture = MapSelect.LoadMapPreview(map);
    }
    private void SetStatus(string text)
    {
        if (_status != null)
            _status.Text = text;
        GD.Print($"[Lan] {text}");
    }

    private int Port() =>
        int.TryParse(_port?.Text?.Trim(), out int p) && p > 0 ? p : DefaultPort;

    private void ApplyChoicesToSession(bool applyMap)
    {
        if (applyMap && _mapOption != null && _mapOption.Selected >= 0 &&
            _mapOption.Selected < _mapIds.Count)
            Session.MapId = _mapIds[_mapOption.Selected];

        Session.StartHqLevel = Mathf.Clamp(_hqLevel, 1, 3);


    }

    /// <summary>调试：自动载入未完成对局。</summary>
    private void DemoRestoreNow()
    {
        LanSaveStore.SaveData data = LanSaveStore.Load();
        if (data != null)
            ApplyRestore(data);
    }
    /// <summary>启动联机模式时，若存在未完成对局，先询问是否载入。</summary>
    private void ShowRestorePrompt()
    {
        if (_restorePanel != null && IsInstanceValid(_restorePanel))
            return;

        LanSaveStore.SaveData data = LanSaveStore.Load();
        if (data?.Payload?.Config == null)
            return;

        var layer = new CanvasLayer { Name = "RestoreLayer", Layer = 20 };
        AddChild(layer);

        var root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Stop };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(root);

        var dim = new ColorRect { Color = new Color(0.03f, 0.02f, 0.02f, 0.55f) };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(dim);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(center);

        _restorePanel = new PanelContainer { CustomMinimumSize = new Vector2(520, 0) };
        center.AddChild(_restorePanel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 26);
        margin.AddThemeConstantOverride("margin_right", 26);
        margin.AddThemeConstantOverride("margin_top", 22);
        margin.AddThemeConstantOverride("margin_bottom", 22);
        _restorePanel.AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        margin.AddChild(box);

        var title = new Label { Text = Loc.T("lan.restore_title"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        title.AddThemeColorOverride("font_color", UiTheme.Ink);
        box.AddChild(title);

        box.AddChild(new HSeparator());

        var desc = new Label
        {
            Text = Loc.T("lan.restore_desc", data.MapName, data.Turn, data.SavedAt),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        desc.AddThemeFontSizeOverride("font_size", 15);
        desc.AddThemeColorOverride("font_color", UiTheme.SandDark);
        box.AddChild(desc);

        box.AddChild(new HSeparator());

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        box.AddChild(row);

        var load = new Button
        {
            Text = Loc.T("lan.restore_load"),
            CustomMinimumSize = new Vector2(0, 46),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        load.Pressed += () =>
        {
            layer.QueueFree();
            ApplyRestore(data);
        };
        row.AddChild(load);

        var discard = new Button
        {
            Text = Loc.T("lan.restore_discard"),
            CustomMinimumSize = new Vector2(0, 46),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        discard.AddThemeColorOverride("font_color", new Color("C0392B"));
        discard.Pressed += () =>
        {
            LanSaveStore.Delete();
            layer.QueueFree();
            SetStatus(Loc.T("lan.idle"));
        };
        row.AddChild(discard);

        ButtonJuice.AttachAll(root);
        UiSound.AttachAll(root);
    }

    /// <summary>载入未完成对局：按存档地图与动作历史重建，并开房等对方加入。</summary>
    private void ApplyRestore(LanSaveStore.SaveData data)
    {
        if (data?.Payload?.Config == null)
            return;

        Session.RestoreLanSave = true;
        Session.LanBattle = true;
        Session.SingleBattle = false;
        Session.MapId = data.Payload.Config.MapId;
        Session.Difficulty = (AIDifficulty)data.Payload.Config.Difficulty;
        LanSession.Config = data.Payload.Config;

        if (NetBridge.Instance == null)
            return;

        int port = data.Port > 0 ? data.Port : DefaultPort;
        if (!NetBridge.Instance.Host(port, out string error))
        {
            Session.RestoreLanSave = false;
            SetStatus(error);
            return;
        }

        GetTree().ChangeSceneToFile("res://scenes/battleScene.tscn");
    }
    private void OnHostClicked()
    {
        if (!IsValidPort(_port?.Text, out int chosenPort))
        {
            SetStatus($"端口无效：{_port?.Text}（应为 1~65535）");
            ValidateInputs();
            return;
        }

        ApplyChoicesToSession(applyMap: true);
        if (NetBridge.Instance == null)
        {
            SetStatus("联机桥未初始化（NetBridge autoload 缺失）");
            return;
        }

        if (!NetBridge.Instance.Host(Port(), out string error))
        {
            SetStatus(error);
            ValidateInputs();
            return;
        }

        EnterNetworkingState();
        SetStatus($"{Loc.T("lan.waiting")}  {LanSession.LocalIpv4()}:{Port()}");
    }

    private void OnJoinClicked()
    {
        ApplyChoicesToSession(applyMap: false);   // 地图由主机决定，基地等级用自己的
        if (_mapOption != null)
            _mapOption.Disabled = true;

        if (NetBridge.Instance == null)
        {
            SetStatus("联机桥未初始化（NetBridge autoload 缺失）");
            return;
        }

        string ip = string.IsNullOrWhiteSpace(_ip.Text) ? "127.0.0.1" : _ip.Text.Trim();
        if (!IsValidIp(_ip?.Text))
        {
            SetStatus($"IP 无效：{_ip?.Text}（示例 192.168.1.10）");
            ValidateInputs();
            return;
        }

        if (!NetBridge.Instance.Join(ip, Port(), out string error))
        {
            SetStatus(error);
            ValidateInputs();
            return;
        }

        EnterNetworkingState();
        SetStatus($"{Loc.T("lan.connecting")} {ip}:{Port()}");
    }

    private async void DemoCancel()
    {
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
        OnHostClicked();
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Cancel] 建房后 地图可交互={!_mapOption.Disabled} 取消按钮={_cancelBtn.Visible} " +
                 $"建房可用={!_hostBtn.Disabled}");

        CancelSession();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Cancel] 取消后 地图可交互={!_mapOption.Disabled} 取消按钮={_cancelBtn.Visible} " +
                 $"建房可用={!_hostBtn.Disabled} 联机中={LanSession.Active}");
    }
    private void DemoInvalidUi()
    {
        _ip.Text = "192.168.1";
        _port.Text = "70000";
        ValidateInputs();
        SetStatus("IP / 端口无效：红字提示，按钮已禁用");
    }
    private void DemoValidate()
    {
        foreach (string ip in new[] { "127.0.0.1", "192.168.1.10", "localhost", "256.1.1.1", "192.168.1", "abc", "" })
            GD.Print($"[Validate] ip '{ip}' => {IsValidIp(ip)}");

        foreach (string port in new[] { "7777", "1", "65535", "0", "65536", "-1", "abc", "" })
            GD.Print($"[Validate] port '{port}' => {IsValidPort(port, out int v)} ({v})");

        // 顺带验证按钮状态
        _ip.Text = "999.1.1.1";
        _port.Text = "70000";
        ValidateInputs();
        GD.Print($"[Validate] 无效输入 建房禁用={_hostBtn.Disabled} 加入禁用={_joinBtn.Disabled} " +
                 $"IP颜色={_ip.GetThemeColor("font_color").ToHtml(false)} 端口颜色={_port.GetThemeColor("font_color").ToHtml(false)} " +
                 $"建房字色={_hostBtn.GetThemeColor("font_color").ToHtml(false)} 加入字色={_joinBtn.GetThemeColor("font_color").ToHtml(false)}");

        _ip.Text = "192.168.1.10";
        _port.Text = "7777";
        ValidateInputs();
        GD.Print($"[Validate] 有效输入 建房禁用={_hostBtn.Disabled} 加入禁用={_joinBtn.Disabled} " +
                 $"IP颜色={_ip.GetThemeColor("font_color").ToHtml(false)} 端口颜色={_port.GetThemeColor("font_color").ToHtml(false)} " +
                 $"建房字色={_hostBtn.GetThemeColor("font_color").ToHtml(false)} 加入字色={_joinBtn.GetThemeColor("font_color").ToHtml(false)}");
    }
    private async void DemoClose()
    {
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
        GD.Print("[Lan] 播放关闭动画");
        PlayClose(() =>
        {
            GD.Print("[Lan] 关闭动画结束，切回主菜单");
            GetTree().ChangeSceneToFile("res://scenes/mainMenu.tscn");
        });
    }
    private void DebugHost()
    {
        OSDebug("--lan-host");
        OnHostClicked();
    }

    private void DebugJoin()
    {
        OSDebug("--lan-join");
        OnJoinClicked();
    }

    private void OSDebug(string tag)
    {
        if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
            GD.Print($"[Lan] 自动执行 {tag}");
    }

    // ── 主机：房间信息先发给客户端，等它回卡组 ──
    private void OnPeerConnectedForOffer(int peerId)
    {
        MapDef map = GameDatabase.Map(Session.MapId) ?? GameDatabase.Maps.Values.FirstOrDefault();
        if (map == null)
            return;

        NetBridge.Instance.SendOffer(map.Id, (int)Session.Difficulty, Session.StartHqLevel, peerId);
    }

    // ── 主机：收到客户端的卡组 + 基地等级后，定稿配置并开局 ──
    private void OnClientHello(int peerId, NetClientHello hello)
    {
        if (_starting)
            return;

        // 版本不一致会导致 RPC 方法表不同、重连后操作失效，先拦住并明确提示
        string localVer = (string)ProjectSettings.GetSetting("application/config/version", "");
        if (!string.IsNullOrEmpty(hello.Version) && !string.IsNullOrEmpty(localVer) &&
            hello.Version != localVer)
        {
            SetStatus($"版本不一致：主机 {localVer} / 客机 {hello.Version}，请更新到同一版本");
            GD.Print($"[Lan] 拒绝版本不一致的客户端：{hello.Version} != {localVer}");
            NetBridge.Instance?.Abort();
            return;
        }

        MapDef map = GameDatabase.Map(Session.MapId) ?? GameDatabase.Maps.Values.FirstOrDefault();
        if (map == null)
        {
            SetStatus("找不到地图数据");
            return;
        }

        NetBattleConfig cfg = BattleScene.BuildNetConfig(map, hello);
        LanSession.Config = cfg;
        NetBridge.Instance.SendStart(cfg, peerId);

        Session.LanBattle = true;
        Session.SingleBattle = false;
        _starting = true;
        _cancelBtn.Visible = false;
        SetStatus($"对手已连接（{hello.Deck.Length} 张卡 / Lv{hello.HqLevel}），开始对战");
        PlayClose(() => GetTree().ChangeSceneToFile("res://scenes/battleScene.tscn"));
    }

    // ── 客户端：收到房间信息，先记录地图/难度（卡组在 NetBridge 里回发）──
    private void OnOfferReceived(NetRoomOffer offer)
    {
        if (offer == null)
            return;

        Session.MapId = offer.MapId;
        Session.Difficulty = (AIDifficulty)offer.Difficulty;
        if (_mapOption != null)
        {
            int idx = _mapIds.IndexOf(offer.MapId);
            if (idx >= 0)
                _mapOption.Selected = idx;
            _mapOption.Disabled = true;
        }


        UpdatePreviewFromSelection();
        SetStatus($"房间信息：{offer.MapId}  难度 {offer.Difficulty}，已发送我方卡组");
    }

    // ── 客户端：收到配置后进战斗 ──
    private void OnStartReceived(NetBattleConfig cfg)
    {
        if (_starting)
            return;

        Session.MapId = cfg.MapId;
        Session.Difficulty = (AIDifficulty)cfg.Difficulty;
        Session.LanBattle = true;

        _starting = true;
        SetStatus($"已收到开局配置（我方 {cfg.DeckClient.Length} 张卡），进入战斗");
        PlayClose(() => GetTree().ChangeSceneToFile("res://scenes/battleScene.tscn"));
    }
}
