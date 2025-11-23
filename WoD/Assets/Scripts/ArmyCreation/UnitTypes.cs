// UnitTypes.cs
public enum UnitType { Rifleman, Grenader, Sniper, Tank, Turret, Wall, BarbedWire, TankTrap, Sandbags }

public static class UnitPrices
{
    public static readonly System.Collections.Generic.Dictionary<UnitType, int> Cost =
        new()
        {
            { UnitType.Rifleman, 10 },
            { UnitType.Grenader, 20 },
            { UnitType.Sniper,   30 },
            { UnitType.Tank,     60 },
            { UnitType.Turret,   40 },   // stationary shooter
            { UnitType.Wall,     15 },   // cover
            { UnitType.BarbedWire, 12 }, // slows / decorative cover
            { UnitType.TankTrap, 25 },   // anti-tank hedgehog
            { UnitType.Sandbags, 18 },   // sandbag cover
        };
}

