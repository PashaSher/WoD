using System.Collections.Generic;

public static class SPArmyState
{
    private static Dictionary<UnitType, int> _selectedCounts;
    private static int _startingPoints;

    public static bool HasSelection => _selectedCounts != null;

    public static void SaveSelection(Dictionary<UnitType, int> counts, int startingPoints)
    {
        _startingPoints = startingPoints;
        _selectedCounts = new Dictionary<UnitType, int>(counts);
    }

    public static bool TryGetSelection(out Dictionary<UnitType, int> counts, out int startingPoints)
    {
        startingPoints = _startingPoints;
        if (_selectedCounts == null)
        {
            counts = null;
            return false;
        }
        counts = new Dictionary<UnitType, int>(_selectedCounts);
        return true;
    }

    public static void Clear()
    {
        _selectedCounts = null;
        _startingPoints = 0;
    }
}



