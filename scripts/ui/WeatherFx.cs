using Godot;
using SevenDay.Battle;
using SevenDay.Core;

namespace SevenDay.UI;

/// <summary>天气 2D 屏幕特效：雨、雷击、沙尘、极光、月虹、礼物与龙卷风提示。</summary>
public partial class WeatherFx : Control
{
    private BattleState _state;
    private Camera3D _camera;
    private float _time;
    private float _flash;
    private float _weatherAge;
    private float _fxAlpha;
    private bool _fadingOut;
    private WeatherType _displayWeather = WeatherType.None;
    private Vector2 _tornadoScreen;
    private bool _tornadoScreenReady;
    private WeatherType _pendingWeather = WeatherType.None;
    private int _lastSerial = -1;
    private int _lastStrikeSerial = -1;
    private ColorRect _flashOverlay;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // 独立于天气特效 Modulate 的闪光层，天气淡入期间也能立刻看到落雷闪光。
        _flashOverlay = new ColorRect
        {
            Color = Colors.Transparent,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            ZIndex = 100,
        };
        GetParent().AddChild(_flashOverlay);
        _flashOverlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _ExitTree()
    {
        if (_flashOverlay != null && GodotObject.IsInstanceValid(_flashOverlay))
            _flashOverlay.QueueFree();
    }

    public void SetState(BattleState state) => _state = state;
    public void SetCamera(Camera3D camera) => _camera = camera;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _time += dt;
        if (_flash > 0f)
            _flash = Mathf.Max(0f, _flash - dt * 1.5f);

        // 新场景第一次看到状态时只初始化天气，不播放闪光。
        bool firstFrame = _lastSerial == -1;

        if (_state != null && _lastSerial != _state.WeatherSerial)
        {
            _lastSerial = _state.WeatherSerial;
            WeatherType next = _state.Weather;
            if (next == WeatherType.None)
            {
                _fadingOut = true;
            }
            else if (_displayWeather == WeatherType.None || _displayWeather == next)
            {
                _displayWeather = next;
                _pendingWeather = WeatherType.None;
                _weatherAge = 0f;
                _fxAlpha = 0f;
                _fadingOut = false;
            }
            else
            {
                _pendingWeather = next;
                _fadingOut = true;
            }

            if (next == WeatherType.Thunderstorm && !firstFrame)
                _flash = 1f;
        }

        if (_state != null && _state.WeatherStrikeSerial != _lastStrikeSerial)
        {
            _lastStrikeSerial = _state.WeatherStrikeSerial;
            if (!firstFrame)
                _flash = 1f;
        }

        if (_fadingOut)
        {
            _weatherAge += dt;
            _fxAlpha = Mathf.MoveToward(_fxAlpha, 0f, dt / 0.8f);
            if (_fxAlpha <= 0.001f)
            {
                _displayWeather = _pendingWeather;
                _pendingWeather = WeatherType.None;
                _fadingOut = false;
                _weatherAge = 0f;
                _fxAlpha = 0f;
            }
        }
        else
        {
            _weatherAge += dt;
            _fxAlpha = Mathf.MoveToward(_fxAlpha, _displayWeather == WeatherType.None ? 0f : 1f, dt / 1.4f);
        }

        if (_displayWeather == WeatherType.Tornado && _state != null)
        {
            Vector2 target = Project(_state.TornadoCoord);
            if (target.X > -100f)
            {
                if (!_tornadoScreenReady)
                {
                    _tornadoScreen = target;
                    _tornadoScreenReady = true;
                }
                else
                {
                    _tornadoScreen = _tornadoScreen.MoveToward(target, 130f * dt);
                }
            }
        }
        else
        {
            _tornadoScreenReady = false;
        }

        Modulate = new Color(1f, 1f, 1f, _fxAlpha);

        if (_flashOverlay != null && GodotObject.IsInstanceValid(_flashOverlay))
        {
            _flashOverlay.Visible = _flash > 0.001f;
            _flashOverlay.Color = new Color(1f, 1f, 0.86f, 0.42f * _flash);
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_state == null || _displayWeather == WeatherType.None)
            return;

        Vector2 size = GetViewportRect().Size;
        switch (_displayWeather)
        {
            case WeatherType.Thunderstorm:
                // 雷雨天整体压暗，雨丝和闪电闪光叠在上面。
                DrawRect(new Rect2(Vector2.Zero, size), new Color(0.05f, 0.07f, 0.13f, 0.26f));
                DrawRain(size, new Color(0.55f, 0.72f, 0.92f, 0.34f), 82, 0.30f);
                DrawLightning(size);
                break;
            case WeatherType.Sandstorm:
                DrawSandstorm(size);
                break;
            case WeatherType.Tornado:
                DrawRect(new Rect2(Vector2.Zero, size), new Color(0.16f, 0.18f, 0.20f, 0.10f));
                DrawWind(size);
                DrawTornado();
                break;
            case WeatherType.Aurora:
                DrawNightSky(size);
                DrawAurora(size);
                DrawStarTwinkle(size);
                break;
            case WeatherType.Moonbow:
                DrawNightSky(size);
                DrawStarTwinkle(size);
                break;
            case WeatherType.GiftRain:
                DrawSnow(size);
                break;
            case WeatherType.ClearSky:
                DrawClearSky(size);
                break;
        }
    }

    private void DrawRain(Vector2 size, Color color, int count, float speed)
    {
        float speedScale = speed / 0.30f;
        for (int i = 0; i < count; i++)
        {
            // 每滴雨有独立的下落速度、横向风和相位，避免整屏雨线一起平移。
            float fall = (470f + (i * 37 % 11) * 42f) * speedScale;
            float wind = -30f + (i * 53 % 9) * 7f;
            float startX = i * 137.17f;
            float startY = i * 89.31f;
            float length = 11f + (i * 19 % 5) * 4f;

            float x = (startX + _time * wind) % (size.X + 120f) - 60f;
            float y = (startY + _time * fall) % (size.Y + 120f) - 60f;
            float dx = wind / Mathf.Max(1f, fall) * length;

            Color drop = new(color.R, color.G, color.B, color.A * (0.65f + (i % 4) * 0.10f));
            DrawLine(new Vector2(x, y), new Vector2(x + dx, y + length),
                drop, 1.1f + (i % 3) * 0.25f, true);
        }
    }

    private void DrawLightning(Vector2 size)
    {
        // 全屏闪光由独立的 _flashOverlay 绘制，不受天气淡入/淡出 Modulate 影响。
    }

    private void DrawSandstorm(Vector2 size)
    {
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.50f, 0.32f, 0.12f, 0.26f));

        for (int i = 0; i < 28; i++)
        {
            float x = (i * 157.3f - _time * (18f + i % 5 * 7f)) % (size.X + 300f) - 150f;
            float y = (i * 83.1f + Mathf.Sin(_time * 0.35f + i) * 24f) % (size.Y + 160f) - 80f;
            float r = 46f + (i % 6) * 24f;
            DrawCircle(new Vector2(x, y), r,
                new Color(0.72f, 0.53f, 0.27f, 0.09f + (i % 3) * 0.025f));
        }

        for (int i = 0; i < 90; i++)
        {
            float x = (i * 173.7f - _time * (220f + i % 7 * 35f)) % (size.X + 80f) - 40f;
            float y = (i * 59.3f + Mathf.Sin(_time * 1.4f + i) * 22f) % (size.Y + 80f) - 40f;
            DrawCircle(new Vector2(x, y), 0.8f + (i % 3) * 0.6f,
                new Color(0.92f, 0.76f, 0.46f, 0.34f + (i % 4) * 0.07f));
        }
    }



    private void DrawClearSky(Vector2 size)
    {
        DrawRect(new Rect2(Vector2.Zero, size), new Color(1f, 0.84f, 0.48f, 0.055f));

        Vector2 sun = new(size.X * 0.82f, size.Y * 0.16f);
        for (int i = 3; i >= 1; i--)
            DrawCircle(sun, 34f + i * 26f, new Color(1f, 0.88f, 0.52f, 0.025f + i * 0.008f));

        for (int i = 0; i < 18; i++)
        {
            float angle = -2.45f + i * 0.12f + Mathf.Sin(_time * 0.12f + i * 0.7f) * 0.02f;
            Vector2 dir = new(Mathf.Cos(angle), Mathf.Sin(angle));
            DrawLine(sun, sun + dir * size.Length() * 1.15f,
                new Color(1f, 0.91f, 0.60f, 0.012f + (i % 4) * 0.007f),
                18f + (i % 3) * 8f, true);
        }

        for (int i = 0; i < 42; i++)
        {
            float x = (i * 143.7f + Mathf.Sin(_time * 0.25f + i) * 18f) % (size.X + 40f) - 20f;
            float y = (i * 79.3f - _time * (5f + i % 5 * 2f)) % (size.Y + 50f) - 25f;
            DrawCircle(new Vector2(x, y), 0.9f + (i % 4) * 0.55f,
                new Color(1f, 0.92f, 0.62f, 0.20f + (i % 3) * 0.09f));
        }

        for (int i = 0; i < 4; i++)
        {
            float y = size.Y * (0.25f + i * 0.16f) + Mathf.Sin(_time * 0.45f + i) * 12f;
            DrawRect(new Rect2(0f, y, size.X, 28f), new Color(1f, 0.93f, 0.72f, 0.018f));
        }
    }

    private void DrawNightSky(Vector2 size)
    {
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.025f, 0.045f, 0.13f, 0.38f));
        for (int i = 0; i < 5; i++)
        {
            float y = i * size.Y * 0.22f;
            DrawRect(new Rect2(0f, y, size.X, size.Y * 0.22f),
                new Color(0.02f, 0.04f, 0.12f, 0.035f - i * 0.005f));
        }
    }

    private void DrawStarTwinkle(Vector2 size)
    {
        for (int i = 0; i < 64; i++)
        {
            float x = (i * 149.3f + _time * (2.2f + (i % 5) * 0.7f)
                + Mathf.Sin(_time * 0.08f + i) * 6f) % size.X;
            float y = (i * 83.7f + Mathf.Cos(_time * 0.07f + i * 1.3f) * 5f) % (size.Y * 0.78f);
            float twinkle = 0.5f + 0.5f * Mathf.Sin(_time * (0.7f + i % 5 * 0.16f) + i * 1.9f);
            float alpha = 0.08f + twinkle * 0.28f;
            Color c = i % 4 == 0
                ? new Color(1f, 0.87f, 0.58f, alpha)
                : new Color(0.78f, 0.88f, 1f, alpha);
            float radius = 0.7f + (i % 3) * 0.55f + twinkle * 0.5f;
            DrawCircle(new Vector2(x, y), radius, c);

            if (i % 9 == 0)
            {
                float len = 2.5f + twinkle * 4.5f;
                DrawLine(new Vector2(x - len, y), new Vector2(x + len, y),
                    new Color(c.R, c.G, c.B, alpha * 0.75f), 0.8f, true);
                DrawLine(new Vector2(x, y - len), new Vector2(x, y + len),
                    new Color(c.R, c.G, c.B, alpha * 0.75f), 0.8f, true);
            }
        }
    }

    private void DrawWind(Vector2 size)
    {
        Color wind = new(0.82f, 0.84f, 0.82f, 0.14f);
        for (int i = 0; i < 44; i++)
        {
            float speed = 180f + (i % 6) * 45f;
            float baseY = (i * 53.7f + _time * (9f + i % 4 * 3f)) % (size.Y + 80f) - 40f;
            float x = (i * 137.3f - _time * speed) % (size.X + 260f) - 130f;
            float len = 70f + (i % 5) * 35f;
            float wave = Mathf.Sin(_time * 1.8f + i * 0.9f) * (6f + i % 3 * 4f);
            float alpha = 0.07f + (i % 4) * 0.025f;
            Color c = new(wind.R, wind.G, wind.B, alpha);

            Vector2 prev = new(x, baseY + wave);
            for (int j = 1; j <= 5; j++)
            {
                float t = j / 5f;
                Vector2 point = new(x + len * t,
                    baseY + wave + Mathf.Sin(t * 5f + i) * 3f);
                DrawLine(prev, point, c, 1.0f + (i % 3) * 0.4f, true);
                prev = point;
            }
        }

        for (int i = 0; i < 24; i++)
        {
            float x = (i * 211.7f - _time * (260f + i % 5 * 35f)) % (size.X + 40f) - 20f;
            float y = (i * 71.3f + Mathf.Sin(_time * 2f + i) * 14f) % (size.Y + 40f) - 20f;
            DrawCircle(new Vector2(x, y), 1.2f + (i % 3) * 0.5f,
                new Color(0.75f, 0.68f, 0.54f, 0.22f));
        }
    }

    private void DrawTornado()
    {
        if (!_tornadoScreenReady)
            return;
        Vector2 p = _tornadoScreen;

        float sway = Mathf.Sin(_time * 1.7f) * 10f;
        for (int i = 0; i < 5; i++)
        {
            float t = i / 4f;
            float y = p.Y - 16f - t * 62f;
            float w = 8f + t * 20f;
            Color c = new(0.55f, 0.50f, 0.44f, 0.22f + t * 0.25f);
            DrawArc(new Vector2(p.X + sway * (1f - t), y), w, 0f, Mathf.Tau, 20, c, 3f, true);
        }
    }

    private void DrawAurora(Vector2 size)
    {
        (Color Color, float Phase, float Speed, float BaseY)[] layers =
        {
            (new Color(0.16f, 0.95f, 0.52f, 1f), 0.0f, 0.18f, 0.08f),
            (new Color(0.34f, 0.82f, 0.94f, 1f), 2.3f, 0.14f, 0.12f),
            (new Color(0.62f, 0.40f, 1.00f, 1f), 4.6f, 0.11f, 0.05f),
        };

        foreach ((Color color, float phase, float speed, float baseY) in layers)
        {
            const int samples = 56;
            var top = new Vector2[samples];
            var bottom = new Vector2[samples];
            for (int i = 0; i < samples; i++)
            {
                float n = i / (float)(samples - 1);
                float x = size.X * n;
                float topY = size.Y * (baseY
                    + 0.035f * Mathf.Sin(n * 9f + _time * speed + phase)
                    + 0.018f * Mathf.Sin(n * 21f - _time * speed * 1.3f));
                float bottomY = size.Y * (0.52f
                    + 0.10f * Mathf.Sin(n * 6f + phase - _time * speed * 0.8f)
                    + 0.045f * Mathf.Sin(n * 17f + _time * speed * 1.1f));
                top[i] = new Vector2(x, topY);
                bottom[i] = new Vector2(x, bottomY);
            }

            // 主帘幕：整块柔和填充，不使用竖线。
            DrawAuroraBand(top, bottom, new Color(color.R, color.G, color.B, 0.055f));

            // 多层半透明内层，形成从顶部到底部的柔光渐变。
            for (int band = 0; band < 4; band++)
            {
                float a = 0.05f + band * 0.12f;
                var innerTop = new Vector2[samples];
                var innerBottom = new Vector2[samples];
                for (int i = 0; i < samples; i++)
                {
                    innerTop[i] = top[i].Lerp(bottom[i], a);
                    innerBottom[i] = top[i].Lerp(bottom[i], 0.96f - band * 0.08f);
                }
                DrawAuroraBand(innerTop, innerBottom,
                    new Color(color.R, color.G, color.B, 0.030f - band * 0.004f));
            }

            // 底边亮带。
            var edgeBottom = new Vector2[samples];
            for (int i = 0; i < samples; i++)
                edgeBottom[i] = bottom[i] + new Vector2(0f, 16f + 7f * Mathf.Sin(i * 0.7f + phase));
            DrawAuroraBand(bottom, edgeBottom,
                new Color(color.R, color.G, color.B, 0.10f));
        }
    }

    private void DrawAuroraBand(Vector2[] top, Vector2[] bottom, Color color)
    {
        var polygon = new Vector2[top.Length + bottom.Length];
        top.CopyTo(polygon, 0);
        for (int i = 0; i < bottom.Length; i++)
            polygon[top.Length + i] = bottom[bottom.Length - 1 - i];
        DrawColoredPolygon(polygon, color);
    }

    private void DrawGifts()
    {
        foreach (HexCoord cell in _state.GiftCells)
        {
            Vector2 p = Project(cell);
            if (p.X < -50f)
                continue;

            float bob = Mathf.Sin(_time * 2.2f + cell.Q) * 3f;
            p.Y += bob;
            DrawRect(new Rect2(p.X - 10f, p.Y - 14f, 20f, 18f), new Color(0.78f, 0.20f, 0.16f, 0.95f));
            DrawRect(new Rect2(p.X - 3f, p.Y - 14f, 6f, 18f), new Color(0.98f, 0.82f, 0.35f, 1f));
            DrawLine(new Vector2(p.X - 10f, p.Y - 7f), new Vector2(p.X + 10f, p.Y - 7f),
                new Color(0.98f, 0.82f, 0.35f, 1f), 2f);
        }
    }

    private void DrawSnow(Vector2 size)
    {
        Color snow = new(1f, 1f, 1f, 0.68f);
        float progress = Mathf.Clamp(_weatherAge / 3.2f, 0f, 1f);
        float revealY = size.Y * progress;
        for (int i = 0; i < 92; i++)
        {
            float speed = 18f + (i % 5) * 6f;
            float sway = Mathf.Sin(_time * 1.5f + i * 2.1f) * 11f
                         + Mathf.Sin(_time * 0.55f + i * 0.7f) * 18f;
            float x = (i * 97.31f + sway) % (size.X + 20f) - 10f;
            float y = (i * 53.17f + _time * speed) % (size.Y + 20f) - 10f;
            // 2D 雪从屏幕顶部逐渐向下铺开。
            if (y > revealY)
                continue;

            float r = 1.9f + i % 3 * 0.8f;
            DrawSnowflake(new Vector2(x, y), r, snow,
                _time * (0.2f + (i % 7) * 0.08f) + i * 0.83f, i % 4);
        }
    }

    private void DrawSnowflake(Vector2 center, float r, Color color, float rotation, int variant)
    {
        float w = Mathf.Max(1f, r * (variant == 2 ? 0.17f : 0.22f));
        for (int k = 0; k < 6; k++)
        {
            float a = rotation + k * Mathf.Pi / 3f;
            Vector2 dir = new(Mathf.Cos(a), Mathf.Sin(a));
            DrawLine(center, center + dir * r, color, w, true);

            if (variant == 0 && r > 2.5f)
            {
                Vector2 at = center + dir * (r * 0.62f);
                for (int side = -1; side <= 1; side += 2)
                {
                    float ba = a + side * Mathf.Pi / 4.2f;
                    Vector2 bdir = new(Mathf.Cos(ba), Mathf.Sin(ba));
                    DrawLine(at, at + bdir * (r * 0.34f), color, w * 0.75f, true);
                }
            }
            else if (variant == 1 && r > 2.2f)
            {
                Vector2 at = center + dir * (r * 0.68f);
                for (int side = -1; side <= 1; side += 2)
                {
                    float ba = a + side * Mathf.Pi / 3.2f;
                    Vector2 bdir = new(Mathf.Cos(ba), Mathf.Sin(ba));
                    DrawLine(at, at + bdir * (r * 0.25f), color, w * 0.8f, true);
                }
            }
            else if (variant == 2 && r > 2.6f)
            {
                for (float frac = 0.35f; frac < 0.85f; frac += 0.26f)
                {
                    Vector2 at = center + dir * (r * frac);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float ba = a + side * Mathf.Pi / 2.4f;
                        Vector2 bdir = new(Mathf.Cos(ba), Mathf.Sin(ba));
                        DrawLine(at, at + bdir * (r * 0.18f), color, w * 0.65f, true);
                    }
                }
            }
            else if (variant == 3 && r > 2.4f)
            {
                Vector2 tip = center + dir * r;
                for (int side = -1; side <= 1; side += 2)
                {
                    float ba = a + side * Mathf.Pi / 2f;
                    Vector2 bdir = new(Mathf.Cos(ba), Mathf.Sin(ba));
                    DrawLine(tip, tip + bdir * (r * 0.24f), color, w * 0.75f, true);
                }
            }
        }
    }

    private Vector2 Project(HexCoord coord)
    {
        if (_camera == null || !IsInstanceValid(_camera))
            return new Vector2(-9999f, -9999f);
        Vector3 world = HexLayout.ToWorld(coord, HexMapView.TileTop + 0.3f);
        return _camera.UnprojectPosition(world);
    }
}
