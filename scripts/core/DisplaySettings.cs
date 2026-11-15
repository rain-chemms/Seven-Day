using Godot;

namespace SevenDay.Core;

/// <summary>
/// 启动时把「视频设置」里的分辨率 / 垂直同步 / 全屏应用到窗口上。
/// 之前这些只在设置面板里切换时生效，下次启动会回到窗口模式。
/// </summary>
public static class DisplaySettings
{
    public static readonly Vector2I[] Resolutions =
    {
        new(1280, 720),
        new(1600, 900),
        new(1920, 1080),
    };

    public static void ApplyFromSettings()
    {
        int index = Mathf.Clamp(Settings.GetInt("video", "resolution", 0), 0, Resolutions.Length - 1);
        bool fullscreen = Settings.GetBool("video", "fullscreen", false);
        bool vsync = Settings.GetBool("video", "vsync", true);

        DisplayServer.WindowSetVsyncMode(vsync
            ? DisplayServer.VSyncMode.Enabled
            : DisplayServer.VSyncMode.Disabled);

        Vector2I size = Resolutions[index];

        // 全屏时尺寸由显示器决定，只在窗口模式里设置大小并居中
        if (!fullscreen)
        {
            DisplayServer.WindowSetSize(size);
            Vector2I screen = DisplayServer.ScreenGetSize();
            Vector2I pos = (screen - size) / 2;
            DisplayServer.WindowSetPosition(new Vector2I(Mathf.Max(0, pos.X), Mathf.Max(0, pos.Y)));
        }

        DisplayServer.WindowSetMode(fullscreen
            ? DisplayServer.WindowMode.Fullscreen
            : DisplayServer.WindowMode.Windowed);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--anim-log") >= 0)
        {
            GD.Print($"[Display] 设置 分辨率={size} 全屏={fullscreen} 垂直同步={vsync} " +
                     $"当前模式={DisplayServer.WindowGetMode()}");
        }
    }
}
