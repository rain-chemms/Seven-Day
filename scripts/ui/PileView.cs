using Godot;
using SevenDay.Core;

namespace SevenDay.UI;

/// <summary>
/// 底部角落的牌堆图标：一张卡背 + 右下角的红色数字徽章（类似杀戮尖塔）。
/// 点一下打开查看器。
/// </summary>
public partial class PileView : Control
{
    public enum PileKind
    {
        Deck,
        Discard,
        Exhaust,
    }

    [Signal]
    public delegate void PileClickedEventHandler(int kind);

    public PileKind Kind { get; private set; } = PileKind.Deck;

    private string _title = "抽牌堆";
    private int _count;
    private float _bump;

    private static readonly Color CardFill = new("F2E7CF");
    private static readonly Color CardEdge = new("2E2418");
    private static readonly Color InnerLine = new("C9B48C");
    private static readonly Color Ink = new("F4E9D2");

    public PileView()
    {
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(84, 98);

        MouseEntered += () =>
        {
            AnimateScale(HoverScale, 0.12f);
            AudioManager.PlaySfx("res://gameAsset/audios/ui/button_hover.ogg", -7f);
        };
        MouseExited += () => AnimateScale(1f, 0.14f);
    }

    private const float HoverScale = 1.08f;
    private Tween _scaleTween;

    /// <summary>悬停放大 / 离开还原（以控件中心为轴心）。</summary>
    private void AnimateScale(float target, float duration)
    {
        PivotOffset = Size * 0.5f;
        _scaleTween?.Kill();
        _scaleTween = CreateTween();
        _scaleTween.TweenProperty(this, "scale", Vector2.One * target, duration)
                   .SetTrans(Tween.TransitionType.Quad)
                   .SetEase(Tween.EaseType.Out);
    }

    public void Setup(PileKind kind, string title)
    {
        Kind = kind;
        _title = title;
        Name = $"Pile{kind}";
        QueueRedraw();
    }

    public void SetCount(int count)
    {
        if (_count == count)
            return;

        _count = count;
        QueueRedraw();
    }

    public int Count => _count;

    /// <summary>动画目标点（全局坐标，卡堆中心）。</summary>
    public Vector2 StackCenter => GlobalPosition + new Vector2(Size.X * 0.5f, Size.Y * 0.5f - 10f);

    public void Bump() => _bump = 1f;

    /// <summary>三种牌堆的主题色：抽牌=蓝灰，弃牌=棕，消耗=紫。</summary>
    private Color Accent => Kind switch
    {
        PileKind.Discard => new Color("B07A4A"),
        PileKind.Exhaust => new Color("8A5FA8"),
        _ => new Color("4A7EA8"),
    };

    public override void _Process(double delta)
    {
        if (_bump <= 0f)
            return;

        _bump = Mathf.Max(0f, _bump - (float)delta * 4f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        float pop = 1f + _bump * 0.07f;
        Vector2 center = new(Size.X * 0.5f, Size.Y * 0.5f - Size.Y * 0.06f);
        Vector2 cardSize = new Vector2(Size.X * 0.55f, Size.Y * 0.62f) * pop;

        // 后面的两张卡背（错开一点，做出叠放的厚度）
        for (int i = 2; i >= 1; i--)
        {
            Vector2 off = new Vector2(i * 4f, -i * 4f);
            Rect2 r = new(center - cardSize * 0.5f + off, cardSize);
            DrawRect(r.Grow(1.5f), CardEdge);
            DrawRect(r, CardFill.Darkened(0.10f * i));
        }

        // 正面那张
        Rect2 card = new(center - cardSize * 0.5f, cardSize);
        DrawRect(card.Grow(1.5f), CardEdge);
        DrawRect(card, CardFill);

        // 内部装饰框
        Rect2 inner = card.Grow(-5f);
        DrawRect(inner, InnerLine, false, 1.5f);

        // 中间的图标：抽牌=箭头，弃牌=折角，消耗=火焰
        DrawGlyph(center, cardSize);

        // 右下角红色数字徽章（尺寸跟着控件缩放）
        float badgeRadius = Size.X * 0.135f;
        Vector2 badge = card.Position + new Vector2(cardSize.X - 2f, cardSize.Y - 2f);
        DrawCircle(badge, badgeRadius, CardEdge);
        DrawCircle(badge, badgeRadius - 2f, new Color("C4392F"));

        Font font = ThemeDB.FallbackFont;
        string text = _count.ToString();
        int size = Mathf.Max(9, Mathf.RoundToInt(badgeRadius * 1.1f));
        if (_count > 99)
            size = Mathf.Max(8, size - 2);

        Vector2 textSize = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
        DrawString(font, badge - new Vector2(textSize.X * 0.5f, -textSize.Y * 0.5f + 1f),
            text, HorizontalAlignment.Left, -1, size, new Color("FFF3E2"));

        // 名称
        int nameSize = Mathf.Max(10, Mathf.RoundToInt(Size.X * 0.13f));
        Vector2 nameW = font.GetStringSize(_title, HorizontalAlignment.Left, -1, nameSize);
        DrawString(font, new Vector2(Size.X * 0.5f - nameW.X * 0.5f + 2f, Size.Y - 3f),
            _title, HorizontalAlignment.Left, -1, nameSize, new Color("3A2C1C"));
    }

    private void DrawGlyph(Vector2 center, Vector2 cardSize)
    {
        Color accent = Accent;
        float w = cardSize.X * 0.34f;

        switch (Kind)
        {
            case PileKind.Exhaust:
            {
                // 火焰：两个叠起来的三角
                Vector2[] flame =
                {
                    center + new Vector2(0f, -w * 0.9f),
                    center + new Vector2(w * 0.8f, w * 0.5f),
                    center + new Vector2(-w * 0.8f, w * 0.5f),
                };
                DrawColoredPolygon(flame, accent);
                Vector2[] flame2 =
                {
                    center + new Vector2(0f, -w * 0.35f),
                    center + new Vector2(w * 0.5f, w * 0.5f),
                    center + new Vector2(-w * 0.5f, w * 0.5f),
                };
                DrawColoredPolygon(flame2, accent.Lightened(0.3f));
                break;
            }

            case PileKind.Discard:
            {
                // 折角：一个朝下的方块 + 折线
                Rect2 box = new(center - new Vector2(w * 0.8f, w * 0.8f), new Vector2(w * 1.6f, w * 1.6f));
                DrawRect(box, accent, false, 3f);
                DrawRect(new Rect2(box.Position, new Vector2(box.Size.X, box.Size.Y * 0.55f)),
                    accent with { A = 0.45f });
                break;
            }

            default:
            {
                // 抽牌：向上的箭头
                Vector2[] arrow =
                {
                    center + new Vector2(0f, -w),
                    center + new Vector2(w * 0.85f, w * 0.25f),
                    center + new Vector2(w * 0.35f, w * 0.25f),
                    center + new Vector2(w * 0.35f, w),
                    center + new Vector2(-w * 0.35f, w),
                    center + new Vector2(-w * 0.35f, w * 0.25f),
                    center + new Vector2(-w * 0.85f, w * 0.25f),
                };
                DrawColoredPolygon(arrow, accent);
                break;
            }
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            PlayPressAnimation();
            AudioManager.PlaySfx("res://gameAsset/audios/ui/button_click_enterOrEnable.ogg", -1.5f);
            EmitSignal(SignalName.PileClicked, (int)Kind);
            AcceptEvent();
        }
    }

    private Tween _press;

    /// <summary>点击动画：按下缩一下，再弹回原大小。</summary>
    private void PlayPressAnimation()
    {
        PivotOffset = Size * 0.5f;
        _press?.Kill();
        _press = CreateTween();
        _press.TweenProperty(this, "scale", new Vector2(0.9f, 0.9f), 0.07f)
              .SetTrans(Tween.TransitionType.Quad)
              .SetEase(Tween.EaseType.Out);
        _press.TweenProperty(this, "scale", Vector2.One, 0.18f)
              .SetTrans(Tween.TransitionType.Back)
              .SetEase(Tween.EaseType.Out);
    }
}
