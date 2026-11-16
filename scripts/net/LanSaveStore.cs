using System;
using System.Text.Json;
using Godot;

namespace SevenDay.Net;

/// <summary>
/// 未完成联机对局的存档：房主断线时保存，下次进入联机模式时可选择载入。
/// 只存“配置 + 已执行动作”，载入时回放重建，避免直接序列化整个战斗状态。
/// </summary>
public static class LanSaveStore
{
    private const string Path = "user://lan_unfinished_match.json";

    public sealed class SaveData
    {
        public NetResumePayload Payload { get; set; }
        public string MapName { get; set; } = "";
        public int Turn { get; set; }
        public string SavedAt { get; set; } = "";
        public int Port { get; set; } = 7777;

        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        public static SaveData FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<SaveData>(json, Options);
            }
            catch (Exception e)
            {
                GD.PushWarning($"[Net] 存档解析失败：{e.Message}");
                return null;
            }
        }
    }

    public static bool Exists()
    {
        using FileAccess f = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
        return f != null;
    }

    public static SaveData Load()
    {
        using FileAccess f = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
        if (f == null)
            return null;

        return SaveData.FromJson(f.GetAsText());
    }

    public static void Save(NetResumePayload payload, string mapName, int turn, int port)
    {
        var data = new SaveData
        {
            Payload = payload,
            MapName = mapName,
            Turn = turn,
            SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Port = port,
        };

        using FileAccess f = FileAccess.Open(Path, FileAccess.ModeFlags.Write);
        f?.StoreString(data.ToJson());
        GD.Print($"[Net] 已保存未完成对局：{Path}（{payload.History?.Length ?? 0} 条动作，第 {turn} 回合）");
    }

    /// <summary>把存档放进重连暂存区（房主载入对局时用）。</summary>
    public static NetResumePayload RestoreIntoPending()
    {
        SaveData data = Load();
        if (data?.Payload == null)
        {
            GD.PushWarning("[Net] 没有可载入的未完成对局");
            return null;
        }

        NetResumeStore.Pending = data.Payload;
        GD.Print($"[Net] 载入未完成对局：{data.MapName} 第 {data.Turn} 回合（{data.Payload.History?.Length ?? 0} 条动作）");
        return data.Payload;
    }

    public static void Delete()
    {
        if (Exists())
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(Path));
    }
}
