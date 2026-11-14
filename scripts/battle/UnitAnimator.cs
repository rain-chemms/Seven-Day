using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SevenDay.Battle;

/// <summary>
/// 兵种动画：这些 glb 把每个部位拆成独立片段（Idle_Tank_Hull / Idle_Wheel_L1 / Fire_Tank_Turret ...），
/// 而且片段之间会互相重叠（Fire_Tank_Hull 里也有炮塔的轨道）。所以这里把同一状态的片段**合并成一条动画**，
/// 同一条轨道只保留第一次出现的版本，最后每个状态只用一个 AnimationPlayer 播放。
///
/// 状态：
///   · Idle —— 循环，默认
///   · Move —— 循环，移动中
///   · Attack —— 播一次（Fire_ / Shoot_ / Attack_ 前缀），播完自动回到 Idle/Move
///
/// 炮塔（坦克的 Tank_Turret）：从所有轨道里剔除，改为由 <see cref="SetTurretYaw"/> 单独控制朝向。
/// </summary>
public partial class UnitAnimator : Node
{
    /// <summary>Idle / Move / Attack 合成在同一个播放器里，切换时用混入时间做过渡。</summary>
    private AnimationPlayer _player;
    private string _current = "";

    /// <summary>旋翼：不走动画，按固定角速度自己转，避免片段里的缓动造成一快一慢。</summary>
    private sealed class RotorSpin
    {
        public Node3D Node;
        public Vector3 Axis;
        public Quaternion Rest;
        public float RadPerSecond;
        public float Angle;
    }

    private readonly List<RotorSpin> _rotors = new();

    private float _attackDuration;
    private float _attackLeft = -1f;
    private bool _moving;

    /// <summary>「等这一轮移动动画播完再切 Idle」的剩余时间（&lt;0 表示没有等待）。</summary>
    private float _idleDelay = -1f;
    private float _moveLoopLength;

    private Node3D _turret;
    private Vector3 _turretRest = Vector3.Zero;
    private float _turretYaw;
    private bool _hasAny;

    public bool HasAnimations => _hasAny;
    public bool IsAttacking => _attackLeft > 0f;
    public bool HasTurret => _turret != null;

    /// <summary>炮塔当前相对车体的角度（度）。</summary>
    public float TurretYaw => _turret == null ? 0f : _turret.RotationDegrees.Y - _turretRest.Y;

    public static UnitAnimator Build(Node3D model)
    {
        var animator = new UnitAnimator { Name = "UnitAnimator" };
        model.AddChild(animator);

        // 找炮塔节点（坦克的 Tank_Turret）：它会被单独控制朝向，相关轨道要剔除
        foreach (Node n in model.FindChildren("*", "Node3D", true, false))
        {
            if (n is not Node3D n3 || n is AnimationPlayer)
                continue;
            if (!n3.Name.ToString().Contains("Turret"))
                continue;

            animator._turret = n3;
            animator._turretRest = n3.RotationDegrees;
            break;
        }

        string turretName = animator._turret?.Name.ToString();

        var idleClips = new List<Godot.Animation>();
        var moveClips = new List<Godot.Animation>();
        var attackClips = new List<Godot.Animation>();
        var rotorClips = new List<Godot.Animation>();

        foreach (Node n in model.FindChildren("*", "AnimationPlayer", true, false))
        {
            if (n is not AnimationPlayer src)
                continue;

            foreach (string clip in src.GetAnimationList())
            {
                Godot.Animation anim = src.GetAnimation(clip);
                if (anim == null)
                    continue;

                ClipState? state = Classify(clip);
                if (state == null)
                    continue;

                // 旋翼 / 螺旋桨片段单独收集：不参与状态动画，交给常驻的旋翼播放器
                if (clip.Contains("Rotor") || clip.Contains("Prop"))
                {
                    rotorClips.Add(anim);
                    continue;
                }

                switch (state.Value)
                {
                    case ClipState.Idle:
                        idleClips.Add(anim);
                        break;
                    case ClipState.Move:
                        moveClips.Add(anim);
                        break;
                    case ClipState.Attack:
                        attackClips.Add(anim);
                        animator._attackDuration = Mathf.Max(animator._attackDuration, (float)anim.Length);
                        break;
                }
            }
        }

        Godot.Animation idleAnim = Merge(idleClips, loop: true, turretName, model);
        Godot.Animation moveAnim = Merge(moveClips, loop: true, turretName, model);
        Godot.Animation attackAnim = Merge(attackClips, loop: false, turretName, model);
        animator._moveLoopLength = (float)(moveAnim?.Length ?? 0);

        var library = new AnimationLibrary();
        if (idleAnim != null && idleAnim.GetTrackCount() > 0)
            library.AddAnimation("Idle", idleAnim);
        if (moveAnim != null && moveAnim.GetTrackCount() > 0)
            library.AddAnimation("Move", moveAnim);
        if (attackAnim != null && attackAnim.GetTrackCount() > 0)
            library.AddAnimation("Attack", attackAnim);

        if (library.GetAnimationList().Count > 0)
        {
            animator._player = new AnimationPlayer { Name = "AP_All" };
            animator._player.AddAnimationLibrary("", library);
            model.AddChild(animator._player);
            animator._hasAny = true;
        }

        // 旋翼：从片段里读出「转轴 + 平均角速度」，之后自己匀速转
        animator.BuildRotorSpins(model, rotorClips);

        // 原来的播放器已经没用了
        foreach (Node n in model.FindChildren("*", "AnimationPlayer", true, false))
        {
            if (n is AnimationPlayer ap && ap != animator._player)
            {
                ap.Stop();
                ap.QueueFree();
            }
        }

        GD.Print($"[UnitAnim] idle={idleClips.Count} move={moveClips.Count} attack={attackClips.Count} " +
                 $"attackDur={animator._attackDuration:F2}s turret={turretName ?? "none"} " +
                 $"hasTurret={animator.HasTurret} inst={animator.GetInstanceId()}");

        return animator;
    }

    private enum ClipState
    {
        Idle,
        Move,
        Attack,
    }

    private static ClipState? Classify(string clip)
    {
        if (clip.StartsWith("Idle"))
            return ClipState.Idle;
        if (clip.StartsWith("Move") || clip.StartsWith("Walk") || clip.StartsWith("Run"))
            return ClipState.Move;
        if (clip.StartsWith("Fire") || clip.StartsWith("Shoot") || clip.StartsWith("Attack"))
            return ClipState.Attack;
        return null;
    }

    private AnimationPlayer MakePlayer(Node3D model, string name, Godot.Animation anim, out bool ok)
    {
        ok = anim != null && anim.GetTrackCount() > 0;
        if (!ok)
            return null;

        var library = new AnimationLibrary();
        library.AddAnimation(name, anim);

        var player = new AnimationPlayer { Name = name };
        player.AddAnimationLibrary("", library);
        model.AddChild(player);
        return player;
    }

    /// <summary>
    /// 把 glb 里的轨道路径解析到实例化后的真实节点：
    /// 1) 原路径直接能取到就用它；2) 去掉「根节点名/」前缀再试；
    /// 3) 最后按叶子名递归查找。找不到返回 null。
    /// </summary>
    private static Node3D ResolveTrackNode(Node3D model, NodePath path)
    {
        if (model == null)
            return null;

        string text = path.ToString();
        int colon = text.IndexOf(':');
        if (colon >= 0)
            text = text[..colon];

        if (model.GetNodeOrNull(text) is Node3D direct)
            return direct;

        int slash = text.IndexOf('/');
        if (slash >= 0 && model.GetNodeOrNull(text[(slash + 1)..]) is Node3D stripped)
            return stripped;

        string leaf = slash >= 0 ? text[(text.LastIndexOf('/') + 1)..] : text;
        return model.FindChild(leaf, true, false) as Node3D;
    }

    /// <summary>用「模型根 → 目标节点」的相对路径重写轨道，附带原来的属性后缀（如 :rotation）。</summary>
    private static NodePath FixedPath(Node3D model, Node3D node, NodePath original)
    {
        string text = original.ToString();
        int colon = text.IndexOf(':');
        string suffix = colon >= 0 ? text[colon..] : "";
        return new NodePath(model.GetPathTo(node).ToString() + suffix);
    }
    /// <summary>
    /// 把一组片段合并成一条动画：同一节点同一属性只保留第一次出现的那条轨道。
    /// rotorOnly = true 时只保留「旋翼节点」的轨道（用于常驻的旋翼动画）。
    /// </summary>
    private static Godot.Animation Merge(List<Godot.Animation> clips, bool loop, string turretName,
        Node3D model, bool rotorOnly = false)
    {
        if (clips.Count == 0)
            return null;

        var merged = new Godot.Animation
        {
            LoopMode = loop ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None,
        };

        var seen = new HashSet<string>();
        double length = 0;

        foreach (Godot.Animation src in clips)
        {
            length = Mathf.Max(length, src.Length);

            for (int i = 0; i < src.GetTrackCount(); i++)
            {
                Godot.Animation.TrackType type = src.TrackGetType(i);
                // 只跳过音频 / 方法 / 动画播放轨道，其余（Value、Position3D、Rotation3D、Scale3D、BlendShape）都拷
                if (type is Godot.Animation.TrackType.Audio or Godot.Animation.TrackType.Method
                    or Godot.Animation.TrackType.Animation)
                    continue;

                NodePath path = src.TrackGetPath(i);
                if (turretName != null && IsTurretPath(path, turretName))
                    continue;

                bool isRotor = IsRotorPath(path);
                if (rotorOnly != isRotor)
                    continue;

                // glb 里的轨道路径是「根节点名/子节点」，实例化后根名可能被改过，
                // 这里统一重定向到模型内的真实节点，否则轨道会指向不存在的路径
                Node3D trackNode = ResolveTrackNode(model, path);
                if (trackNode == null)
                    continue;

                NodePath fixedPath = FixedPath(model, trackNode, path);

                string key = fixedPath + "|" + type;
                if (!seen.Add(key))
                    continue;

                int index = merged.AddTrack(type);
                merged.TrackSetPath(index, fixedPath);
                merged.TrackSetInterpolationType(index, src.TrackGetInterpolationType(i));

                int keys = src.TrackGetKeyCount(i);
                for (int k = 0; k < keys; k++)
                    merged.TrackInsertKey(index, src.TrackGetKeyTime(i, k), src.TrackGetKeyValue(i, k));
            }
        }

        merged.Length = length;
        return merged;
    }

    private static bool IsTurretPath(NodePath path, string turretName)
    {
        string text = path.ToString();
        int colon = text.IndexOf(':');
        if (colon >= 0)
            text = text[..colon];

        int slash = text.LastIndexOf('/');
        string leaf = slash >= 0 ? text[(slash + 1)..] : text;
        return leaf == turretName;
    }

    /// <summary>轨道是否指向旋翼/螺旋桨节点（MainRotor / TailRotor / Prop_FL ...）。</summary>
    private static bool IsRotorPath(NodePath path)
    {
        string text = path.ToString();
        int colon = text.IndexOf(':');
        if (colon >= 0)
            text = text[..colon];

        int slash = text.LastIndexOf('/');
        string leaf = slash >= 0 ? text[(slash + 1)..] : text;
        return leaf.Contains("Rotor") || leaf.Contains("Prop");
    }

    /// <summary>移动动画的播放倍速（飞机的盘旋循环慢得像悬停，需要加快）。</summary>
    public void SetMoveSpeed(float scale)
    {
        _moveSpeed = Mathf.Max(scale, 0.1f);
        if (_player != null && _current == "Move")
            _player.SpeedScale = _moveSpeed;
    }

    private float _moveSpeed = 1f;

    /// <summary>切换状态时的混入时长：Idle↔Move 慢一点，进攻击快一点、出来稍慢。</summary>
    private const float BlendIdleMove = 0.26f;
    private const float BlendIntoAttack = 0.08f;
    private const float BlendOutOfAttack = 0.22f;

    /// <summary>切到某个状态：同一播放器内用 blend 秒做姿势过渡，避免生硬跳变。</summary>
    private void PlayState(string name, float blend)
    {
        if (_player == null || !_player.HasAnimation(name))
            return;

        if (_current == name && _player.IsPlaying())
            return;

        _current = name;
        _player.SpeedScale = name == "Move" ? _moveSpeed : 1f;
        _player.Play(name, blend, 1f, false);
    }

    public void PlayIdle()
    {
        _moving = false;
        if (IsAttacking)
            return;

        // 移动动画还没播完一轮时不要硬切：先让它把这一轮走完，
        // 否则像战斗机这种长循环的动作会在中途被掐断，出现明显跳变
        if (_player != null && _current == "Move" && _player.IsPlaying() && _moveLoopLength > 0.08f)
        {
            double length = _player.CurrentAnimationLength > 0.01
                ? _player.CurrentAnimationLength
                : _moveLoopLength;
            double pos = _player.CurrentAnimationPosition % length;
            // 剩余时间是「动画时间」除以播放倍速，才是真实等待秒数
            float left = (float)((length - pos) / Mathf.Max(_moveSpeed, 0.1f)) + 0.02f;
            // 最多再等 2.5 秒：避免某个特别长的循环让单位站着「滑行」太久
            _idleDelay = Mathf.Min(left, 2.5f);
            return;
        }

        _idleDelay = -1f;
        PlayState("Idle", BlendIdleMove);
    }

    public void PlayMove()
    {
        _moving = true;
        _idleDelay = -1f;
        if (IsAttacking)
            return;

        PlayState("Move", BlendIdleMove);
    }

    public void PlayAttack()
    {
        if (_player == null || !_player.HasAnimation("Attack"))
            return;

        PlayState("Attack", BlendIntoAttack);
        _attackLeft = Mathf.Max(_attackDuration, 0.35f);
    }

    /// <summary>
    /// 从旋翼片段里提取每个旋翼节点的「转轴」和「平均角速度」，
    /// 之后每帧按固定角速度更新，不会因为片段里的缓动而忽快忽慢。
    /// </summary>
    private void BuildRotorSpins(Node3D model, List<Godot.Animation> clips)
    {
        foreach (Godot.Animation anim in clips)
        {
            for (int i = 0; i < anim.GetTrackCount(); i++)
            {
                Godot.Animation.TrackType type = anim.TrackGetType(i);
                if (type is not (Godot.Animation.TrackType.Rotation3D or Godot.Animation.TrackType.Value))
                    continue;

                NodePath path = anim.TrackGetPath(i);

                if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--anim-log") >= 0)
                    GD.Print($"[RotorDbg] anim={anim.ResourceName} type={type} path={path} rotor={IsRotorPath(path)}");

                if (!IsRotorPath(path))
                    continue;

                string text = path.ToString();
                int colon = text.IndexOf(':');
                if (colon >= 0)
                    text = text[..colon];

                if (ResolveTrackNode(model, path) is not Node3D node)
                    continue;
                if (_rotors.Any(r => r.Node == node))
                    continue;

                int keys = anim.TrackGetKeyCount(i);

                bool isProp = node.Name.ToString().Contains("Prop");
                float maxSpin = isProp ? 42f : 16f;

                float total = 0f;
                Vector3 axis = Vector3.Zero;
                if (keys >= 2)
                {
                    // 把相邻关键帧之间转过的角度累加成「这条片段总共转了多少」
                    Quaternion first = anim.TrackGetKeyValue(i, 0).AsQuaternion();
                    for (int k = 1; k < keys; k++)
                    {
                        Quaternion q = anim.TrackGetKeyValue(i, k).AsQuaternion();
                        total += Mathf.Abs((q * first.Inverse()).GetAngle());
                    }

                    Quaternion last = anim.TrackGetKeyValue(i, keys - 1).AsQuaternion();
                    axis = (last * first.Inverse()).GetAxis();
                }

                // 螺旋桨：关键帧太稀时角度差会因四元数回绕而算成 0，
                // 这时直接给一个固定的「看起来在转」的转速，绕本地 Y 轴
                if (isProp && (total < 0.01f || axis.LengthSquared() < 0.01f))
                {
                    _rotors.Add(new RotorSpin
                    {
                        Node = node,
                        Axis = Vector3.Up,
                        Rest = node.Quaternion,
                        RadPerSecond = 30f,
                    });
                    continue;
                }

                if (total < 0.01f || anim.Length <= 0.01 || axis.LengthSquared() < 0.01f)
                    continue;

                _rotors.Add(new RotorSpin
                {
                    Node = node,
                    Axis = axis.Normalized(),
                    Rest = node.Quaternion,
                    // 用片段自带的平均角速度，并限制在一个「看起来自然」的范围内，
                    // 太快会帧采样成倒转（车轮效应）
                    RadPerSecond = Mathf.Clamp(total / (float)anim.Length, 4f, maxSpin),
                });
            }
        }

        if (_rotors.Count > 0)
        {
            _hasAny = true;
            GD.Print($"[UnitAnim] 旋翼 {_rotors.Count} 个，转速 " +
                     string.Join(" / ", _rotors.Select(r => $"{r.RadPerSecond:F1}rad/s")));
        }
    }

    /// <summary>调试：旋翼状态。</summary>
    public string DebugRotor()
    {
        if (_rotors.Count == 0)
            return "none";

        return string.Join(" ", _rotors.Select(r =>
            $"{r.Node.Name}:{r.Angle:F2}rad/{r.RadPerSecond:F1}rad/s"));
    }

    /// <summary>调试：当前实际在播的动画组（正常情况下只该有一个）。</summary>
    public string DebugPlaying() =>
        _player != null && _player.IsPlaying() ? _current : "none";

    /// <summary>把炮塔转到「相对车体」的某个角度（度）。</summary>
    public void SetTurretYaw(float localYawDeg)
    {
        if (_turret == null)
            return;

        while (localYawDeg > 180f)
            localYawDeg -= 360f;
        while (localYawDeg < -180f)
            localYawDeg += 360f;

        _turretYaw = localYawDeg;
        ApplyTurret();
    }

    /// <summary>炮塔回到车头方向。</summary>
    public void ResetTurret() => SetTurretYaw(0f);

    private void ApplyTurret()
    {
        if (_turret != null)
            _turret.RotationDegrees = new Vector3(_turretRest.X, _turretRest.Y + _turretYaw, _turretRest.Z);
    }

    public override void _Process(double delta)
    {
        // 旋翼匀速转
        foreach (RotorSpin rotor in _rotors)
        {
            rotor.Angle += rotor.RadPerSecond * (float)delta;
            if (rotor.Angle > Mathf.Tau)
                rotor.Angle -= Mathf.Tau;

            rotor.Node.Quaternion = rotor.Rest * new Quaternion(rotor.Axis, rotor.Angle);
        }

        // 每帧重申炮塔朝向，避免被任何残留动画覆盖
        if (_turret != null && !Mathf.IsEqualApprox(_turret.RotationDegrees.Y - _turretRest.Y, _turretYaw))
            ApplyTurret();

        // 等移动动画这一轮播完，再切回 Idle
        if (_idleDelay > 0f)
        {
            _idleDelay -= (float)delta;
            if (_idleDelay <= 0f)
            {
                _idleDelay = -1f;
                PlayState("Idle", BlendIdleMove);
            }
        }

        if (_attackLeft <= 0f)
            return;

        _attackLeft -= (float)delta;
        if (_attackLeft > 0f)
            return;

        PlayState(_moving ? "Move" : "Idle", BlendOutOfAttack);
    }

}
