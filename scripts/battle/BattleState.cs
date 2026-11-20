using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.Battle;

public sealed class Cell
{
    public HexCoord Coord;
    public TerrainType Terrain;
    public BuildingInst Building;

    /// <summary>每格可以有多个单位（参考-6）。</summary>
    public readonly List<UnitInst> Units = new();

    /// <summary>该格属于哪个玩家的控制区；双方同时覆盖时为争夺区。</summary>
    public bool ControlPlayer;
    public bool ControlEnemy;

    public bool Contested => ControlPlayer && ControlEnemy;
    public bool PassableGround => Terrain != TerrainType.Water && Building == null;

    /// <summary>
    /// 陆军能否进入这一格：不能过水；有建筑时只能是**己方**建筑
    /// （允许走进并停留在自家建筑上，但不能进敌方建筑）。
    /// </summary>
    public bool CanGroundEnter(PlayerId owner) =>
        Terrain != TerrainType.Water && (Building == null || Building.Owner == owner);
}

public sealed class BuildingInst
{
    public int Id;
    public string DefId = "";
    public BuildingDef Def;
    public PlayerId Owner;
    public HexCoord Coord;
    public int Level = 1;
    public int Hp;

    /// <summary>本回合是否已经花过钱多开一炮（每回合重置）。</summary>
    public bool ExtraFired;

    /// <summary>本回合是否已经出过兵（每回合重置）。</summary>
    public bool SpawnUsed;

    /// <summary>是否允许这座建筑每回合产卡（可开关，默认开）。</summary>
    public bool ProduceEnabled = true;

    /// <summary>所属战场（用于读取所在格地形）。</summary>
    public BattleState State;

    public int MaxHp => Def.HpAt(Level);

    /// <summary>控制半径：雪地上的建筑 +1。</summary>
    public int ControlRadius =>
        Def.ControlAt(Level) + (State != null && State.TerrainAt(Coord) == TerrainType.Snow ? 1 : 0);
    public bool IsHq => DefId == "hq";
    public bool IsDead => Hp <= 0;
}

public sealed class UnitInst
{
    public int Id;
    public string DefId = "";
    public UnitDef Def;
    public PlayerId Owner;
    public HexCoord Coord;
    public int Hp;
    public bool Moved;
    public bool Attacked;

    /// <summary>本回合刚由建筑出兵：登场当回合不能行动，下回合恢复。</summary>
    public bool SpawnedThisTurn;

    /// <summary>潜艇：当前是否深潜（默认浮出）。</summary>
    public bool Submerged;

    /// <summary>潜艇：本回合是否已经切换过潜浮。</summary>
    public bool DiveUsed;

    /// <summary>召唤这个单位时支付的价格（强制兵役阵亡负债用）。</summary>
    public int SummonCost;

    /// <summary>航母：本回合是否已经用过雷达搜索。</summary>
    public bool RadarUsed;

    /// <summary>航母：本回合是否已经出过兵。</summary>
    public bool CarrierSpawnUsed;

    public int MaxHp => Def.Hp;
    public bool IsAir => Def.IsAir;
    public bool Alive => Hp > 0;
    public bool CanAct => !Moved || !Attacked;
}

public sealed class PlayerState
{
    public PlayerId Id;
    public int Gold;
    public readonly List<string> Deck = new();
    public readonly List<string> Hand = new();
    public readonly List<string> Discard = new();

    /// <summary>延时决策等赋予的「保留」实例标记（不改变卡牌定义）。</summary>
    public readonly List<string> Retained = new();

    /// <summary>呼叫增援拿到的卡：手牌实例费用临时为 0（打出后消失）。</summary>
    public readonly List<string> ZeroCost = new();

    /// <summary>消耗堆：打出「消耗」关键字的卡进这里，永远不会洗回牌库。</summary>
    public readonly List<string> Exhaust = new();

    /// <summary>
    /// 手牌里「由哪座建筑产出」的记录（目前只有导弹袭击卡需要）：
    /// 拆除对应发射井时只烧掉它产出的那几张。
    /// </summary>
    public readonly List<(int SiloId, string Card)> Produced = new();

    /// <summary>手牌里「发射井产出、打出后应进消耗堆」的卡（按张记录，出牌时消耗一个）。</summary>
    public readonly List<string> ProducedExhaust = new();

    /// <summary>
    /// 带「虚无」关键字的手牌实例（建筑发给你的兵种卡）：
    /// 己方回合结束时若还在手上，直接进消耗堆而不是弃牌堆。
    /// </summary>
    public readonly List<string> Ethereal = new();

    /// <summary>消耗堆里那些「虚无」卡（查看器里照样显示关键字）。</summary>
    public readonly List<string> EtherealExhausted = new();

    /// <summary>弃牌堆里那些「虚无」卡（打出去后关键字不能丢）。</summary>
    public readonly List<string> EtherealDiscarded = new();

    /// <summary>抽牌堆里那些「虚无」卡（洗回牌库时标记跟着走）。</summary>
    public readonly List<string> EtherealDeck = new();

    /// <summary>弃牌堆里那些「发射井产出、打出即消耗」的卡。</summary>
    public readonly List<string> ProducedExhaustDiscarded = new();

    /// <summary>抽牌堆里那些「发射井产出、打出即消耗」的卡。</summary>
    public readonly List<string> ProducedExhaustDeck = new();

    /// <summary>消耗堆里仍然带「消耗」关键字的实例（如发射井产出的导弹）。</summary>
    public readonly List<string> ProducedExhaustExhausted = new();

    /// <summary>各牌堆的「保留」实例标记。</summary>
    public readonly List<string> RetainedExhausted = new();
    public readonly List<string> RetainedDiscarded = new();
    public readonly List<string> RetainedDeck = new();

    /// <summary>各牌堆的「临时 0 费」实例标记。</summary>
    public readonly List<string> ZeroCostExhausted = new();
    public readonly List<string> ZeroCostDiscarded = new();
    public readonly List<string> ZeroCostDeck = new();

    /// <summary>「强行军」能力剩余回合数：&gt;0 时地面单位移动 +1。</summary>
    public int ForcedMarchTurns;

    /// <summary>发行债券：下回合开始时需要扣除的负债金额。</summary>
    public int PendingBondDebt;

    /// <summary>房屋补贴：下回合开始时要免费升级的建筑数量。</summary>
    public int PendingHousingUpgrades;

    /// <summary>房屋补贴生效的回合号（打出后的下一回合）。</summary>
    public int HousingUpgradeDueTurn;


    /// <summary>强制兵役：建筑召唤军队免费，阵亡时按召唤价 110% 负债。</summary>
    public bool Conscription;

    /// <summary>战时医疗层数：每层回合开始恢复已损生命 25%。</summary>
    public int MedicalStacks;

    /// <summary>「世界如此变幻莫测」的累计打出次数，用于让每次随机候选不同。</summary>
    public int RandomCardPlays;

    public bool IsPlayer => Id == PlayerId.Player;
}

/// <summary>一局战斗的全部状态。纯数据，不依赖节点，方便后面接 AI 与存档。</summary>
public sealed class BattleState
{
    /// <summary>每回合的基础抽牌数（不含指挥部/兵营等建筑的额外发牌）。</summary>
    public const int DrawPerTurn = 3;

    public const int HandLimit = 10;

    public MapDef Map;
    public AIDifficulty Difficulty = AIDifficulty.Normal;

    public readonly Dictionary<HexCoord, Cell> Cells = new();
    public readonly List<BuildingInst> Buildings = new();
    public readonly List<UnitInst> Units = new();

    public PlayerState Player;
    public PlayerState Enemy;

    public BattlePhase Phase = BattlePhase.Setup;
    public int TurnNumber = 1;
    public PlayerId CurrentPlayer = PlayerId.Player;

    /// <summary>当前回合是否还没走完双方（联机镜像下不能按 Player/Enemy 判断）。</summary>
    public bool FirstHalf = true;
    public PlayerId Winner = PlayerId.None;
    public bool EnemyHqDestroyed;
    public bool PlayerHqDestroyed;

    private int _nextId = 1;

    // ── 随机数：按阵营分开，联机时由主机下发种子保证两端一致 ──
    public int PlayerSeed;
    public int EnemySeed;

    /// <summary>联机双方的全局随机种子（两端一致）。</summary>
    public int GlobalSeed;

    /// <summary>全局随机卡牌生成序号（两端按同一出牌顺序递增）。</summary>
    public int RandomRollSerial;

    private Random _rngPlayer;
    private Random _rngEnemy;

    public Random RngOf(PlayerId id) =>
        id == PlayerId.Player ? (_rngPlayer ??= new Random()) : (_rngEnemy ??= new Random());

    public static BattleState Create(MapDef map, AIDifficulty difficulty) =>
        Create(map, difficulty, null, null, Session.StartHqLevel, 1, 0, 0);

    /// <summary>
    /// 联机/自定义开局：可以指定双方卡组、基地等级、随机种子（0 = 不固定，单机随机）。
    /// </summary>
    public static BattleState Create(
        MapDef map,
        AIDifficulty difficulty,
        List<string> playerDeck,
        List<string> enemyDeck,
        int playerHqLevel = 1,
        int enemyHqLevel = 1,
        int playerSeed = 0,
        int enemySeed = 0,
        bool mirrorHq = false)
    {
        GameDatabase.EnsureLoaded();
        var s = new BattleState
        {
            Map = map,
            Difficulty = difficulty,
            PlayerSeed = playerSeed,
            EnemySeed = enemySeed,
            GlobalSeed = unchecked(playerSeed ^ enemySeed),
            _rngPlayer = playerSeed != 0 ? new Random(playerSeed) : new Random(),
            _rngEnemy = enemySeed != 0 ? new Random(enemySeed) : new Random(),
        };

        for (int r = 0; r < map.Height; r++)
        {
            string row = r < map.TerrainRows.Length ? map.TerrainRows[r] : "";
            int startQ = -r / 2;
            for (int c = 0; c < map.Width; c++)
            {
                char ch = c < row.Length ? row[c] : 'g';
                var coord = new HexCoord(startQ + c, r);
                s.Cells[coord] = new Cell
                {
                    Coord = coord,
                    Terrain = TerrainFromChar(ch),
                };
            }
        }

        s.Player = new PlayerState { Id = PlayerId.Player, Gold = map.StartGold };
        s.Enemy = new PlayerState { Id = PlayerId.Enemy, Gold = map.StartGold };
        List<string> pDeck = playerDeck ?? Session.EffectiveDeck(map);
        List<string> eDeck = enemyDeck ?? new List<string>(map.EnemyDeck);
        s.Player.Deck.AddRange(pDeck);
        s.Enemy.Deck.AddRange(eDeck);

        // 开局洗牌一次（按阵营各自的随机数；联机时两端种子一致，洗出的顺序相同）
        s.Shuffle(s.Player.Deck, PlayerId.Player);
        s.Shuffle(s.Enemy.Deck, PlayerId.Enemy);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--deck-log") >= 0)
            GD.Print($"[Deck] 洗牌后 P={string.Join(",", s.Player.Deck.Take(Mathf.Min(6, s.Player.Deck.Count)))} " +
                     $"E={string.Join(",", s.Enemy.Deck.Take(Mathf.Min(6, s.Enemy.Deck.Count)))}");

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--deck-log") >= 0)
            GD.Print($"[Deck] 本局使用 {pDeck.Count} 张：{string.Join(",", pDeck)}");

        // 联机镜像视角：客户端把自己的基地放在地图的另一端
        HexCoord playerHq = new(mirrorHq ? map.EnemyHq[0] : map.PlayerHq[0],
                                mirrorHq ? map.EnemyHq[1] : map.PlayerHq[1]);
        HexCoord enemyHq = new(mirrorHq ? map.PlayerHq[0] : map.EnemyHq[0],
                               mirrorHq ? map.PlayerHq[1] : map.EnemyHq[1]);
        s.SpawnBuilding("hq", PlayerId.Player, playerHq, level: Mathf.Max(1, playerHqLevel), free: true);
        s.SpawnBuilding("hq", PlayerId.Enemy, enemyHq, level: Mathf.Max(1, enemyHqLevel), free: true);

        s.RecalculateZones();
        return s;
    }

    public static TerrainType TerrainFromChar(char ch) => ch switch
    {
        's' => TerrainType.Sand,
        'd' => TerrainType.Dirt,
        'r' => TerrainType.Rock,
        'n' => TerrainType.Snow,
        'w' => TerrainType.Water,
        _ => TerrainType.Grass,
    };

    public Cell Cell(HexCoord c) => Cells.TryGetValue(c, out Cell cell) ? cell : null;

    public PlayerState StateOf(PlayerId id) => id == PlayerId.Player ? Player : Enemy;
    public PlayerId OpponentOf(PlayerId id) => id == PlayerId.Player ? PlayerId.Enemy : PlayerId.Player;

    // ─────────────────────────── 建筑 ───────────────────────────

    public BuildingInst SpawnBuilding(string defId, PlayerId owner, HexCoord coord, int level = 1, bool free = false)
    {
        BuildingDef def = GameDatabase.Building(defId);
        Cell cell = Cell(coord);
        if (def == null || cell == null || cell.Building != null)
            return null;

        var b = new BuildingInst
        {
            Id = _nextId++,
            DefId = defId,
            Def = def,
            Owner = owner,
            Coord = coord,
            Level = level,
            Hp = def.HpAt(level),
            State = this,
        };
        cell.Building = b;
        Buildings.Add(b);
        RecalculateZones();
        return b;
    }

    public BuildingInst BuildingAt(HexCoord c) => Cell(c)?.Building;

    /// <summary>
    /// 该格是否可以被 player 建造：陆地建筑要非水面；海军基地要「临接陆地的水面」。
    /// 都要求无建筑、己方控制区且不与敌方重叠。
    /// </summary>
    public bool CanBuildAt(HexCoord coord, PlayerId player, string defId = "")
    {
        Cell cell = Cell(coord);
        if (cell == null || cell.Building != null)
            return false;

        bool navyBase = defId == "navyBase";
        if (navyBase)
        {
            if (cell.Terrain != TerrainType.Water)
                return false;

            // 四周全是水面的格子不能建海军基地
            bool nearLand = coord.Neighbors().Any(nb =>
            {
                Cell n = Cell(nb);
                return n != null && n.Terrain != TerrainType.Water;
            });
            if (!nearLand)
                return false;
        }
        else if (cell.Terrain == TerrainType.Water)
        {
            // 陆军建筑不能建在水面
            return false;
        }

        // 敌方单位站着的格子不能造（否则会出现「陆军停在敌方建筑上」）
        PlayerId foe = player == PlayerId.Player ? PlayerId.Enemy : PlayerId.Player;
        if (cell.Units.Any(u => u.Alive && u.Owner == foe))
            return false;

        if (!cell.ControlPlayer && !cell.ControlEnemy)
            return false;
        // 争夺区（双方控制区重叠）也允许双方建造
        return player == PlayerId.Player ? cell.ControlPlayer : cell.ControlEnemy;
    }

    /// <summary>这一格能否建造任意建筑（含海军基地）。</summary>
    public bool CanBuildAnythingAt(HexCoord coord, PlayerId player) =>
        CanBuildAt(coord, player) || CanBuildAt(coord, player, "navyBase");

    public bool TryBuild(PlayerId player, string defId, HexCoord coord, out string error)
    {
        error = "";
        BuildingDef def = GameDatabase.Building(defId);
        if (def == null)
        {
            error = "未知建筑";
            return false;
        }

        if (!CanBuildAt(coord, player, defId))
        {
            Cell target = Cell(coord);
            if (defId == "navyBase")
            {
                error = target == null || target.Terrain != TerrainType.Water
                    ? "海军基地只能建在水面上"
                    : !coord.Neighbors().Any(nb => Cell(nb) != null && Cell(nb).Terrain != TerrainType.Water)
                        ? "海军基地必须临接陆地"
                        : "只能建在自己或争夺区的控制区内";
            }
            else
            {
                error = target?.Terrain == TerrainType.Water
                    ? "水面不能建造（海军基地除外）"
                    : "只能建在自己或争夺区的控制区内";
            }

            return false;
        }

        PlayerState st = StateOf(player);
        int cost = def.UpgradeCostAt(0);
        if (st.Gold < cost)
        {
            error = "金币不足";
            return false;
        }

        st.Gold -= cost;
        SpawnBuilding(defId, player, coord);
        return true;
    }

    public bool TryUpgrade(BuildingInst b, out string error)
    {
        error = "";
        if (b == null || b.Level >= b.Def.MaxLevel)
        {
            error = "已经满级";
            return false;
        }

        PlayerState st = StateOf(b.Owner);
        int cost = b.Def.UpgradeCostAt(b.Level);
        if (st.Gold < cost)
        {
            error = "金币不足";
            return false;
        }

        st.Gold -= cost;
        b.Level++;
        b.Hp = b.MaxHp;
        RecalculateZones();
        return true;
    }

    /// <summary>因为失去发射井而从手牌里移除的卡（表现层用来播「烧卡」特效）。</summary>
    public readonly List<(PlayerId Owner, string Card)> RemovedCards = new();

    /// <summary>拆掉这座建筑能返还多少金币（本金的 65%）。</summary>
    public static int DemolishRefund(BuildingInst b)
    {
        if (b == null || b.Def == null)
            return 0;

        // 本金 = 建造价 + 已付的各级升级价
        int invested = b.Def.UpgradeCostAt(0);
        for (int lv = 1; lv < b.Level; lv++)
            invested += b.Def.UpgradeCostAt(lv);

        return (int)Math.Floor(invested * 0.65f + 0.5f);
    }
    public void Demolish(BuildingInst b)
    {
        if (b == null)
            return;
        PlayerState owner = StateOf(b.Owner);
        int refund = DemolishRefund(b);
        if (refund > 0)
            owner.Gold += refund;

        Cell cell = Cell(b.Coord);
        if (cell != null && cell.Building == b)
            cell.Building = null;
        Buildings.Remove(b);

        // 导弹井没了，它产出的导弹袭击卡也就失效了（从手牌移除，交给表现层播烧卡动画）
        // 只烧「这座井产出」的那几张，别的井产的卡不受影响
        if (b.DefId == "missileSilo")
        {
            PlayerState st = StateOf(b.Owner);
            int owned = st.Produced.Count(p => p.SiloId == b.Id);
            st.Produced.RemoveAll(p => p.SiloId == b.Id);

            for (int i = 0; i < owned; i++)
            {
                int at = st.Hand.IndexOf("magic_missileStrike");
                if (at < 0)
                    break;

                int occurrence = OccurrenceAt(st.Hand, at);
                st.Hand.RemoveAt(at);
                TakeOccurrence(st.Ethereal, "magic_missileStrike", occurrence);
                TakeOccurrence(st.ProducedExhaust, "magic_missileStrike", occurrence);
                TakeOccurrence(st.ZeroCost, "magic_missileStrike", occurrence);
                TakeOccurrence(st.Retained, "magic_missileStrike", occurrence);
                RemovedCards.Add((b.Owner, "magic_missileStrike"));
            }
        }

        RecalculateZones();
    }

    /// <summary>重新计算所有格子的控制区归属。每次建筑增删/升级后调用。</summary>
    public void RecalculateZones()
    {
        foreach (Cell c in Cells.Values)
        {
            c.ControlPlayer = false;
            c.ControlEnemy = false;
        }

        foreach (BuildingInst b in Buildings)
        {
            if (b.IsDead)
                continue;
            foreach (HexCoord h in HexCoord.Spiral(b.Coord, b.ControlRadius))
            {
                Cell cell = Cell(h);
                if (cell == null)
                    continue;
                if (b.Owner == PlayerId.Player)
                    cell.ControlPlayer = true;
                else
                    cell.ControlEnemy = true;
            }
        }
    }

    // ─────────────────────────── 建筑出兵 ───────────────────────────

    /// <summary>该建筑在当前等级可以出的所有兵种（按等级累加）。</summary>
    public static List<string> SpawnOptions(BuildingInst b)
    {
        var result = new List<string>();
        string[][] table = b?.Def?.SpawnByLevel;
        if (table == null || table.Length == 0)
            return result;

        int index = Mathf.Clamp(b.Level - 1, 0, table.Length - 1);
        foreach (string id in table[index])
        {
            if (!string.IsNullOrEmpty(id))
                result.Add(id);
        }

        return result;
    }

    /// <summary>兵种对应的卡牌价格（找不到就按 0 处理）。</summary>
    public static int UnitCost(string cardId)
    {
        CardDef card = GameDatabase.Card(cardId);
        return card?.Cost ?? 0;
    }

    /// <summary>能否从这座建筑出兵：等级 / 金币 / 本回合次数 / 格子占用。</summary>
    public bool CanSpawnFrom(BuildingInst b, string cardId, out string reason)
    {
        reason = "";
        if (b == null || b.IsDead)
        {
            reason = "建筑不可用";
            return false;
        }

        if (!SpawnOptions(b).Contains(cardId))
        {
            reason = "当前等级无法生产该兵种";
            return false;
        }

        if (b.SpawnUsed)
        {
            reason = "本回合已出过兵";
            return false;
        }

        if (!StateOf(b.Owner).Conscription && StateOf(b.Owner).Gold < UnitCost(cardId))
        {
            reason = "金币不足";
            return false;
        }

        if (Cell(b.Coord) == null)
        {
            reason = "格子不可用";
            return false;
        }

        return true;
    }

    /// <summary>在建筑本格召唤一个兵种（扣费 + 标记本回合已出兵）。</summary>
    public UnitInst TrySpawnFromBuilding(BuildingInst b, string cardId, out string reason)
    {
        if (!CanSpawnFrom(b, cardId, out reason))
            return null;

        CardDef card = GameDatabase.Card(cardId);
        UnitDef def = GameDatabase.Unit(card?.Unit);
        if (def == null)
        {
            reason = "找不到兵种";
            return null;
        }

        PlayerState spawner = StateOf(b.Owner);
        if (!spawner.Conscription)
            spawner.Gold -= UnitCost(cardId);

        UnitInst spawned = SpawnUnit(def.Id, b.Owner, b.Coord, UnitCost(cardId));
        if (spawned != null)
        {
            spawned.SpawnedThisTurn = true;
            spawned.Moved = true;      // 刚登场：本回合不能移动
            spawned.Attacked = true;   // 本回合不能攻击
        }

        b.SpawnUsed = true;
        reason = "";
        return spawned;
    }
    // ─────────────────────────── 海军特殊能力 ───────────────────────────
    /// <summary>航母可出动的舰载机卡。</summary>
    public static readonly string[] CarrierAircraft = { "unit_fighter", "unit_helicopter" };

    /// <summary>切换潜艇潜浮状态（每回合一次）。</summary>
    public bool ToggleSubmarineDive(UnitInst u, out string error)
    {
        error = "";
        if (u == null || !u.Alive || u.DefId != "submarine")
        {
            error = "不是潜水艇";
            return false;
        }

        if (u.DiveUsed)
        {
            error = "本回合已经切换过潜浮状态";
            return false;
        }

        u.Submerged = !u.Submerged;
        u.DiveUsed = true;
        return true;
    }

    /// <summary>航母雷达搜索：射程内敌方潜艇全部浮出（每回合一次）。</summary>
    public bool UseCarrierRadar(UnitInst carrier, out string error, out int revealed)
    {
        error = "";
        revealed = 0;
        if (carrier == null || !carrier.Alive || carrier.DefId != "carrier")
        {
            error = "不是航空母舰";
            return false;
        }

        if (carrier.RadarUsed)
        {
            error = "本回合已经搜索过";
            return false;
        }

        int range = AttackRangeOf(carrier);
        foreach (UnitInst other in Units)
        {
            if (!other.Alive || other.Owner == carrier.Owner || other.DefId != "submarine")
                continue;
            if (!other.Submerged)
                continue;
            if (HexCoord.Distance(carrier.Coord, other.Coord) > range)
                continue;

            other.Submerged = false;
            revealed++;
        }

        carrier.RadarUsed = true;
        return true;
    }

    public bool CanCarrierSpawn(UnitInst carrier, string cardId, out string error)
    {
        error = "";
        if (carrier == null || !carrier.Alive || carrier.DefId != "carrier")
        {
            error = "不是航空母舰";
            return false;
        }

        if (System.Array.IndexOf(CarrierAircraft, cardId) < 0)
        {
            error = "航母只能出动战斗机或武装直升机";
            return false;
        }

        if (carrier.CarrierSpawnUsed)
        {
            error = "本回合已经出过兵";
            return false;
        }

        int cost = UnitCost(cardId);
        if (StateOf(carrier.Owner).Gold < cost)
        {
            error = "金币不足";
            return false;
        }

        return true;
    }

    public UnitInst TryCarrierSpawn(UnitInst carrier, string cardId, out string error)
    {
        if (!CanCarrierSpawn(carrier, cardId, out error))
            return null;

        CardDef card = GameDatabase.Card(cardId);
        UnitDef def = GameDatabase.Unit(card?.Unit);
        if (def == null)
        {
            error = "找不到兵种";
            return null;
        }

        StateOf(carrier.Owner).Gold -= UnitCost(cardId);
        UnitInst spawned = SpawnUnit(def.Id, carrier.Owner, carrier.Coord, UnitCost(cardId));
        if (spawned != null)
        {
            spawned.SpawnedThisTurn = true;
            spawned.Moved = true;
            spawned.Attacked = true;
        }

        carrier.CarrierSpawnUsed = true;
        error = "";
        return spawned;
    }
    // ─────────────────────────── 地形效果 ───────────────────────────

    /// <summary>该格地形（越界按泥土处理）。</summary>
    public TerrainType TerrainAt(HexCoord c) => Cell(c)?.Terrain ?? TerrainType.Dirt;

    /// <summary>地形对陆军移动力的修正：沙地 -1，岩地 +1，其余 0。</summary>
    public static int TerrainMoveDelta(TerrainType t) => t switch
    {
        TerrainType.Sand => -1,
        TerrainType.Rock => 1,
        _ => 0,
    };

    /// <summary>单位实际移动力（基础 + 战术加成 + 地形修正，至少 1；空军不吃地形）。</summary>
    public int MoveStepsOf(UnitInst u)
    {
        if (u == null)
            return 0;

        int steps = u.Def.Move;
        if (StateOf(u.Owner).ForcedMarchTurns > 0 && !u.IsAir)
            steps += 1;

        if (!u.IsAir)
            steps += TerrainMoveDelta(TerrainAt(u.Coord));

        return Mathf.Max(1, steps);
    }

    /// <summary>单位实际攻击射程：雪地 +1。</summary>
    public int AttackRangeOf(UnitInst u)
    {
        if (u == null)
            return 0;

        return u.Def.Range + (TerrainAt(u.Coord) == TerrainType.Snow ? 1 : 0);
    }

    /// <summary>某玩家本回合所有产出金币的来源（建筑）。</summary>
    public IEnumerable<(BuildingInst Building, int Amount)> IncomeSources(PlayerId who) =>
        Buildings.Where(b => b.Owner == who && !b.IsDead)
                 .Select(b => (b, IncomeOf(b)))
                 .Where(x => x.Item2 > 0);

    /// <summary>建筑收入：草地上的建筑 +20%。</summary>
    public int IncomeOf(BuildingInst b)
    {
        if (b == null)
            return 0;

        int income = b.Def.IncomeAt(b.Level);
        if (TerrainAt(b.Coord) == TerrainType.Grass)
            income = Mathf.RoundToInt(income * 1.2f);

        return income;
    }
    // ─────────────────────────── 单位 ───────────────────────────

    public UnitInst SpawnUnit(string defId, PlayerId owner, HexCoord coord, int summonCost = 0)
    {
        UnitDef def = GameDatabase.Unit(defId);
        Cell cell = Cell(coord);
        if (def == null || cell == null)
            return null;

        var u = new UnitInst
        {
            Id = _nextId++,
            DefId = defId,
            Def = def,
            Owner = owner,
            Coord = coord,
            Hp = def.Hp,
            SummonCost = summonCost,
        };
        cell.Units.Add(u);
        Units.Add(u);
        return u;
    }

    /// <summary>单位阵亡：移除并结算「强制兵役」的召唤金额 110% 负债。</summary>
    public void KillUnit(UnitInst u, Cell cell = null)
    {
        if (u == null)
            return;

        (cell ?? Cell(u.Coord))?.Units.Remove(u);
        Units.Remove(u);

        PlayerState st = StateOf(u.Owner);
        if (st.Conscription && u.SummonCost > 0)
            st.Gold -= Mathf.CeilToInt(u.SummonCost * 1.1f);
    }

    private const string MarkerPlaceholderPrefix = "\u0001";

    private static string MarkerPlaceholder(string cardId) => MarkerPlaceholderPrefix + cardId;

    private static bool IsMarkerSlot(string entry, string cardId) =>
        entry == cardId || entry == MarkerPlaceholder(cardId);

    /// <summary>牌堆第 index 张是否带某个实例关键字（供查看器配对下标）。</summary>
    public static bool HasMarkerAt(List<string> cards, List<string> markers, int index)
    {
        if (cards == null || markers == null || index < 0 || index >= cards.Count)
            return false;

        string id = cards[index];
        return HasOccurrenceAt(markers, id, OccurrenceAt(cards, index));
    }

    private static bool HasOccurrenceAt(List<string> markers, string cardId, int occurrence)
    {
        int seen = 0;
        foreach (string m in markers)
        {
            if (!IsMarkerSlot(m, cardId))
                continue;
            if (seen++ == occurrence)
                return m == cardId;
        }

        return false;
    }

    /// <summary>给某个牌堆最后一张实例写入各关键字槽。</summary>
    private static void MarkZoneLast(List<string> zone, string cardId,
        List<string> etherealList, bool ethereal,
        List<string> exhaustList, bool exhaust,
        List<string> retainedList, bool retained,
        List<string> zeroCostList, bool zeroCost)
    {
        if (zone.Count == 0)
            return;

        int occurrence = OccurrenceAt(zone, zone.Count - 1);
        if (ethereal)
            GrantOccurrence(etherealList, cardId, occurrence);
        if (exhaust)
            GrantOccurrence(exhaustList, cardId, occurrence);
        if (retained)
            GrantOccurrence(retainedList, cardId, occurrence);
        if (zeroCost)
            GrantOccurrence(zeroCostList, cardId, occurrence);
    }

    /// <summary>给刚加入手牌最后一张的实例添加关键字。</summary>
    private static void MarkLastOccurrence(PlayerState st, string cardId,
        bool ethereal = false, bool producedExhaust = false,
        bool retained = false, bool zeroCost = false)
    {
        if (st == null || st.Hand.Count == 0)
            return;

        int occurrence = OccurrenceAt(st.Hand, st.Hand.Count - 1);
        if (ethereal)
            GrantOccurrence(st.Ethereal, cardId, occurrence);
        if (producedExhaust)
            GrantOccurrence(st.ProducedExhaust, cardId, occurrence);
        if (retained)
            GrantOccurrence(st.Retained, cardId, occurrence);
        if (zeroCost)
            GrantOccurrence(st.ZeroCost, cardId, occurrence);
    }

    /// <summary>把「第 occurrence 次出现的 cardId」标记为真；不足的槽位补占位符。</summary>
    private static bool GrantOccurrence(List<string> markers, string cardId, int occurrence)
    {
        if (occurrence < 0)
            return false;

        int seen = 0;
        for (int i = 0; i < markers.Count; i++)
        {
            if (!IsMarkerSlot(markers[i], cardId))
                continue;

            if (seen == occurrence)
            {
                if (markers[i] == cardId)
                    return false;
                markers[i] = cardId;
                return true;
            }
            seen++;
        }

        while (seen <= occurrence)
        {
            markers.Add(seen == occurrence ? cardId : MarkerPlaceholder(cardId));
            seen++;
        }
        return true;
    }

    /// <summary>手牌第 index 张的显示费用（呼叫增援得到的牌为 0）。</summary>
    public int CostAt(PlayerState st, int handIndex)
    {
        if (st == null || handIndex < 0 || handIndex >= st.Hand.Count)
            return 0;

        string id = st.Hand[handIndex];
        CardDef card = GameDatabase.Card(id);
        if (st.Conscription && card?.KindEnum == CardKind.Unit)
            return 0;

        int occurrence = OccurrenceAt(st.Hand, handIndex);
        if (HasOccurrenceAt(st.ZeroCost, id, occurrence))
            return 0;

        return card?.Cost ?? 0;
    }

    /// <summary>这张手牌实际是否带「保留」：定义自带，或延时决策赋予。</summary>
    public bool HasRetainAt(PlayerState st, int handIndex)
    {
        if (st == null || handIndex < 0 || handIndex >= st.Hand.Count)
            return false;

        string id = st.Hand[handIndex];
        if (GameDatabase.Card(id)?.Retain == true)
            return true;

        int occurrence = OccurrenceAt(st.Hand, handIndex);
        return HasOccurrenceAt(st.Retained, id, occurrence);
    }

    /// <summary>给这张手牌实例添加「保留」关键字。</summary>
    public bool GrantRetainAt(PlayerState st, int handIndex)
    {
        if (st == null || handIndex < 0 || handIndex >= st.Hand.Count)
            return false;

        if (HasRetainAt(st, handIndex))
            return false;

        int occurrence = OccurrenceAt(st.Hand, handIndex);
        return GrantOccurrence(st.Retained, st.Hand[handIndex], occurrence);
    }

    /// <summary>可以成为「延时决策」目标的手牌下标（过滤已有保留的牌）。</summary>
    public List<int> RetainTargetIndices(PlayerState st)
    {
        var result = new List<int>();
        for (int i = 0; i < st.Hand.Count; i++)
        {
            if (!HasRetainAt(st, i))
                result.Add(i);
        }

        return result;
    }
    /// <summary>当前打出的这张牌的实际费用（临时 0 费在这里生效）。</summary>
    public int EffectiveCost(PlayerState st, string cardId)
    {
        CardDef def = GameDatabase.Card(cardId);
        int baseCost = def?.Cost ?? 0;
        if (st == null)
            return baseCost;

        // 强制兵役：从手牌部署单位卡同样免费
        if (st.Conscription && def?.KindEnum == CardKind.Unit)
            return 0;

        int at = PlayIndex;
        if (at < 0 || at >= st.Hand.Count || st.Hand[at] != cardId)
            at = st.Hand.IndexOf(cardId);
        if (at < 0)
            return baseCost;

        int occurrence = OccurrenceAt(st.Hand, at);
        return HasOccurrenceAt(st.ZeroCost, cardId, occurrence) ? 0 : baseCost;
    }
    /// <summary>把一张牌以「虚无 + 消耗」的实例标记加入手牌。</summary>
    public bool AddCardWithMarkers(PlayerState st, string cardId, bool zeroCost = false)
    {
        if (st == null || st.Hand.Count >= HandLimit)
            return false;

        st.Hand.Add(cardId);
        int occurrence = OccurrenceAt(st.Hand, st.Hand.Count - 1);
        GrantOccurrence(st.Ethereal, cardId, occurrence);
        GrantOccurrence(st.ProducedExhaust, cardId, occurrence);
        if (zeroCost)
            GrantOccurrence(st.ZeroCost, cardId, occurrence);
        return true;
    }

    /// <summary>消耗手牌中的一张（不进弃牌堆，直接进消耗堆）。</summary>
    public bool ExhaustCardFromHand(PlayerState st, int handIndex)
    {
        if (st == null || handIndex < 0 || handIndex >= st.Hand.Count)
            return false;

        string id = st.Hand[handIndex];
        int occurrence = OccurrenceAt(st.Hand, handIndex);
        st.Hand.RemoveAt(handIndex);

        bool ethereal = TakeEtherealOccurrence(st, id, occurrence);
        bool produced = TakeOccurrence(st.ProducedExhaust, id, occurrence);
        bool zeroCost = TakeOccurrence(st.ZeroCost, id, occurrence);
        bool retained = TakeOccurrence(st.Retained, id, occurrence);

        st.Exhaust.Add(id);
        MarkZoneLast(st.Exhaust, id,
            st.EtherealExhausted, ethereal,
            st.ProducedExhaustExhausted, produced,
            st.RetainedExhausted, retained,
            st.ZeroCostExhausted, zeroCost);
        ExhaustedThisTurn.Add(id);
        return true;
    }

    /// <summary>复制一张手牌（默认 2 张复制品，复制品不带原卡关键字）。</summary>
    public int DuplicateHandCard(PlayerState st, int handIndex, int count = 2)
    {
        if (st == null || handIndex < 0 || handIndex >= st.Hand.Count)
            return 0;

        string id = st.Hand[handIndex];
        int occurrence = OccurrenceAt(st.Hand, handIndex);

        // 复制时连同实例关键字一起复制：虚无 / 消耗 / 保留 / 临时 0 费
        bool ethereal = HasOccurrenceAt(st.Ethereal, id, occurrence);
        bool producedExhaust = HasOccurrenceAt(st.ProducedExhaust, id, occurrence);
        bool retained = HasOccurrenceAt(st.Retained, id, occurrence);
        bool zeroCost = HasOccurrenceAt(st.ZeroCost, id, occurrence);

        int added = 0;
        for (int i = 0; i < count && st.Hand.Count < HandLimit; i++)
        {
            st.Hand.Add(id);
            int copyOccurrence = OccurrenceAt(st.Hand, st.Hand.Count - 1);
            if (ethereal)
                GrantOccurrence(st.Ethereal, id, copyOccurrence);
            if (producedExhaust)
                GrantOccurrence(st.ProducedExhaust, id, copyOccurrence);
            if (retained)
                GrantOccurrence(st.Retained, id, copyOccurrence);
            if (zeroCost)
                GrantOccurrence(st.ZeroCost, id, copyOccurrence);
            added++;
        }

        return added;
    }

    /// <summary>完全随机滚出 5 张卡（两端同种子，联机一致）。</summary>
    public List<string> RollRandomCardChoices(PlayerState st)
    {
        var pool = GameDatabase.Cards.Values
            .Select(c => c.Id)
            .OrderBy(id => id)
            .ToList();

        RandomRollSerial++;
        var rng = new System.Random(unchecked(91357 + GlobalSeed * 104729 +
            TurnNumber * 197 + RandomRollSerial * 7919));
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        return pool.Take(5).ToList();
    }

    private void AddCardWithMarkersOrDiscard(PlayerState st, string cardId)
    {
        if (!AddCardWithMarkers(st, cardId))
            st.Discard.Add(cardId);
    }

    /// <summary>从 5 张随机候选中取指定下标（最多 2 张）加入手牌。</summary>
    public int TakeRandomChoices(PlayerState st, System.Collections.Generic.IReadOnlyList<int> indices)
    {
        if (st == null || indices == null)
            return 0;

        int added = 0;
        foreach (int index in indices)
        {
            if (index < 0 || index >= PendingRandomChoices.Count)
                continue;

            string cardId = PendingRandomChoices[index];
            AddCardWithMarkersOrDiscard(st, cardId);
            added++;
            if (added >= 2)
                break;
        }

        PendingRandomChoices.Clear();
        return added;
    }

    /// <summary>从抽牌堆取指定下标的牌：手牌放不下的进弃牌堆。</summary>
    public int TakeDeckCards(PlayerState st, System.Collections.Generic.IReadOnlyList<int> indices)
    {
        if (st == null || indices == null)
            return 0;

        var sorted = new List<int>();
        foreach (int index in indices)
        {
            if (index >= 0 && index < st.Deck.Count && !sorted.Contains(index))
                sorted.Add(index);
        }
        sorted.Sort();
        sorted.Reverse();

        int moved = 0;
        foreach (int index in sorted)
        {
            string id = st.Deck[index];
            st.Deck.RemoveAt(index);
            if (st.Hand.Count < HandLimit)
                st.Hand.Add(id);
            else
                st.Discard.Add(id);
            moved++;
        }

        PendingDeckPick = false;
        return moved;
    }

    /// <summary>滚出三张随机的军队单位卡（同一局两端使用相同种子，联机一致）。</summary>
    public List<string> RollReinforceChoices(PlayerState st)
    {
        var pool = GameDatabase.Cards.Values
            .Where(c => c.KindEnum == CardKind.Unit)
            .Select(c => c.Id)
            .OrderBy(id => id)
            .ToList();

        var rng = new System.Random(31337 + TurnNumber * 131 + st.Hand.Count * 7);
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        return pool.Take(3).ToList();
    }

    /// <summary>玩家从三张增援中选择一张。</summary>
    public bool TakeReinforceChoice(PlayerState st, int index)
    {
        if (index < 0 || index >= PendingReinforceChoices.Count)
            return false;

        string cardId = PendingReinforceChoices[index];
        PendingReinforceChoices.Clear();
        return AddCardWithMarkers(st, cardId, zeroCost: true);
    }

    /// <summary>玩家选择一张手牌消耗，然后抽两张。</summary>
    public bool ResolveExhaustPick(PlayerState st, int handIndex)
    {
        if (!PendingExhaustPick || !ExhaustCardFromHand(st, handIndex))
            return false;

        PendingExhaustPick = false;
        DrawCard(st, 2);
        return true;
    }

    /// <summary>玩家选择一张手牌，复制两张加入手牌。</summary>
    public bool ResolveDuplicatePick(PlayerState st, int handIndex)
    {
        if (!PendingDuplicatePick)
            return false;

        DuplicateHandCard(st, handIndex, 2);
        PendingDuplicatePick = false;
        return true;
    }

    /// <summary>本回合免费升级过的建筑 id（表现层播升级动画）。</summary>
    public readonly List<int> LastFreeUpgradedBuildings = new();

    /// <summary>下回合免费升级若干己方建筑（房屋补贴）。</summary>
    private void UpgradeBuildingsForFree(PlayerState st, int count)
    {
        // 坐标排序 + 与阵营无关的种子：联机两端挑选结果一致
        var eligible = Buildings
            .Where(b => b.Owner == st.Id && !b.IsDead && b.Level < b.Def.MaxLevel)
            .OrderBy(b => b.Coord.Q)
            .ThenBy(b => b.Coord.R)
            .ToList();

        LastFreeUpgradedBuildings.Clear();
        var rng = new System.Random(7919 + TurnNumber * 97 + eligible.Count * 13);
        for (int i = 0; i < count && eligible.Count > 0; i++)
        {
            int pick = rng.Next(eligible.Count);
            BuildingInst b = eligible[pick];
            eligible.RemoveAt(pick);
            b.Level++;
            b.Hp = b.MaxHp;
            LastFreeUpgradedBuildings.Add(b.Id);
        }
    }
    public IEnumerable<UnitInst> UnitsAt(HexCoord c) =>
        Cell(c)?.Units.Where(u => u.Alive) ?? Enumerable.Empty<UnitInst>();

    /// <summary>单位本回合可移动到的格子（BFS，地形/建筑阻挡，单位可叠加）。</summary>
    public HashSet<HexCoord> MoveRange(UnitInst u)
    {
        var result = new HashSet<HexCoord>();
        if (u == null || !u.Alive || u.Moved)
            return result;

        int steps = MoveStepsOf(u);
        var visited = new Dictionary<HexCoord, int> { [u.Coord] = 0 };
        var queue = new Queue<HexCoord>();
        queue.Enqueue(u.Coord);

        while (queue.Count > 0)
        {
            HexCoord cur = queue.Dequeue();
            int d = visited[cur];
            if (d >= steps)
                continue;
            foreach (HexCoord nb in cur.Neighbors())
            {
                Cell cell = Cell(nb);
                if (cell == null)
                    continue;
                if (u.Def.Navy)
                {
                    // 海军只能在水面移动；有建筑时只能停自家建筑
                    if (cell.Terrain != TerrainType.Water)
                        continue;
                    if (cell.Building != null && cell.Building.Owner != u.Owner)
                        continue;
                }
                else if (!u.IsAir && !cell.CanGroundEnter(u.Owner))
                    continue;
                if (visited.ContainsKey(nb))
                    continue;
                visited[nb] = d + 1;
                result.Add(nb);
                queue.Enqueue(nb);
            }
        }

        return result;
    }

    public bool TryMove(UnitInst u, HexCoord to, out string error)
    {
        error = "";
        if (u == null || !u.Alive)
        {
            error = "目标无效";
            return false;
        }

        if (u.Moved)
        {
            error = "本回合已经移动过";
            return false;
        }

        if (!MoveRange(u).Contains(to))
        {
            error = "不在移动范围内";
            return false;
        }

        Cell from = Cell(u.Coord);
        from?.Units.Remove(u);
        u.Coord = to;
        Cell(  to).Units.Add(u);
        u.Moved = true;
        return true;
    }

    /// <summary>该建筑攻击能覆盖的格子（不含自身所在格）。ControlRadius 同时就是它的射程。</summary>
    public IEnumerable<HexCoord> BuildingRangeCells(BuildingInst b)
    {
        if (b == null || b.IsDead || b.Def == null || b.Def.AttackRange <= 0)
            yield break;

        foreach (HexCoord h in HexCoord.Spiral(b.Coord, b.ControlRadius))
        {
            if (h != b.Coord && Cell(h) != null)
                yield return h;
        }
    }

    /// <summary>该建筑射程内、确实能打的敌方单位/建筑所在格。</summary>
    public HashSet<HexCoord> BuildingTargetCells(BuildingInst b)
    {
        var result = new HashSet<HexCoord>();
        if (b == null || b.IsDead || b.Def == null || b.Def.AttackRange <= 0)
            return result;

        foreach (HexCoord h in BuildingRangeCells(b))
        {
            Cell cell = Cell(h);
            if (cell == null)
                continue;

            if (cell.Units.Any(u => u.Alive && u.Owner != b.Owner &&
                                    (u.IsAir ? b.Def.CanHitAir : b.Def.CanHitGround)))
            {
                result.Add(h);
                continue;
            }

            if (cell.Building != null && !cell.Building.IsDead && cell.Building.Owner != b.Owner)
                result.Add(h);
        }

        return result;
    }

    /// <summary>该单位射程覆盖的所有格子（不含自身所在格），用来画「可攻击」标记。</summary>
    public IEnumerable<HexCoord> AttackRangeCells(UnitInst u)
    {
        if (u == null || !u.Alive)
            yield break;

        foreach (HexCoord h in HexCoord.Spiral(u.Coord, AttackRangeOf(u)))
        {
            if (h != u.Coord && Cell(h) != null)
                yield return h;
        }
    }

    public IEnumerable<(UnitInst unit, BuildingInst building)> AttackTargets(UnitInst u)
    {
        if (u == null || !u.Alive || u.Attacked)
            yield break;

        // 深潜状态只能移动，不能攻击
        if (u.DefId == "submarine" && u.Submerged)
            yield break;

        foreach (HexCoord h in HexCoord.Spiral(u.Coord, AttackRangeOf(u)))
        {
            Cell cell = Cell(h);
            if (cell == null)
                continue;

            foreach (UnitInst other in cell.Units)
            {
                if (!other.Alive || other.Owner == u.Owner)
                    continue;
                if (other.IsAir && !u.Def.CanHitAir)
                    continue;
                if (!other.IsAir && !u.Def.CanHitGround)
                    continue;

                // 敌方深潜潜艇不可见、不可被瞄准
                if (other.DefId == "submarine" && other.Submerged)
                    continue;

                yield return (other, null);
            }

            if (cell.Building != null && cell.Building.Owner != u.Owner)
            {
                if (cell.Building.Hp > 0)
                    yield return (null, cell.Building);
            }
        }
    }

    public bool CanAttack(UnitInst u, HexCoord target) =>
        AttackTargets(u).Any(t => (t.unit != null && t.unit.Coord == target) ||
                                  (t.building != null && t.building.Coord == target));

    /// <summary>执行攻击，返回造成伤害的说明文字。</summary>
    /// <summary>维修费：按损失的血量折算（每点血约等于「建造价 / 一级血量」的 60%），最低 10。</summary>
    public static int RepairCost(BuildingInst b)
    {
        if (b == null || b.Def == null)
            return 0;

        int missing = Mathf.Max(0, b.MaxHp - b.Hp);
        if (missing <= 0)
            return 0;

        int baseHp = Mathf.Max(1, b.Def.HpAt(1));
        float perHp = b.Def.UpgradeCostAt(0) / (float)baseHp * 0.6f;
        return Mathf.Max(10, Mathf.CeilToInt(missing * perHp));
    }

    /// <summary>花钱把建筑修满。</summary>
    public bool TryRepair(BuildingInst b, out string error)
    {
        error = "";
        if (b == null || b.IsDead)
        {
            error = "建筑无效";
            return false;
        }

        int cost = RepairCost(b);
        if (cost <= 0)
        {
            error = "建筑没有受损";
            return false;
        }

        if (StateOf(b.Owner).Gold < cost)
        {
            error = "金币不足";
            return false;
        }

        StateOf(b.Owner).Gold -= cost;
        b.Hp = b.MaxHp;
        return true;
    }

    /// <summary>
    /// 单位实际攻击力：血量越低越弱，最低保留满血的 35%。
    /// （满血 100%，残血线性降到 35%）
    /// </summary>
    public static int EffectiveDamage(UnitInst u)
    {
        if (u == null || u.Def == null)
            return 0;

        float ratio = Mathf.Clamp((float)u.Hp / Mathf.Max(1, u.MaxHp), 0f, 1f);
        float scale = Mathf.Lerp(0.35f, 1f, ratio);
        return Mathf.Max(1, Mathf.RoundToInt(u.Def.Damage * scale));
    }

    /// <summary>血量比例（0~1）。</summary>
    public static float HpRatio(UnitInst u) =>
        u == null ? 0f : Mathf.Clamp((float)u.Hp / Mathf.Max(1, u.MaxHp), 0f, 1f);

    public string PerformAttack(UnitInst u, HexCoord target)
    {
        if (u == null || !u.Alive || u.Attacked)
            return "";

        Cell cell = Cell(target);
        if (cell == null)
            return "";

        int dmg = EffectiveDamage(u);
        var log = new List<string>();

        // 自杀式单位（FPV 无人机）：先冲到目标格，再自爆。
        // 注意：如果目标就是自己脚下那一格（站在敌方建筑上打它），也照样自爆，
        // 不能因为「没移动」就跳过自毁。
        bool suicide = u.Def.Suicide;
        if (suicide && u.Coord != target)
        {
            Cell origin = Cell(u.Coord);
            origin?.Units.Remove(u);
            cell.Units.Add(u);
            u.Coord = target;
        }

        foreach (UnitInst t in cell.Units.ToList())
        {
            if (!t.Alive || t.Owner == u.Owner)
                continue;
            if (t.IsAir && !u.Def.CanHitAir)
                continue;
            if (!t.IsAir && !u.Def.CanHitGround)
                continue;
            t.Hp -= dmg;
            log.Add($"{t.Def.NameZh} -{dmg}");
            if (t.Hp <= 0)
            {
                KillUnit(t, cell);
                log.Add($"{t.Def.NameZh} 被消灭");
            }
        }

        if (cell.Building != null && cell.Building.Owner != u.Owner)
        {
            cell.Building.Hp -= dmg;
            log.Add($"{cell.Building.Def.NameZh} -{dmg}");
            if (cell.Building.Hp <= 0)
            {
                var b = cell.Building;
                Demolish(b);
                log.Add($"{b.Def.NameZh} 被摧毁");
            }
        }

        u.Attacked = true;

        // 自爆：打完自己也报销（无人机撞上去就没了）
        if (suicide)
        {
            KillUnit(u, cell);
            u.Hp = 0;
            log.Add($"{u.Def.NameZh} 自爆");
        }

        CheckVictory();
        return string.Join("，", log);
    }

    // ─────────────────────────── 卡牌 ───────────────────────────

    public bool CanDeployCard(string cardId, PlayerId player, HexCoord coord, out string error)
    {
        error = "";
        CardDef card = GameDatabase.Card(cardId);
        if (card == null)
        {
            error = "未知卡牌";
            return false;
        }

        if (card.KindEnum != CardKind.Unit)
        {
            error = "这张卡不需要指定地块";
            return false;
        }

        Cell cell = Cell(coord);
        if (cell == null)
        {
            error = "格子不存在";
            return false;
        }

        UnitDef unitDef = GameDatabase.Unit(card.Unit);
        if (unitDef != null)
        {
            if (unitDef.Navy)
            {
                // 海军只能部署在水面
                if (cell.Terrain != TerrainType.Water)
                {
                    error = "海军只能部署在水面上";
                    return false;
                }
            }
            else if (!unitDef.IsAir && cell.Terrain == TerrainType.Water)
            {
                error = "陆军不能部署在水面上";
                return false;
            }
        }

        bool mine = player == PlayerId.Player ? cell.ControlPlayer : cell.ControlEnemy;
        if (!mine)
        {
            error = "只能部署在自己的控制区内（争夺区双方都可以出兵）";
            return false;
        }

        // 争夺区（双方控制区重叠）允许双方出兵
        return true;
    }

    /// <summary>打出单位卡：扣金币、部署单位、从手牌移除。</summary>
    public bool PlayUnitCard(PlayerState st, string cardId, HexCoord coord, out string error)
    {
        CardDef card = GameDatabase.Card(cardId);
        if (card == null)
        {
            error = "未知卡牌";
            return false;
        }

        if (!CanDeployCard(cardId, st.Id, coord, out error))
            return false;

        int playCost = EffectiveCost(st, cardId);
        if (st.Gold < playCost)
        {
            error = "金币不足";
            return false;
        }

        st.Gold -= playCost;
        RemoveCardFromHand(st, cardId);
        SendToPlayedPile(st, card);
        SpawnUnit(card.Unit, st.Id, coord, card.Cost);
        error = "";
        return true;
    }

    /// <summary>
    /// 只校验「这张卡能不能在这格打出去」，不结算、不播特效。
    /// 出牌流程要先用它挡掉非法操作，避免特效放完才发现打不了。
    /// </summary>
    public bool CanPlayGlobalCard(PlayerState st, string cardId, HexCoord? target, out string error)
    {
        error = "";

        CardDef card = GameDatabase.Card(cardId);
        if (card == null)
        {
            error = "未知卡牌";
            return false;
        }

        if (st.Gold < EffectiveCost(st, cardId))
        {
            error = "金币不足";
            return false;
        }

        if (card.KindEnum == CardKind.Unit)
        {
            error = "这张卡需要指定部署地块";
            return false;
        }

        // 需要「选择一张手牌」的卡：手里至少还要有另一张牌
        if (card.Effect is "fight_corruption" or "tactical_preference" && st.Hand.Count <= 1)
        {
            error = "手里没有可选择的其它卡牌";
            return false;
        }

        if (card.Effect == "conscription" && st.Conscription)
        {
            error = "已经拥有「强制兵役」能力";
            return false;
        }

        if (card.Effect == "attrition" && RetainTargetIndices(st).Count == 0)
        {
            error = "没有可添加「保留」的手牌";
            return false;
        }

        if (card.KindEnum != CardKind.Strike)
            return true;

        if (target == null || Cell(target.Value) == null)
        {
            error = "需要选择一个目标格子";
            return false;
        }

        Cell cell = Cell(target.Value);

        // 维修卡：目标格必须有己方单位
        if (card.Effect == "repair")
        {
            if (!cell.Units.Any(u => u.Alive && u.Owner == st.Id))
            {
                error = "这一格没有可以维修的己方单位";
                return false;
            }

            return true;
        }

        // 直接伤害卡不能砸指挥部
        if (cell.Building is { IsDead: false } hq && hq.IsHq && hq.Owner != st.Id)
        {
            error = "不能直接攻击敌方指挥部，请派部队推进";
            return false;
        }

        if (card.Effect == "airstrike" && InEnemyAaRange(cell.Coord, st.Id))
        {
            error = "该格在敌方防空炮保护范围内，无法使用空袭";
            return false;
        }

        return true;
    }

    /// <summary>维修卡：治疗指定格子里所有己方单位。</summary>
    private string PlayRepairCard(PlayerState st, CardDef card, Cell cell, out string error)
    {
        error = "";

        var targets = cell.Units.Where(u => u.Alive && u.Owner == st.Id).ToList();
        if (targets.Count == 0)
        {
            error = "这一格没有可以维修的己方单位";
            return "";
        }

        int playCost = EffectiveCost(st, card.Id);
        if (st.Gold < playCost)
        {
            error = "金币不足";
            return "";
        }

        var healed = new List<string>();
        foreach (UnitInst u in targets)
        {
            int before = u.Hp;
            u.Hp = Mathf.Min(u.MaxHp, u.Hp + card.Power);
            int delta = u.Hp - before;
            healed.Add(delta > 0 ? $"{u.Def.NameZh} +{delta}" : $"{u.Def.NameZh} 已满血");
        }

        st.Gold -= playCost;
        RemoveCardFromHand(st, card.Id);
        SendToPlayedPile(st, card);

        return $"{card.NameZh}：" + string.Join("，", healed);
    }

    /// <summary>
    /// 打出不需要「部署」的卡：魔法卡（全局效果）与打击卡（指定一格）。
    /// 返回一句给玩家看的战报；失败时 error 非空。
    /// </summary>
    public string PlayGlobalCard(PlayerState st, string cardId, HexCoord? target, out string error)
    {
        error = "";

        if (!CanPlayGlobalCard(st, cardId, target, out error))
            return "";

        CardDef card = GameDatabase.Card(cardId);
        if (card == null)
        {
            error = "未知卡牌";
            return "";
        }

        if (card.KindEnum == CardKind.Strike)
        {
            Cell strikeCell = Cell(target!.Value);

            // 维修卡：治疗该格内的己方单位（不是伤害卡，跳过下面所有敌意判定）
            if (card.Effect == "repair")
                return PlayRepairCard(st, card, strikeCell, out error);
        }

        int playCost = EffectiveCost(st, cardId);
        if (st.Gold < playCost)
        {
            error = "金币不足";
            return "";
        }

        if (card.KindEnum == CardKind.Unit)
        {
            error = "这张卡需要指定部署地块";
            return "";
        }

        if (card.KindEnum == CardKind.Strike)
        {
            if (target == null || Cell(target.Value) == null)
            {
                error = "需要选择一个目标格子";
                return "";
            }

            Cell cell = Cell(target.Value);

            if (card.Effect == "airstrike" && InEnemyAaRange(cell.Coord, st.Id))
            {
                error = "该格在敌方防空炮保护范围内，无法使用空袭";
                return "";
            }

            int power = card.Power;
            var hits = new List<string>();

            foreach (UnitInst u in cell.Units.ToList())
            {
                if (!u.Alive || u.Owner == st.Id)
                    continue;
                u.Hp -= power;
                hits.Add($"{u.Def.NameZh} -{power}");
                if (u.Hp <= 0)
                {
                    KillUnit(u, cell);
                    hits.Add($"{u.Def.NameZh} 被消灭");
                }
            }

            if (cell.Building != null && cell.Building.Owner != st.Id)
            {
                cell.Building.Hp -= power;
                hits.Add($"{cell.Building.Def.NameZh} -{power}");
                if (cell.Building.Hp <= 0)
                {
                    var b = cell.Building;
                    Demolish(b);
                    hits.Add($"{b.Def.NameZh} 被摧毁");
                }
            }

            st.Gold -= playCost;
            RemoveCardFromHand(st, cardId);
            SendToPlayedPile(st, card);
            CheckVictory();
            return hits.Count == 0 ? $"{card.NameZh} 没有命中任何目标" : $"{card.NameZh}：" + string.Join("，", hits);
        }

        // 全局效果
        switch (card.Effect)
        {
            case "repair":
            {
                // 维修卡已经改成「指定格子」的 targeted 卡（见 PlayRepairCard），
                // 这里保留分支只为兼容旧卡表
                error = "这张卡需要指定格子";
                return "";
            }
            case "boost":
            {
                // 强行军是持续能力：每次打出延长 1 回合，能力本身提供地面单位移动 +1
                st.ForcedMarchTurns += Mathf.Max(1, card.Power);
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);
                return $"{card.NameZh}：地面单位移动 +1，持续 {st.ForcedMarchTurns} 回合";
            }
            case "supply":
            {
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);
                DrawCard(st, card.Power);

                // 抽完之后还要弃 1 张：玩家由界面弹选牌，AI 直接挑一张最没用的
                // 联机中即使应用在 Enemy 侧也不能自动弃，必须等客户端选择
                if (st.Id == PlayerId.Enemy && !Session.LanBattle)
                {
                    int pick = PickAutoDiscardIndex(st);
                    if (pick >= 0)
                        DiscardOne(st, pick);

                    return $"{card.NameZh}：抽 {card.Power} 张，弃掉 1 张";
                }

                if (st.Hand.Count > 0)
                    PendingDiscard++;

                return $"{card.NameZh}：抽 {card.Power} 张，选择一张手牌弃掉";
            }
            case "reclaim_exhaust":
            {
                // 战场扫荡：从消耗堆拿回一张牌（先检查有没有可拿的，避免白扣费用）
                if (st.Exhaust.Count == 0)
                {
                    error = "消耗堆是空的";
                    return "";
                }

                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);

                if (Session.LanBattle || st.Id == PlayerId.Player)
                    PendingReclaimExhaust = true;

                return $"{card.NameZh}：选择消耗堆中的一张牌";
            }
            case "gold_percent":
            {
                // 汇率调节：按「当前资金」的比例追加（先扣费用再加，费用为 0 时等价）
                st.Gold -= playCost;
                int gain = Mathf.Max(0, Mathf.FloorToInt(st.Gold * card.Power / 100f));
                st.Gold += gain;

                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);

                return $"{card.NameZh}：获得 {gain}$";
            }
            case "unpredictable_world":
            {
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);

                st.RandomCardPlays++;
                List<string> choices = RollRandomCardChoices(st);
                bool interactive = Session.LanBattle || st.Id == PlayerId.Player;
                if (interactive)
                {
                    PendingRandomChoices.Clear();
                    PendingRandomChoices.AddRange(choices);
                    return $"{card.NameZh}：从 5 张随机卡中选择 2 张";
                }

                for (int i = 0; i < 2 && i < choices.Count; i++)
                    AddCardWithMarkersOrDiscard(st, choices[i]);
                return $"{card.NameZh}：获得 2 张随机卡";
            }
            case "battlefield_survey":
            {
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);

                bool interactive = Session.LanBattle || st.Id == PlayerId.Player;
                if (interactive && st.Deck.Count > 2)
                {
                    PendingDeckPick = true;
                    return $"{card.NameZh}：从抽牌堆选择两张";
                }

                int take = Mathf.Min(2, st.Deck.Count);
                var picked = new List<int>();
                for (int i = 0; i < take; i++)
                    picked.Add(i);
                TakeDeckCards(st, picked);
                return $"{card.NameZh}：取 {take} 张牌";
            }
            case "attrition":
            {
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);

                List<int> targets = RetainTargetIndices(st);
                const int RetainCount = 2;
                bool interactive = Session.LanBattle || st.Id == PlayerId.Player;
                if (interactive && targets.Count > RetainCount)
                {
                    PendingRetainPick = true;
                    return $"{card.NameZh}：选择两张手牌添加「保留」";
                }

                int applied = Mathf.Min(RetainCount, targets.Count);
                for (int i = 0; i < applied; i++)
                    GrantRetainAt(st, targets[i]);
                return applied >= 2
                    ? $"{card.NameZh}：两张手牌获得「保留」"
                    : $"{card.NameZh}：{applied} 张手牌获得「保留」";
            }            case "reinforce":
            {
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);

                List<string> choices = RollReinforceChoices(st);
                bool interactive = Session.LanBattle || st.Id == PlayerId.Player;
                if (interactive)
                {
                    PendingReinforceChoices.Clear();
                    PendingReinforceChoices.AddRange(choices);
                    return $"{card.NameZh}：选择一张军队单位牌";
                }

                AddCardWithMarkers(st, choices[0], zeroCost: true);
                return $"{card.NameZh}：获得 {GameDatabase.Card(choices[0])?.NameZh}";
            }
            case "issue_bonds":
            {
                st.Gold += 500;
                st.PendingBondDebt += 675;
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);
                return $"{card.NameZh}：获得 500$，下回合开始负债 {st.PendingBondDebt}$";
            }
            case "fight_corruption":
            {
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);

                bool interactive = Session.LanBattle || st.Id == PlayerId.Player;
                if (interactive && st.Hand.Count > 0)
                {
                    PendingExhaustPick = true;
                    return $"{card.NameZh}：选择一张手牌消耗，然后抽 2 张";
                }

                if (st.Hand.Count > 0)
                    ExhaustCardFromHand(st, 0);
                DrawCard(st, 2);
                return $"{card.NameZh}：消耗 1 张，抽 2 张";
            }
            case "tactical_preference":
            {
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);

                bool interactive = Session.LanBattle || st.Id == PlayerId.Player;
                if (interactive && st.Hand.Count > 0)
                {
                    PendingDuplicatePick = true;
                    return $"{card.NameZh}：选择一张手牌，复制 2 张";
                }

                if (st.Hand.Count > 0)
                    DuplicateHandCard(st, 0, 2);
                return $"{card.NameZh}：复制 2 张手牌";
            }
            case "housing_subsidy":
            {
                st.PendingHousingUpgrades += 2;
                st.HousingUpgradeDueTurn = st.HousingUpgradeDueTurn <= 0
                    ? TurnNumber + 1
                    : Mathf.Min(st.HousingUpgradeDueTurn, TurnNumber + 1);
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);
                return $"{card.NameZh}：下回合开始随机升级 2 栋建筑";
            }
            case "conscription":
            {
                st.Conscription = true;
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);
                return $"{card.NameZh}：获得「强制兵役」能力";
            }
            case "medical_care":
            {
                st.MedicalStacks++;
                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);
                return $"{card.NameZh}：战时医疗提升至 {st.MedicalStacks * 25}%";
            }            default:                st.Gold -= playCost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);
                return card.NameZh;
        }
    }

    /// <summary>该格是否处在「敌方」防空炮的覆盖范围内（用于封锁空袭卡）。</summary>
    public bool InEnemyAaRange(HexCoord coord, PlayerId caster)
    {
        return Buildings.Any(b =>
            b.DefId == "aaGun" &&
            b.Owner != caster &&
            !b.IsDead &&
            HexCoord.Distance(b.Coord, coord) <= b.ControlRadius);
    }

    // ─────────────────────────── 回合 ───────────────────────────

    public void BeginTurn(PlayerId who)
    {
        CurrentPlayer = who;
        Phase = who == PlayerId.Player ? BattlePhase.PlayerTurn : BattlePhase.EnemyTurn;

        PlayerState st = StateOf(who);
        // 强行军的持续时间在 EndTurn 递减，不在这里清空
        DrawnThisTurn = 0;
        LastDrawReshuffled = false;
        ProducedThisTurn.Clear();
        DrawnIndicesThisTurn.Clear();

        // ── 上回合挂起的能力结算（发行债券 / 房屋补贴 / 战时医疗）──
        if (st.PendingBondDebt > 0)
        {
            st.Gold -= st.PendingBondDebt;
            st.PendingBondDebt = 0;
        }

        if (st.PendingHousingUpgrades > 0)
        {
            int upgrades = st.PendingHousingUpgrades;
            st.PendingHousingUpgrades = 0;
            UpgradeBuildingsForFree(st, upgrades);
        }

        if (st.MedicalStacks > 0)
        {
            int percent = 25 * st.MedicalStacks;
            foreach (UnitInst u in Units.Where(u => u.Alive && u.Owner == who))
            {
                int missing = u.MaxHp - u.Hp;
                if (missing <= 0)
                    continue;

                u.Hp = Mathf.Min(u.MaxHp, u.Hp + Mathf.CeilToInt(missing * percent / 100f));
            }
        }

        // 金币收入
        int income = 0;
        foreach (BuildingInst b in Buildings.Where(b => b.Owner == who && !b.IsDead))
            income += IncomeOf(b);
        st.Gold += income;

        // 每回合基础抽牌数
        DrawCard(st, DrawPerTurn);

        // 重置建筑出兵次数
        foreach (BuildingInst b in Buildings.Where(b => b.Owner == who))
            b.SpawnUsed = false;

        // 重置本单位行动
        foreach (UnitInst u in Units.Where(u => u.Owner == who))
        {
            u.Moved = false;
            u.Attacked = false;
            u.SpawnedThisTurn = false;
            u.DiveUsed = false;
            u.RadarUsed = false;
            u.CarrierSpawnUsed = false;
        }

        foreach (BuildingInst b in Buildings.Where(b => b.Owner == who))
            b.ExtraFired = false;

        // 建筑开火：炮台/防空炮打最近的敌人，指挥部打控制区内目标
        LastBuildingLog = string.Join("；", ResolveBuildingAttacks(who));

        // 建筑发卡：指挥部 / 兵营 / 直升机坪 / 导弹井
        // 依次添加；手牌满就取消后续添加（不会出现「加到一半」的情况）
        foreach (BuildingInst b in Buildings.Where(b => b.Owner == who && !b.IsDead).ToList())
        {
            if (st.Hand.Count >= HandLimit)
                break;

            GrantBuildingCards(b, st);
        }
    }

    public string LastBuildingLog = "";

    /// <summary>本次打出的手牌下标（-1 表示未指定，退回按 id 找第一张）。</summary>
    public int PlayIndex = -1;

    /// <summary>「战场扫荡」打出后等待玩家从消耗堆选一张。</summary>
    public bool PendingReclaimExhaust;

    /// <summary>呼叫增援：等待玩家从三张随机单位卡里选一张。</summary>
    public readonly List<string> PendingReinforceChoices = new();

    /// <summary>整改反贪：等待玩家选择一张手牌消耗。</summary>
    public bool PendingExhaustPick;

    /// <summary>战术偏好：等待玩家选择一张手牌复制。</summary>
    public bool PendingDuplicatePick;

    /// <summary>延时决策：等待玩家选择一张手牌添加保留。</summary>
    public bool PendingRetainPick;

    /// <summary>世界如此变幻莫测：5 张随机候选，等待选择 2 张。</summary>
    public readonly List<string> PendingRandomChoices = new();

    /// <summary>战场勘测：等待从抽牌堆选择两张。</summary>
    public bool PendingDeckPick;

    /// <summary>刚从手牌移除的那张是不是「虚无」实例。</summary>
    public bool LastRemovedEthereal { get; private set; }

    /// <summary>刚从手牌移除的那张是不是「保留」实例。</summary>
    public bool LastRemovedRetained { get; private set; }

    /// <summary>刚从手牌移除的那张是不是「临时 0 费」实例。</summary>
    public bool LastRemovedZeroCost { get; private set; }

    /// <summary>刚从手牌移除的那张是不是「发射井产出、打出即消耗」实例。</summary>
    public bool LastRemovedProducedExhaust { get; private set; }

    /// <summary>最近打出的卡是否虚无（表现层给飞行/烧卡加关键字）。</summary>
    public bool LastPlayedEthereal { get; private set; }

    /// <summary>本次回合开始一共进了几张牌（表现层用来播抽牌动画）。</summary>
    public int DrawnThisTurn;

    /// <summary>本次抽牌有没有发生「弃牌堆洗回牌库」。</summary>
    public bool LastDrawReshuffled;

    /// <summary>洗回牌库的累计次数（两端一致，用于联机同步播放洗牌动画）。</summary>
    public int ReshuffleSerial;

    /// <summary>本回合防御建筑的开火记录（给表现层放特效用）。</summary>
    public readonly List<(HexCoord From, HexCoord To, string Kind)> Volleys = new();

    /// <summary>
    /// 回合开始时建筑自动开火（草案：炮台/防空炮每回合自动攻击控制区内最近的敌人，
    /// 指挥部每回合攻击控制区内一格）。返回战报文本。
    /// </summary>
    public List<string> ResolveBuildingAttacks(PlayerId who)
    {
        var log = new List<string>();
        Volleys.Clear();

        // 只有炮台/防空炮这类防御建筑是「每回合自动开火」；
        // 指挥部的攻击在草案里是玩家主动选择的操作，这里不自动触发。
        foreach (BuildingInst b in Buildings.Where(x => x.Owner == who && !x.IsDead &&
                                                        x.Def.AttackRange > 0 && !x.IsHq).ToList())
        {
            if (b.IsDead)
                continue;

            int radius = b.ControlRadius;
            int dmg = b.Def.DamageAt(b.Level);
            if (dmg <= 0)
                continue;

            bool fired = false;

            // 先打单位
            UnitInst target = Units
                .Where(u => u.Alive && u.Owner != who && HexCoord.Distance(u.Coord, b.Coord) <= radius)
                .Where(u => u.IsAir ? b.Def.CanHitAir : b.Def.CanHitGround)
                .OrderBy(u => u.IsAir ? 1 : 0)
                .ThenBy(u => HexCoord.Distance(u.Coord, b.Coord))
                .FirstOrDefault();

            if (target != null)
            {
                target.Hp -= dmg;
                Volleys.Add((b.Coord, target.Coord,
                    b.Def.CanHitAir && !b.Def.CanHitGround ? "aa" : "shell"));
                log.Add($"{b.Def.NameZh} 击中 {target.Def.NameZh} -{dmg}");
                if (target.Hp <= 0)
                {
                    KillUnit(target);
                    log.Add($"{target.Def.NameZh} 被消灭");
                }

                fired = true;
            }

            // 打不到单位时，指挥部可以拆敌方建筑
            if (!fired && b.IsHq)
            {
                BuildingInst enemyBuilding = Buildings
                    .Where(x => x.Owner != who && !x.IsDead && x != b &&
                                HexCoord.Distance(x.Coord, b.Coord) <= radius)
                    .OrderBy(x => HexCoord.Distance(x.Coord, b.Coord))
                    .FirstOrDefault();

                if (enemyBuilding != null)
                {
                    enemyBuilding.Hp -= dmg;
                    Volleys.Add((b.Coord, enemyBuilding.Coord, "shell"));
                    log.Add($"{b.Def.NameZh} 轰击 {enemyBuilding.Def.NameZh} -{dmg}");
                    if (enemyBuilding.Hp <= 0)
                    {
                        Demolish(enemyBuilding);
                        log.Add($"{enemyBuilding.Def.NameZh} 被摧毁");
                    }
                }
            }
        }

        CheckVictory();
        return log;
    }

    /// <summary>
    /// 付费额外开炮：花 ExtraFireCost 金币，让这座防御建筑在当回合再打一次。
    /// 目标选择与自动开火一致（射程内最近的敌方单位，够不到时改打敌方建筑）。
    /// </summary>
    public bool TryExtraFire(BuildingInst b, out string message)
    {
        message = "";

        if (b == null || b.IsDead)
        {
            message = "建筑不存在";
            return false;
        }

        if (b.Def == null || b.Def.ExtraFireCost <= 0 || b.Def.AttackRange <= 0)
        {
            message = "这座建筑不能额外开炮";
            return false;
        }

        if (b.ExtraFired)
        {
            message = "本回合已经额外开过炮了";
            return false;
        }

        PlayerState st = StateOf(b.Owner);
        int cost = b.Def.ExtraFireCost;
        if (st.Gold < cost)
        {
            message = $"金币不足（需要 {cost}$）";
            return false;
        }

        int radius = b.ControlRadius;
        int dmg = b.Def.DamageAt(b.Level);
        if (dmg <= 0)
        {
            message = "这座建筑没有攻击力";
            return false;
        }

        UnitInst target = Units
            .Where(u => u.Alive && u.Owner != b.Owner &&
                        HexCoord.Distance(u.Coord, b.Coord) <= radius)
            .Where(u => u.IsAir ? b.Def.CanHitAir : b.Def.CanHitGround)
            .OrderBy(u => u.IsAir ? 1 : 0)
            .ThenBy(u => HexCoord.Distance(u.Coord, b.Coord))
            .FirstOrDefault();

        if (target != null)
        {
            target.Hp -= dmg;
            message = $"{b.Def.NameZh} 额外开炮命中 {target.Def.NameZh} -{dmg}";
            if (target.Hp <= 0)
            {
                KillUnit(target);
                message += $"，{target.Def.NameZh} 被消灭";
            }
        }
        else
        {
            // 建筑算地面目标：只打空中的（防空炮）不能拿它轰建筑
            BuildingInst enemyBuilding = b.Def.CanHitGround
                ? Buildings
                    .Where(x => x.Owner != b.Owner && !x.IsDead && x != b &&
                                HexCoord.Distance(x.Coord, b.Coord) <= radius)
                    .OrderBy(x => HexCoord.Distance(x.Coord, b.Coord))
                    .FirstOrDefault()
                : null;

            if (enemyBuilding == null)
            {
                message = "射程内没有目标";
                return false;
            }

            enemyBuilding.Hp -= dmg;
            message = $"{b.Def.NameZh} 额外开炮轰击 {enemyBuilding.Def.NameZh} -{dmg}";
            if (enemyBuilding.Hp <= 0)
            {
                Demolish(enemyBuilding);
                message += $"，{enemyBuilding.Def.NameZh} 被摧毁";
            }
        }

        st.Gold -= cost;
        b.ExtraFired = true;
        CheckVictory();
        return true;
    }

    /// <summary>这座建筑会不会产卡（指挥部抽牌 / 兵营、机坪发兵种卡 / 导弹井发导弹卡）。</summary>
    public static bool IsCardProducer(string defId) =>
        defId is "hq" or "barracks" or "helipad" or "missileSilo" or "navyBase";

    private void GrantBuildingCards(BuildingInst b, PlayerState st)
    {
        if (!b.ProduceEnabled)
            return;                    // 玩家手动关掉了产卡

        switch (b.DefId)
        {
            case "hq":
                if (st.Hand.Count < HandLimit)
                    DrawCard(st, b.Level >= 2 ? 2 : 1);
                break;
            case "barracks":
            {
                string card = b.Level >= 3 ? "unit_tank" : "unit_infantry";
                if (AddToHand(st, card))
                {
                    MarkLastOccurrence(st, card, ethereal: true);   // 建筑发的兵种卡带「虚无」
                    ProducedThisTurn.Add((card, b.Coord, st.Hand.Count - 1));
                }
                break;
            }
            case "helipad":
            {
                string card = b.Level >= 3 ? "unit_fighter" : "unit_helicopter";
                if (AddToHand(st, card))
                {
                    MarkLastOccurrence(st, card, ethereal: true);
                    ProducedThisTurn.Add((card, b.Coord, st.Hand.Count - 1));
                }
                break;
            }
            case "navyBase":
                // 每 3 回合给一张潜水艇（虚无 + 消耗）
                if (TurnNumber % 3 == 0 && AddToHand(st, "unit_submarine"))
                {
                    st.Produced.Add((b.Id, "unit_submarine"));
                    MarkLastOccurrence(st, "unit_submarine", ethereal: true, producedExhaust: true);
                    ProducedThisTurn.Add(("unit_submarine", b.Coord, st.Hand.Count - 1));
                }
                break;
            case "missileSilo":
                int every = Mathf.Max(1, 4 - b.Level);
                if (TurnNumber % every == 0 && AddToHand(st, "magic_missileStrike"))
                {
                    st.Produced.Add((b.Id, "magic_missileStrike"));   // 记住这张卡是哪座井产出的
                    MarkLastOccurrence(st, "magic_missileStrike", producedExhaust: true);
                    ProducedThisTurn.Add(("magic_missileStrike", b.Coord, st.Hand.Count - 1));
                }
                break;
        }
    }

    private static bool AddToHand(PlayerState st, string cardId)
    {
        if (st.Hand.Count >= HandLimit)
            return false;
        st.Hand.Add(cardId);
        return true;
    }

    /// <summary>从消耗堆取回第 index 张到手牌；返回手牌下标（失败 -1）。</summary>
    public int ReclaimFromExhaust(PlayerState st, int index)
    {
        if (st == null || index < 0 || index >= st.Exhaust.Count)
            return -1;

        string id = st.Exhaust[index];
        int occurrence = OccurrenceAt(st.Exhaust, index);
        bool eth = HasOccurrenceAt(st.EtherealExhausted, id, occurrence);
        bool pex = HasOccurrenceAt(st.ProducedExhaustExhausted, id, occurrence);
        bool ret = HasOccurrenceAt(st.RetainedExhausted, id, occurrence);
        bool zero = HasOccurrenceAt(st.ZeroCostExhausted, id, occurrence);

        st.Exhaust.RemoveAt(index);
        TakeOccurrence(st.EtherealExhausted, id, occurrence);
        TakeOccurrence(st.ProducedExhaustExhausted, id, occurrence);
        TakeOccurrence(st.RetainedExhausted, id, occurrence);
        TakeOccurrence(st.ZeroCostExhausted, id, occurrence);

        st.Hand.Add(id);
        MarkZoneLast(st.Hand, id,
            st.Ethereal, eth,
            st.ProducedExhaust, pex,
            st.Retained, ret,
            st.ZeroCost, zero);

        PendingReclaimExhaust = false;
        return st.Hand.Count - 1;
    }
    /// <summary>从消耗堆回收多张牌（序号按当前消耗堆顺序，内部从后往前清）。</summary>
    public int ReclaimManyFromExhaust(PlayerState st, System.Collections.Generic.IReadOnlyList<int> indices, int maxCards = 1)
    {
        if (st == null || indices == null)
            return 0;

        var sorted = new List<int>();
        foreach (int index in indices)
        {
            if (index >= 0 && index < st.Exhaust.Count && !sorted.Contains(index))
                sorted.Add(index);
        }
        sorted.Sort();
        sorted.Reverse();

        int got = 0;
        foreach (int index in sorted)
        {
            if (got >= maxCards || st.Hand.Count >= HandLimit)
                break;
            if (ReclaimFromExhaust(st, index) >= 0)
                got++;
        }

        return got;
    }
    /// <summary>从手牌移除一张卡，同时清掉它的「来源建筑 / 虚无」记录。</summary>
    private void RemoveCardFromHand(PlayerState st, string cardId)
    {
        // 手牌里同名牌可能同时存在「虚无 / 产出」两种实例，
        // 必须按「第几次出现」精确移除，否则打普通那张会误删虚无那张。
        int at = PlayIndex;
        if (at < 0 || at >= st.Hand.Count || st.Hand[at] != cardId)
            at = st.Hand.IndexOf(cardId);

        if (at < 0)
            return;

        int occurrence = 0;
        for (int i = 0; i < at; i++)
        {
            if (st.Hand[i] == cardId)
                occurrence++;
        }

        st.Hand.RemoveAt(at);

        // 先判断这一张带哪些实例关键字，后面决定要不要保留到弃牌堆/消耗堆
        LastRemovedEthereal = TakeEtherealOccurrence(st, cardId, occurrence);
        LastRemovedProducedExhaust = TakeOccurrence(st.ProducedExhaust, cardId, occurrence);
        LastRemovedZeroCost = TakeOccurrence(st.ZeroCost, cardId, occurrence);
        LastRemovedRetained = TakeOccurrence(st.Retained, cardId, occurrence);

        int produced = 0;
        for (int i = 0; i < st.Produced.Count; i++)
        {
            if (st.Produced[i].Card != cardId)
                continue;

            if (produced++ == occurrence)
            {
                st.Produced.RemoveAt(i);
                break;
            }
        }

    }

    /// <summary>移除列表里「第 occurrence 次出现的 cardId」。</summary>
    private static void RemoveOccurrence(List<string> list, string cardId, int occurrence)
    {
        int seen = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] != cardId)
                continue;

            if (seen++ == occurrence)
            {
                list.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>取出列表里「第 occurrence 次出现的 cardId」，成功返回 true。</summary>
    private static bool TakeOccurrence(List<string> list, string cardId, int occurrence)
    {
        int seen = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (!IsMarkerSlot(list[i], cardId))
                continue;

            if (seen++ == occurrence)
            {
                bool marked = list[i] == cardId;
                list.RemoveAt(i);
                return marked;
            }
        }

        return false;
    }

    /// <summary>取出「第 occurrence 次出现的虚无标记」。</summary>
    private static bool TakeEtherealOccurrence(PlayerState st, string cardId, int occurrence) =>
        TakeOccurrence(st.Ethereal, cardId, occurrence);

    /// <summary>手牌第 index 张是同名卡里的第几次出现。</summary>
    private static int OccurrenceAt(List<string> hand, int index)
    {
        int occurrence = 0;
        for (int i = 0; i < index && i < hand.Count; i++)
        {
            if (hand[i] == hand[index])
                occurrence++;
        }

        return occurrence;
    }
    /// <summary>打完的卡去哪：带「消耗」关键字进消耗堆，否则进弃牌堆（可洗回）。</summary>
    private void SendToPlayedPile(PlayerState st, CardDef card)
    {
        if (card == null)
            return;

        bool exhaust = card.Exhaust || LastRemovedProducedExhaust;
        LastPlayedExhaust = exhaust;
        LastPlayedEthereal = LastRemovedEthereal;

        if (exhaust)
        {
            st.Exhaust.Add(card.Id);
            MarkZoneLast(st.Exhaust, card.Id,
                st.EtherealExhausted, LastRemovedEthereal,
                st.ProducedExhaustExhausted, LastRemovedProducedExhaust,
                st.RetainedExhausted, LastRemovedRetained,
                st.ZeroCostExhausted, LastRemovedZeroCost);
        }
        else
        {
            st.Discard.Add(card.Id);
            MarkZoneLast(st.Discard, card.Id,
                st.EtherealDiscarded, LastRemovedEthereal,
                st.ProducedExhaustDiscarded, LastRemovedProducedExhaust,
                st.RetainedDiscarded, LastRemovedRetained,
                st.ZeroCostDiscarded, LastRemovedZeroCost);
        }
    }

    /// <summary>最近一次打出的卡是否进消耗堆（表现层据此播烧卡/飞牌动画）。</summary>
    public bool LastPlayedExhaust { get; private set; }

    /// <summary>若这张卡是发射井产出的，消耗一个标记并返回 true。</summary>
    private static bool ConsumeProducedExhaust(PlayerState st, string cardId)
    {
        int idx = st.ProducedExhaust.IndexOf(cardId);
        if (idx < 0)
            return false;

        st.ProducedExhaust.RemoveAt(idx);
        return true;
    }

    public void DrawCard(PlayerState st, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (st.Hand.Count >= HandLimit)
                return;
            if (st.Deck.Count == 0)
            {
                if (st.Discard.Count == 0)
                    return;
                st.Deck.AddRange(st.Discard);
                st.Discard.Clear();

                // 弃牌堆里的虚无标记跟着洗进牌库，不能丢
                st.EtherealDeck.AddRange(st.EtherealDiscarded);
                st.EtherealDiscarded.Clear();

                st.ProducedExhaustDeck.AddRange(st.ProducedExhaustDiscarded);
                st.ProducedExhaustDiscarded.Clear();

                st.RetainedDeck.AddRange(st.RetainedDiscarded);
                st.RetainedDiscarded.Clear();

                st.ZeroCostDeck.AddRange(st.ZeroCostDiscarded);
                st.ZeroCostDiscarded.Clear();

                ShuffleDeckKeepingMarkers(st);
                LastDrawReshuffled = true;
                ReshuffleSerial++;

                if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--anim-log") >= 0)
                    GD.Print($"[Reshuffle] side={st.Id} serial={ReshuffleSerial}");
            }

            string card = st.Deck[0];
            bool eth = HasOccurrenceAt(st.EtherealDeck, card, 0);
            bool pex = HasOccurrenceAt(st.ProducedExhaustDeck, card, 0);
            bool ret = HasOccurrenceAt(st.RetainedDeck, card, 0);
            bool zero = HasOccurrenceAt(st.ZeroCostDeck, card, 0);

            st.Deck.RemoveAt(0);
            TakeOccurrence(st.EtherealDeck, card, 0);
            TakeOccurrence(st.ProducedExhaustDeck, card, 0);
            TakeOccurrence(st.RetainedDeck, card, 0);
            TakeOccurrence(st.ZeroCostDeck, card, 0);

            st.Hand.Add(card);
            MarkZoneLast(st.Hand, card,
                st.Ethereal, eth,
                st.ProducedExhaust, pex,
                st.Retained, ret,
                st.ZeroCost, zero);

            DrawnThisTurn++;
            DrawnIndicesThisTurn.Add(st.Hand.Count - 1);
        }
    }

    /// <summary>手牌第 index 张是不是「发射井产出、打出即消耗」的那张。</summary>
    public bool IsProducedExhaustAt(int handIndex)
    {
        PlayerState st = Player;
        if (handIndex < 0 || handIndex >= st.Hand.Count)
            return false;

        string id = st.Hand[handIndex];
        return HasOccurrenceAt(st.ProducedExhaust, id, OccurrenceAt(st.Hand, handIndex));
    }
    /// <summary>手牌第 index 张是不是「虚无」实例（同种卡按出现顺序对应）。</summary>
    public bool IsEtherealAt(int handIndex)
    {
        PlayerState st = Player;
        if (handIndex < 0 || handIndex >= st.Hand.Count)
            return false;

        string id = st.Hand[handIndex];
        return HasOccurrenceAt(st.Ethereal, id, OccurrenceAt(st.Hand, handIndex));
    }

    /// <summary>手牌换位（扇形手牌里左右拖动）。</summary>
    public void MoveCardInHand(PlayerState st, int from, int to)
    {
        if (st == null)
            return;
        if (from < 0 || from >= st.Hand.Count || to < 0 || to >= st.Hand.Count || from == to)
            return;

        string card = st.Hand[from];
        int oldOccurrence = OccurrenceAt(st.Hand, from);
        int total = st.Hand.Count(id => id == card);

        // 记录移动前每个同名实例的关键字
        var rows = new List<(bool Eth, bool Pex, bool Ret, bool Zero)>();
        for (int occurrence = 0; occurrence < total; occurrence++)
        {
            rows.Add((
                HasOccurrenceAt(st.Ethereal, card, occurrence),
                HasOccurrenceAt(st.ProducedExhaust, card, occurrence),
                HasOccurrenceAt(st.Retained, card, occurrence),
                HasOccurrenceAt(st.ZeroCost, card, occurrence)));
        }

        st.Hand.RemoveAt(from);
        st.Hand.Insert(to, card);

        if (total > 0)
        {
            int newOccurrence = OccurrenceAt(st.Hand, to);
            var moved = rows[oldOccurrence];
            rows.RemoveAt(oldOccurrence);
            rows.Insert(newOccurrence, moved);
            RebuildMarkerSlots(st.Ethereal, card, rows.Select(r => r.Eth).ToArray());
            RebuildMarkerSlots(st.ProducedExhaust, card, rows.Select(r => r.Pex).ToArray());
            RebuildMarkerSlots(st.Retained, card, rows.Select(r => r.Ret).ToArray());
            RebuildMarkerSlots(st.ZeroCost, card, rows.Select(r => r.Zero).ToArray());
        }
    }

    /// <summary>按新的同名实例顺序重写某个关键字的槽位。</summary>
    private static void RebuildMarkerSlots(List<string> markers, string cardId, bool[] flags)
    {
        bool hadSlots = markers.Any(m => IsMarkerSlot(m, cardId));
        if (!hadSlots && !flags.Any(f => f))
            return;

        markers.RemoveAll(m => IsMarkerSlot(m, cardId));
        foreach (bool flag in flags)
            markers.Add(flag ? cardId : MarkerPlaceholder(cardId));
    }

    /// <summary>洗牌时把每张牌的关键字一起带走，避免洗牌后标记错位。</summary>
    private void ShuffleDeckKeepingMarkers(PlayerState st)
    {
        int n = st.Deck.Count;
        var entries = new List<(string Card, bool Eth, bool Pex, bool Ret, bool Zero)>(n);
        for (int i = 0; i < n; i++)
        {
            string id = st.Deck[i];
            int occurrence = OccurrenceAt(st.Deck, i);
            entries.Add((id,
                HasOccurrenceAt(st.EtherealDeck, id, occurrence),
                HasOccurrenceAt(st.ProducedExhaustDeck, id, occurrence),
                HasOccurrenceAt(st.RetainedDeck, id, occurrence),
                HasOccurrenceAt(st.ZeroCostDeck, id, occurrence)));
        }

        Random rng = RngOf(st.Id);
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }

        st.Deck.Clear();
        st.EtherealDeck.Clear();
        st.ProducedExhaustDeck.Clear();
        st.RetainedDeck.Clear();
        st.ZeroCostDeck.Clear();

        var seen = new Dictionary<string, int>();
        foreach (var e in entries)
        {
            st.Deck.Add(e.Card);
            seen.TryGetValue(e.Card, out int occurrence);
            if (e.Eth) GrantOccurrence(st.EtherealDeck, e.Card, occurrence);
            if (e.Pex) GrantOccurrence(st.ProducedExhaustDeck, e.Card, occurrence);
            if (e.Ret) GrantOccurrence(st.RetainedDeck, e.Card, occurrence);
            if (e.Zero) GrantOccurrence(st.ZeroCostDeck, e.Card, occurrence);
            seen[e.Card] = occurrence + 1;
        }
    }

    /// <summary>按阵营的随机数洗牌：联机时同一阵营在两个客户端用同一颗种子。</summary>
    private void Shuffle(List<string> list, PlayerId side)
    {
        Random rng = RngOf(side);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public void EndTurn()
    {
        if (Phase == BattlePhase.GameOver)
            return;

        // 强行军等持续能力：结束自己的回合时消耗 1 回合持续时间
        PlayerState ending = StateOf(CurrentPlayer);
        if (ending.ForcedMarchTurns > 0)
            ending.ForcedMarchTurns--;

        // 回合结束：把当前玩家手里「没有保留关键字」的卡弃掉
        DiscardHand(CurrentPlayer);

        PlayerId next = OpponentOf(CurrentPlayer);
        if (FirstHalf)
        {
            FirstHalf = false;
            BeginTurn(next);
        }
        else
        {
            FirstHalf = true;
            TurnNumber++;
            BeginTurn(next);
        }
    }

    /// <summary>本回合被弃掉的手牌（表现层用来播「飞向弃牌堆」的动画）。</summary>
    public readonly List<string> DiscardedThisTurn = new();

    /// <summary>
    /// 「物资运输」这类卡抽完牌后还欠几次弃牌（由表现层弹选牌界面）。
    /// 每次弃完减 1，减到 0 恢复自由操作。
    /// </summary>
    public int PendingDiscard;

    /// <summary>弃掉手牌里的第 index 张（含「虚无」处理）。</summary>
    public bool DiscardOne(PlayerState st, int handIndex)
    {
        if (st == null || handIndex < 0 || handIndex >= st.Hand.Count)
            return false;

        string id = st.Hand[handIndex];
        int occurrence = OccurrenceAt(st.Hand, handIndex);
        st.Hand.RemoveAt(handIndex);

        bool zeroCost = TakeOccurrence(st.ZeroCost, id, occurrence);
        bool retained = TakeOccurrence(st.Retained, id, occurrence);
        bool hadProduced = TakeOccurrence(st.ProducedExhaust, id, occurrence);
        bool ethereal = TakeEtherealOccurrence(st, id, occurrence);

        // 弃牌操作一律进弃牌堆；虚无/消耗关键字随牌带过去
        st.Discard.Add(id);
        DiscardedThisTurn.Add(id);
        MarkZoneLast(st.Discard, id,
            st.EtherealDiscarded, ethereal,
            st.ProducedExhaustDiscarded, hadProduced,
            st.RetainedDiscarded, retained,
            st.ZeroCostDiscarded, zeroCost);   // 关键字跟着牌走

        int produced = st.Produced.FindIndex(p => p.Card == id);
        if (produced >= 0)
            st.Produced.RemoveAt(produced);

        return true;
    }

    /// <summary>AI 用：挑一张「最没用」的手牌（保留卡与高费卡优先留下）。</summary>
    private static int PickAutoDiscardIndex(PlayerState st)
    {
        int best = -1;
        int bestScore = int.MaxValue;

        for (int i = 0; i < st.Hand.Count; i++)
        {
            CardDef card = GameDatabase.Card(st.Hand[i]);
            if (card == null)
                continue;

            if (card.Retain)
                continue;

            // 费用越低越不值钱；单位卡再优先一点（部队还能上场）
            int score = card.Cost;
            if (card.KindEnum == CardKind.Unit)
                score -= 40;

            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best >= 0 ? best : (st.Hand.Count > 0 ? 0 : -1);
    }

    /// <summary>本回合从牌库抽到手里的手牌下标（表现层只给这些卡播「从牌库飞入」）。</summary>
    public readonly List<int> DrawnIndicesThisTurn = new();

    /// <summary>本回合建筑产出的卡：卡牌 id、产出建筑所在格、进手牌时的下标（表现层播飞牌）。</summary>
    public readonly List<(string Card, HexCoord From, int HandIndex)> ProducedThisTurn = new();

    /// <summary>本回合因「虚无」被耗掉的手牌（表现层用来播「烧卡」动画）。</summary>
    public readonly List<string> ExhaustedThisTurn = new();

    /// <summary>回合结束弃手牌：保留关键字留在手上；虚无的卡进消耗堆。</summary>
    public void DiscardHand(PlayerId who)
    {
        DiscardedThisTurn.Clear();
        ExhaustedThisTurn.Clear();

        PlayerState st = StateOf(who);
        for (int i = st.Hand.Count - 1; i >= 0; i--)
        {
            string id = st.Hand[i];
            CardDef card = GameDatabase.Card(id);
            int occurrence = OccurrenceAt(st.Hand, i);
            bool isEthereal = HasOccurrenceAt(st.Ethereal, id, occurrence);

            // 虚无优先：即使同时带保留，回合结束也必须消耗
            if (!isEthereal && card != null &&
                (card.Retain || HasOccurrenceAt(st.Retained, id, occurrence)))
                continue;                     // 仅保留：跨回合留在手里

            st.Hand.RemoveAt(i);
            // 所有实例槽都要清掉，避免同名保留卡的槽位错位
            bool zeroCost = TakeOccurrence(st.ZeroCost, id, occurrence);
            bool retained = TakeOccurrence(st.Retained, id, occurrence);
            bool hadProduced = TakeOccurrence(st.ProducedExhaust, id, occurrence);
            bool ethereal = TakeEtherealOccurrence(st, id, occurrence);

            // 虚无：进消耗堆，永不洗回
            if (ethereal)
            {
                st.Exhaust.Add(id);
                MarkZoneLast(st.Exhaust, id,
                    st.EtherealExhausted, true,
                    st.ProducedExhaustExhausted, hadProduced,
                    st.RetainedExhausted, retained,
                    st.ZeroCostExhausted, zeroCost);
                ExhaustedThisTurn.Add(id);
                continue;
            }

            st.Discard.Add(id);
            DiscardedThisTurn.Add(id);
            MarkZoneLast(st.Discard, id,
                st.EtherealDiscarded, false,
                st.ProducedExhaustDiscarded, hadProduced,
                st.RetainedDiscarded, retained,
                st.ZeroCostDiscarded, zeroCost);

            // 导弹卡进弃牌堆后就不再跟发射井绑定了
            int pd = st.Produced.FindIndex(p => p.Card == id);
            if (pd >= 0)
                st.Produced.RemoveAt(pd);
        }

        DiscardedThisTurn.Reverse();
    }

    public void CheckVictory()
    {
        bool playerHqAlive = Buildings.Any(b => b.Owner == PlayerId.Player && b.IsHq && !b.IsDead);
        bool enemyHqAlive = Buildings.Any(b => b.Owner == PlayerId.Enemy && b.IsHq && !b.IsDead);
        PlayerHqDestroyed = !playerHqAlive;
        EnemyHqDestroyed = !enemyHqAlive;

        if (playerHqAlive && enemyHqAlive)
            return;

        Winner = (playerHqAlive, enemyHqAlive) switch
        {
            (true, false) => PlayerId.Player,
            (false, true) => PlayerId.Enemy,
            _ => PlayerId.None,
        };
        Phase = BattlePhase.GameOver;
    }
}
