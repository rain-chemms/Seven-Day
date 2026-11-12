using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.Battle;

/// <summary>
/// 电脑对手。四档难度目前只影响「回合内动作数」和「是否建造/升级」，
/// 后面可以在这里继续加评估函数。
/// </summary>
public sealed class AIController
{
    private readonly BattleState _s;
    private readonly PlayerId _id;
    private readonly PlayerState _me;
    private readonly AIDifficulty _difficulty;
    private readonly List<string> _log = new();

    public AIController(BattleState state, AIDifficulty difficulty, PlayerId who = PlayerId.Enemy)
    {
        _s = state;
        _id = who;
        _me = state.StateOf(who);
        _difficulty = difficulty;
    }

    public string RunTurn()
    {
        _log.Clear();

        int deployBudget = _difficulty switch
        {
            AIDifficulty.Easy => 1,
            AIDifficulty.Normal => 2,
            AIDifficulty.Hard => 3,
            _ => 4,
        };

        DeployUnits(deployBudget);
        MoveAndAttack();
        TryBuild();

        return string.Join("；", _log);
    }

    private HexCoord HqCoord => _s.Buildings.FirstOrDefault(b => b.Owner == _id && b.IsHq)?.Coord
                                ?? new HexCoord(0, 0);

    private HexCoord TargetHqCoord => _s.Buildings.FirstOrDefault(b => b.Owner != _id && b.IsHq)?.Coord
                                      ?? new HexCoord(0, 0);

    private void DeployUnits(int budget)
    {
        int deployed = 0;
        int guard = 0;
        while (deployed < budget && guard++ < 12)
        {
            int index = -1;
            CardDef chosen = null;

            for (int i = 0; i < _me.Hand.Count; i++)
            {
                CardDef c = GameDatabase.Card(_me.Hand[i]);
                if (c == null || c.KindEnum != CardKind.Unit || _me.Gold < c.Cost)
                    continue;
                if (chosen == null || c.Cost > chosen.Cost)
                {
                    chosen = c;
                    index = i;
                }
            }

            if (chosen == null || index < 0)
                break;

            HexCoord? spot = FindDeploySpot();
            if (spot == null)
                break;

            if (!_s.PlayUnitCard(_me, _me.Hand[index], spot.Value, out string err))
            {
                _log.Add("部署失败:" + err);
                break;
            }

            deployed++;
            _log.Add($"部署 {chosen.NameZh}");
        }
    }

    private HexCoord? FindDeploySpot()
    {
        HexCoord hq = HqCoord;
        HexCoord target = TargetHqCoord;

        return _s.Cells.Values
            .Where(c => _s.CanDeployCard("unit_infantry", _id, c.Coord, out _))
            .OrderBy(c => HexCoord.Distance(c.Coord, target))
            .Select(c => (HexCoord?)c.Coord)
            .FirstOrDefault();
    }

    private void MoveAndAttack()
    {
        foreach (UnitInst u in _s.Units.Where(u => u.Owner == _id && u.Alive).ToList())
        {
            if (!u.Alive)
                continue;

            var targets = _s.AttackTargets(u).ToList();
            if (targets.Count > 0)
            {
                var best = targets
                    .OrderBy(t => t.building != null && t.building.IsHq ? 0 : 1)
                    .ThenBy(t => t.building != null ? 0 : 1)
                    .First();

                HexCoord tc = best.building != null ? best.building.Coord : best.unit.Coord;
                string r = _s.PerformAttack(u, tc);
                if (!string.IsNullOrEmpty(r))
                    _log.Add(r);
                continue;
            }

            HexCoord goal = TargetHqCoord;
            HashSet<HexCoord> moves = _s.MoveRange(u);
            if (moves.Count == 0)
                continue;

            HexCoord best2 = moves.OrderBy(m => HexCoord.Distance(m, goal)).First();
            if (HexCoord.Distance(best2, goal) >= HexCoord.Distance(u.Coord, goal))
                continue;

            _s.TryMove(u, best2, out _);

            var after = _s.AttackTargets(u).ToList();
            if (after.Count > 0)
            {
                var t = after.OrderBy(x => x.building != null && x.building.IsHq ? 0 : 1).First();
                HexCoord tc = t.building != null ? t.building.Coord : t.unit.Coord;
                string r = _s.PerformAttack(u, tc);
                if (!string.IsNullOrEmpty(r))
                    _log.Add(r);
            }
        }
    }

    private void TryBuild()
    {
        if (_difficulty == AIDifficulty.Easy)
            return;
        if (_me.Gold < 250)
            return;

        BuildingInst hq = _s.Buildings.FirstOrDefault(b => b.Owner == _id && b.IsHq);
        if (hq != null && hq.Level < 2 && _me.Gold >= hq.Def.UpgradeCostAt(1))
        {
            _s.TryUpgrade(hq, out _);
            _log.Add("升级指挥部");
            return;
        }

        HexCoord? spot = _s.Cells.Values
            .Where(c => _s.CanBuildAt(c.Coord, _id) && c.Building == null)
            .OrderByDescending(c => HexCoord.Distance(c.Coord, HqCoord))
            .Select(c => (HexCoord?)c.Coord)
            .FirstOrDefault();

        if (spot is { } coord && _s.TryBuild(_id, "city", coord, out _))
            _log.Add("建造城市");
    }
}
