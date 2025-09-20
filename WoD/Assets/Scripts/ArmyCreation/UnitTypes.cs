// UnitTypes.cs
public enum UnitType { Rifleman, Grenader, Sniper, Tank }

public static class UnitPrices
{
    public static readonly System.Collections.Generic.Dictionary<UnitType, int> Cost =
        new()
        {
            { UnitType.Rifleman, 10 },
            { UnitType.Grenader, 20 },
            { UnitType.Sniper,   30 },
            { UnitType.Tank,     60 },
        };
}

