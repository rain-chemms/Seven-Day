using System.Text.Json;
using System.Text.Json.Serialization;

namespace SevenDay.Net;

/// <summary>
/// 开局配置：主机决定地图、难度、双方卡组、双方基地等级、双方洗牌种子，
/// 客户端收到后按“镜像视角”重建自己的 BattleState。
/// </summary>
public sealed class NetBattleConfig
{
    public string MapId { get; set; } = "map_01";
    public int Difficulty { get; set; } = 1;

    /// <summary>主机方的基地等级 / 卡组 / 洗牌种子。</summary>
    public int HqHost { get; set; } = 1;
    public string[] DeckHost { get; set; } = System.Array.Empty<string>();
    public int SeedHost { get; set; }

    /// <summary>客户端方的基地等级 / 卡组 / 洗牌种子。</summary>
    public int HqClient { get; set; } = 1;
    public string[] DeckClient { get; set; } = System.Array.Empty<string>();
    public int SeedClient { get; set; }

    /// <summary>天气开关 / 无天气持续回合数，由主机决定并同步。</summary>
    public bool WeatherEnabled { get; set; } = true;
    public int WeatherGrace { get; set; } = 2;
    public int WeatherMask { get; set; } = 0x1FE;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static NetBattleConfig FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NetBattleConfig>(json, Options);
        }
        catch (System.Exception e)
        {
            Godot.GD.PushWarning($"[Net] 配置解析失败：{e.Message}");
            return null;
        }
    }
}
