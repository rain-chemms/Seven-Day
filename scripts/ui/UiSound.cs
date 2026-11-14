using System.Collections.Generic;
using Godot;
using SevenDay.Core;

namespace SevenDay.UI;

/// <summary>
/// 通用按钮音效：
///   · 悬停 —— ui/button_hover
///   · 点击（普通按钮） —— ui/button_click_enterOrEnable
///   · 点击（返回/取消/关闭类） —— ui/button_click_backOrDisable
///
/// 用 <see cref="AttachAll"/> 递归挂到整棵 UI 树上；重复调用不会重复挂（靠 meta 判断）。
/// </summary>
public static class UiSound
{
    private const string HoverPath = "res://gameAsset/audios/ui/button_hover.ogg";
    private const string EnterPath = "res://gameAsset/audios/ui/button_click_enterOrEnable.ogg";
    private const string BackPath = "res://gameAsset/audios/ui/button_click_backOrDisable.ogg";

    /// <summary>悬停音效比点击轻一些，不然鼠标扫过一整排按钮会很吵。</summary>
    private const float HoverVolumeDb = -7f;
    private const float ClickVolumeDb = -1.5f;

    private static readonly string[] BackWords =
    {
        "返回", "取消", "关闭", "退出",
        "Back", "Cancel", "Close", "Quit", "Exit",
    };

    private static readonly string[] BackNames =
    {
        "back", "cancel", "close", "quit", "exit",
    };

    /// <summary>给整棵子树里的按钮挂上音效。</summary>
    public static void AttachAll(Node root)
    {
        if (root == null || !GodotObject.IsInstanceValid(root))
            return;

        foreach (Node node in root.FindChildren("*", "BaseButton", true, false))
        {
            if (node is BaseButton button)
                Attach(button);
        }

        if (root is BaseButton self)
            Attach(self);
    }

    public static void Attach(BaseButton button)
    {
        if (button == null || !GodotObject.IsInstanceValid(button) || button.HasMeta("ui_sound"))
            return;

        button.SetMeta("ui_sound", true);

        button.MouseEntered += () => AudioManager.PlaySfx(HoverPath, HoverVolumeDb);
        button.Pressed += () =>
            AudioManager.PlaySfx(IsBackButton(button) ? BackPath : EnterPath, ClickVolumeDb);
    }

    /// <summary>返回 / 取消 / 关闭 / 退出 这类按钮用「后退」音效。</summary>
    public static bool IsBackButton(BaseButton button)
    {
        if (button == null)
            return false;

        string text = (button as Button)?.Text ?? "";
        foreach (string word in BackWords)
        {
            if (!string.IsNullOrEmpty(text) && text.Contains(word))
                return true;
        }

        string name = button.Name.ToString().ToLowerInvariant();
        foreach (string key in BackNames)
        {
            if (name.Contains(key))
                return true;
        }

        return false;
    }
}
