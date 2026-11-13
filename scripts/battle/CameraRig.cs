using Godot;
using SevenDay.Core;

namespace SevenDay.Battle;

/// <summary>RTS 视角相机：绕焦点旋转、缩放、平移，并提供屏幕坐标到地面的射线求交。</summary>
public partial class CameraRig : Node3D
{
    public Camera3D Camera { get; private set; }

    private Vector3 _focus = Vector3.Zero;
    private float _distance = 20f;
    private float _yawDeg = 35f;
    private float _pitchDeg = 52f;

    private const float MinDistance = 6f;
    private const float MaxDistance = 34f;
    private const float MinPitch = 28f;
    private const float MaxPitch = 82f;

    public override void _Ready()
    {
        Camera = new Camera3D
        {
            Name = "Camera3D",
            Current = true,
            Fov = 45f,
            Near = 0.1f,
            Far = 400f,
        };
        AddChild(Camera);
        LoadSettings();
        Apply();
    }

    // ─────────────────────────── 操作速度（可在设置里调） ───────────────────────────

    private float _panSpeed = 1f;
    private float _rotateSpeed = 1f;
    private float _zoomSpeed = 1f;

    /// <summary>从设置里读战斗摄像机的手感倍率（1 = 默认）。</summary>
    public void LoadSettings()
    {
        _panSpeed = Mathf.Clamp(Core.Settings.GetFloat("camera", "pan", 1f), 0.1f, 4f);
        _rotateSpeed = Mathf.Clamp(Core.Settings.GetFloat("camera", "rotate", 1f), 0.1f, 4f);
        _zoomSpeed = Mathf.Clamp(Core.Settings.GetFloat("camera", "zoom", 1f), 0.1f, 4f);
    }

    public void FocusOn(Vector3 world)
    {
        _focus = world;
        Apply();
    }

    /// <summary>按地图范围自动取景：distance 是相机到焦点的距离，pitch 是俯角。</summary>
    public void FrameMap(float mapSpan, float extraDistance = 1.12f)
    {
        _distance = Mathf.Clamp(mapSpan * extraDistance, MinDistance, MaxDistance);
        Apply();
    }

    public void SetPitch(float degrees)
    {
        _pitchDeg = Mathf.Clamp(degrees, MinPitch, MaxPitch);
        Apply();
    }

    /// <summary>把画面整体上下挪一点，避开底部手牌区（负值让地图往上走）。</summary>
    public void SetVerticalOffset(float offset)
    {
        if (Camera != null)
            Camera.VOffset = offset;
    }

    // ─────────────────────────── 初始视角记忆 / 复位 ───────────────────────────

    private Vector3 _initFocus;
    private float _initDistance = 20f;
    private float _initYaw;
    private float _initPitch = 55f;
    private bool _hasInit;
    private Tween _resetTween;

    /// <summary>把当前取景记成「初始视角」，之后可以一键复位。</summary>
    public void SaveInitialView()
    {
        _initFocus = _focus;
        _initDistance = _distance;
        _initYaw = _yawDeg;
        _initPitch = _pitchDeg;
        _hasInit = true;
    }

    /// <summary>调试用：打印当前取景参数。</summary>
    public string DebugState() =>
        $"focus={_focus} d={_distance:F2} yaw={_yawDeg:F1} pitch={_pitchDeg:F1} " +
        $"init=({_initFocus} {_initDistance:F2} {_initYaw:F1} {_initPitch:F1})";

    /// <summary>复位到初始视角（带一点过渡，不是瞬移）。</summary>
    public void ResetView(float duration = 0.35f)
    {
        if (!_hasInit)
            return;

        Vector3 fromFocus = _focus;
        float fromDistance = _distance;
        float fromYaw = _yawDeg;
        float fromPitch = _pitchDeg;

        // yaw 走最短路径，避免绕一大圈
        float yawDelta = Mathf.Wrap(_initYaw - fromYaw, -180f, 180f);

        _resetTween?.Kill();
        _resetTween = CreateTween();
        _resetTween.TweenMethod(Callable.From<float>(t =>
        {
            _focus = fromFocus.Lerp(_initFocus, t);
            _distance = Mathf.Lerp(fromDistance, _initDistance, t);
            _yawDeg = Mathf.Wrap(fromYaw + yawDelta * t, 0f, 360f);
            _pitchDeg = Mathf.Lerp(fromPitch, _initPitch, t);
            Apply();
        }), 0f, 1f, duration)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.InOut);
    }

    public void Pan(Vector2 screenDelta)
    {
        float scale = _distance * 0.0022f * _panSpeed;
        Basis b = Basis.FromEuler(new Vector3(0, Mathf.DegToRad(_yawDeg), 0));
        Vector3 right = b * Vector3.Right;
        Vector3 forward = b * Vector3.Forward;
        // 抓住画面拖动的手感：光标往哪拖，地图就往哪走
        _focus += (-right * screenDelta.X + forward * screenDelta.Y) * scale;
        Apply();
    }

    public void Strafe(Vector2 input, float delta)
    {
        if (input.LengthSquared() < 0.001f)
            return;
        float speed = _distance * 0.9f * delta;
        Basis b = Basis.FromEuler(new Vector3(0, Mathf.DegToRad(_yawDeg), 0));
        Vector3 right = b * Vector3.Right;
        Vector3 forward = b * Vector3.Forward;
        _focus += (right * input.X + forward * -input.Y) * speed;
        Apply();
    }

    public void Zoom(float steps)
    {
        _distance = Mathf.Clamp(_distance - steps * 1.6f * _zoomSpeed, MinDistance, MaxDistance);
        Apply();
    }

    public void Rotate(float degrees)
    {
        _yawDeg = Mathf.Wrap(_yawDeg + degrees * _rotateSpeed, 0f, 360f);
        Apply();
    }

    public void Tilt(float degrees)
    {
        _pitchDeg = Mathf.Clamp(_pitchDeg + degrees * _rotateSpeed, MinPitch, MaxPitch);
        Apply();
    }

    private float _shakeAmount;
    private float _shakeLeft;
    private float _shakeDuration = 1f;

    /// <summary>屏幕震动（爆炸用）：amount 是世界单位的抖动幅度。</summary>
    public void Shake(float amount, float duration)
    {
        _shakeAmount = Mathf.Max(_shakeAmount, amount);
        _shakeDuration = Mathf.Max(duration, 0.05f);
        _shakeLeft = _shakeDuration;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (_shakeLeft <= 0f)
        {
            _shakeAmount = 0f;
            Apply();
            SetProcess(false);
            return;
        }

        _shakeLeft -= (float)delta;
        Apply();
    }

    private void Apply()
    {
        if (Camera == null)
            return;

        float yaw = Mathf.DegToRad(_yawDeg);
        float pitch = Mathf.DegToRad(_pitchDeg);

        Vector3 offset = new(
            _distance * Mathf.Cos(pitch) * Mathf.Sin(yaw),
            _distance * Mathf.Sin(pitch),
            _distance * Mathf.Cos(pitch) * Mathf.Cos(yaw));

        // 爆炸震动：幅度随时间线性衰减
        Vector3 shake = Vector3.Zero;
        if (_shakeLeft > 0f)
        {
            float k = _shakeAmount * Mathf.Clamp(_shakeLeft / _shakeDuration, 0f, 1f);
            shake = new Vector3((float)GD.RandRange(-k, k), (float)GD.RandRange(-k, k) * 0.6f, 0f);
        }

        Camera.GlobalPosition = _focus + offset + shake;
        Camera.LookAt(_focus, Vector3.Up);
    }

    /// <summary>屏幕坐标 → 地面 (y = planeY) 的世界坐标。</summary>
    public bool TryPickGround(Vector2 screenPos, float planeY, out Vector3 world)
    {
        world = Vector3.Zero;
        if (Camera == null)
            return false;

        Vector3 origin = Camera.ProjectRayOrigin(screenPos);
        Vector3 dir = Camera.ProjectRayNormal(screenPos);
        if (Mathf.Abs(dir.Y) < 0.0001f)
            return false;

        float t = (planeY - origin.Y) / dir.Y;
        if (t < 0f)
            return false;

        world = origin + dir * t;
        return true;
    }
}
