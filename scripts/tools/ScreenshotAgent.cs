using System.Linq;
using Godot;

namespace SevenDay.Tools;

/// <summary>
/// 开发期自动截图：命令行带 <c>--shot=path.png</c> 时，等画面稳定后把视口存成 PNG 再退出。
/// 只在传参时生效，不影响正常游戏。
/// 用法：Godot.exe --path . res://scenes/battleScene.tscn -- --shot=D:/shots/battle.png
/// </summary>
public partial class ScreenshotAgent : Node
{
    private string _path;
    private double _wait;
    private double _elapsed;
    private bool _done;

    public override void _Ready()
    {
        _path = OS.GetCmdlineUserArgs()
            .Concat(OS.GetCmdlineArgs())
            .FirstOrDefault(a => a.StartsWith("--shot="))
            ?.Substring("--shot=".Length);

        if (string.IsNullOrEmpty(_path))
            return;

        _wait = 1.6;
        string waitArg = OS.GetCmdlineUserArgs()
            .FirstOrDefault(a => a.StartsWith("--shot-wait="));
        if (waitArg != null && float.TryParse(waitArg.Substring("--shot-wait=".Length), out float w))
            _wait = Mathf.Clamp(w, 0.2f, 20f);

        GD.Print($"[ScreenshotAgent] 将在 {_wait:0.0}s 后截图到 {_path}");
    }

    public override void _Process(double delta)
    {
        if (string.IsNullOrEmpty(_path) || _done)
            return;

        _elapsed += delta;
        if (_elapsed < _wait)
            return;

        _done = true;
        Capture();
    }

    private async void Capture()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image img = GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(_path);
        GD.Print($"[ScreenshotAgent] saved={err} size={img.GetWidth()}x{img.GetHeight()} -> {_path}");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }
}
