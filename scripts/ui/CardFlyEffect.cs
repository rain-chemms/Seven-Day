using System;
using Godot;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>
/// 出牌飞行动画：复制一张卡，沿曲线飞向目标格并逐渐缩小，落地后回调生效。
/// </summary>
public partial class CardFlyEffect : Control
{
    private CardView _card;
    private Vector2 _from;
    private Vector2 _to;
    private Vector2 _ctrl;
    private float _duration;
    private float _t;
    private Action _onArrive;
    private bool _fired;

    public CardFlyEffect()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        SetProcess(true);
    }

    /// <summary>从 from（卡牌位置）飞到 to（目标格屏幕坐标），duration 秒后回调。</summary>
    public void Play(CardDef def, Rect2 from, Vector2 to, float duration = 0.38f)
    {
        _from = new Vector2(from.Position.X + from.Size.X * 0.5f, from.Position.Y + from.Size.Y * 0.5f);
        _to = to;
        _duration = Mathf.Max(duration, 0.12f);
        _t = 0f;
        _fired = false;

        // 控制点往上方拱一下，形成弧线
        float dist = _from.DistanceTo(_to);
        _ctrl = (_from + _to) * 0.5f + new Vector2(0f, -Mathf.Clamp(dist * 0.35f, 40f, 220f));

        _card = new CardView { MouseFilter = MouseFilterEnum.Ignore };
        _card.Setup(def, -1, true);
        _card.Position = _from - new Vector2(CardView.CardWidth * 0.5f, CardView.CardHeight * 0.5f);
        _card.PivotOffset = new Vector2(CardView.CardWidth * 0.5f, CardView.CardHeight * 0.5f);
        AddChild(_card);

        Visible = true;
    }

    public override void _Process(double delta)
    {
        if (_card == null)
            return;

        _t += (float)delta / _duration;
        float t = Mathf.Clamp(_t, 0f, 1f);

        // 位置：二次贝塞尔；先快后慢
        float e = 1f - Mathf.Pow(1f - t, 2f);
        Vector2 p = (1 - e) * (1 - e) * _from + 2 * (1 - e) * e * _ctrl + e * e * _to;

        // 缩放：1 → 0.22，同时稍微淡出
        float scale = Mathf.Lerp(1f, 0.22f, e);
        _card.Position = p - new Vector2(CardView.CardWidth * 0.5f, CardView.CardHeight * 0.5f) * scale;
        _card.Scale = Vector2.One * scale;
        _card.Modulate = new Color(1f, 1f, 1f, Mathf.Lerp(1f, 0.55f, e));

        if (t < 1f)
            return;

        // 落地：触发一次生效，然后自毁
        if (!_fired)
        {
            _fired = true;
            _onArrive?.Invoke();
        }

        _card.QueueFree();
        _card = null;
        QueueFree();
    }

    public Action OnArrive
    {
        set => _onArrive = value;
    }
}
