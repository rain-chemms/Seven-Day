namespace SevenDay.Core;

public enum PlayerId
{
    None = 0,
    Player = 1,
    Enemy = 2,
}

public enum TerrainType
{
    Grass,
    Sand,
    Dirt,
    Rock,
    Snow,
    Water,
}

public enum UnitKind
{
    Infantry,
    Tank,
    MissileLauncher,
    Helicopter,
    Fighter,
}

public enum BuildingKind
{
    Hq,
    City,
    Barracks,
    GunTurret,
    AaGun,
    MissileSilo,
    Helipad,
}

public enum CardKind
{
    /// <summary>需要指定落点格子的卡（兵种卡）。</summary>
    Unit,

    /// <summary>不需要指定格子的卡（空袭、维修、加成等）。</summary>
    Global,

    /// <summary>需要指定敌方格子的卡（导弹袭击）。</summary>
    Strike,
}

public enum AIDifficulty
{
    Easy,
    Normal,
    Hard,
    Lunatic,
}

/// <summary>战斗阶段。</summary>
public enum BattlePhase
{
    Setup,
    PlayerTurn,
    EnemyTurn,
    GameOver,
}
