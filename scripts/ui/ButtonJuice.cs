using System.Collections.Generic;
using Godot;

namespace SevenDay.UI;

/// <summary>
/// 全局按钮「果冻」动效，参考视频里的交互手感：
///   · 悬停：放大到 1.07、微微倾斜、整体提亮
///   · 按下：横向撑开、纵向压扁（1.05 × 0.90）
///   · 松开：回弹过冲再落回
///   · 点击：额外一个小「啵」+ 摆动
/// 只动 Scale / Rotation（容器不会覆盖这两个属性），所以放在任何布局里都安全。
/// </summary>
public static class ButtonJuice
{
    private const float HoverScale = 1.07f;
    private const float PressScaleX = 1.06f;
    private const float PressScaleY = 0.90f;
    private const float TiltDegrees = 1.6f;

    private sealed class State
    {
        public bool Hover;
        public Tween Tween;
        public float Tilt;
        public Vector2 BaseScale = Vector2.One;
    }

    private static readonly Dictionary<ulong, State> States = new();

    /// <summary>把场景里所有按钮都挂上动效。</summary>
    public static void AttachAll(Node root)
    {
        if (root == null)
            return;

        foreach (Node node in root.FindChildren("*", "Button", true, false))
        {
            if (node is Button b)
                Attach(b);
        }
    }

    public static void Attach(Button button)
    {
        if (button == null || button.HasMeta("juice"))
            return;

        button.SetMeta("juice", true);
        ulong id = button.GetInstanceId();

        // 用实例 id 决定倾斜方向，避免所有按钮歪向同一边
        float tilt = (id % 2 == 0 ? 1f : -1f) * TiltDegrees;
        var state = new State { Tilt = tilt };
        States[id] = state;

        button.PivotOffset = button.Size * 0.5f;
        button.Resized += () => button.PivotOffset = button.Size * 0.5f;

        button.MouseEntered += () =>
        {
            state.Hover = true;
            button.ZIndex = 10;
            // 悬停只放大，不做任何倾斜（保持水平）
            Animate(button, state, new Vector2(HoverScale, HoverScale), 0f, 0.14f,
                Tween.TransitionType.Back, Tween.EaseType.Out);
        };

        button.MouseExited += () =>
        {
            state.Hover = false;
            button.ZIndex = 0;
            Animate(button, state, Vector2.One, 0f, 0.14f,
                Tween.TransitionType.Cubic, Tween.EaseType.Out);
        };

        button.ButtonDown += () =>
        {
            button.ZIndex = 20;
            Animate(button, state, new Vector2(PressScaleX, PressScaleY), 0f, 0.07f,
                Tween.TransitionType.Sine, Tween.EaseType.Out);
        };

        button.ButtonUp += () =>
        {
            // 松手：一条时间轴走完「弹出去 → 甩一下 → 回正」，中间不给别的补间打断，
            // 否则倾斜会在半路被强行归零，看起来又抖又僵。
            Vector2 startScale = button.Scale;
            float startRot = button.Rotation;
            Vector2 overshoot = new(1.10f, 1.10f);
            Vector2 restScale = state.Hover ? new Vector2(HoverScale, HoverScale) : Vector2.One;
            float tiltRad = Mathf.DegToRad(state.Tilt * 0.9f);

            if (state.Tween != null && state.Tween.IsValid())
                state.Tween.Kill();

            Tween tw = button.CreateTween();
            state.Tween = tw;

            tw.TweenMethod(Callable.From<float>(t =>
            {
                button.Scale = startScale.Lerp(overshoot, EaseOutCubic(t));
                button.Rotation = Mathf.Lerp(startRot, tiltRad, EaseOutCubic(t));
            }), 0f, 1f, 0.07f);

            tw.TweenMethod(Callable.From<float>(t =>
            {
                button.Scale = overshoot.Lerp(restScale, EaseOutBack(t));
                button.Rotation = Mathf.Lerp(tiltRad, 0f, EaseOutBack(t));
            }), 0f, 1f, 0.24f);

            tw.TweenCallback(Callable.From(() =>
            {
                button.Scale = restScale;
                button.Rotation = 0f;
                button.ZIndex = state.Hover ? 10 : 0;
            }));
        };
    }

    private static void Animate(Button button, State state, Vector2 scale, float rotationDeg,
        float duration, Tween.TransitionType trans, Tween.EaseType ease)
    {
        if (state.Tween != null && state.Tween.IsValid())
            state.Tween.Kill();

        state.Tween = button.CreateTween();
        state.Tween.SetParallel(true);
        state.Tween.TweenProperty(button, "scale", scale, duration)
            .SetTrans(trans).SetEase(ease);
        state.Tween.TweenProperty(button, "rotation", Mathf.DegToRad(rotationDeg), duration)
            .SetTrans(trans).SetEase(ease);
    }

    private static float EaseOutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);

    private static float EaseOutBack(float t)
    {
        const float C1 = 1.70158f;
        const float C3 = C1 + 1f;
        float p = t - 1f;
        return 1f + C3 * p * p * p + C1 * p * p;
    }

    /// <summary>调试/测试用：查询某个按钮当前的缩放。</summary>
    public static Vector2 DebugScale(Button button) => button?.Scale ?? Vector2.One;
}
