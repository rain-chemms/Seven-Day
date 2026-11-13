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

    public int MaxHp => Def.HpAt(Level);
    public int ControlRadius => Def.ControlAt(Level);
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

    /// <summary>消耗堆：打出「消耗」关键字的卡进这里，永远不会洗回牌库。</summary>
    public readonly List<string> Exhaust = new();

    /// <summary>
    /// 手牌里「由哪座建筑产出」的记录（目前只有导弹袭击卡需要）：
    /// 拆除对应发射井时只烧掉它产出的那几张。
    /// </summary>
    public readonly List<(int SiloId, string Card)> Produced = new();

    /// <summary>
    /// 带「虚无」关键字的手牌实例（建筑发给你的兵种卡）：
    /// 己方回合结束时若还在手上，直接进消耗堆而不是弃牌堆。
    /// </summary>
    public readonly List<string> Ethereal = new();

    /// <summary>消耗堆里那些「虚无」卡（查看器里照样显示关键字）。</summary>
    public readonly List<string> EtherealExhausted = new();

    public bool BonusMove;

    public bool IsPlayer => Id == PlayerId.Player;
}

/// <summary>一局战斗的全部状态。纯数据，不依赖节点，方便后面接 AI 与存档。</summary>
public sealed class BattleState
{
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
    public PlayerId Winner = PlayerId.None;
    public bool EnemyHqDestroyed;
    public bool PlayerHqDestroyed;

    private int _nextId = 1;

    public static BattleState Create(MapDef map, AIDifficulty difficulty)
    {
        GameDatabase.EnsureLoaded();
        var s = new BattleState { Map = map, Difficulty = difficulty };

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
        s.Player.Deck.AddRange(map.PlayerDeck);
        s.Enemy.Deck.AddRange(map.EnemyDeck);

        HexCoord playerHq = new(map.PlayerHq[0], map.PlayerHq[1]);
        HexCoord enemyHq = new(map.EnemyHq[0], map.EnemyHq[1]);
        s.SpawnBuilding("hq", PlayerId.Player, playerHq, free: true);
        s.SpawnBuilding("hq", PlayerId.Enemy, enemyHq, free: true);

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
        };
        cell.Building = b;
        Buildings.Add(b);
        RecalculateZones();
        return b;
    }

    public BuildingInst BuildingAt(HexCoord c) => Cell(c)?.Building;

    /// <summary>该格是否可以被 player 建造：非水面、无建筑、在己方控制区、不在敌方控制区。</summary>
    public bool CanBuildAt(HexCoord coord, PlayerId player)
    {
        Cell cell = Cell(coord);
        if (cell == null || cell.Building != null)
            return false;

        // 水面不能建造
        if (cell.Terrain == TerrainType.Water)
            return false;

        // 敌方单位站着的格子不能造（否则会出现「陆军停在敌方建筑上」）
        PlayerId foe = player == PlayerId.Player ? PlayerId.Enemy : PlayerId.Player;
        if (cell.Units.Any(u => u.Alive && u.Owner == foe))
            return false;

        if (!cell.ControlPlayer && !cell.ControlEnemy)
            return false;
        bool mine = player == PlayerId.Player ? cell.ControlPlayer : cell.ControlEnemy;
        bool theirs = player == PlayerId.Player ? cell.ControlEnemy : cell.ControlPlayer;
        return mine && !theirs;
    }

    public bool TryBuild(PlayerId player, string defId, HexCoord coord, out string error)
    {
        error = "";
        BuildingDef def = GameDatabase.Building(defId);
        if (def == null)
        {
            error = "未知建筑";
            return false;
        }

        if (!CanBuildAt(coord, player))
        {
            error = Cell(coord)?.Terrain == TerrainType.Water
                ? "水面不能建造"
                : "只能建在自己的控制区内，且不能与敌方控制区重叠";
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

    public void Demolish(BuildingInst b)
    {
        if (b == null)
            return;
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
                if (st.Hand.Remove("magic_missile"))
                    RemovedCards.Add((b.Owner, "magic_missile"));
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

    // ─────────────────────────── 单位 ───────────────────────────

    public UnitInst SpawnUnit(string defId, PlayerId owner, HexCoord coord)
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
        };
        cell.Units.Add(u);
        Units.Add(u);
        return u;
    }

    public IEnumerable<UnitInst> UnitsAt(HexCoord c) =>
        Cell(c)?.Units.Where(u => u.Alive) ?? Enumerable.Empty<UnitInst>();

    /// <summary>单位本回合可移动到的格子（BFS，地形/建筑阻挡，单位可叠加）。</summary>
    public HashSet<HexCoord> MoveRange(UnitInst u)
    {
        var result = new HashSet<HexCoord>();
        if (u == null || !u.Alive || u.Moved)
            return result;

        int steps = u.Def.Move + (StateOf(u.Owner).BonusMove && !u.IsAir ? 1 : 0);
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
                if (!u.IsAir && !cell.CanGroundEnter(u.Owner))
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

        foreach (HexCoord h in HexCoord.Spiral(u.Coord, u.Def.Range))
        {
            if (h != u.Coord && Cell(h) != null)
                yield return h;
        }
    }

    public IEnumerable<(UnitInst unit, BuildingInst building)> AttackTargets(UnitInst u)
    {
        if (u == null || !u.Alive || u.Attacked)
            yield break;

        foreach (HexCoord h in HexCoord.Spiral(u.Coord, u.Def.Range))
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
                cell.Units.Remove(t);
                Units.Remove(t);
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

        // 陆军不能部署到水面（空军可以）
        UnitDef unitDef = GameDatabase.Unit(card.Unit);
        if (unitDef != null && !unitDef.IsAir && cell.Terrain == TerrainType.Water)
        {
            error = "陆军不能部署在水面上";
            return false;
        }

        bool mine = player == PlayerId.Player ? cell.ControlPlayer : cell.ControlEnemy;
        bool theirs = player == PlayerId.Player ? cell.ControlEnemy : cell.ControlPlayer;
        if (!mine || theirs)
        {
            error = "只能部署在自己的控制区内";
            return false;
        }

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

        if (st.Gold < card.Cost)
        {
            error = "金币不足";
            return false;
        }

        st.Gold -= card.Cost;
        RemoveCardFromHand(st, cardId);
        SendToPlayedPile(st, card);
        SpawnUnit(card.Unit, st.Id, coord);
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

        if (st.Gold < card.Cost)
        {
            error = "金币不足";
            return false;
        }

        if (card.KindEnum == CardKind.Unit)
        {
            error = "这张卡需要指定部署地块";
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

        if (st.Gold < card.Cost)
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

        st.Gold -= card.Cost;
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

        if (st.Gold < card.Cost)
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
                    cell.Units.Remove(u);
                    Units.Remove(u);
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

            st.Gold -= card.Cost;
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
                st.BonusMove = true;
                st.Gold -= card.Cost;
                RemoveCardFromHand(st, cardId);
                SendToPlayedPile(st, card);
                return $"{card.NameZh}：本回合地面单位移动 +{card.Power}";
            }
            default:
                st.Gold -= card.Cost;
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
        st.BonusMove = false;
        DrawnThisTurn = 0;
        LastDrawReshuffled = false;

        // 金币收入
        int income = 0;
        foreach (BuildingInst b in Buildings.Where(b => b.Owner == who && !b.IsDead))
            income += b.Def.IncomeAt(b.Level);
        st.Gold += income;

        // 每回合抽五张
        DrawCard(st, 5);

        // 重置本单位行动
        foreach (UnitInst u in Units.Where(u => u.Owner == who))
        {
            u.Moved = false;
            u.Attacked = false;
        }

        foreach (BuildingInst b in Buildings.Where(b => b.Owner == who))
            b.ExtraFired = false;

        // 建筑开火：炮台/防空炮打最近的敌人，指挥部打控制区内目标
        LastBuildingLog = string.Join("；", ResolveBuildingAttacks(who));

        // 建筑发卡：指挥部 / 兵营 / 直升机坪 / 导弹井
        foreach (BuildingInst b in Buildings.Where(b => b.Owner == who && !b.IsDead).ToList())
            GrantBuildingCards(b, st);
    }

    public string LastBuildingLog = "";

    /// <summary>本次回合开始一共进了几张牌（表现层用来播抽牌动画）。</summary>
    public int DrawnThisTurn;

    /// <summary>本次抽牌有没有发生「弃牌堆洗回牌库」。</summary>
    public bool LastDrawReshuffled;

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
                    Cell(target.Coord)?.Units.Remove(target);
                    Units.Remove(target);
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
                Cell(target.Coord)?.Units.Remove(target);
                Units.Remove(target);
                message += $"，{target.Def.NameZh} 被消灭";
            }
        }
        else
        {
            BuildingInst enemyBuilding = Buildings
                .Where(x => x.Owner != b.Owner && !x.IsDead && x != b &&
                            HexCoord.Distance(x.Coord, b.Coord) <= radius)
                .OrderBy(x => HexCoord.Distance(x.Coord, b.Coord))
                .FirstOrDefault();

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

    private void GrantBuildingCards(BuildingInst b, PlayerState st)
    {
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
                    st.Ethereal.Add(card);      // 建筑发的兵种卡带「虚无」
                    DrawnThisTurn++;
                }
                break;
            }
            case "helipad":
            {
                string card = b.Level >= 3 ? "unit_fighter" : "unit_helicopter";
                if (AddToHand(st, card))
                {
                    st.Ethereal.Add(card);
                    DrawnThisTurn++;
                }
                break;
            }
            case "missileSilo":
                int every = Mathf.Max(1, 4 - b.Level);
                if (TurnNumber % every == 0 && AddToHand(st, "magic_missile"))
                {
                    st.Produced.Add((b.Id, "magic_missile"));   // 记住这张卡是哪座井产出的
                    DrawnThisTurn++;
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

    /// <summary>从手牌移除一张卡，同时清掉它的「来源建筑 / 虚无」记录。</summary>
    private static void RemoveCardFromHand(PlayerState st, string cardId)
    {
        if (!st.Hand.Remove(cardId))
            return;

        int idx = st.Produced.FindIndex(p => p.Card == cardId);
        if (idx >= 0)
            st.Produced.RemoveAt(idx);

        int ethereal = st.Ethereal.IndexOf(cardId);
        if (ethereal >= 0)
            st.Ethereal.RemoveAt(ethereal);
    }

    /// <summary>打完的卡去哪：带「消耗」关键字进消耗堆，否则进弃牌堆（可洗回）。</summary>
    private static void SendToPlayedPile(PlayerState st, CardDef card)
    {
        if (card == null)
            return;

        if (card.Exhaust)
            st.Exhaust.Add(card.Id);
        else
            st.Discard.Add(card.Id);
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
                Shuffle(st.Deck);
                LastDrawReshuffled = true;
            }

            string card = st.Deck[0];
            st.Deck.RemoveAt(0);
            st.Hand.Add(card);
            DrawnThisTurn++;
        }
    }

    /// <summary>手牌第 index 张是不是「虚无」实例（同种卡按出现顺序对应）。</summary>
    public bool IsEtherealAt(int handIndex)
    {
        PlayerState st = Player;
        if (handIndex < 0 || handIndex >= st.Hand.Count)
            return false;

        string id = st.Hand[handIndex];
        int etherealCount = st.Ethereal.Count(e => e == id);
        if (etherealCount <= 0)
            return false;

        int occurrence = 0;
        for (int i = 0; i < handIndex; i++)
        {
            if (st.Hand[i] == id)
                occurrence++;
        }

        return occurrence < etherealCount;
    }

    /// <summary>手牌换位（扇形手牌里左右拖动）。</summary>
    public void MoveCardInHand(PlayerState st, int from, int to)
    {
        if (st == null)
            return;
        if (from < 0 || from >= st.Hand.Count || to < 0 || to >= st.Hand.Count || from == to)
            return;

        string card = st.Hand[from];
        st.Hand.RemoveAt(from);
        st.Hand.Insert(to, card);
    }

    public static void Shuffle(List<string> list)    {
        var rng = new Random();
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

        // 回合结束：把当前玩家手里「没有保留关键字」的卡弃掉
        DiscardHand(CurrentPlayer);

        if (CurrentPlayer == PlayerId.Player)
        {
            BeginTurn(PlayerId.Enemy);
        }
        else
        {
            TurnNumber++;
            BeginTurn(PlayerId.Player);
        }
    }

    /// <summary>本回合被弃掉的手牌（表现层用来播「飞向弃牌堆」的动画）。</summary>
    public readonly List<string> DiscardedThisTurn = new();

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
            if (card != null && card.Retain)
                continue;                     // 保留：跨回合留在手里

            st.Hand.RemoveAt(i);

            // 虚无：进消耗堆，永不洗回
            int ethereal = st.Ethereal.IndexOf(id);
            if (ethereal >= 0)
            {
                st.Ethereal.RemoveAt(ethereal);
                st.Exhaust.Add(id);
                st.EtherealExhausted.Add(id);
                ExhaustedThisTurn.Add(id);
                continue;
            }

            st.Discard.Add(id);
            DiscardedThisTurn.Add(id);

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
