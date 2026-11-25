using System.Collections.Generic;
using SevenDay.Core;

namespace SevenDay.Battle;

/// <summary>
/// 一条「持续到本回合结束」的增益/减益状态，用于左上角图标条。
/// </summary>
public sealed class ActiveEffect
{
    /// <summary>状态 id（同时作为图标文件名 abilityIcon/&lt;id&gt;.png）。</summary>
    public string Id { get; init; } = "";

    public string NameKey { get; init; } = "";
    public string DescKey { get; init; } = "";

    /// <summary>剩余时间文案（目前都是「本回合」）。</summary>
    public string DurationKey { get; init; } = "ability.duration.turn";

    /// <summary>正向（绿/金）还是负向（红）效果。</summary>
    public bool Positive { get; init; } = true;

    /// <summary>能力数值（显示在图标右下角；也是描述里 {0} 的值）。</summary>
    public int Value { get; init; }

    /// <summary>描述里是否包含 {0} 动态数值。</summary>
    public bool HasValue => Value != 0;

    public string IconPath => $"res://gameAsset/textures/abilityIcon/{Id}.png";
}

/// <summary>
/// 从当前对局状态收集需要展示的持续效果。
/// 新卡牌如果只是「本回合 +X / -X」，在这里加一条即可，
/// HUD 会自动生成图标、悬浮说明和实时刷新。
/// </summary>
public static class ActiveEffects
{
    public static List<ActiveEffect> Collect(PlayerState player)
    {
        var list = new List<ActiveEffect>();
        if (player == null)
            return list;

        // 强行军：地面单位移动 +1，数值 = 剩余回合数
        if (player.ForcedMarchTurns > 0)
        {
            list.Add(new ActiveEffect
            {
                Id = "boost",
                NameKey = "ability.boost.name",
                DescKey = "ability.boost.desc",
                Positive = true,
                Value = player.ForcedMarchTurns,
                DurationKey = "",
            });
        }

        // 发行债券：数值 = 下回合要扣的负债金额
        if (player.PendingBondDebt > 0)
        {
            list.Add(new ActiveEffect
            {
                Id = "bond",
                NameKey = "ability.bond.name",
                DescKey = "ability.bond.desc",
                Positive = false,
                Value = player.PendingBondDebt,
                DurationKey = "",
            });
        }

        // 房屋补贴：数值 = 下回合要免费升级的建筑数量
        if (player.PendingHousingUpgrades > 0)
        {
            list.Add(new ActiveEffect
            {
                Id = "housing",
                NameKey = "ability.housing.name",
                DescKey = "ability.housing.desc",
                Positive = true,
                Value = player.PendingHousingUpgrades,
                DurationKey = "",
            });
        }

        // 强制兵役：常驻，无数值
        if (player.Conscription)
        {
            list.Add(new ActiveEffect
            {
                Id = "conscription",
                NameKey = "ability.conscription.name",
                DescKey = "ability.conscription.desc",
                Positive = true,
                DurationKey = "",
            });
        }

        // 战时医疗：数值 = 每回合恢复的已损生命百分比
        if (player.MedicalStacks > 0)
        {
            list.Add(new ActiveEffect
            {
                Id = "medical",
                NameKey = "ability.medical.name",
                DescKey = "ability.medical.desc",
                Positive = true,
                Value = player.MedicalStacks * 25,
                DurationKey = "",
            });
        }

        // 后续负面卡牌示例（例如减速）：
        // if (player.MovePenalty > 0) list.Add(new ActiveEffect { Id = "slow", ... Positive = false });

        if (player.SanctionTurns > 0)
        {
            list.Add(new ActiveEffect
            {
                Id = "sanction",
                NameKey = "ability.sanction.name",
                DescKey = "ability.sanction.desc",
                Positive = false,
                Value = player.SanctionTurns,
                DurationKey = "",
            });
        }

        return list;
    }
}
