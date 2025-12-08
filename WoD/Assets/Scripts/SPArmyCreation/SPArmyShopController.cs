using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class SPArmyShopController : MonoBehaviour
{
    [Header("Debug")]
    [SerializeField] private bool verboseLogs = true;

    [Header("Points")]
    [SerializeField] private int startingPoints = 100;
    [SerializeField] private TextMeshProUGUI pointsText;

    [Header("Status (errors only)")]
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("Tiles (per unit)")]
    [SerializeField] private SPUnitTile riflemanTile;
    [SerializeField] private SPUnitTile grenaderTile;
    [SerializeField] private SPUnitTile sniperTile;
    [SerializeField] private SPUnitTile tankTile;
    [Tooltip("Optional. If set, missing tiles (new UnitType values) will be auto-created under Tiles Parent.")]
    [SerializeField] private SPUnitTile tilePrefab;
    [Tooltip("Optional explicit parent for auto-created tiles. If null, will use the parent of Rifleman tile.")]
    [SerializeField] private RectTransform tilesParent;

    [Header("Previews (optional)")]
    [Tooltip("Shop-only previews: if set, these sprites will be shown on tiles (does NOT affect battle visuals).")]
    [SerializeField] private PreviewSpriteEntry[] previewSprites;
    [Tooltip("Alternative source for previews: UnitStats (uses UnitStats.sprite only for shop).")]
    [SerializeField] private UnitStats[] previewStats;

    [Header("Tiles auto-create")]
    [Tooltip("If OFF, extra tiles (for types beyond the 4 manual ones) will NOT be auto-created even if Tile Prefab is set.")]
    [SerializeField] private bool autoCreateMissingTiles = true;

    [Header("Per-type toggles")]
    [Tooltip("Optional. If empty, all types are considered enabled. If filled, only types with Enabled=true are shown/auto-created.")]
    [SerializeField] private TypeToggle[] typeToggles;

    [System.Serializable]
    public struct PreviewSpriteEntry
    {
        public UnitType type;
        public Sprite sprite;
    }
    [System.Serializable]
    public struct TypeToggle
    {
        public UnitType Type;
        public bool Enabled;
    }

    private int _points;
    private readonly Dictionary<UnitType, int> _counts = new();
    private readonly Dictionary<UnitType, UnitStats> _previewByType = new();
    private readonly HashSet<UnitType> _plusPending = new();

    private void Awake()
    {
        Log("Awake() begin");
        _points = startingPoints;
        foreach (UnitType t in Enum.GetValues(typeof(UnitType))) _counts[t] = 0;

        _previewByType.Clear();
        if (previewStats != null)
        {
            foreach (var s in previewStats)
                if (s != null) _previewByType[s.unitType] = s;
        }

        if (_previewByType.Count > 0)
        {
            var keys = string.Join(", ", _previewByType.Keys);
            Log($"Preview types available: {keys}");
        }

        Log($"Preview stats loaded: {_previewByType.Count}");

        if (riflemanTile) { if (IsTypeEnabled(UnitType.Rifleman)) { riflemanTile.Init(this, UnitType.Rifleman); ApplyPreview(riflemanTile, UnitType.Rifleman); } else TryDisable(riflemanTile); }
        if (grenaderTile) { if (IsTypeEnabled(UnitType.Grenader)) { grenaderTile.Init(this, UnitType.Grenader); ApplyPreview(grenaderTile, UnitType.Grenader); } else TryDisable(grenaderTile); }
        if (sniperTile)   { if (IsTypeEnabled(UnitType.Sniper))   { sniperTile.Init(this, UnitType.Sniper);   ApplyPreview(sniperTile,   UnitType.Sniper); }   else TryDisable(sniperTile); }
        if (tankTile)     { if (IsTypeEnabled(UnitType.Tank))     { tankTile.Init(this, UnitType.Tank);     ApplyPreview(tankTile,     UnitType.Tank); }     else TryDisable(tankTile); }

        TryCreateMissingTiles();
        RedrawPoints();
        RedrawAllTiles();
        Log("Awake() done");
    }

    private void TryDisable(SPUnitTile tile)
    {
        try { tile.gameObject.SetActive(false); } catch { }
    }

    private void TryCreateMissingTiles()
    {
        Log("TryCreateMissingTiles()");
        if (!autoCreateMissingTiles) return;
        if (tilePrefab == null) return;

        var existing = new HashSet<UnitType>();
        if (riflemanTile) existing.Add(UnitType.Rifleman);
        if (grenaderTile) existing.Add(UnitType.Grenader);
        if (sniperTile)   existing.Add(UnitType.Sniper);
        if (tankTile)     existing.Add(UnitType.Tank);

        var parent = tilesParent != null
            ? tilesParent
            : (tankTile != null ? tankTile.transform.parent as RectTransform : null);
        if (parent == null) return;

        foreach (UnitType t in Enum.GetValues(typeof(UnitType)))
        {
            if (!IsTypeEnabled(t)) continue;
            if (existing.Contains(t)) continue;
            var tile = Instantiate(tilePrefab, parent);
            tile.gameObject.name = $"SPTile_{t}";
            tile.Init(this, t);
            ApplyPreview(tile, t);
            Log($"Created tile for {t}");
        }
    }

    private bool IsTypeEnabled(UnitType t)
    {
        if (typeToggles == null || typeToggles.Length == 0) return true;
        for (int i = 0; i < typeToggles.Length; i++)
            if (typeToggles[i].Type == t) return typeToggles[i].Enabled;
        return true;
    }

    private void ApplyPreview(SPUnitTile tile, UnitType type)
    {
        Log($"ApplyPreview({type})");
        if (previewSprites != null)
        {
            for (int i = 0; i < previewSprites.Length; i++)
            {
                if (previewSprites[i].type == type && previewSprites[i].sprite != null)
                {
                    tile.SetPreview(previewSprites[i].sprite);
                    Log($"Preview via explicit sprite for {type}: {previewSprites[i].sprite.name}");
                    return;
                }
            }
        }
        if (_previewByType.TryGetValue(type, out var stats) && stats != null && stats.sprite != null)
        {
            tile.SetPreview(stats.sprite);
            Log($"Preview via UnitStats for {type}: {stats.sprite.name}");
            return;
        }
        Log($"No preview found for {type}");
    }

    private void RedrawAllTiles()
    {
        Log("RedrawAllTiles()");
        if (riflemanTile) riflemanTile.SetCount(_counts[UnitType.Rifleman]);
        if (grenaderTile) grenaderTile.SetCount(_counts[UnitType.Grenader]);
        if (sniperTile)   sniperTile.SetCount(_counts[UnitType.Sniper]);
        if (tankTile)     tankTile.SetCount(_counts[UnitType.Tank]);

        var parent = tilesParent != null
            ? tilesParent
            : (tankTile != null ? tankTile.transform.parent as RectTransform : null);
        if (parent != null)
        {
            var tiles = parent.GetComponentsInChildren<SPUnitTile>(true);
            foreach (var tile in tiles)
            {
                if (Enum.TryParse<UnitType>(tile.gameObject.name.Replace("SPTile_", ""), out var parsed))
                {
                    tile.SetCount(_counts.TryGetValue(parsed, out var c) ? c : 0);
                }
            }
        }
    }

    private void RedrawPoints()
    {
        if (pointsText != null) pointsText.text = $"Points: {_points}";
        Log($"Points={_points}");
    }

    public void OnPlus(UnitType type)
    {
        Log($"OnPlus({type}) begin");
        if (_plusPending.Contains(type)) return;
        int price = UnitPrices.Cost[type];
        if (_points < price)
        {
            ShowOnlyNotEnoughPoints(type, price);
            return;
        }
        _plusPending.Add(type);
        try
        {
            _counts[type] = _counts.TryGetValue(type, out var c) ? c + 1 : 1;
            _points -= price;
            ClearStatus();
            RedrawPoints();
            RedrawAllTiles();
            Log($"OnPlus({type}) OK: count={_counts[type]}, points={_points}");
        }
        finally
        {
            _plusPending.Remove(type);
        }
    }

    public void OnMinus(UnitType type)
    {
        Log($"OnMinus({type}) begin");
        if (_counts.TryGetValue(type, out var c) && c > 0)
        {
            _counts[type] = c - 1;
            _points += UnitPrices.Cost[type];
            ClearStatus();
            RedrawPoints();
            RedrawAllTiles();
            Log($"OnMinus({type}) OK: count={_counts[type]}, points={_points}");
        }
        else
        {
            Log($"OnMinus({type}) skipped: count=0");
        }
    }

    public void ConfirmSelectionAndGoToBattle()
    {
        Log("ConfirmSelectionAndGoToBattle()");
        SPArmyState.SaveSelection(_counts, startingPoints);
        UnityEngine.SceneManagement.SceneManager.LoadScene("SPBattleScene", UnityEngine.SceneManagement.LoadSceneMode.Single);
    }

    private void ShowOnlyNotEnoughPoints(UnitType type, int price)
    {
        if (statusText == null) return;
        statusText.text = $"Not enough points for {type}. Need {price}, have {_points}.";
    }

    private void ClearStatus()
    {
        if (statusText == null) return;
        statusText.text = "";
    }

    private void Log(string msg)
    {
        if (verboseLogs)
            Debug.Log($"[SPArmyShop] {msg}");
    }
}


