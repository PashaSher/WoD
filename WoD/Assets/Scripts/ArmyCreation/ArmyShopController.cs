// ArmyShopController.cs
// ArmyShopController.cs
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using System.Collections;
using UnityEngine.UI;

public class ArmyShopController : MonoBehaviour
{
    [Header("Services")]
    [SerializeField] private FirebaseArmyService firebase;

    [Header("Points")]
    [SerializeField] private int startingPoints = 100;
    [SerializeField] private TextMeshProUGUI pointsText;

    [Header("Status (errors only)")]
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("Legacy tiles (optional)")]
    [SerializeField] private UnitTile riflemanTile;
    [SerializeField] private UnitTile grenaderTile;
    [SerializeField] private UnitTile sniperTile;
    [SerializeField] private UnitTile tankTile;

    [Header("Scroll layout (optional)")]
    [SerializeField] private ScrollRect scrollRect;
    [SerializeField] private RectTransform tilesContent;   // content under ScrollRect
    [SerializeField] private UnitTile tilePrefab;          // template tile to clone

    [Header("Enemy summary (live)")]
    [SerializeField] private TextMeshProUGUI enemyPickedText;

    private int _points;
    private readonly Dictionary<UnitType, int> _counts = new();
    private readonly Dictionary<UnitType, int> _enemyCounts = new();
    private readonly Dictionary<UnitType, UnitTile> _tileByType = new();

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

        // ensure scroll rect exists
        EnsureScrollLayout();

        // Use given prefab or fallback to riflemanTile as template
        if (tilePrefab == null) tilePrefab = riflemanTile;

        BuildTiles();
        RedrawPoints();
    }

    private void EnsureScrollLayout()
    {
        if (scrollRect != null && tilesContent != null) return;
        // Try to find in children
        if (scrollRect == null) scrollRect = GetComponentInChildren<ScrollRect>(true);
        if (scrollRect != null && tilesContent == null) tilesContent = scrollRect.content;
        if (scrollRect != null && tilesContent != null) return;

        // Auto-create minimal ScrollRect
        var goScroll = new GameObject("ShopScroll", typeof(RectTransform), typeof(Image), typeof(Mask), typeof(ScrollRect));
        var rtScroll = (RectTransform)goScroll.transform;
        rtScroll.SetParent(transform, false);
        rtScroll.anchorMin = new Vector2(0.05f, 0.25f);
        rtScroll.anchorMax = new Vector2(0.95f, 0.85f);
        rtScroll.offsetMin = Vector2.zero; rtScroll.offsetMax = Vector2.zero;
        var sr = goScroll.GetComponent<ScrollRect>();
        sr.horizontal = true; sr.vertical = false;
        sr.viewport = rtScroll;
        goScroll.GetComponent<Image>().color = new Color(1,1,1,0); // transparent
        goScroll.GetComponent<Mask>().showMaskGraphic = false;

        var content = new GameObject("Content", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        var rtContent = (RectTransform)content.transform;
        rtContent.SetParent(goScroll.transform, false);
        var h = content.GetComponent<HorizontalLayoutGroup>();
        h.spacing = 20; h.childForceExpandHeight = false; h.childForceExpandWidth = false;
        sr.content = rtContent;

        scrollRect = sr;
        tilesContent = rtContent;
    }

    private void BuildTiles()
    {
        // If legacy tiles exist, reparent them into scroll and register
        RegisterTile(riflemanTile, UnitType.Rifleman);
        RegisterTile(grenaderTile, UnitType.Grenader);
        RegisterTile(sniperTile, UnitType.Sniper);
        RegisterTile(tankTile, UnitType.Tank);

        // Create tiles for all remaining unit types dynamically
        foreach (UnitType t in Enum.GetValues(typeof(UnitType)))
        {
            if (_tileByType.ContainsKey(t)) continue;
            if (tilePrefab == null)
            {
                Debug.LogWarning("[ASC] tilePrefab not set; cannot create tile for " + t);
                continue;
            }
            var tile = Instantiate(tilePrefab, tilesContent);
            tile.gameObject.name = $"Tile_{t}";
            tile.Init(this, t);
            _tileByType[t] = tile;
        }
    }

    private void RegisterTile(UnitTile tile, UnitType type)
    {
        if (!tile) return;
        tile.transform.SetParent(tilesContent, false);
        tile.Init(this, type);
        _tileByType[type] = tile;
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
        foreach (var kv in _tileByType)
        {
            var type = kv.Key;
            var tile = kv.Value;
            if (!tile) continue;
            int count = _counts.TryGetValue(type, out var c) ? c : 0;
            tile.SetCount(count);
        }
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
         await firebase.AddUnitAsync(type);

         // НЕ трогаем _counts[type] и не SetCount()
         _points -= price;           // локально меняем только очки
         RedrawPoints();
         ClearStatus();
         // Количество обновится из ListenArmyChanges → SyncCountsFromDb/GetCountsAsync
        }
       catch (Exception e)
       {
          Debug.LogWarning($"Add {type} failed: {e.Message}");
       }
    }

    public async void OnMinus(UnitType type)
    {
       // Тоже не проверяем локально _counts[type] — состояние источника истины в RTDB
       try
       {
          var removed = await firebase.RemoveUnitAsync(type);
          if (!string.IsNullOrEmpty(removed))
          {
            _points += UnitPrices.Cost[type];
            RedrawPoints();
            ClearStatus();
            // Количество снова придёт из слушателя
          }
          else
          {
            // ничего не удалилось — можно подсветить если нужно
        }
       }
         catch (Exception e)
        {
          Debug.LogWarning($"Remove {type} failed: {e.Message}");
        }
    }

    private UnitTile GetTile(UnitType t) =>
        _tileByType.TryGetValue(t, out var tile) ? tile : null;

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
