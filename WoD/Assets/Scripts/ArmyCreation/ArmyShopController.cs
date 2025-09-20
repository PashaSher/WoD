// ArmyShopController.cs
 // ArmyShopController.cs
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using System.Collections;

public class ArmyShopController : MonoBehaviour
{
    [Header("Services")]
    [SerializeField] private FirebaseArmyService firebase;

    [Header("Points")]
    [SerializeField] private int startingPoints = 100;
    [SerializeField] private TextMeshProUGUI pointsText;

    [Header("Status (errors only)")]
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("Tiles (per unit)")]
    [SerializeField] private UnitTile riflemanTile;
    [SerializeField] private UnitTile grenaderTile;
    [SerializeField] private UnitTile sniperTile;
    [SerializeField] private UnitTile tankTile;

    [Header("Enemy summary (live)")]
    [SerializeField] private TextMeshProUGUI enemyPickedText;

    private int _points;
    private readonly Dictionary<UnitType, int> _counts = new();
    private readonly Dictionary<UnitType, int> _enemyCounts = new();

    private void Awake()
    {
        if (firebase == null)
        {
#if UNITY_2022_2_OR_NEWER
            firebase = FindFirstObjectByType<FirebaseArmyService>(FindObjectsInactive.Include);
#else
            firebase = Object.FindObjectOfType<FirebaseArmyService>(true);
#endif
        }

        _points = startingPoints;
        foreach (UnitType t in Enum.GetValues(typeof(UnitType))) _counts[t] = 0;

        riflemanTile.Init(this, UnitType.Rifleman);
        grenaderTile.Init(this, UnitType.Grenader);
        sniperTile.Init(this, UnitType.Sniper);
        tankTile.Init(this, UnitType.Tank);

        RedrawPoints();
    }


    private IEnumerator Start()
    {
        if (firebase == null)
        {
            Debug.LogError("[ASC] FirebaseArmyService not found in scene.");
            yield break;
        }

        // ждём пока сервис получит контекст (Matchmaker его заполняет)
        yield return new WaitUntil(() => !string.IsNullOrEmpty(firebase.SessionId));

        Debug.Log($"[ASC] READY sid='{firebase.SessionId}', ifHost={firebase.IfHost}");
        Debug.Log($"[ASC] MyPath=sessions/{firebase.SessionId}/{(firebase.IfHost ? "hostArmy" : "clientArmy")}");
        Debug.Log($"[ASC] OppPath={firebase.OpponentArmyPath}");

        // Подписка на противника
        firebase.ListenOpponentChanges(async () =>
        {
            Debug.Log("[ASC] Opponent listener fired");
            var fresh = await firebase.GetEnemyCountsAsync();
            foreach (var kv in fresh) _enemyCounts[kv.Key] = kv.Value;
            UpdateEnemySummary();
        });

        // Подписка на свою армию
        firebase.ListenArmyChanges(async () =>
        {
            var fresh = await firebase.GetCountsAsync();
            foreach (var kv in fresh) _counts[kv.Key] = kv.Value;
            RedrawAllTiles();
        });

        // Первичная загрузка
        _ = SyncEnemyFromDb();
        _ = SyncCountsFromDb();
    }
    // helper только для логов
    private string GetPathSafe(FirebaseArmyService fas, bool mine)
    {
        try
        {
            return mine ? $"sessions/{fas.SessionId}/{(fas.IfHost ? "hostArmy" : "clientArmy")}"
                          : fas.OpponentArmyPath;
        }
        catch { return "<n/a>"; }
    }

// аккуратная отписка (если хочешь)
private void OnDisable()
{
    if (firebase == null) return;
    firebase.StopArmyChanges();
    firebase.StopOpponentChanges();
}


    private async System.Threading.Tasks.Task SyncCountsFromDb()
    {
        var fresh = await firebase.GetCountsAsync();
        foreach (var kv in fresh) _counts[kv.Key] = kv.Value;
        RedrawAllTiles();
    }

    private void RedrawAllTiles()
    {
        riflemanTile.SetCount(_counts[UnitType.Rifleman]);
        grenaderTile.SetCount(_counts[UnitType.Grenader]);
        sniperTile.SetCount(_counts[UnitType.Sniper]);
        tankTile.SetCount(_counts[UnitType.Tank]);
    }

    private void RedrawPoints()
    {
        if (pointsText != null) pointsText.text = $"Points: {_points}";
    }
    private async System.Threading.Tasks.Task SyncEnemyFromDb()
    {
       if (firebase == null) return;
       var freshE = await firebase.GetEnemyCountsAsync();
       foreach (var kv in freshE) _enemyCounts[kv.Key] = kv.Value;
       UpdateEnemySummary();
    }

    private void UpdateEnemySummary()
    {
       if (enemyPickedText == null) return;
       var parts = new List<string>();
       foreach (var kv in _enemyCounts)
        if (kv.Value > 0) parts.Add($"{kv.Key}{kv.Value}");
       enemyPickedText.text = parts.Count > 0 ? $"Enemy: {string.Join(" ", parts)}"
                                           : "Enemy: None";
   }

    public async void OnPlus(UnitType type)
    {
        int price = UnitPrices.Cost[type];
        if (_points < price)
        {
            ShowOnlyNotEnoughPoints(type, price);
            return;
        }

        try
        {
            string _ = await firebase.AddUnitAsync(type);
            _counts[type]++;
            _points -= price;
            RedrawPoints();
            GetTile(type)?.SetCount(_counts[type]);
            ClearStatus();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Add {type} failed: {e.Message}");
            // статус не показываем — по требованию только "not enough points"
        }
    }

    public async void OnMinus(UnitType type)
    {
        if (_counts[type] <= 0) return;

        try
        {
            await firebase.RemoveUnitAsync(type);
            _counts[type]--;
            _points += UnitPrices.Cost[type];
            RedrawPoints();
            GetTile(type)?.SetCount(_counts[type]);
            ClearStatus();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Remove {type} failed: {e.Message}");
            // статус не показываем
        }
    }

    private UnitTile GetTile(UnitType t) =>
        t switch
        {
            UnitType.Rifleman => riflemanTile,
            UnitType.Grenader => grenaderTile,
            UnitType.Sniper   => sniperTile,
            UnitType.Tank     => tankTile,
            _ => null
        };

    // --- статус только для "не хватает очков"
    private void ShowOnlyNotEnoughPoints(UnitType type, int price)
    {
        if (statusText == null) return;
        statusText.text = $"Not enough points for {type}. Need {price}, have {_points}.";
    }

    private void ClearStatus()
    {
        if (statusText == null) return;
        statusText.text = ""; // очищаем при успешных операциях
    }
}
