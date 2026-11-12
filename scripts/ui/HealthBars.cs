using System.Collections.Generic;
using Godot;

namespace SevenDay.UI;

/// <summary>
/// 建筑 / 兵种的血条：把世界坐标投影到屏幕，用 2D 画在上层。
/// 圆角矩形 + 深棕描边 + 米色底槽，血量变化用渐变（还会留一条「残影」慢慢跟上）。
/// </summary>
public partial class HealthBars : Control
{
    public struct Bar
    {
        public int Id;
        public bool Building;
        public Vector3 World;
        public float Ratio;      // 目标血量比例 0..1
        public Color Accent;     // 阵营色
        public bool Selected;
    }

    private static readonly Color Outline = new("3B2E16");
    private static readonly Color SlotColor = new(0.16f, 0.12f, 0.06f, 0.92f);
    private static readonly Color GoodColor = new("7FD35B");
    private static readonly Color WarnColor = new("F2C230");
    private static readonly Color BadColor = new("E8563F");
    private static readonly Color GhostColor = new(1f, 0.95f, 0.82f, 0.55f);

    /// <summary>当前显示值 / 残影值，按实体 id 记住，这样血量变化时能平滑过渡。</summary>
    private readonly Dictionary<(bool, int), float> _shown = new();
    private readonly Dictionary<(bool, int), float> _ghost = new();
    private readonly List<Bar> _bars = new();

    private Camera3D _camera;

    // 圆角样式（复用，避免每帧新建）
    private StyleBoxFlat _outlineStyle;
    private StyleBoxFlat _slotStyle;
    private StyleBoxFlat _selectionStyle;

    public HealthBars()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        SetProcess(true);
    }

    public void SetCamera(Camera3D camera) => _camera = camera;

    /// <summary>每帧由战斗场景更新要画的血条（目标值）。</summary>
    public void SetBars(List<Bar> bars)
    {
        _bars.Clear();
        if (bars != null)
            _bars.AddRange(bars);
    }

    public override void _Process(double delta)
    {
        if (_bars.Count == 0)
        {
            _shown.Clear();
            _ghost.Clear();
            return;
        }

        float dt = (float)delta;
        var alive = new HashSet<(bool, int)>();

        foreach (Bar bar in _bars)
        {
            var key = (bar.Building, bar.Id);
            alive.Add(key);

            if (!_shown.TryGetValue(key, out float shown))
            {
                // 新出现的血条直接从目标值开始，不要从 0 涨上来
                shown = bar.Ratio;
                _ghost[key] = bar.Ratio;
            }

            shown = Mathf.MoveToward(shown, bar.Ratio, dt * 2.4f);
            _shown[key] = shown;

            float ghost = _ghost.GetValueOrDefault(key, shown);
            if (ghost < shown)
                ghost = shown;          // 回血时残影直接跟上
            else
                ghost = Mathf.MoveToward(ghost, shown, dt * 0.9f);   // 掉血时慢慢缩
            _ghost[key] = ghost;
        }

        foreach ((bool, int) key in new List<(bool, int)>(_shown.Keys))
        {
            if (alive.Contains(key))
                continue;
            _shown.Remove(key);
            _ghost.Remove(key);
        }

        QueueRedraw();
    }

    /// <summary>调试：某个血条的显示值 / 残影值。</summary>
    public string DebugShown(bool building, int id)
    {
        var key = (building, id);
        return $"shown={_shown.GetValueOrDefault(key, -1f):F2} ghost={_ghost.GetValueOrDefault(key, -1f):F2}";
    }

    public override void _Draw()
    {
        if (_camera == null || _bars.Count == 0)
            return;

        EnsureStyles();

        foreach (Bar bar in _bars)
        {
            Vector2 p = _camera.UnprojectPosition(bar.World);
            if (p.X < -60 || p.Y < -60 || p.X > Size.X + 60 || p.Y > Size.Y + 60)
                continue;

            float width = bar.Building ? 58f : 42f;
            float height = bar.Building ? 11f : 9f;
            Vector2 origin = new(p.X - width * 0.5f, p.Y);
            var rect = new Rect2(origin, new Vector2(width, height));

            var key = (bar.Building, bar.Id);
            float shown = Mathf.Clamp(_shown.GetValueOrDefault(key, bar.Ratio), 0f, 1f);
            float ghost = Mathf.Clamp(_ghost.GetValueOrDefault(key, shown), 0f, 1f);

            // 深棕描边（圆角）
            DrawStyleBox(_outlineStyle,
                new Rect2(origin - new Vector2(2f, 2f), new Vector2(width + 4f, height + 4f)));

            // 底槽
            DrawStyleBox(_slotStyle, rect);

            // 残影（掉血时慢慢缩回去的那一截）
            if (ghost > shown + 0.002f)
            {
                DrawStyleBox(FillStyle(GhostColor),
                    new Rect2(origin, new Vector2(width * ghost, height)));
            }

            // 实际血量
            if (shown > 0.002f)
            {
                Color fill = shown > 0.55f ? GoodColor : shown > 0.28f ? WarnColor : BadColor;
                float fillWidth = Mathf.Max(width * shown, height);   // 太短时保持圆角形状
                DrawStyleBox(FillStyle(fill), new Rect2(origin, new Vector2(fillWidth, height)));
            }

            // 左侧阵营色小圆点
            DrawCircle(new Vector2(origin.X - 6f, origin.Y + height * 0.5f), 3f, Outline);
            DrawCircle(new Vector2(origin.X - 6f, origin.Y + height * 0.5f), 2f, bar.Accent);

            // 选中的加一圈金框
            if (bar.Selected)
            {
                DrawStyleBox(_selectionStyle,
                    new Rect2(origin - new Vector2(5f, 5f), new Vector2(width + 10f, height + 10f)));
            }
        }
    }

    // 填充色的圆角样式按颜色缓存（绿/黄/红/残影 共 4 个）
    private readonly Dictionary<Color, StyleBoxFlat> _fillStyles = new();

    private StyleBoxFlat FillStyle(Color color)
    {
        if (_fillStyles.TryGetValue(color, out StyleBoxFlat cached))
            return cached;

        var style = new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            AntiAliasing = true,
        };
        _fillStyles[color] = style;
        return style;
    }

    private void EnsureStyles()
    {
        if (_outlineStyle != null)
            return;

        _outlineStyle = new StyleBoxFlat
        {
            BgColor = Outline,
            CornerRadiusBottomLeft = 7,
            CornerRadiusBottomRight = 7,
            CornerRadiusTopLeft = 7,
            CornerRadiusTopRight = 7,
            AntiAliasing = true,
        };

        _slotStyle = new StyleBoxFlat
        {
            BgColor = SlotColor,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            AntiAliasing = true,
        };

        _selectionStyle = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = new Color("F2C230"),
            BorderWidthBottom = 2,
            BorderWidthTop = 2,
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            CornerRadiusBottomLeft = 9,
            CornerRadiusBottomRight = 9,
            CornerRadiusTopLeft = 9,
            CornerRadiusTopRight = 9,
            AntiAliasing = true,
        };
    }
}
