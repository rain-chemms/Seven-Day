using System;
using System.Collections.Generic;
using Godot;

namespace SevenDay.Core;

/// <summary>
/// 音频：一条 BGM 通道 + 若干 SFX 通道，走 Master/Music/SFX 三条总线。
/// 自动加载单例，切场景不会断。
/// </summary>
public partial class AudioManager : Node
{
    public static AudioManager Instance { get; private set; }

    private AudioStreamPlayer _bgm;
    private AudioStreamPlayer _stinger;
    private readonly List<AudioStreamPlayer> _sfx = new();
    private string _currentBgm = "";
    private Tween _fade;

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
        AudioBuses.Ensure();

        _bgm = new AudioStreamPlayer { Name = "Bgm", Bus = "Music" };
        AddChild(_bgm);

        // 结算音效（胜利 / 失败）单独一条通道，不会被普通 SFX 抢走
        _stinger = new AudioStreamPlayer { Name = "Stinger", Bus = "SFX" };
        AddChild(_stinger);

        for (int i = 0; i < 8; i++)
        {
            var p = new AudioStreamPlayer { Name = "Sfx" + i, Bus = "SFX" };
            AddChild(p);
            _sfx.Add(p);
        }
    }

    // ─────────────────────────── BGM ───────────────────────────

    public static void PlayBgm(string path, float fadeSeconds = 0.8f)
    {
        if (Instance == null || string.IsNullOrEmpty(path))
            return;
        Instance.PlayBgmInternal(path, fadeSeconds);
    }

    private void PlayBgmInternal(string path, float fadeSeconds)
    {
        if (_currentBgm == path && _bgm.Playing)
            return;

        var stream = GD.Load<AudioStream>(path);

        if (stream == null)
            return;

        _currentBgm = path;
        _bgm.Stream = stream;
        _bgm.VolumeDb = -24f;
        _bgm.Play();

        _fade?.Kill();
        _fade = CreateTween();
        _fade.TweenProperty(_bgm, "volume_db", 0f, Mathf.Max(0.05f, fadeSeconds));
    }

    public static void SetIntensity(int stage)
    {
        // 战斗 BGM 分三段，按回合数推进
        string file = stage switch
        {
            <= 0 => "battle_phase1",
            1 => "battle_phase2",
            _ => "battle_phase3",
        };
        PlayBgm($"res://gameAsset/audios/bgm/{file}.ogg", 1.2f);
    }

    public static void StopBgm(float fadeSeconds = 0.6f)
    {
        if (Instance == null)
            return;
        Instance._fade?.Kill();
        Instance._fade = Instance.CreateTween();
        Instance._fade.TweenProperty(Instance._bgm, "volume_db", -60f, Mathf.Max(0.05f, fadeSeconds));
        Instance._fade.TweenCallback(Callable.From(() =>
        {
            Instance._bgm.Stop();
            Instance._currentBgm = "";
        }));
    }

    // ─────────────────────────── SFX ───────────────────────────

    /// <summary>胜利音效音量（dB）：比失败音效轻一些，不至于盖过一切。</summary>
    private const float VictoryVolumeDb = -9f;

    /// <summary>胜利结算音效（游戏结束那一刻播）。</summary>
    public static void PlayVictory() =>
        PlayStinger("res://gameAsset/audios/sfx/gameState_win.mp3", VictoryVolumeDb);

    /// <summary>失败结算音效。</summary>
    public static void PlayDefeat() =>
        PlayStinger("res://gameAsset/audios/sfx/gameState_defeat.mp3");

    /// <summary>停掉结算音效（重开一局时用）。</summary>
    public static void StopStinger() => Instance?._stinger?.Stop();

    private static void PlayStinger(string path, float volumeDb = 0f)
    {
        if (Instance == null || Instance._stinger == null)
            return;

        if (!ResourceLoader.Exists(path))
        {
            if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--audio-log") >= 0)
                GD.Print($"[Audio] 缺少结算音效 {path}");
            return;
        }

        AudioStream stream = GD.Load<AudioStream>(path);
        if (stream == null)
            return;

        Instance._stinger.Stream = stream;
        Instance._stinger.VolumeDb = volumeDb;
        Instance._stinger.Play();

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--audio-log") >= 0)
            GD.Print($"[Audio] 结算音效 {path.GetFile()} vol={volumeDb:F1}dB len={stream.GetLength():F1}s");
    }
    public static void PlaySfx(string path, float volumeDb = 0f)
    {
        if (Instance == null || string.IsNullOrEmpty(path))
            return;
        if (!ResourceLoader.Exists(path))
        {
            if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--audio-log") >= 0)
                GD.Print($"[Audio] 缺少音效 {path}");
            return;
        }

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--audio-log") >= 0)
            GD.Print($"[Audio] 播放 {path.GetFile()}");

        var stream = GD.Load<AudioStream>(path);
        if (stream == null)
            return;

        AudioStreamPlayer player = Instance._sfx.Find(p => !p.Playing) ?? Instance._sfx[0];
        player.Stream = stream;
        player.VolumeDb = volumeDb;
        player.Play();
    }

    /// <summary>units.json / buildings.json 里的 id → 音频文件名（个别名字不一致）。</summary>
    private static readonly Dictionary<string, string> ClipNames = new()
    {
        ["missileLauncher"] = "missile_launcher",
        ["missileSilo"] = "missileSilo",
        ["missileSiloUpgrade"] = "missile_silo",
    };

    private static string Clip(string id) => ClipNames.TryGetValue(id, out string v) ? v : id;

    /// <summary>
    /// 没有专属音效的单位借同类的音频：
    /// FPV 无人机的旋翼声直接复用直升机的 move/select 片段。
    /// </summary>
    private static readonly Dictionary<string, string> Borrowed =
        new()
        {
            ["move"] = "helicopter",
            ["select"] = "helicopter",
        };

    /// <summary>拼出音效路径：有专属的用专属，没有就按 Borrowed 借。</summary>
    private static string SfxPath(string prefix, string defId)
    {
        string own = $"res://gameAsset/audios/sfx/{prefix}_{Clip(defId)}.ogg";
        if (ResourceLoader.Exists(own))
            return own;

        if (defId == "fpvDrone" && Borrowed.TryGetValue(prefix, out string alt))
        {
            string borrowed = $"res://gameAsset/audios/sfx/{prefix}_{alt}.ogg";
            if (ResourceLoader.Exists(borrowed))
                return borrowed;
        }

        return own;      // 不存在时 PlaySfx 会静默跳过
    }

    public static void PlaySelect(string defId) =>
        PlaySfx(SfxPath("select", defId));

    public static void PlayMove(string defId) =>
        PlaySfx(SfxPath("move", defId));

    /// <summary>
    /// 部署音效：优先用专属的 deploy_*.ogg，没有就退回 move_*.ogg。
    /// 新音效直接丢进 res://gameAsset/audios/sfx/ 即可被识别。
    /// </summary>
    public static void PlayDeploy(string defId)
    {
        string own = $"res://gameAsset/audios/sfx/deploy_{Clip(defId)}.ogg";
        PlaySfx(ResourceLoader.Exists(own) ? own : SfxPath("move", defId));
    }

    public static void PlayAttack(string defId) =>
        PlaySfx(SfxPath("attack", defId));

    public static void PlayUpgrade(string defId)
    {
        string file = defId == "missileSilo" ? "missile_silo" : Clip(defId);
        PlaySfx($"res://gameAsset/audios/sfx/upgrade_{file}.ogg");
    }

    public static void PlayUi(string name) =>
        PlaySfx($"res://gameAsset/audios/sfx/{name}.ogg");

    /// <summary>
    /// 爆炸音效（导弹/炮弹落地）。带随机的音高与音量微扰，连续爆炸不会完全一样。
    /// 想换音效：直接替换 res://gameAsset/audios/sfx/explosion.ogg 即可。
    /// </summary>
    public static void PlayExplosion(float volumeScale = 1f)
    {
        if (Instance == null)
            return;

        const string path = "res://gameAsset/audios/sfx/explosion.ogg";
        if (!ResourceLoader.Exists(path))
            return;

        var stream = GD.Load<AudioStream>(path);

        if (stream == null)
            return;

        AudioStreamPlayer player = Instance._sfx.Find(p => !p.Playing) ?? Instance._sfx[0];
        player.Stream = stream;
        player.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(volumeScale, 0.05f, 1.2f));
        player.PitchScale = (float)GD.RandRange(0.9, 1.08);
        player.Play();

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--audio-log") >= 0)
            GD.Print($"[Audio] 爆炸 vol={player.VolumeDb:F1}dB pitch={player.PitchScale:F2} len={stream.GetLength():F2}s");
    }

    /// <summary>
    /// 维修/回血音效（柔和的铃声）。想换音效：替换 res://gameAsset/audios/sfx/heal.ogg。
    /// </summary>
    public static void PlayHeal()
    {
        if (Instance == null)
            return;

        const string path = "res://gameAsset/audios/sfx/heal.ogg";
        if (!ResourceLoader.Exists(path))
            return;

        var stream = GD.Load<AudioStream>(path);

        if (stream == null)
            return;

        AudioStreamPlayer player = Instance._sfx.Find(p => !p.Playing) ?? Instance._sfx[0];
        player.Stream = stream;
        player.VolumeDb = Mathf.LinearToDb(0.85f);
        player.PitchScale = (float)GD.RandRange(0.98, 1.04);
        player.Play();

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--audio-log") >= 0)
            GD.Print($"[Audio] 维修 vol={player.VolumeDb:F1}dB len={stream.GetLength():F2}s");
    }

    /// <summary>
    /// 导弹飞行音效（喷流轰鸣）。替换 res://gameAsset/audios/sfx/missile_fly.ogg 即可换音。
    /// </summary>
    public static void PlayMissileFlight(float volumeScale = 0.9f)
    {
        if (Instance == null)
            return;

        const string path = "res://gameAsset/audios/sfx/missile_fly.ogg";
        if (!ResourceLoader.Exists(path))
            return;

        var stream = GD.Load<AudioStream>(path);

        if (stream == null)
            return;

        AudioStreamPlayer player = Instance._sfx.Find(p => !p.Playing) ?? Instance._sfx[0];
        player.Stream = stream;
        player.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(volumeScale, 0.05f, 1.2f));
        player.PitchScale = (float)GD.RandRange(0.96, 1.06);
        player.Play();

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--audio-log") >= 0)
            GD.Print($"[Audio] 导弹飞行 vol={player.VolumeDb:F1}dB len={stream.GetLength():F2}s");
    }
}

/// <summary>确保 Master / Music / SFX 三条音频总线的存在与音量映射。</summary>
public static class AudioBuses
{
    private static bool _ready;

    public static void Ensure()
    {
        if (_ready)
            return;
        _ready = true;
        EnsureBus("Music");
        EnsureBus("SFX");
    }

    private static void EnsureBus(string name)
    {
        if (AudioServer.GetBusIndex(name) >= 0)
            return;
        int idx = AudioServer.BusCount;
        AudioServer.AddBus(idx);
        AudioServer.SetBusName(idx, name);
        AudioServer.SetBusSend(idx, "Master");
    }

    public static void SetVolume(string bus, float linear)
    {
        int idx = AudioServer.GetBusIndex(bus);
        if (idx < 0)
            return;
        AudioServer.SetBusVolumeDb(idx, linear <= 0.0001f ? -80f : Mathf.LinearToDb(linear));
        AudioServer.SetBusMute(idx, linear <= 0.0001f);
    }
}
