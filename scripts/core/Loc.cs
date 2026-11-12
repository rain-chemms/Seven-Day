using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace SevenDay.Core;

/// <summary>
/// 极简本地化：键 → 中/英文本。词条放在 res://data/localization.json，改文案不用改代码。
/// 切换语言后抛 Changed，界面收到后重刷文本。
/// </summary>
public static class Loc
{
    public static event Action Changed;

    private static readonly Dictionary<string, Dictionary<string, string>> Table = new();
    private static string _lang = "zh";

    public static string Language => _lang;
    public static bool IsChinese => _lang == "zh";

    public static void EnsureLoaded()
    {
        if (Table.Count > 0)
            return;

        string text = Godot.FileAccess.GetFileAsString("res://data/localization.json");
        if (string.IsNullOrWhiteSpace(text))
        {
            GD.PushWarning("[Loc] 找不到 localization.json，界面会用键名代替文本");
            return;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(text);
            if (parsed != null)
            {
                foreach (KeyValuePair<string, Dictionary<string, string>> kv in parsed)
                    Table[kv.Key] = kv.Value;
            }
        }
        catch (Exception e)
        {
            GD.PushError("[Loc] localization.json 解析失败：" + e.Message);
        }

        string saved = Settings.GetString("ui", "language", "");
        if (!string.IsNullOrEmpty(saved) && Table.ContainsKey(saved))
            _lang = saved;
    }

    public static void SetLanguage(string lang)
    {
        EnsureLoaded();
        if (lang == _lang || !Table.ContainsKey(lang))
            return;
        _lang = lang;
        Settings.SetString("ui", "language", lang);
        Settings.Save();
        Changed?.Invoke();
    }

    public static string T(string key)
    {
        EnsureLoaded();
        if (Table.TryGetValue(_lang, out Dictionary<string, string> map) && map.TryGetValue(key, out string v))
            return v;
        return key;
    }

    public static string T(string key, params object[] args)
    {
        string s = T(key);
        try
        {
            return string.Format(s, args);
        }
        catch
        {
            return s;
        }
    }

    /// <summary>数据表里同时有中文名和英文名时用它挑。</summary>
    public static string Name(string zh, string en) => IsChinese || string.IsNullOrEmpty(en) ? zh : en;
}

/// <summary>user:// 下的简易配置读写（音量、语言、画质）。</summary>
public static class Settings
{
    private static ConfigFile _config;

    private static ConfigFile Config => _config ??= Load();

    private static ConfigFile Load()
    {
        var c = new ConfigFile();
        Error err = c.Load("user://settings.cfg");
        if (err != Error.Ok)
            GD.Print("[Settings] 新建配置文件");
        return c;
    }

    public static string GetString(string section, string key, string def) =>
        Config.GetValue(section, key, def).AsString();

    public static int GetInt(string section, string key, int def) =>
        Config.GetValue(section, key, def).AsInt32();

    public static float GetFloat(string section, string key, float def) =>
        (float)Config.GetValue(section, key, def).AsDouble();

    public static bool GetBool(string section, string key, bool def) =>
        Config.GetValue(section, key, def).AsBool();

    public static void SetString(string section, string key, string value) =>
        Config.SetValue(section, key, value);

    public static void SetInt(string section, string key, int value) =>
        Config.SetValue(section, key, value);

    public static void SetFloat(string section, string key, float value) =>
        Config.SetValue(section, key, value);

    public static void SetBool(string section, string key, bool value) =>
        Config.SetValue(section, key, value);

    public static void Save() => Config.Save("user://settings.cfg");
}
