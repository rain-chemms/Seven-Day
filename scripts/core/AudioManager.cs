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

    public static void PlaySfx(string path)
    {
        if (Instance == null || string.IsNullOrEmpty(path))
            return;
        if (!ResourceLoader.Exists(path))
            return;

        var stream = GD.Load<AudioStream>(path);
        if (stream == null)
            return;

        AudioStreamPlayer player = Instance._sfx.Find(p => !p.Playing) ?? Instance._sfx[0];
        player.Stream = stream;
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

    public static void PlaySelect(string defId) =>
        PlaySfx($"res://gameAsset/audios/sfx/select_{Clip(defId)}.ogg");

    public static void PlayMove(string defId) =>
        PlaySfx($"res://gameAsset/audios/sfx/move_{Clip(defId)}.ogg");

    public static void PlayAttack(string defId) =>
        PlaySfx($"res://gameAsset/audios/sfx/attack_{Clip(defId)}.ogg");

    public static void PlayUpgrade(string defId)
    {
        string file = defId == "missileSilo" ? "missile_silo" : Clip(defId);
        PlaySfx($"res://gameAsset/audios/sfx/upgrade_{file}.ogg");
    }

    public static void PlayUi(string name) =>
        PlaySfx($"res://gameAsset/audios/sfx/{name}.ogg");
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
