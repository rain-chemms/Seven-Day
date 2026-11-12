using Godot;

namespace SevenDay.UI;

/// <summary>
/// 出牌指示箭头：从卡牌顶端画一条明显弯曲的箭头指到鼠标/目标格。
/// 动画：沿曲线流动的光点 + 控制点轻微摆动 + 目标环脉动。
/// 目标合法时变绿，非法时变红棕。纯 2D 绘制，画在手牌下面、3D 场景上面。
/// </summary>
public partial class TargetArrow : Control
{
    private static readonly Color ValidColor = new("4FBF5A");
    private static readonly Color InvalidColor = new("C2543E");
    private static readonly Color OutlineColor = new(0.11f, 0.08f, 0.03f, 0.62f);

    private const int Segments = 40;
    private const int FlowDots = 7;
    private const float FlowSpeed = 0.75f;

    private Vector2 _from;
    private Vector2 _to;
    private bool _valid;
    private bool _shown;
    private float _time;

    public TargetArrow()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        SetProcess(true);
    }

    public void Aim(Vector2 from, Vector2 to, bool valid)
    {
        _from = from;
        _to = to;
        _valid = valid;
        SetProcess(true);
        _shown = true;
        QueueRedraw();
    }

    public void HideArrow()
    {
        if (!_shown)
            return;
        _shown = false;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (!_shown)
            return;

        _time += (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_shown)
            return;

        Color color = _valid ? ValidColor : InvalidColor;
        float dist = _from.DistanceTo(_to);

        // 距离太近就不画，免得在卡牌上抖一团
        if (dist < 26f)
            return;

        // 二次贝塞尔：控制点往垂直方向顶出去一大截，做出一条明显的弧；
        // 再叠一点正弦摆动，让箭头看起来是「活的」。
        Vector2 dir = (_to - _from) / dist;
        Vector2 perp = new(-dir.Y, dir.X);
        Vector2 mid = (_from + _to) * 0.5f;

        float bend = Mathf.Clamp(dist * 0.38f, 50f, 220f);
        float wobble = Mathf.Sin(_time * 4.2f) * 9f;
        Vector2 ctrl = mid + perp * (bend + wobble);

        var pts = new Vector2[Segments];
        for (int i = 0; i < Segments; i++)
        {
            float t = i / (float)(Segments - 1);
            float u = 1f - t;
            pts[i] = u * u * _from + 2f * u * t * ctrl + t * t * _to;
        }

        // 深色底 + 彩色主线
        DrawPolyline(pts, OutlineColor, 13f, true);
        DrawPolyline(pts, color, 6.5f, true);

        // 沿曲线流动的光点
        for (int i = 0; i < FlowDots; i++)
        {
            float t = Mathf.PosMod(_time * FlowSpeed + i / (float)FlowDots, 1f);
            Vector2 p = Bezier(_from, ctrl, _to, t);
            float fade = Mathf.Sin(t * Mathf.Pi); // 两端淡、中间实
            float r = 3f + 3.2f * fade;
            DrawCircle(p, r + 2f, new Color(OutlineColor, 0.5f * fade));
            DrawCircle(p, r, new Color(color, Mathf.Clamp(0.35f + 0.65f * fade, 0f, 1f)));
        }

        // 箭头
        Vector2 tipDir = (pts[Segments - 1] - pts[Segments - 4]).Normalized();
        if (tipDir.LengthSquared() < 0.001f)
            tipDir = dir;
        Vector2 normal = new(-tipDir.Y, tipDir.X);

        // 箭头的「脖子」稍微加宽一点，看起来更有力
        Vector2 baseCenter = _to - tipDir * 24f;
        Vector2[] head =
        {
            _to,
            baseCenter + normal * 13f,
            baseCenter - normal * 13f,
        };
        DrawColoredPolygon(head, OutlineColor);
        Vector2[] headInner =
        {
            _to - tipDir * 3.5f,
            baseCenter + normal * 8f,
            baseCenter - normal * 8f,
        };
        DrawColoredPolygon(headInner, color);

        // 起点圆点，带呼吸
        float pulse = 1f + 0.12f * Mathf.Sin(_time * 6f);
        DrawCircle(_from, 8f * pulse, OutlineColor);
        DrawCircle(_from, 5f * pulse, color);

        // 目标环：脉动 + 一圈向内收的提示环
        if (_valid)
        {
            float ring = 17f + 3.5f * Mathf.Sin(_time * 7f);
            DrawArc(_to, ring, 0f, Mathf.Tau, 40, color, 3f, true);

            float inward = Mathf.PosMod(_time * 1.6f, 1f);
            float r2 = Mathf.Lerp(30f, 12f, inward);
            DrawArc(_to, r2, 0f, Mathf.Tau, 40, new Color(color, 0.55f * (1f - inward)), 2f, true);
        }
    }

    private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, float t)
    {
        float u = 1f - t;
        return u * u * a + 2f * u * t * b + t * t * c;
    }
}
