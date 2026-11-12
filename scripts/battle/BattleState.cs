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
    public bool BonusMove;

    public bool IsPlayer => Id == PlayerId.Player;
}

/// <summary>一局战斗的全部状态。纯数据，不依赖节点，方便后面接 AI 与存档。</summary>
public sealed class BattleState
{
    public const int HandLimit = 8;

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

    /// <summary>该格是否可以被 player 建造：无建筑、在己方控制区、不在敌方控制区。</summary>
    public bool CanBuildAt(HexCoord coord, PlayerId player)
    {
        Cell cell = Cell(coord);
        if (cell == null || cell.Building != null)
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
            error = "只能建在自己的控制区内，且不能与敌方控制区重叠";
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

    public void Demolish(BuildingInst b)
    {
        if (b == null)
            return;
        Cell cell = Cell(b.Coord);
        if (cell != null && cell.Building == b)
            cell.Building = null;
        Buildings.Remove(b);
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
    public string PerformAttack(UnitInst u, HexCoord target)
    {
        if (u == null || !u.Alive || u.Attacked)
            return "";

        Cell cell = Cell(target);
        if (cell == null)
            return "";

        int dmg = u.Def.Damage;
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
        st.Hand.Remove(cardId);
        st.Discard.Add(cardId);
        SpawnUnit(card.Unit, st.Id, coord);
        error = "";
        return true;
    }

    /// <summary>
    /// 打出不需要「部署」的卡：魔法卡（全局效果）与打击卡（指定一格）。
    /// 返回一句给玩家看的战报；失败时 error 非空。
    /// </summary>
    public string PlayGlobalCard(PlayerState st, string cardId, HexCoord? target, out string error)
    {
        error = "";
        CardDef card = GameDatabase.Card(cardId);
        if (card == null)
        {
            error = "未知卡牌";
            return "";
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
            st.Hand.Remove(cardId);
            st.Discard.Add(cardId);
            CheckVictory();
            return hits.Count == 0 ? $"{card.NameZh} 没有命中任何目标" : $"{card.NameZh}：" + string.Join("，", hits);
        }

        // 全局效果
        switch (card.Effect)
        {
            case "repair":
            {
                int healed = 0;
                foreach (BuildingInst b in Buildings.Where(b => b.Owner == st.Id && !b.IsDead))
                {
                    int before = b.Hp;
                    b.Hp = Mathf.Min(b.MaxHp, b.Hp + card.Power);
                    healed += b.Hp - before;
                }

                st.Gold -= card.Cost;
                st.Hand.Remove(cardId);
                st.Discard.Add(cardId);
                return $"{card.NameZh}：修复了 {healed} 点";
            }
            case "boost":
            {
                st.BonusMove = true;
                st.Gold -= card.Cost;
                st.Hand.Remove(cardId);
                st.Discard.Add(cardId);
                return $"{card.NameZh}：本回合地面单位移动 +{card.Power}";
            }
            default:
                st.Gold -= card.Cost;
                st.Hand.Remove(cardId);
                st.Discard.Add(cardId);
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

        // 金币收入
        int income = 0;
        foreach (BuildingInst b in Buildings.Where(b => b.Owner == who && !b.IsDead))
            income += b.Def.IncomeAt(b.Level);
        st.Gold += income;

        // 每回合抽一张
        DrawCard(st, 1);

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

    /// <summary>
    /// 回合开始时建筑自动开火（草案：炮台/防空炮每回合自动攻击控制区内最近的敌人，
    /// 指挥部每回合攻击控制区内一格）。返回战报文本。
    /// </summary>
    public List<string> ResolveBuildingAttacks(PlayerId who)
    {
        var log = new List<string>();

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
                AddToHand(st, b.Level >= 3 ? "unit_tank" : "unit_infantry");
                break;
            case "helipad":
                AddToHand(st, b.Level >= 3 ? "unit_fighter" : "unit_helicopter");
                break;
            case "missileSilo":
                int every = Mathf.Max(1, 4 - b.Level);
                if (TurnNumber % every == 0)
                    AddToHand(st, "magic_missile");
                break;
        }
    }

    private static void AddToHand(PlayerState st, string cardId)
    {
        if (st.Hand.Count >= HandLimit)
            return;
        st.Hand.Add(cardId);
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
            }

            string card = st.Deck[0];
            st.Deck.RemoveAt(0);
            st.Hand.Add(card);
        }
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
