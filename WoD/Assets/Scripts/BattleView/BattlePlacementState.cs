using UnityEngine;

/// <summary>
/// Глобальный флаг фазы расстановки. Используется, чтобы временно отключить боевую логику.
/// </summary>
public static class BattlePlacementState
{
    /// <summary>
    /// True — идёт расстановка юнитов игроком. Боевая логика (автоатака и т.п.) должна быть приостановлена.
    /// </summary>
    public static bool IsPlacementActive { get; private set; }

    public static void BeginPlacement() => IsPlacementActive = true;
    public static void EndPlacement()   => IsPlacementActive = false;
}







