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
    private readonly List<Button> _tabButtons = new();
    private readonly List<Control> _tabPages = new();
    private int _currentTab;
    private float _toastTimer;

    private readonly List<(string Key, Action OnChanged)> _locBindings = new();

    public override void _Ready()
    {
        Loc.EnsureLoaded();
        AudioBuses.Ensure();

        AddChild(new MainMenuBackdrop { Name = "Backdrop" });
        BuildUi();
        ApplyAudioFromSettings();
        AudioManager.StopStinger();       // 从结算界面回来时别让胜利/失败音效继续响
        AudioManager.PlayBgm("res://gameAsset/audios/bgm/menu_theme.ogg", 1.2f);
        Loc.Changed += RebuildTexts;

        // 开发调试：--demo-button 模拟悬停第一个按钮、按下第二个按钮，检查动效
        if (OS.GetCmdlineUserArgs().Contains("--demo-button"))
            CallDeferred(nameof(DemoButtonFlow));

        // 开发调试：--demo-settings 打开设置面板并切到摄像机页
        if (OS.GetCmdlineUserArgs().Contains("--demo-settings"))
            CallDeferred(nameof(DemoSettings));

        if (OS.GetCmdlineUserArgs().Contains("--demo-gallery"))
            CallDeferred(nameof(DemoGallery));

        // 开发调试：--demo-deck 打开出战卡组编辑器
        if (OS.GetCmdlineUserArgs().Contains("--demo-deck"))
            CallDeferred(nameof(DemoDeck));

        // 开发调试：保存 / 清空卡组（验证对局是否用自定义卡组）
        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-features"))
            CallDeferred(nameof(DemoDeckFeatures));
        else if (OS.GetCmdlineUserArgs().Contains("--demo-deck-save") ||
                 OS.GetCmdlineUserArgs().Contains("--demo-deck-clear"))
            CallDeferred(nameof(DemoDeckSaveClear));

        // 开发调试：--demo-deck-individual 打开并切到逐张显示
        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-individual"))
            CallDeferred(nameof(DemoDeckIndividual));

        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-drag"))
            CallDeferred(nameof(DemoDeckDrag));

        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-small"))
            CallDeferred(nameof(DemoDeckSmall));

        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-remove"))
            CallDeferred(nameof(DemoDeckRemove));

        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-fly"))
            CallDeferred(nameof(DemoDeckFly));

        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-slide"))
            CallDeferred(nameof(DemoDeckSlide));

        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-empty"))
            CallDeferred(nameof(DemoDeckEmpty));

        if (OS.GetCmdlineUserArgs().Contains("--demo-preset-rename"))
            CallDeferred(nameof(DemoPresetRename));

        if (OS.GetCmdlineUserArgs().Contains("--demo-btn-hold"))
            CallDeferred(nameof(DemoButtonHold));

        if (OS.GetCmdlineUserArgs().Contains("--demo-click-through"))
            CallDeferred(nameof(DemoClickThrough));

        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-close"))
            CallDeferred(nameof(DemoDeckClose));

        if (OS.GetCmdlineUserArgs().Contains("--demo-rename-cancel"))
            CallDeferred(nameof(DemoRenameCancel));

        if (OS.GetCmdlineUserArgs().Contains("--clear-preset-names"))
        {
            for (int i = 1; i <= Session.PresetSlots; i++)
                Settings.SetString("deck", "preset" + i + "_name", "");
            Settings.Save();
            GD.Print("[DeckTest] 已清除预设自定义名");
            GetTree().Quit();
        }

        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-insert"))
            CallDeferred(nameof(DemoDeckInsert));

        // 开发调试：清空 5 个预设槽位
        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-clear-presets"))
        {
            for (int i = 1; i <= Session.PresetSlots; i++)
                Settings.SetString("deck", "preset" + i, "");
            Settings.Save();
            GD.Print("[DeckTest] 已清空所有预设");
            GetTree().Quit();
        }
    }

    private async void DemoDeckIndividual()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        _deckPanel.DebugToggleMode();
    }

    /// <summary>重命名小窗点取消时的淡出。</summary>
    private async void DemoRenameCancel()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
        _deckPanel.DebugOpenRename(0);
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        _deckPanel.DebugCloseRename();
    }

    /// <summary>打开后关闭，用来看关闭动画。</summary>
    /// <summary>验证进场遮罩：刚进场景时的点击不应该打开卡组面板。</summary>
    private async void DemoClickThrough()
    {
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);

        Vector2 p = new(940f, 355f);      // 「出战卡组」按钮
        Input.WarpMouse(p);
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = p, GlobalPosition = p,
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = p, GlobalPosition = p,
        });

        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Mask] 遮罩期内点击后 卡组面板={_deckPanel.Visible}（应为 False）");

        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = p, GlobalPosition = p,
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = p, GlobalPosition = p,
        });
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Mask] 遮罩结束后点击后 卡组面板={_deckPanel.Visible}（应为 True）");
    }
    private async void DemoDeckClose()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.7), SceneTreeTimer.SignalName.Timeout);
        _deckPanel.Close();
    }

    /// <summary>按住一个按钮，用来看「按下/选中」时的配色。</summary>
    private async void DemoButtonHold()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.45), SceneTreeTimer.SignalName.Timeout);

        Vector2 p = _deckPanel.DebugButtonCenter("重命名");
        Input.WarpMouse(p);
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = p, GlobalPosition = p,
        });
    }

    /// <summary>预设重命名：设置名字后应显示在预设按钮上。</summary>
    private async void DemoPresetRename()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[PresetRename] 重命名前：{_deckPanel.DebugPresetLabel(0)}");
        _deckPanel.DebugRenamePreset(0, "速攻流");
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[PresetRename] 重命名后：{_deckPanel.DebugPresetLabel(0)}");

        if (OS.GetCmdlineUserArgs().Contains("--show-dialog"))
            _deckPanel.DebugOpenRename(0);
    }

    /// <summary>空卡组提示。</summary>
    private async void DemoDeckEmpty()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        _deckPanel.OnDebugClear();

        // 清空后保存预设会弹「至少 7 张」提示，用来截提示样式
        if (OS.GetCmdlineUserArgs().Contains("--toast"))
            _deckPanel.DebugSavePreset();
    }

    /// <summary>逐张模式下把牌库的卡拖到中间某一位，验证按光标位置插入。</summary>
    private async void DemoDeckInsert()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        _deckPanel.DebugToggleMode();                 // 逐张模式

        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);

        Control poolCell = _deckPanel.DebugPoolCell(4);   // 战斗机
        Control deckCell = _deckPanel.DebugDeckCell(2);   // 第 3 张
        if (poolCell == null || deckCell == null)
            return;

        Vector2 from = poolCell.GlobalPosition + poolCell.Size * 0.5f;
        // 落在第 3 张的右半边 → 应该插到索引 3
        Vector2 to = deckCell.GlobalPosition + new Vector2(deckCell.Size.X * 0.8f, deckCell.Size.Y * 0.5f);

        GD.Print($"[Insert] 插入前 {_deckPanel.DebugTotal()} 张：{_deckPanel.DebugDeckIds()}");

        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = from, GlobalPosition = from,
        });
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);

        Input.ParseInputEvent(new InputEventMouseMotion { Position = to, GlobalPosition = to });
        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Insert] 拖动中 空位索引={_deckPanel.DebugGapIndex()}");

        if (OS.GetCmdlineUserArgs().Contains("--hold"))
        {
            _deckPanel.DebugPreviewGap(3);
            await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        }

        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = to, GlobalPosition = to,
        });

        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Insert] 插入后 {_deckPanel.DebugTotal()} 张：{_deckPanel.DebugDeckIds()}");
    }

    /// <summary>验证位置变化是滑动过去而不是瞬移。</summary>
    private async void DemoDeckSlide()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        int last = _deckPanel.DebugDeckCount() - 1;
        Vector2 before = _deckPanel.DebugDeckCellPos(last);
        _deckPanel.DebugRemoveAt(5);      // 删掉整张「导弹车」，后面的类型整体左移一格

        int last2 = _deckPanel.DebugDeckCount() - 1;
        Vector2 atStart = _deckPanel.DebugDeckCellPos(last2);
        await ToSignal(GetTree().CreateTimer(0.08), SceneTreeTimer.SignalName.Timeout);
        Vector2 mid = _deckPanel.DebugDeckCellPos(last2);
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        Vector2 end = _deckPanel.DebugDeckCellPos(last2);

        GD.Print($"[Slide] 原第2张位置={before} 删除后 起点={atStart} 80ms={mid} 结束={end}");
        GD.Print($"[Slide] 起点→中间变化={atStart.DistanceTo(mid):F1}px 中间→结束={mid.DistanceTo(end):F1}px（应都 > 0）");
    }

    /// <summary>验证飞行动画的目标点（两种显示模式各加一张）。</summary>
    private async void DemoDeckFly()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        _deckPanel.DebugAdd("unit_fighter");                    // 合并模式
        await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);

        _deckPanel.DebugToggleMode();                            // 逐张模式
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        _deckPanel.DebugAdd("unit_helicopter");
        await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);
    }

    /// <summary>把右侧一张卡拖到左边（区域外）应移除。</summary>
    private async void DemoDeckRemove()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);

        Control cell = _deckPanel.DebugDeckCell(0);
        Rect2 deck = _deckPanel.DebugDeckRect();
        if (cell == null)
            return;

        Vector2 from = cell.GlobalPosition + cell.Size * 0.5f;
        Vector2 outside = new(deck.Position.X - 220f, deck.Position.Y + 180f);
        int before = _deckPanel.DebugTotal();

        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = from, GlobalPosition = from,
        });
        Input.ParseInputEvent(new InputEventMouseMotion { Position = outside, GlobalPosition = outside });
        Input.WarpMouse(outside);

        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Remove] 拖动中 跟手卡可见={_deckPanel.DebugGhostVisible()}");

        if (OS.GetCmdlineUserArgs().Contains("--hold"))
            await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);

        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = outside, GlobalPosition = outside,
        });

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Remove] 拖出左侧松开：{before} → {_deckPanel.DebugTotal()} 张（应 -1）");

        // 拖出去再拖回来：应该放回去，总数不变
        Control cell3 = _deckPanel.DebugDeckCell(0);
        int before3 = _deckPanel.DebugTotal();
        Vector2 p3 = cell3.GlobalPosition + cell3.Size * 0.5f;
        Vector2 outPos = new(deck.Position.X - 200f, deck.Position.Y + 150f);

        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = p3, GlobalPosition = p3,
        });

        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = outPos, GlobalPosition = outPos });
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
        int during = _deckPanel.DebugTotal();
        Vector2 back = deck.Position + deck.Size * 0.5f;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = back, GlobalPosition = back });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = back, GlobalPosition = back,
        });

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Remove] 拖出再拖回：{before3} → 拖动中{during} → 松手后{_deckPanel.DebugTotal()}（应回到 {before3}）");
        GD.Print($"[Remove] 卡组内容={_deckPanel.DebugDeckIds()}");

        // 原地点击也应移除
        Control cell2 = _deckPanel.DebugDeckCell(0);
        if (cell2 == null)
            return;

        Vector2 p2 = cell2.GlobalPosition + cell2.Size * 0.5f;
        int before2 = _deckPanel.DebugTotal();
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = p2, GlobalPosition = p2,
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = p2, GlobalPosition = p2,
        });

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Remove] 原地点击：{before2} → {_deckPanel.DebugTotal()} 张（应 -1）");
    }

    /// <summary>只有两张牌时，验证纵向居中。</summary>
    private async void DemoDeckSmall()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
        _deckPanel.OnDebugClear();
        _deckPanel.DebugAdd("unit_infantry");
        _deckPanel.DebugAdd("magic_airstrike");
    }

    /// <summary>验证拖拽：跟手卡应贴着光标，松开只在右侧区域才加牌。</summary>
    private async void DemoDeckDrag()
    {
        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);

        Control cell = _deckPanel.DebugPoolCell(0);
        if (cell == null)
            return;

        Vector2 from = cell.GlobalPosition + cell.Size * 0.5f;
        Rect2 deck = _deckPanel.DebugDeckRect();
        Vector2 to = deck.Position + deck.Size * 0.5f;
        Vector2 mid = from.Lerp(to, 0.5f);
        int before = _deckPanel.DebugTotal();

        Input.WarpMouse(from);
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = from, GlobalPosition = from,
        });
        Input.ParseInputEvent(new InputEventMouseMotion { Position = mid, GlobalPosition = mid });

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Drag] 半途 拖动中={_deckPanel.DebugDragging()} 跟手卡可见={_deckPanel.DebugGhostVisible()} " +
                 $"卡位={_deckPanel.DebugGhostPos()} 鼠标={mid}");

        Input.ParseInputEvent(new InputEventMouseMotion { Position = to, GlobalPosition = to });
        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Drag] 到右侧 卡位={_deckPanel.DebugGhostPos()} 鼠标={to}");

        // 停一下，方便截图看跟手卡是否贴着光标（把真实光标也挪过去，避免引擎回报旧位置）
        Input.WarpMouse(to);
        if (OS.GetCmdlineUserArgs().Contains("--hold"))
        {
            await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
            Vector2 mouse = DisplayServer.MouseGetPosition();
            Vector2 ghostCenter = _deckPanel.DebugGhostPos() + new Vector2(46f, 70f);
            GD.Print($"[Drag] 系统鼠标={mouse} 跟手卡中心={ghostCenter} 偏差={mouse.DistanceTo(ghostCenter):F1}");
            await ToSignal(GetTree().CreateTimer(0.7), SceneTreeTimer.SignalName.Timeout);
        }

        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = to, GlobalPosition = to,
        });

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Drag] 放置后 {before} → {_deckPanel.DebugTotal()} 张，跟手卡可见={_deckPanel.DebugGhostVisible()} " +
                 $"飞行中卡数={_deckPanel.DebugFlyingCount()}（拖拽加入应为 0）");

        // 再验一次：拖到左侧空白处松手，不应加牌
        int afterFirst = _deckPanel.DebugTotal();
        Vector2 nowhere = new(deck.Position.X - 120f, deck.Position.Y - 60f);
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = from, GlobalPosition = from,
        });
        Input.ParseInputEvent(new InputEventMouseMotion { Position = nowhere, GlobalPosition = nowhere });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = nowhere, GlobalPosition = nowhere,
        });

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Drag] 拖到空白处松手：{afterFirst} → {_deckPanel.DebugTotal()} 张（应不变）");
    }

    /// <summary>验证筛选 / 显示模式切换 / 预设保存与载入。</summary>
    private async void DemoDeckFeatures()
    {
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);

        _deckPanel.Open();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        GD.Print($"[DeckFx] 初始 {_deckPanel.DebugTotal()} 张，牌库 {_deckPanel.DebugPoolCount()} 张");

        _deckPanel.DebugAdd("unit_fighter");
        _deckPanel.DebugAdd("unit_fighter");
        GD.Print($"[DeckFx] 加 2 张战斗机后 {_deckPanel.DebugTotal()} 张");

        _deckPanel.DebugSetFilter("unit");
        GD.Print($"[DeckFx] 筛选「单位」后牌库 {_deckPanel.DebugPoolCount()} 张");

        _deckPanel.DebugSetFilter("strike");
        GD.Print($"[DeckFx] 筛选「打击」后牌库 {_deckPanel.DebugPoolCount()} 张");

        _deckPanel.DebugSetFilter("all");
        _deckPanel.DebugToggleMode();
        GD.Print("[DeckFx] 已切到逐张显示");
        _deckPanel.DebugToggleMode();
        GD.Print("[DeckFx] 已切回合并显示");

        GD.Print($"[DeckFx] 卡组内容={_deckPanel.DebugDeckIds()}");

        // 预设：存到 1、清空、再读回来
        _deckPanel.DebugSavePreset();
        _deckPanel.OnDebugClear();
        GD.Print($"[DeckFx] 清空后 {_deckPanel.DebugTotal()} 张");
        _deckPanel.DebugLoadPreset();
        GD.Print($"[DeckFx] 载入预设后 {_deckPanel.DebugTotal()} 张");

        // 拖拽：从牌库第一张拖到右侧出战区
        Control cell = _deckPanel.DebugPoolCell(0);
        Rect2 deckRect = _deckPanel.DebugDeckRect();
        if (cell != null && deckRect.Size.X > 0)
        {
            Vector2 from = cell.GlobalPosition + cell.Size * 0.5f;
            Vector2 to = deckRect.Position + deckRect.Size * 0.5f;
            int before = _deckPanel.DebugTotal();

            Input.WarpMouse(from);
            Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Pressed = true,
                Position = from, GlobalPosition = from,
            });
            Input.ParseInputEvent(new InputEventMouseMotion { Position = to, GlobalPosition = to });
            Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Pressed = false,
                Position = to, GlobalPosition = to,
            });

            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            GD.Print($"[DeckFx] 拖拽加入：{before} → {_deckPanel.DebugTotal()} 张");
        }
    }

    /// <summary>写入 / 清空自定义卡组，验证对局是否使用它。</summary>
    private void DemoDeckSaveClear()
    {
        if (OS.GetCmdlineUserArgs().Contains("--demo-deck-save"))
        {
            Session.SaveDeck(new[]
            {
                "unit_infantry", "unit_infantry", "unit_tank", "unit_tank",
                "unit_missile_launcher", "magic_airstrike", "magic_repair", "unit_helicopter",
            });
            GD.Print($"[DeckTest] 已保存 {Session.PlayerDeck.Count} 张自定义卡组");
            GetTree().Quit();
        }
        else if (OS.GetCmdlineUserArgs().Contains("--demo-deck-clear"))
        {
            Session.ClearDeck();
            GD.Print("[DeckTest] 已清空自定义卡组");
            GetTree().Quit();
        }
    }

    private void DemoDeck()
    {
        _deckPanel.Open();
        GD.Print($"[Deck] 打开编辑器，当前 {Session.PlayerDeck.Count} 张（默认 {Session.DefaultDeck().Count} 张）");
        CallDeferred(nameof(DemoDeckClick));
        CallDeferred(nameof(DemoDeckLayout));
    }

    /// <summary>打印首次打开时的排版信息（列数 / 宽度）。</summary>
    private async void DemoDeckLayout()
    {
        await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[DeckLayout] 首次打开 宽度={_deckPanel.DebugScrollWidth():F0} 列数={_deckPanel.DebugDeckColumns()} 行偏移={_deckPanel.DebugRowOffset():F0}");
    }

    /// <summary>真的往第一张可选卡上点一下，确认「加入卡组」生效。</summary>
    private async void DemoDeckClick()
    {
        Control cell = _deckPanel.DebugPoolCell(0);
        if (cell == null)
            return;

        int before = _deckPanel.DebugTotal();
        Vector2 pos = cell.GlobalPosition + cell.Size * 0.5f;
        Input.WarpMouse(pos);

        var press = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = pos,
            GlobalPosition = pos,
        };
        Input.ParseInputEvent(press);

        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        var release = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
            Position = pos,
            GlobalPosition = pos,
        };
        Input.ParseInputEvent(release);

        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Deck] 点击池中卡牌：{before} → {_deckPanel.DebugTotal()} 张");
    }

    private void DemoGallery()
    {
        RefreshHistory();
        OpenGallery();

        if (!OS.GetCmdlineUserArgs().Contains("--anim-log"))
            return;

        CallDeferred(nameof(LogGalleryAnim));
    }

    private async void LogGalleryAnim()
    {
        GD.Print($"[Anim] gallery t=0 alpha={_galleryPanel.Modulate.A:F2} scale={_galleryPanel.Scale.X:F3} dim={_galleryDim.Color.A:F2}");
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Anim] gallery t=0.3 alpha={_galleryPanel.Modulate.A:F2} scale={_galleryPanel.Scale.X:F3} dim={_galleryDim.Color.A:F2}");

        if (!OS.GetCmdlineUserArgs().Contains("--also-close"))
            return;

        CloseGallery();
        await ToSignal(GetTree().CreateTimer(0.07), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Anim] gallery close t=0.07 alpha={_galleryPanel.Modulate.A:F2} dim={_galleryDim.Color.A:F2} vis={_galleryPanel.Visible}");
        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Anim] gallery close t=0.22 vis={_galleryPanel.Visible}");
    }

    private void DemoSettings()
    {
        OpenSettings();
        SelectSettingsTab(OS.GetCmdlineUserArgs().Contains("--video-tab") ? 0 : 2);

        if (OS.GetCmdlineUserArgs().Contains("--anim-log"))
            CallDeferred(nameof(LogSettingsAnim));

        if (OS.GetCmdlineUserArgs().Contains("--wheel-test"))
            CallDeferred(nameof(TestSettingsWheel));

        // 把鼠标停在「关闭」上，检查悬停时有没有被裁边
        if (OS.GetCmdlineUserArgs().Contains("--hover-close"))
        {
            Button best = null;
            foreach (Node node in _settingsPanel.FindChildren("*", "Button", true, false))
            {
                if (node is not Button b)
                    continue;

                GD.Print($"[CloseHover] 候选 '{b.Text}' rect={b.GetGlobalRect()} vis={b.IsVisibleInTree()}");
                if (b.Text != Loc.T("settings.close") || !b.IsVisibleInTree())
                    continue;

                if (best == null || b.Size.X > best.Size.X)
                    best = b;
            }

            // 布局坐标这一刻还没刷新，直接用实测的按钮中心
            Vector2 p2 = new(640f, 575f);
            Input.WarpMouse(p2);
            Input.ParseInputEvent(new InputEventMouseMotion { Position = p2, GlobalPosition = p2 });
            GD.Print($"[CloseHover] warp={p2} best={(best != null)}");

            // --click-close：再点一下，用来听「后退」音效
            if (OS.GetCmdlineUserArgs().Contains("--click-close"))
            {
                Input.ParseInputEvent(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left, Pressed = true, Position = p2, GlobalPosition = p2,
                });
                Input.ParseInputEvent(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left, Pressed = false, Position = p2, GlobalPosition = p2,
                });
            }
        }

        // 把鼠标停在某个页签上，用来看悬停样式
        if (OS.GetCmdlineUserArgs().Contains("--hover-tab"))
        {
            Vector2 p = new(470f, 192f);   // 「音频设置」页签中心
            Input.WarpMouse(p);
            Input.ParseInputEvent(new InputEventMouseMotion { Position = p, GlobalPosition = p });



            // 再按下去，用来看「按下缩小」；--release-tab 时松开并检查切页
            if (OS.GetCmdlineUserArgs().Contains("--press-tab"))
            {

                Input.ParseInputEvent(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left, Pressed = true, Position = p, GlobalPosition = p,
                });
            }
        }
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
        BuildDeckPanel();
        ButtonJuice.AttachAll(_root);
        UiSound.AttachAll(_root);
        RebuildTexts();

        // 切场景瞬间的输入遮罩：从地图选择点「返回」时鼠标还按着，
        // 不挡住的话这一下会穿透到菜单的「出战卡组」上
        BuildTransitionMask();
    }

    private ColorRect _transitionMask;

    /// <summary>
    /// 进场遮罩：铺满屏幕、吃掉所有鼠标事件，约 0.35s 内淡出。
    /// 主要防「上一个场景按下的键在这里被当成点击」。
    /// </summary>
    private void BuildTransitionMask()
    {
        _transitionMask = new ColorRect
        {
            Name = "TransitionMask",
            Color = new Color(0.05f, 0.04f, 0.02f, 0.5f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 60,
        };
        _transitionMask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_transitionMask);

        Tween tween = CreateTween();
        tween.TweenInterval(0.12f);
        tween.TweenProperty(_transitionMask, "color:a", 0f, 0.24f);
        tween.TweenCallback(Callable.From(() =>
        {
            if (_transitionMask != null && IsInstanceValid(_transitionMask))
                _transitionMask.QueueFree();
            _transitionMask = null;
        }));
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
        AddMenuButton(col, "menu.deck", OnDeck);
        AddMenuButton(col, "menu.settings", OpenSettings);
        AddMenuButton(col, "menu.gallery", () =>
        {
            RefreshHistory();
            OpenGallery();
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

    private Tween _settingsTween;
    private ColorRect _settingsDim;

    /// <summary>设置面板：淡入 + 轻微放大。</summary>
    private void OpenSettings()
    {
        if (_settingsPanel == null)
            return;

        _settingsPanel.Visible = true;
        _settingsPanel.PivotOffset = _settingsPanel.Size * 0.5f;
        _settingsPanel.Modulate = new Color(1f, 1f, 1f, 0f);
        _settingsPanel.Scale = Vector2.One * 0.955f;

        KillSettingsTween();
        _settingsTween = CreateTween().SetParallel(true);
        _settingsTween.TweenProperty(_settingsPanel, "modulate:a", 1f, 0.16f);
        _settingsTween.TweenProperty(_settingsPanel, "scale", Vector2.One, 0.22f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);

        if (_settingsDim != null)
        {
            _settingsDim.Visible = true;
            _settingsTween.TweenProperty(_settingsDim, "color:a", 0.45f, 0.16f);
        }
    }

    /// <summary>设置面板：缩小 + 淡出后再隐藏。</summary>
    private void CloseSettings()
    {
        if (_settingsPanel == null || !_settingsPanel.Visible)
            return;

        _settingsPanel.PivotOffset = _settingsPanel.Size * 0.5f;

        KillSettingsTween();
        _settingsTween = CreateTween().SetParallel(true);
        _settingsTween.TweenProperty(_settingsPanel, "modulate:a", 0f, 0.13f);
        _settingsTween.TweenProperty(_settingsPanel, "scale", Vector2.One * 0.965f, 0.13f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);

        if (_settingsDim != null)
            _settingsTween.TweenProperty(_settingsDim, "color:a", 0f, 0.13f);

        _settingsTween.Chain().TweenCallback(Callable.From(() =>
        {
            _settingsPanel.Visible = false;
            _settingsPanel.Scale = Vector2.One;
            _settingsPanel.Modulate = Colors.White;

            if (_settingsDim != null)
                _settingsDim.Visible = false;
        }));
    }

    private void KillSettingsTween()
    {
        if (_settingsTween != null && _settingsTween.IsValid())
            _settingsTween.Kill();
    }
    /// <summary>测试：滚轮是否让设置面板左右滚动。</summary>
    private async void TestSettingsWheel()
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);

        Vector2 p = _settingsPanel.GlobalPosition + _settingsPanel.Size * 0.5f;
        Input.WarpMouse(p);
        GD.Print($"[Wheel] 滚动前 x={_settingsScroll.ScrollHorizontal} " +
                 $"max={_settingsScroll.GetHScrollBar().MaxValue} page={_settingsScroll.GetHScrollBar().Page} panel={_settingsPanel.Size}");

        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.WheelDown, Pressed = true, Position = p, GlobalPosition = p, Factor = 1f,
        });

        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Wheel] 滚轮后 x={_settingsScroll.ScrollHorizontal}");

        // 页签栏：把鼠标移到页签上再滚一次
        Vector2 tp = _tabScroll.GlobalPosition + _tabScroll.Size * 0.5f;
        Input.WarpMouse(tp);
        GD.Print($"[Wheel] 页签栏 滚动前 x={_tabScroll.ScrollHorizontal} " +
                 $"max={_tabScroll.GetHScrollBar().MaxValue} page={_tabScroll.GetHScrollBar().Page}");

        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.WheelDown, Pressed = true, Position = tp, GlobalPosition = tp, Factor = 1f,
        });

        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Wheel] 页签栏 滚轮后 x={_tabScroll.ScrollHorizontal}");
    }
    private async void LogSettingsAnim()
    {
        if (_settingsScroll != null)
            GD.Print($"[Size] panel={_settingsPanel.Size} scroll={_settingsScroll.Size} " +
                     $"hmax={_settingsScroll.GetHScrollBar().MaxValue} hpage={_settingsScroll.GetHScrollBar().Page}");
        GD.Print($"[Anim] t=0 alpha={_settingsPanel.Modulate.A:F2} scale={_settingsPanel.Scale.X:F3}");
        await ToSignal(GetTree().CreateTimer(0.06), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Anim] t=0.06 alpha={_settingsPanel.Modulate.A:F2} scale={_settingsPanel.Scale.X:F3}");
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Anim] t=0.26 alpha={_settingsPanel.Modulate.A:F2} scale={_settingsPanel.Scale.X:F3}");

        if (!OS.GetCmdlineUserArgs().Contains("--also-close"))
            return;

        CloseSettings();
        GD.Print($"[Anim] close t=0 alpha={_settingsPanel.Modulate.A:F2} scale={_settingsPanel.Scale.X:F3} vis={_settingsPanel.Visible}");
        await ToSignal(GetTree().CreateTimer(0.07), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Anim] close t=0.07 alpha={_settingsPanel.Modulate.A:F2} vis={_settingsPanel.Visible}");
        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"[Anim] close t=0.22 alpha={_settingsPanel.Modulate.A:F2} vis={_settingsPanel.Visible}");
    }

    private ScrollContainer _settingsScroll;
    private ScrollContainer _tabScroll;

    /// <summary>面板定宽（窗口太窄时才收窄），内容宽度固定，超出就靠横向滚动看。</summary>
    private const float SettingsContentWidth = 690f;

    private void ApplySettingsSize()
    {
        if (_settingsPanel == null)
            return;

        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        float w = Mathf.Min(760f, Mathf.Max(320f, viewport.X - 80f));

        // 调试：强制收窄面板，用来看横向滚动
        if (OS.GetCmdlineUserArgs().Contains("--narrow-panel"))
            w = 520f;

        if (OS.GetCmdlineUserArgs().Contains("--narrow-tabs"))
            w = 400f;
        float h = Mathf.Min(560f, Mathf.Max(240f, viewport.Y - 80f));

        _settingsPanel.OffsetLeft = -w * 0.5f;
        _settingsPanel.OffsetRight = w * 0.5f;
        _settingsPanel.OffsetTop = -h * 0.5f;
        _settingsPanel.OffsetBottom = h * 0.5f;
    }

    /// <summary>页签栏的滚轮：放不下时左右滚。</summary>
    private void OnTabScrollInput(InputEvent @event)
    {
        if (_tabScroll == null)
            return;

        if (@event is not InputEventMouseButton { Pressed: true } mb)
            return;

        if (mb.ButtonIndex != MouseButton.WheelUp && mb.ButtonIndex != MouseButton.WheelDown)
            return;

        float overflow = (float)_tabScroll.GetHScrollBar().MaxValue - _tabScroll.Size.X;
        if (overflow <= 0f)
            return;

        int step = mb.ButtonIndex == MouseButton.WheelDown ? 80 : -80;
        _tabScroll.ScrollHorizontal = Mathf.Clamp(
            _tabScroll.ScrollHorizontal + step, 0, Mathf.RoundToInt(overflow));

        _tabScroll.AcceptEvent();
    }
    /// <summary>滚轮：内容放不下时左右滚动（放得下就交回默认行为）。</summary>
    private void OnSettingsScrollInput(InputEvent @event)
    {
        if (_settingsScroll == null)
            return;

        if (@event is not InputEventMouseButton { Pressed: true } mb)
            return;

        if (mb.ButtonIndex != MouseButton.WheelUp && mb.ButtonIndex != MouseButton.WheelDown)
            return;

        float overflow = (float)_settingsScroll.GetHScrollBar().MaxValue - _settingsScroll.Size.X;
        if (overflow <= 0f)
            return;

        int step = mb.ButtonIndex == MouseButton.WheelDown ? 60 : -60;
        _settingsScroll.ScrollHorizontal = Mathf.Clamp(
            _settingsScroll.ScrollHorizontal + step, 0, Mathf.RoundToInt(overflow));

        _settingsScroll.AcceptEvent();
    }
    /// <summary>选中的分类页：按钮保持按下态，对应页面显示。</summary>
    private void SelectSettingsTab(int index)
    {
        _currentTab = Mathf.Clamp(index, 0, _tabButtons.Count - 1);

        for (int i = 0; i < _tabButtons.Count; i++)
            _tabButtons[i].ButtonPressed = i == _currentTab;

        for (int i = 0; i < _tabPages.Count; i++)
            _tabPages[i].Visible = i == _currentTab;
    }

    /// <summary>单个控件的缩放反馈（轴心在中心）。</summary>
    private void TweenControlScale(Control control, float scale, float duration,
                                   Tween.TransitionType trans, Tween.EaseType ease)
    {
        if (control == null || !IsInstanceValid(control))
            return;

        control.PivotOffset = control.Size * 0.5f;

        Tween tween = CreateTween();
        tween.TweenProperty(control, "scale", Vector2.One * scale, duration)
            .SetTrans(trans).SetEase(ease);
    }
    private void BuildSettingsPanel()
    {
        _settingsDim = new ColorRect
        {
            Name = "SettingsDim",
            Visible = false,
            Color = new Color(0f, 0f, 0f, 0f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 6,
        };
        _settingsDim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_settingsDim);

        _settingsPanel = new PanelContainer { Name = "Settings", Visible = false, ZIndex = 12 };
        _settingsPanel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _root.AddChild(_settingsPanel);
        ApplySettingsSize();
        GetViewport().SizeChanged += ApplySettingsSize;

        // 内容定宽 + 靠左；窗口比面板窄时用滚轮左右滚动
        _settingsScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _settingsScroll.GuiInput += OnSettingsScrollInput;

        // 面板纵向布局：内容区（可横向滚动）+ 底部按钮区
        var layout = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        layout.AddThemeConstantOverride("separation", 0);
        _settingsPanel.AddChild(layout);
        layout.AddChild(_settingsScroll);

        var pad = new MarginContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 16);
        pad.AddThemeConstantOverride("margin_right", 16);
        pad.AddThemeConstantOverride("margin_top", 16);
        pad.AddThemeConstantOverride("margin_bottom", 16);
        _settingsScroll.AddChild(pad);

        var outer = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(SettingsContentWidth, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        outer.AddThemeConstantOverride("separation", 10);
        pad.AddChild(outer);

        var title = new Label { Text = Loc.T("settings.title") };
        title.AddThemeFontSizeOverride("font_size", 26);
        outer.AddChild(title);
        _locBindings.Add(("settings.title", () => title.Text = Loc.T("settings.title")));

        // 自绘页签：TabContainer 的页签是引擎内部绘制的，没法单独缩放某一个，
        // 这里用三个 Button 代替，按下时每个按钮可以各自回缩。
        // 按钮定宽 + 靠左；放不下时用滚轮在这条页签栏里左右滚。
        _tabScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 40),
            HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _tabScroll.GuiInput += OnTabScrollInput;
        outer.AddChild(_tabScroll);

        var tabRow = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        tabRow.AddThemeConstantOverride("separation", 4);
        _tabScroll.AddChild(tabRow);

        _tabButtons.Clear();

        string[] tabKeys = { "settings.video", "settings.audio", "settings.camera" };
        for (int i = 0; i < tabKeys.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Text = Loc.T(tabKeys[i]),
                ToggleMode = true,
                CustomMinimumSize = new Vector2(
                    OS.GetCmdlineUserArgs().Contains("--wide-tabs") ? 240f : 160f, 36),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            };
            button.ButtonDown += () => TweenControlScale(button, 0.93f, 0.07f, Tween.TransitionType.Quad, Tween.EaseType.Out);
            button.ButtonUp += () => TweenControlScale(button, 1f, 0.22f, Tween.TransitionType.Back, Tween.EaseType.Out);
            button.Pressed += () => SelectSettingsTab(index);
            tabRow.AddChild(button);
            _tabButtons.Add(button);
        }

        // 页面容器：三页都放进去，只显示当前那页
        var pages = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            CustomMinimumSize = new Vector2(0, 300),
        };
        pages.AddThemeStyleboxOverride("panel",
            UiTheme.Box("res://gameAsset/textures/ui/panel/panel_light.png", 18, 14, 14, 14, 14));
        outer.AddChild(pages);

        _tabPages.Clear();
        _tabPages.Add(BuildVideoTab());
        _tabPages.Add(BuildAudioTab());
        _tabPages.Add(BuildCameraTab());
        foreach (Control page in _tabPages)
        {
            page.Visible = false;
            pages.AddChild(page);
        }

        SelectSettingsTab(OS.GetCmdlineUserArgs().Contains("--video-tab") ? 0 : 2);

        // 底部按钮区：上下两个可伸缩空白把「关闭」夹在剩余空间的正中
        var bottom = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        bottom.AddThemeConstantOverride("separation", 0);
        layout.AddChild(bottom);

        bottom.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        var closePad = new MarginContainer();
        closePad.AddThemeConstantOverride("margin_left", 16);
        closePad.AddThemeConstantOverride("margin_right", 16);
        bottom.AddChild(closePad);

        // 关闭按钮：自己做一套「轻微放大 + 按下回缩」，幅度比 ButtonJuice 小，
        // 避免整条横条放大后压到面板边框
        var close = new Button { Text = Loc.T("settings.close"), CustomMinimumSize = new Vector2(0, 46) };
        close.SetMeta("juice", true);
        close.MouseEntered += () => TweenControlScale(close, 1.025f, 0.12f, Tween.TransitionType.Back, Tween.EaseType.Out);
        close.MouseExited += () => TweenControlScale(close, 1f, 0.12f, Tween.TransitionType.Cubic, Tween.EaseType.Out);
        close.ButtonDown += () => TweenControlScale(close, 0.985f, 0.06f, Tween.TransitionType.Sine, Tween.EaseType.Out);
        close.ButtonUp += () => TweenControlScale(close, 1.025f, 0.15f, Tween.TransitionType.Back, Tween.EaseType.Out);
        close.Pressed += CloseSettings;
        closePad.AddChild(close);
        _locBindings.Add(("settings.close", () => close.Text = Loc.T("settings.close")));

        bottom.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
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

        var vsync = new CheckButton { ButtonPressed = Settings.GetBool("video", "vsync", true) };
        vsync.Toggled += on =>
        {
            Settings.SetBool("video", "vsync", on);
            Settings.Save();
            DisplayServer.WindowSetVsyncMode(on ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        };
        tab.AddChild(BoolRow("settings.vsync", vsync));

        var full = new CheckButton { ButtonPressed = Settings.GetBool("video", "fullscreen", false) };
        full.Toggled += on =>
        {
            Settings.SetBool("video", "fullscreen", on);
            Settings.Save();
            DisplayServer.WindowSetMode(on ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        };
        tab.AddChild(BoolRow("settings.fullscreen", full));

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

    /// <summary>开关类选项：文字在左、开关在右，占同一行（不再各占一行）。</summary>
    private Control BoolRow(string key, CheckButton toggle)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);

        Label label = NewRowLabel(key);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(label);

        toggle.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        toggle.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(toggle);

        return row;
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

    private Tween _galleryTween;
    private ColorRect _galleryDim;

    /// <summary>总览面板：淡入 + 轻微放大，同时压暗背景。</summary>
    private void OpenGallery()
    {
        if (_galleryPanel == null)
            return;

        _galleryPanel.Visible = true;
        _galleryPanel.PivotOffset = _galleryPanel.Size * 0.5f;
        _galleryPanel.Modulate = new Color(1f, 1f, 1f, 0f);
        _galleryPanel.Scale = Vector2.One * 0.955f;

        KillGalleryTween();
        _galleryTween = CreateTween().SetParallel(true);
        _galleryTween.TweenProperty(_galleryPanel, "modulate:a", 1f, 0.16f);
        _galleryTween.TweenProperty(_galleryPanel, "scale", Vector2.One, 0.22f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);

        if (_galleryDim != null)
        {
            _galleryDim.Visible = true;
            _galleryTween.TweenProperty(_galleryDim, "color:a", 0.45f, 0.16f);
        }
    }

    /// <summary>总览面板：缩小 + 淡出后再隐藏，遮罩同步。</summary>
    private void CloseGallery()
    {
        if (_galleryPanel == null || !_galleryPanel.Visible)
            return;

        _galleryPanel.PivotOffset = _galleryPanel.Size * 0.5f;

        KillGalleryTween();
        _galleryTween = CreateTween().SetParallel(true);
        _galleryTween.TweenProperty(_galleryPanel, "modulate:a", 0f, 0.13f);
        _galleryTween.TweenProperty(_galleryPanel, "scale", Vector2.One * 0.965f, 0.13f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);

        if (_galleryDim != null)
            _galleryTween.TweenProperty(_galleryDim, "color:a", 0f, 0.13f);

        _galleryTween.Chain().TweenCallback(Callable.From(() =>
        {
            _galleryPanel.Visible = false;
            _galleryPanel.Scale = Vector2.One;
            _galleryPanel.Modulate = Colors.White;

            if (_galleryDim != null)
                _galleryDim.Visible = false;
        }));
    }

    private void KillGalleryTween()
    {
        if (_galleryTween != null && _galleryTween.IsValid())
            _galleryTween.Kill();
    }
    private void BuildGalleryPanel()
    {
        _galleryDim = new ColorRect
        {
            Name = "GalleryDim",
            Visible = false,
            Color = new Color(0f, 0f, 0f, 0f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 6,
        };
        _galleryDim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_galleryDim);

        _galleryPanel = new PanelContainer { Name = "Gallery", Visible = false, ZIndex = 12 };
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

        // 撑开中间，把「返回」压到面板最底部
        outer.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        var back = new Button { Text = Loc.T("common.back"), CustomMinimumSize = new Vector2(0, 46) };
        back.Pressed += CloseGallery;
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

    private DeckBuilderPanel _deckPanel;

    private void BuildDeckPanel()
    {
        _deckPanel = new DeckBuilderPanel();
        _root.AddChild(_deckPanel);
        _deckPanel.Saved += deck =>
        {
            ShowToast(Loc.T("deck.saved"));
            GD.Print($"[Deck] 保存卡组 {deck.Count} 张：{string.Join(",", deck)}");
        };
    }

    private void OnDeck()
    {
        _deckPanel.Open();
        UiSound.AttachAll(_deckPanel);      // 卡组面板是懒加载的，打开后补挂音效
    }

    private void RebuildTexts()
    {
        foreach ((string _, Action onChanged) in _locBindings)
            onChanged();

        string[] tabKeys = { "settings.video", "settings.audio", "settings.camera" };
        for (int i = 0; i < _tabButtons.Count && i < tabKeys.Length; i++)
            _tabButtons[i].Text = Loc.T(tabKeys[i]);
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
            if (_deckPanel != null && _deckPanel.Visible)
                _deckPanel.Close();
            else if (_settingsPanel.Visible)
                CloseSettings();
            else if (_galleryPanel.Visible)
                CloseGallery();
        }
    }
}
