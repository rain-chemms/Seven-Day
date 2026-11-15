using Godot;

namespace SevenDay.Core;

/// <summary>
/// 全局动画速度（「加速模式」）。用 Engine.TimeScale 统一加速：
/// Tween、SceneTreeTimer、粒子、物理插值都会按倍率播放，
/// 因此卡牌动画与单位移动/攻击动画一起变快，不用逐个改时长。
/// </summary>
public static class AnimSpeed
{
    public const float FastMultiplier = 2.0f;

    public static bool Fast { get; private set; }

    /// <summary>从设置读取并应用（启动时调用）。</summary>
    public static void ApplyFromSettings() => Apply(Settings.GetBool("video", "fast_mode", false), save: false);

    public static void Apply(bool fast, bool save = true)
    {
        Fast = fast;
        Engine.TimeScale = fast ? FastMultiplier : 1.0;

        if (!save)
            return;

        Settings.SetBool("video", "fast_mode", fast);
        Settings.Save();
    }
}