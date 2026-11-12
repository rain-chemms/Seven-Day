using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace SevenDay.Core;

public sealed class MatchRecord
{
    public string Date { get; set; } = "";
    public string MapId { get; set; } = "";
    public string MapName { get; set; } = "";
    public int Difficulty { get; set; }
    public int Turns { get; set; }
    public bool Win { get; set; }
    public int PlayerHqHp { get; set; }
    public int EnemyHqHp { get; set; }
}

/// <summary>对局历史，存在 user://history.json，主菜单与结算画面都能读。</summary>
public static class MatchHistory
{
    public const int MaxRecords = 30;
    private const string Path = "user://history.json";

    public static List<MatchRecord> Records { get; private set; } = new();
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        if (!Godot.FileAccess.FileExists(Path))
            return;

        string text = Godot.FileAccess.GetFileAsString(Path);
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            Records = JsonSerializer.Deserialize<List<MatchRecord>>(text) ?? new List<MatchRecord>();
        }
        catch (Exception e)
        {
            GD.PushWarning("[MatchHistory] 历史记录解析失败：" + e.Message);
            Records = new List<MatchRecord>();
        }
    }

    public static void Add(MatchRecord record)
    {
        EnsureLoaded();
        Records.Insert(0, record);
        if (Records.Count > MaxRecords)
            Records.RemoveRange(MaxRecords, Records.Count - MaxRecords);
        Save();
    }

    private static void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Records, new JsonSerializerOptions { WriteIndented = true });
            using FileAccess file = FileAccess.Open(Path, FileAccess.ModeFlags.Write);
            file?.StoreString(json);
        }
        catch (Exception e)
        {
            GD.PushWarning("[MatchHistory] 保存失败：" + e.Message);
        }
    }

    public static string DifficultyName(int d) => Loc.T(d switch
    {
        0 => "diff.easy",
        2 => "diff.hard",
        3 => "diff.lunatic",
        _ => "diff.normal",
    });

    /// <summary>一行摘要，给列表用。</summary>
    public static string Summarize(MatchRecord r)
    {
        string mark = r.Win ? "W" : "L";
        return $"[{mark}] {r.Date}  {r.MapName}  {DifficultyName(r.Difficulty)}  {r.Turns} 回合";
    }

    public static IEnumerable<MatchRecord> Recent(int count) =>
        Records.Take(Mathf.Max(0, count));
}
