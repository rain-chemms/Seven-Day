using Godot;

namespace SevenDay.UI;

/// <summary>地图选择界面的可缩放/拖动预览。</summary>
public partial class MapPreviewView : Control
{
    private Texture2D _texture;
    private float _zoom = 0.85f;
    private Vector2 _offset;
    private bool _dragging;
    private bool _viewReady;
    private StyleBoxFlat _frame;

    public Texture2D Texture
    {
        get => _texture;
        set
        {
            _texture = value;
            _viewReady = false;
            ResetView();
        }
    }

    public override void _Ready()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.Click;
        CustomMinimumSize = new Vector2(0, 232);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        _frame = new StyleBoxFlat
        {
            BgColor = Colors.Transparent,
            BorderColor = new Color("6F5238"),
            BorderWidthLeft = 5,
            BorderWidthTop = 5,
            BorderWidthRight = 5,
            BorderWidthBottom = 5,
            CornerRadiusTopLeft = 14,
            CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14,
            CornerRadiusBottomRight = 14,
        };
        Resized += OnResized;
    }

    private void OnResized() => ResetView();

    public void ResetView()
    {
        if (_texture == null)
        {
            _viewReady = false;
            QueueRedraw();
            return;
        }

        Vector2 tex = _texture.GetSize();
        float availX = Mathf.Max(1f, Size.X - 16f);
        float availY = Mathf.Max(1f, Size.Y - 16f);
        float fit = Mathf.Min(availX / tex.X, availY / tex.Y);
        _zoom = Mathf.Clamp(fit, 0.05f, 1f);
        _offset = (Size - tex * _zoom) * 0.5f;
        ClampOffset();
        _viewReady = true;
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("FDF2E2"));
        if (_texture == null)
            return;

        DrawTextureRect(_texture,
            new Rect2(_offset, _texture.GetSize() * _zoom), false);
        DrawStyleBox(_frame, new Rect2(Vector2.Zero, Size));
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            {
                ZoomAt(mb.Position, 1.14f);
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            {
                ZoomAt(mb.Position, 1f / 1.14f);
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.Middle)
            {
                _dragging = mb.Pressed;
                if (mb.Pressed)
                    GrabFocus();
                AcceptEvent();
            }
        }
        else if (@event is InputEventMouseMotion mm && _dragging)
        {
            _offset += mm.Relative;
            ClampOffset();
            QueueRedraw();
            AcceptEvent();
        }
    }

    private void ZoomAt(Vector2 mouse, float factor)
    {
        if (_texture == null)
            return;

        float oldZoom = _zoom;
        float newZoom = Mathf.Clamp(_zoom * factor, 0.18f, 3.5f);
        if (Mathf.IsEqualApprox(oldZoom, newZoom))
            return;

        _offset = mouse - (mouse - _offset) * (newZoom / oldZoom);
        _zoom = newZoom;
        ClampOffset();
        QueueRedraw();
    }

    private void ClampOffset()
    {
        if (_texture == null)
            return;

        Vector2 scaled = _texture.GetSize() * _zoom;
        if (scaled.X <= Size.X)
            _offset.X = (Size.X - scaled.X) * 0.5f;
        else
            _offset.X = Mathf.Clamp(_offset.X, Size.X - scaled.X, 0f);

        if (scaled.Y <= Size.Y)
            _offset.Y = (Size.Y - scaled.Y) * 0.5f;
        else
            _offset.Y = Mathf.Clamp(_offset.Y, Size.Y - scaled.Y, 0f);
    }
}
