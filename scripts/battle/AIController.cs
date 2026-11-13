using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.Battle;

/// <summary>
/// 电脑对手。
///
/// 决策顺序（每个回合）：
///   1. 打出手牌里的打击卡（空袭/导弹），挑「最划算」的目标格；
///   2. 部署部队：有空军卡优先出空军，其次是贵的陆军；
///   3. 空军先行动（射程远、能越地形），地面部队再行动；
///   4. 经营：升级指挥部 / 补经济 / 补兵营 / 补机场 / 补防御。
///
/// 难度只影响「部署预算」和「是否建造」，其余策略共用。
/// </summary>
public sealed class AIController
{
    private readonly BattleState _s;
    private readonly PlayerId _id;
    private readonly PlayerState _me;
    private readonly AIDifficulty _difficulty;
    private readonly List<string> _log = new();

    /// <summary>留给下一回合的最低金币，避免把钱全花光。</summary>
    private int Reserve => _difficulty switch
    {
        AIDifficulty.Easy => 0,
        AIDifficulty.Normal => 60,
        AIDifficulty.Hard => 100,
        _ => 150,
    };

    public AIController(BattleState state, AIDifficulty difficulty, PlayerId who = PlayerId.Enemy)
    {
        _s = state;
        _id = who;
        _me = state.StateOf(who);
        _difficulty = difficulty;
    }

    // ── 表现层钩子：设置后，攻击/打击卡会交给场景去播特效，等命中才结算 ──

    /// <summary>单位攻击：参数（攻击者, 目标格），返回的任务完成才继续。</summary>
    public Func<UnitInst, HexCoord, Task> AttackHook;

    /// <summary>打击卡：参数（卡牌 id, 目标格）。</summary>
    public Func<string, HexCoord, Task> StrikeHook;

    /// <summary>同步跑一回合（自动测试/AI 对 AI 用，不播特效）。</summary>
    public string RunTurn() => RunTurnAsync().GetAwaiter().GetResult();

    /// <summary>异步跑一回合：攻击和打击卡交给钩子处理，逐条播放特效。</summary>
    public async Task<string> RunTurnAsync()
    {
        _log.Clear();

        int deployBudget = _difficulty switch
        {
            AIDifficulty.Easy => 1,
            AIDifficulty.Normal => 2,
            AIDifficulty.Hard => 3,
            _ => 4,
        };

        await TryStrikeCards();

        // 先看这一轮想建什么；有钱就先建，没钱就攒钱（部署时把这份钱留着）
        BuildPlan plan = NextBuildPlan();
        bool built = plan != null && _me.Gold >= plan.Cost && ExecutePlan(plan);

        // 只在「再攒一两回合就够」的时候才压缩部队开支；
        // 目标太贵（比如指挥部三级 1000）时照常出兵，不能站着攒钱不打
        int keepGold = 0;
        if (!built && plan != null)
            keepGold = plan.Cost <= _me.Gold + 250 ? Mathf.Min(plan.Cost, _me.Gold) : 90;

        // 有空军卡但还买不起时，优先攒钱出空军（AI 之前几乎不出空军）
        CardDef wantedAir = WantedAirCard();
        if (wantedAir != null && _me.Gold < wantedAir.Cost)
            keepGold = Mathf.Max(keepGold, wantedAir.Cost);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--ai-log") >= 0)
            GD.Print($"[AIPlan] {_id} 紧张度={Tension():F2} 计划={plan?.Label ?? "无"} " +
                     $"费用={plan?.Cost ?? 0} 金币={_me.Gold} 结果={(built ? "已执行" : "未执行")} 留钱={keepGold}");

        DeployUnits(deployBudget, keepGold);

        await MoveAndAttack(air: true);
        await MoveAndAttack(air: false);

        // 部队出完了，如果还有余钱就再补一次建造
        if (!built)
            ManageBuildings();

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--ai-log") >= 0)
        {
            string buildings = string.Join(",", _s.Buildings
                .Where(b => b.Owner == _id && !b.IsDead)
                .GroupBy(b => b.DefId)
                .Select(g => $"{g.Key}x{g.Count()}"));
            int air = MyUnits.Count(u => u.IsAir);
            GD.Print($"[AI] T{_s.TurnNumber} {_id} 金币={_me.Gold} " +
                     $"建筑[{buildings}] 单位={MyUnits.Count}(空军{air})");
        }

        return string.Join("；", _log);
    }

    // ─────────────────────────── 通用信息 ───────────────────────────

    private HexCoord HqCoord => _s.Buildings.FirstOrDefault(b => b.Owner == _id && b.IsHq)?.Coord
                                ?? new HexCoord(0, 0);

    private HexCoord TargetHqCoord => _s.Buildings.FirstOrDefault(b => b.Owner != _id && b.IsHq)?.Coord
                                      ?? new HexCoord(0, 0);

    private int MyBuildingCount(string defId) =>
        _s.Buildings.Count(b => b.Owner == _id && !b.IsDead && b.DefId == defId);

    private List<UnitInst> MyUnits => _s.Units.Where(u => u.Owner == _id && u.Alive).ToList();

    private List<UnitInst> EnemyUnits => _s.Units.Where(u => u.Owner != _id && u.Alive).ToList();

    /// <summary>离敌方最近的一格（用作推进方向）。</summary>
    private HexCoord FrontCoord
    {
        get
        {
            UnitInst nearest = EnemyUnits
                .OrderBy(u => HexCoord.Distance(u.Coord, HqCoord))
                .FirstOrDefault();

            if (nearest != null)
                return nearest.Coord;

            BuildingInst target = _s.Buildings
                .Where(b => b.Owner != _id && !b.IsDead)
                .OrderBy(b => HexCoord.Distance(b.Coord, HqCoord))
                .FirstOrDefault();

            return target?.Coord ?? TargetHqCoord;
        }
    }

    // ─────────────────────────── 打击卡 ───────────────────────────

    /// <summary>
    /// 空袭/导弹这类「点格子」的卡：
    /// 对每张手牌里的打击卡都算一次「最佳目标 + 收益/费用」，挑性价比最高的打出去。
    /// </summary>
    private async Task TryStrikeCards()
    {
        TryRepairCard();

        int guard = 0;
        while (guard++ < 3)
        {
            int bestIndex = -1;
            CardDef bestCard = null;
            HexCoord bestTarget = default;
            float bestValue = 0f;

            for (int i = 0; i < _me.Hand.Count; i++)
            {
                CardDef c = GameDatabase.Card(_me.Hand[i]);
                if (c == null || c.KindEnum != CardKind.Strike || _me.Gold < c.Cost)
                    continue;

                // 维修卡也是「指向格子」的，但它不造成伤害，单独处理
                if (c.Effect == "repair")
                    continue;

                (HexCoord? coord, float score) = BestStrikeTarget(c);
                if (coord == null)
                    continue;

                // 性价比：同样一笔钱，哪张卡打出的收益最高
                float value = score / Mathf.Max(1, c.Cost);
                if (value > bestValue)
                {
                    bestValue = value;
                    bestIndex = i;
                    bestCard = c;
                    bestTarget = coord.Value;
                }
            }

            if (bestCard == null || bestIndex < 0)
                return;

            // 有表现层钩子就交给它（播特效、命中再结算），否则直接结算
            if (StrikeHook != null)
            {
                await StrikeHook(bestCard.Id, bestTarget);
                _log.Add($"使用 {bestCard.NameZh}");
                continue;
            }

            string result = _s.PlayGlobalCard(_me, bestCard.Id, bestTarget, out string error);
            if (!string.IsNullOrEmpty(error))
            {
                _log.Add($"打击卡失败:{error}");
                return;
            }

            _log.Add(string.IsNullOrEmpty(result) ? $"使用 {bestCard.NameZh}" : result);
        }
    }

    /// <summary>维修卡：找己方受伤最重的一格修一下。</summary>
    private void TryRepairCard()
    {
        int index = -1;
        CardDef card = null;

        for (int i = 0; i < _me.Hand.Count; i++)
        {
            CardDef c = GameDatabase.Card(_me.Hand[i]);
            if (c == null || c.Effect != "repair" || _me.Gold < c.Cost)
                continue;
            card = c;
            index = i;
            break;
        }

        if (card == null || index < 0)
            return;

        // 找「己方单位且残血最多」的格子
        HexCoord? best = null;
        int bestMissing = 0;

        foreach (Cell cell in _s.Cells.Values)
        {
            int missing = cell.Units
                .Where(u => u.Alive && u.Owner == _id && u.Hp < u.MaxHp)
                .Sum(u => u.MaxHp - u.Hp);

            if (missing > bestMissing)
            {
                bestMissing = missing;
                best = cell.Coord;
            }
        }

        if (best == null || bestMissing < 80)
            return;

        string result = _s.PlayGlobalCard(_me, card.Id, best, out string error);
        if (string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(result))
            _log.Add(result);
    }

    /// <summary>
    /// 为这张打击卡找最划算的目标格：
    ///   · 单位按「实际能打掉的伤害 × 0.6」计价，能一击必杀再加「对方攻击力 × 1.4」；
    ///   · 建筑按「能打掉的伤害 × 0.5」计价，打掉加 120；
    ///   · 敌方指挥部不吃打击卡，直接跳过；
    ///   · 至少要打出这个卡 45% 的理论伤害才值得用（钱紧时要求更高，钱多时放宽）。
    /// </summary>
    private (HexCoord? Coord, float Score) BestStrikeTarget(CardDef card)
    {
        int power = Mathf.Max(1, card.Power);

        // 金币宽裕时更愿意用（多打点伤害就值），紧张时要求打满价值
        float threshold = _me.Gold >= card.Cost + 300 ? power * 0.35f : power * 0.45f;

        HexCoord? best = null;
        float bestScore = 0f;

        foreach (Cell cell in _s.Cells.Values)
        {
            // 敌方指挥部不吃直接伤害卡
            if (cell.Building is { IsDead: false } hqCell && hqCell.IsHq && hqCell.Owner != _id)
                continue;

            float score = 0f;

            foreach (UnitInst u in cell.Units.Where(u => u.Alive && u.Owner != _id))
            {
                int dealt = Mathf.Min(u.Hp, power);
                score += dealt * 0.6f;
                if (u.Hp <= power)
                    score += u.Def.Damage * 1.4f;      // 能打死，额外价值
            }

            if (cell.Building is { IsDead: false } b && b.Owner != _id)
            {
                int dealt = Mathf.Min(b.Hp, power);
                score += dealt * 0.5f;
                if (b.Hp <= power)
                    score += 120f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = cell.Coord;
            }
        }

        if (best == null || bestScore < threshold)
            return (null, 0f);

        return (best, bestScore);
    }

    // ─────────────────────────── 部署 ───────────────────────────

    private void DeployUnits(int budget, int keepGold = 0)
    {
        int deployed = 0;
        int guard = 0;

        while (deployed < budget && guard++ < 12)
        {
            int index = PickCard();
            if (index < 0)
                break;

            CardDef card = GameDatabase.Card(_me.Hand[index]);

            // 还在攒钱造建筑时，不要把钱花光
            if (keepGold > 0 && _me.Gold - card.Cost < keepGold)
                break;

            HexCoord? spot = FindDeploySpot(card);
            if (spot == null)
                break;

            if (!_s.PlayUnitCard(_me, _me.Hand[index], spot.Value, out string err))
            {
                _log.Add("部署失败:" + err);
                break;
            }

            deployed++;
            _log.Add($"部署 {card.NameZh}");
        }
    }

    /// <summary>
    /// 选牌：先看有没有空军卡（AI 之前基本不出空军，靠这条让它优先用),
    /// 否则挑负担得起的最贵的陆军。
    /// </summary>
    private int PickCard()
    {
        int airIndex = -1;
        CardDef airCard = null;
        int groundIndex = -1;
        CardDef groundCard = null;

        int myAir = MyUnits.Count(u => u.IsAir);
        int enemyAir = EnemyUnits.Count(u => u.IsAir);

        for (int i = 0; i < _me.Hand.Count; i++)
        {
            CardDef c = GameDatabase.Card(_me.Hand[i]);
            if (c == null || c.KindEnum != CardKind.Unit || _me.Gold < c.Cost)
                continue;

            bool isAir = GameDatabase.Unit(c.Unit)?.IsAir ?? false;
            if (isAir)
            {
                if (airCard == null || c.Cost > airCard.Cost)
                {
                    airCard = c;
                    airIndex = i;
                }
            }
            else if (groundCard == null || c.Cost > groundCard.Cost)
            {
                groundCard = c;
                groundIndex = i;
            }
        }

        // 敌方有空军、或自己空军少于 2 架时，优先出空军
        if (airCard != null && (enemyAir > 0 || myAir < 2))
            return airIndex;

        if (groundCard != null)
            return groundIndex;

        return airIndex;
    }

    /// <summary>手里最便宜的空军卡（前提是有机场、且自己空军不足 2 架）。</summary>
    private CardDef WantedAirCard()
    {
        if (MyUnits.Count(u => u.IsAir) >= 2)
            return null;
        if (MyBuildingCount("helipad") == 0)
            return null;

        return _me.Hand
            .Select(GameDatabase.Card)
            .Where(c => c is { KindEnum: CardKind.Unit } &&
                        (GameDatabase.Unit(c.Unit)?.IsAir ?? false))
            .OrderBy(c => c.Cost)
            .FirstOrDefault();
    }

    private HexCoord? FindDeploySpot(CardDef card)
    {
        HexCoord front = FrontCoord;

        IEnumerable<HexCoord> candidates = _s.Cells.Values
            .Where(c => _s.CanDeployCard(card.Id, _id, c.Coord, out _))
            .Select(c => c.Coord);

        // 部署点尽量靠近前线（但仍在自己的控制区里）
        var ordered = candidates
            .OrderBy(c => HexCoord.Distance(c, front))
            .ThenBy(c => HexCoord.Distance(c, HqCoord))
            .ToList();

        return ordered.Count > 0 ? ordered[0] : null;
    }

    // ─────────────────────────── 行动 ───────────────────────────

    private async Task MoveAndAttack(bool air)
    {
        foreach (UnitInst u in MyUnits.Where(x => x.IsAir == air).ToList())
        {
            if (!u.Alive)
                continue;

            if (await TryAttackBest(u))
                continue;

            HexCoord goal = FrontCoord;
            HashSet<HexCoord> moves = _s.MoveRange(u);
            if (moves.Count == 0)
                continue;

            int before = HexCoord.Distance(u.Coord, goal);
            HexCoord best = moves.OrderBy(m => HexCoord.Distance(m, goal)).First();
            if (HexCoord.Distance(best, goal) >= before)
                continue;

            _s.TryMove(u, best, out _);
            await TryAttackBest(u);
        }
    }

    private async Task<bool> TryAttackBest(UnitInst u)
    {
        var targets = _s.AttackTargets(u).ToList();
        if (targets.Count == 0)
            return false;

        // 优先打：能一击必杀的单位 > 指挥部 > 建筑 > 其他单位
        var best = targets
            .OrderByDescending(t =>
            {
                if (t.unit != null)
                    return t.unit.Hp <= u.Def.Damage ? 1000 : 500 - t.unit.Hp * 0.1f;
                return t.building != null && t.building.IsHq ? 900 : 300;
            })
            .First();

        HexCoord tc = best.building != null ? best.building.Coord : best.unit.Coord;

        // 有表现层钩子就交给它（播开火特效、命中再结算）
        if (AttackHook != null)
        {
            await AttackHook(u, tc);
            return true;
        }

        string r = _s.PerformAttack(u, tc);
        if (!string.IsNullOrEmpty(r))
            _log.Add(r);
        return true;
    }

    // ─────────────────────────── 建造经营 ───────────────────────────

    /// <summary>这一轮打算造/升级什么（null = 暂时没有计划，钱可以随便花在部队上）。</summary>
    private sealed class BuildPlan
    {
        public string DefId = "";
        public int Cost;
        public bool Upgrade;
        public string Label = "";

        /// <summary>经济类（城市）：战况吃紧时先不搞经济。</summary>
        public bool Economy;
    }

    /// <summary>
    /// 战局紧张度 0~1：
    ///   · 敌方单位离指挥部越近、数量越多 → 越高；
    ///   · 己方部队越弱、指挥部血越少 → 越高。
    /// 用它决定「先修防御还是先搞经济」。
    /// </summary>
    private float Tension()
    {
        HexCoord hq = HqCoord;

        float enemyNear = 0f;
        foreach (UnitInst u in EnemyUnits)
        {
            int d = HexCoord.Distance(u.Coord, hq);
            if (d > 5)
                continue;

            // 越近威胁越大（距离 1 权重 1.0，距离 5 权重 0.2）
            float proximity = Mathf.Clamp(1.2f - d * 0.2f, 0.2f, 1.2f);
            enemyNear += (u.Def.Damage + u.Hp * 0.3f) * proximity;
        }

        float myPower = MyUnits.Sum(u => u.Def.Damage + u.Hp * 0.3f) + 80f;

        BuildingInst hqInst = _s.Buildings.FirstOrDefault(b => b.Owner == _id && b.IsHq);
        float hqHurt = hqInst != null ? 1f - (float)hqInst.Hp / Mathf.Max(1, hqInst.MaxHp) : 0.6f;

        float ratio = enemyNear / myPower * 0.85f;
        return Mathf.Clamp(ratio + hqHurt * 0.45f, 0f, 1f);
    }

    /// <summary>升级一个建筑的「划算程度」，随紧张度变化：紧张时修防御，平稳时搞经济。</summary>
    private static float UpgradeWeight(BuildingInst b, float tension)
    {
        return b.DefId switch
        {
            "gunTurret" or "aaGun" => 1.15f + tension * 1.9f,      // 越紧张越该修炮
            "barracks" => 0.85f + tension * 1.0f,                  // 兵营直接决定出兵质量
            "helipad" => 1.0f + (1f - tension) * 1.1f,             // 平稳时冲战斗机
            "city" => 0.55f + (1f - tension) * 1.5f,               // 平稳时扩经济
            "hq" => 1.25f + (1f - tension) * 0.5f,                 // 抽牌/控制半径
            _ => 0.5f,
        };
    }

    private BuildPlan NextBuildPlan()
    {
        if (_difficulty == AIDifficulty.Easy)
            return null;

        int turn = _s.TurnNumber;
        int enemyAir = EnemyUnits.Count(u => u.IsAir);
        int cities = MyBuildingCount("city");
        int barracks = MyBuildingCount("barracks");
        int helipads = MyBuildingCount("helipad");
        int defenses = MyBuildingCount("gunTurret") + MyBuildingCount("aaGun");

        float tension = Tension();
        var options = new List<(BuildPlan Plan, float Weight)>();

        // ── 该补的建筑 ──
        if (helipads == 0)
        {
            options.Add((new BuildPlan
            {
                DefId = "helipad",
                Cost = BuildingCost("helipad"),
                Label = "建造直升机坪",
            }, 2.2f));
        }

        if (barracks == 0)
        {
            options.Add((new BuildPlan
            {
                DefId = "barracks",
                Cost = BuildingCost("barracks"),
                Label = "建造兵营",
            }, 1.5f + tension * 0.6f));
        }

        if (cities < 2)
        {
            options.Add((new BuildPlan
            {
                DefId = "city",
                Cost = BuildingCost("city"),
                Label = "建造城市",
                Economy = true,
            }, 1.1f + (1f - tension) * 0.9f));
        }

        if (defenses < 2)
        {
            string want = enemyAir > 0 ? "aaGun" : "gunTurret";
            options.Add((new BuildPlan
            {
                DefId = want,
                Cost = BuildingCost(want),
                Label = want == "aaGun" ? "建造防空炮" : "建造炮台",
            }, 1.0f + tension * 1.8f));
        }

        // 导弹井：中后期、局势平稳时补一座（稳定产出导弹打击卡）
        if (MyBuildingCount("missileSilo") == 0 && turn >= 6)
        {
            options.Add((new BuildPlan
            {
                DefId = "missileSilo",
                Cost = BuildingCost("missileSilo"),
                Label = "建造巡航导弹井",
            }, 0.9f + (1f - tension) * 0.8f));
        }

        // ── 升级：把每座还能升级的建筑都列为候选，按战况加权 ──
        foreach (BuildingInst b in _s.Buildings.Where(b => b.Owner == _id && !b.IsDead &&
                                                           b.Level < b.Def.MaxLevel).ToList())
        {
            // 指挥部别开局就砸钱，前期先铺基础设施
            if (b.IsHq && turn < 4)
                continue;

            options.Add((new BuildPlan
            {
                DefId = b.DefId,
                Cost = b.Def.UpgradeCostAt(b.Level),
                Upgrade = true,
                Label = $"升级{b.Def.NameZh} Lv{b.Level + 1}",
                Economy = b.DefId == "city",
            }, UpgradeWeight(b, tension)));
        }

        if (options.Count == 0)
            return null;

        // 战况吃紧时先别搞经济：优先修防御/出兵建筑
        bool urgent = tension >= 0.6f;
        var pool = urgent ? options.Where(o => !o.Plan.Economy).ToList() : options;
        if (pool.Count == 0)
            pool = options;

        // 优先执行「权重最高且现在就买得起」的；买不起就攒钱攒最高的那个
        var affordable = pool
            .Where(o => _me.Gold >= o.Plan.Cost)
            .OrderByDescending(o => o.Weight)
            .ToList();

        if (affordable.Count > 0)
            return affordable[0].Plan;

        return pool.OrderByDescending(o => o.Weight).First().Plan;
    }

    private bool ExecutePlan(BuildPlan plan)
    {
        // 具体的「建造/升级」日志由 TryBuildFirst / TryUpgradeFirst 输出，这里不再重复记
        return plan.Upgrade ? TryUpgradeFirst(plan.DefId) : TryBuildFirst(plan.DefId);
    }

    /// <summary>兜底的建造：计划执行不了时（没地块等），试着随便补点东西。</summary>
    private void ManageBuildings()
    {
        if (_difficulty == AIDifficulty.Easy)
            return;

        BuildPlan plan = NextBuildPlan();
        if (plan != null && _me.Gold >= plan.Cost)
        {
            if (ExecutePlan(plan))
                return;
        }

        if (_me.Gold >= 700)
        {
            if (TryUpgradeFirst("barracks"))
                return;
            TryBuildFirst("city");
        }
    }

    private static int BuildingCost(string defId) => GameDatabase.Building(defId)?.UpgradeCostAt(0) ?? 9999;

    /// <summary>
    /// 在自己控制区里挑一个空位建造：
    ///   · 生产类建筑（城市 / 兵营 / 机场 / 导弹井）尽量**远离前线**，越靠后越安全；
    ///   · 防御类建筑（炮台 / 防空炮）尽量贴近前线。
    /// </summary>
    private bool TryBuildFirst(string defId)
    {
        BuildingDef def = GameDatabase.Building(defId);
        if (def == null || _me.Gold < def.UpgradeCostAt(0))
            return false;

        bool production = defId is "city" or "barracks" or "helipad" or "missileSilo";

        var cells = _s.Cells.Values
            .Where(c => c.Building == null && _s.CanBuildAt(c.Coord, _id))
            .Select(c => c.Coord)
            .ToList();

        if (cells.Count == 0)
            return false;

        HexCoord front = FrontCoord;
        HexCoord hq = HqCoord;

        var ordered = production
            // 离前线越远越好；同样远时挨着指挥部（好防守、不分散）
            ? cells.OrderByDescending(c => HexCoord.Distance(c, front))
                .ThenBy(c => HexCoord.Distance(c, hq))
            // 防御建筑贴前线
            : cells.OrderBy(c => HexCoord.Distance(c, front))
                .ThenBy(c => HexCoord.Distance(c, hq));

        HexCoord target = ordered.First();
        if (!_s.TryBuild(_id, defId, target, out string err))
        {
            _log.Add($"建造{def.NameZh}失败:{err}");
            return false;
        }

        _log.Add($"建造{def.NameZh}（距前线 {HexCoord.Distance(target, front)}）");
        return true;
    }

    private bool TryUpgradeFirst(string defId)
    {
        BuildingInst b = _s.Buildings
            .Where(x => x.Owner == _id && !x.IsDead && x.DefId == defId && x.Level < x.Def.MaxLevel)
            .OrderBy(x => x.Level)
            .FirstOrDefault();

        if (b == null || _me.Gold < b.Def.UpgradeCostAt(b.Level))
            return false;

        _s.TryUpgrade(b, out _);
        _log.Add($"升级{b.Def.NameZh} Lv{b.Level}");
        return true;
    }
}
