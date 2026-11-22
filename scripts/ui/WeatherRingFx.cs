using Godot;

namespace SevenDay.UI;

/// <summary>天气播报的中心扩散圈，类似绯想天则天气提示的圆形扩散。</summary>
public partial class WeatherRingFx : Control
{
    public Color RingColor = Colors.White;
    public float Progress = 1f;
    private float _age;
    private bool _active;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        SetProcess(true);
    }

    public void Play(Color color)
    {
        RingColor = color;
        _age = 0f;
        Progress = 0f;
        _active = true;
        SetProcess(true);
        Visible = true;
        Size = GetViewportRect().Size;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (!_active)
            return;

        _age += (float)delta;
        Progress = Mathf.Clamp(_age / 2.6f, 0f, 1f);
        QueueRedraw();
        if (Progress >= 1f)
            _active = false;
    }

    public override void _Draw()
    {
        if (Progress <= 0.001f || Progress >= 0.999f)
            return;

        Vector2 center = GetViewportRect().Size * 0.5f;
        Color ringColor = RingColor.Lerp(Colors.White, 0.45f);
        for (int k = 0; k < 3; k++)
        {
            float t = Mathf.Clamp(Progress * 1.35f - k * 0.16f, 0f, 1f);
            if (t <= 0f || t >= 1f)
                continue;

            float eased = 1f - Mathf.Pow(1f - t, 3f);
            float radius = Mathf.Lerp(72f, 560f, eased);
            float alpha = (1f - t) * 0.80f;
            float width = Mathf.Lerp(16f - k * 2f, 1.5f, eased);
            int segments = 128;
            Color rc = new(ringColor.R, ringColor.G, ringColor.B, alpha);
            Color shadow = new(0f, 0f, 0f, alpha * 0.16f);
            Vector2 prev = center + new Vector2(radius, 0f);
            for (int j = 1; j <= segments; j++)
            {
                float angle = Mathf.Tau * j / segments;
                Vector2 pt = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                DrawLine(prev, pt, shadow, width * 1.15f, true);
                DrawLine(prev, pt, rc, width, true);
                prev = pt;
            }
        }

        // 中心柔和亮斑，让扩散圈有起点。
        float core = 1f - Mathf.Clamp(Progress * 1.8f, 0f, 1f);
        if (core > 0.01f)
            DrawCircle(center, 90f * core + 18f,
                new Color(ringColor.R, ringColor.G, ringColor.B, 0.16f * core));
    }
}
