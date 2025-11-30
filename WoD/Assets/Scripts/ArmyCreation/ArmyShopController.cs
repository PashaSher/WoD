// ArmyShopController.cs
 // ArmyShopController.cs
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using System.Collections;
using System.Linq;

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
    [Tooltip("Optional. If set, missing tiles (new UnitType values) will be auto-created under Tiles Parent.")]
    [SerializeField] private UnitTile tilePrefab;
    [Tooltip("Optional explicit parent for auto-created tiles. If null, will use the parent of Rifleman tile.")]
    [SerializeField] private RectTransform tilesParent;

    [Header("Previews (optional)")]
    [Tooltip("Shop-only previews: if set, these sprites will be shown on tiles (does NOT affect battle visuals).")]
    [SerializeField] private PreviewSpriteEntry[] previewSprites;
    [Tooltip("Alternative source for previews: UnitStats (uses UnitStats.sprite only for shop).")]
    [SerializeField] private UnitStats[] previewStats;

    [System.Serializable]
    public struct PreviewSpriteEntry
    {
        public UnitType type;
        public Sprite sprite;
    }

    [Header("Enemy summary (live)")]
    [SerializeField] private TextMeshProUGUI enemyPickedText;

    private int _points;
    private readonly Dictionary<UnitType, int> _counts = new();
    private readonly Dictionary<UnitType, int> _enemyCounts = new();
    private readonly Dictionary<UnitType, UnitStats> _previewByType = new();

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

        // build preview map (UnitStats fallback)
        _previewByType.Clear();
        if (previewStats != null)
        {
            foreach (var s in previewStats)
                if (s != null) _previewByType[s.unitType] = s;
        }

        if (riflemanTile) { riflemanTile.Init(this, UnitType.Rifleman); ApplyPreview(riflemanTile, UnitType.Rifleman); }
        if (grenaderTile) { grenaderTile.Init(this, UnitType.Grenader); ApplyPreview(grenaderTile, UnitType.Grenader); }
        if (sniperTile)   { sniperTile.Init(this, UnitType.Sniper);     ApplyPreview(sniperTile,   UnitType.Sniper); }
        if (tankTile)     { tankTile.Init(this, UnitType.Tank);         ApplyPreview(tankTile,     UnitType.Tank); }

        // Авто‑добавление недостающих плиток (не трогаем существующие)
        TryCreateMissingTiles();

        RedrawPoints();
    }

    private void TryCreateMissingTiles()
    {
        if (tilePrefab == null) return; // без префаба не создаём

        // Собираем набор уже существующих типов (по четырём полям)
        var existing = new HashSet<UnitType>();
        if (riflemanTile) existing.Add(UnitType.Rifleman);
        if (grenaderTile) existing.Add(UnitType.Grenader);
        if (sniperTile)   existing.Add(UnitType.Sniper);
        if (tankTile)     existing.Add(UnitType.Tank);

        // Определяем родителя для новых плиток
		var parent = tilesParent != null
			? tilesParent
			: (tankTile != null ? tankTile.transform.parent as RectTransform : null);
        if (parent == null) return;

        foreach (UnitType t in Enum.GetValues(typeof(UnitType)))
        {
            if (existing.Contains(t)) continue; // уже есть вручную
            var tile = Instantiate(tilePrefab, parent);
            tile.gameObject.name = $"Tile_{t}";
            tile.Init(this, t);
            ApplyPreview(tile, t);
        }
    }

    private void ApplyPreview(UnitTile tile, UnitType type)
    {
        // 1) explicit sprite mapping
        if (previewSprites != null)
        {
            for (int i = 0; i < previewSprites.Length; i++)
            {
                if (previewSprites[i].type == type && previewSprites[i].sprite != null)
                {
                    tile.SetPreview(previewSprites[i].sprite);
                    return;
                }
            }
        }
        // 2) fallback to UnitStats sprite (shop-only)
        if (_previewByType.TryGetValue(type, out var stats) && stats != null && stats.sprite != null)
        {
            tile.SetPreview(stats.sprite);
        }
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
        if (riflemanTile) riflemanTile.SetCount(_counts[UnitType.Rifleman]);
        if (grenaderTile) grenaderTile.SetCount(_counts[UnitType.Grenader]);
        if (sniperTile)   sniperTile.SetCount(_counts[UnitType.Sniper]);
        if (tankTile)     tankTile.SetCount(_counts[UnitType.Tank]);

        // Обновим авто‑созданные плитки (если есть)
        // Пробежимся по всем UnitTile у родителя и обновим их
		var parent = tilesParent != null
			? tilesParent
			: (tankTile != null ? tankTile.transform.parent as RectTransform : null);
        if (parent != null)
        {
            var tiles = parent.GetComponentsInChildren<UnitTile>(true);
            foreach (var tile in tiles)
            {
                // В UnitTile нет публичного доступа к типу, но Init не должен вызываться повторно.
                // Поэтому обновим счётчик по titleText.ToString, если тип определить сложно — пропускаем.
                // Упростим: попробуем по названию GameObject "Tile_<Type>"
                if (Enum.TryParse<UnitType>(tile.gameObject.name.Replace("Tile_", ""), out var parsed))
                {
                    tile.SetCount(_counts.TryGetValue(parsed, out var c) ? c : 0);
                }
            }
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

    private UnitTile GetTile(UnitType t)
    {
        switch (t)
        {
            case UnitType.Rifleman: return riflemanTile;
            case UnitType.Grenader: return grenaderTile;
            case UnitType.Sniper:   return sniperTile;
            case UnitType.Tank:     return tankTile;
            default:
                // попытка найти авто‑созданный Tile_<Type> под родителем
				var parent = tilesParent != null
					? tilesParent
					: (tankTile != null ? tankTile.transform.parent as RectTransform : null);
                if (parent != null)
                {
                    var name = $"Tile_{t}";
                    var tr = parent.Find(name);
                    if (tr != null) return tr.GetComponent<UnitTile>();
                }
                return null;
        }
    }

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
