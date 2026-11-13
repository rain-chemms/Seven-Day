using Godot;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>
/// 小卡牌飞来飞去（抽牌 / 打出 / 洗回）的通用动画。
/// 用法：new 一个塞进 HUD 的 _root，调用 Play(...)，播完自己 QueueFree。
/// </summary>
public partial class CardTossEffect : Control
{
    private string _title = "";
    private int _cost;
    private bool _faceDown;
    private float _t;
    private float _duration = 0.42f;
    private Vector2 _from;
    private Vector2 _to;
    private float _arc = 120f;
    private float _spin;
    private float _startScale = 0.55f;
    private float _endScale = 0.28f;
    private Color _fill = new("6B4A33");
    private System.Action _onDone;

    public CardTossEffect()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public void Play(CardDef def, Vector2 from, Vector2 to, float duration,
                     float endScale = 0.28f, float arc = 120f, bool faceDown = false,
                     System.Action onDone = null)
    {
        _title = def?.NameZh ?? "";
        _cost = def?.Cost ?? 0;
        _faceDown = faceDown;
        _from = from;
        _to = to;
        _duration = Mathf.Max(0.1f, duration);
        _endScale = endScale;
        _arc = arc;
        _spin = (float)GD.RandRange(-1.0, 1.0) * 10f;
        _onDone = onDone;
        _t = 0f;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;

        if (_t >= _duration)
        {
            System.Action done = _onDone;
            _onDone = null;
            done?.Invoke();
            QueueFree();
            return;
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        float k = Mathf.Clamp(_t / _duration, 0f, 1f);
        float ease = 1f - (1f - k) * (1f - k);          // ease-out

        Vector2 pos = _from.Lerp(_to, ease) - new Vector2(0f, Mathf.Sin(k * Mathf.Pi) * _arc);
        float scale = Mathf.Lerp(_startScale, _endScale, ease);
        float alpha = k < 0.7f ? 1f : Mathf.Lerp(1f, 0f, (k - 0.7f) / 0.3f);

        Vector2 cardSize = new Vector2(96f, 128f) * scale;
        Vector2 center = pos;
        Rect2 rect = new(center - cardSize * 0.5f, cardSize);

        DrawSetTransform(center, Mathf.DegToRad(_spin * (1f - ease)), Vector2.One);
        Rect2 local = new(-cardSize * 0.5f, cardSize);

        DrawRect(local.Grow(2f), new Color(0.16f, 0.12f, 0.08f, alpha));
        DrawRect(local, (_faceDown ? new Color("4A3A28") : _fill) with { A = alpha });

        Font font = ThemeDB.FallbackFont;
        if (!_faceDown && scale > 0.35f)
        {
            int size = Mathf.Max(10, Mathf.RoundToInt(13f * scale));
            Vector2 textSize = font.GetStringSize(_title, HorizontalAlignment.Left, -1, size);
            DrawString(font, new Vector2(-textSize.X * 0.5f, local.Size.Y * 0.16f),
                _title, HorizontalAlignment.Left, -1, size,
                new Color(0.96f, 0.91f, 0.82f, alpha));
        }

        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        _ = rect;
    }
}
