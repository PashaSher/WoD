using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Database;
using UnityEngine;

public class ArmySpawner : MonoBehaviour
{
    [Header("Prefabs")]
    [SerializeField] private GameObject unitRootPrefab;   // префаб Unit_Root

    [Header("Stats")]
    [SerializeField] private List<UnitStats> unitStatsList; // Rifleman/Grenader/Sniper/Tank

    [Header("References")]
    [SerializeField] private Transform unitsParent;   // пустой GameObject "Units" в сцене
    [SerializeField] private bool ifHost;             // флаг роли (при желании переопределится из Globalflags)
    [SerializeField] private string sessionId;        // ID сессии из Firebase

    [Header("Visual (test)")]
    [SerializeField] private Sprite testSprite;       // любой PNG как Sprite — для быстрого теста

    [Header("Debug")]
    [SerializeField] private bool verboseLogs = true; // включить подробные логи

    private DatabaseReference root;
    private readonly Dictionary<UnitType, UnitStats> statsByType = new();

    private enum Side { Left, Right }

    // ---------- lifecycle ----------
    private void Awake()
    {
        SafeLog($"Awake() start");

        // пробуем подтянуть контекст, если он есть
        try {
            GameSession.Load();
            if (!string.IsNullOrEmpty(GameSession.SessionId))
                sessionId = GameSession.SessionId;
        } catch { /* ignore */ }

        try { ifHost = Globalflags.ifHost; } catch { /* ignore */ }

        // контейнер Units
        if (unitsParent == null)
        {
            var go = GameObject.Find("Units") ?? new GameObject("Units");
            unitsParent = go.transform;
            SafeLog("Units parent auto-created/attached");
        }

        // собрать словарь статов
        statsByType.Clear();
        if (unitStatsList != null)
        {
            foreach (var s in unitStatsList)
            {
                if (s == null) continue;
                if (!statsByType.ContainsKey(s.unitType))
                    statsByType.Add(s.unitType, s);
            }
        }

        SafeLog($"Awake() done. Stats found: {statsByType.Count}");
    }

    private async void Start()
    {
        SafeLog($"Start() | sessionId='{sessionId}', prefab='{(unitRootPrefab?unitRootPrefab.name:"NULL")}', stats={statsByType.Count}");

        root = FirebaseDatabase.DefaultInstance.RootReference;

        if (unitRootPrefab == null)
        {
            Debug.LogError("[ArmySpawner] UnitRootPrefab не задан — спавн невозможен.");
            return;
        }
        if (string.IsNullOrEmpty(sessionId))
        {
            Debug.LogError("[ArmySpawner] sessionId пуст — укажи ID сессии.");
            return;
        }

        await LoadAndSpawn();   // отдельный метод с try/catch и логами
    }

    // ---------- main flow ----------
    private async Task LoadAndSpawn()
    {
        SafeLog($"LoadAndSpawn() → sessions/{sessionId}");

        DataSnapshot snapshot = null;
        try
        {
            snapshot = await root.Child("sessions").Child(sessionId).GetValueAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ArmySpawner] Firebase GetValueAsync EXCEPTION: {ex.Message}\n{ex.StackTrace}");
            return;
        }

        if (snapshot == null || !snapshot.Exists)
        {
            Debug.LogWarning($"[ArmySpawner] sessions/{sessionId} не найдено или пусто.");
            return;
        }

        var host = snapshot.Child("hostArmy");
        var client = snapshot.Child("clientArmy");
        SafeLog($"RTDB OK. hostArmy={host.ChildrenCount}, clientArmy={client.ChildrenCount}");

        SpawnArmy(host,  Side.Left);
        SpawnArmy(client, Side.Right);
    }

     private void SpawnArmy(DataSnapshot armySnap, Side side)
{
    if (armySnap == null || !armySnap.HasChildren)
    {
        SafeLog($"SpawnArmy({side}) skip: empty");
        return;
    }

    var cam = Camera.main;
    if (cam == null)
    {
        Debug.LogError("[ArmySpawner] Camera.main == null");
        return;
    }

    float halfH = cam.orthographicSize;
    float halfW = halfH * cam.aspect;

    float x = (side == Side.Left) ? -halfW + 2f : halfW - 2f;
    float y =  halfH - 1f;

    bool isHostBranch = (side == Side.Left); // фикс: hostArmy слева
    int spawned = 0;

    // Сколько узлов реально являются юнитами
    int valid = 0;
    foreach (var c in armySnap.Children)
        if (c.HasChildren && c.HasChild("type")) valid++;

    SafeLog($"SpawnArmy({side}) begin at x={x:F2}, yStart={y:F2} | nodes={armySnap.ChildrenCount}, units(with type)={valid}");

    foreach (var child in armySnap.Children)
    {
        string key = child.Key;

        // Спавним только узлы с данными юнита
        if (!child.HasChildren || !child.HasChild("type")) continue;

        // Тип берём из поля "type" (а не из имени ключа)
        string typeStr = child.Child("type").Value?.ToString();
        if (string.IsNullOrEmpty(typeStr))
        {
            Debug.LogWarning($"[ArmySpawner] node '{key}' has no valid 'type'");
            continue;
        }

        if (!Enum.TryParse(typeStr, true, out UnitType type))
        {
            Debug.LogWarning($"[ArmySpawner] Unknown unit type '{typeStr}' at '{key}'");
            continue;
        }

        // Позиция и инстанс
        Vector3 pos = new Vector3(x, y, 0f);
        var go = Instantiate(unitRootPrefab, pos, Quaternion.identity, unitsParent);

        // Визуал (тестовый спрайт, если задан)
        var sr = go.transform.Find("Visual")?.GetComponent<SpriteRenderer>();
        if (sr != null && testSprite != null)
        {
            sr.sprite = testSprite;
            if (sr.sortingOrder < 5) sr.sortingOrder = 5; // на всякий случай поверх фона
        }

        // Применяем статы (если есть)
        var unit = go.GetComponent<Unit>();
        if (unit != null && statsByType != null && statsByType.TryGetValue(type, out var stats))
            unit.Init(type.ToString(), stats);

        // Пишем мету (не мешаем геймплею, если прав нет)
        if (unit != null)
        {
            try { unit.SetFirebaseContextAndPush(sessionId, isHostBranch, key); }
            catch (Exception ex) { Debug.LogWarning($"[ArmySpawner] meta write skip for '{key}': {ex.Message}"); }
        }

        // Цвет кольца и разворот
        var ring = go.transform.Find("SelectionRing")?.GetComponent<SpriteRenderer>();
        if (ring != null) ring.color = isHostBranch ? Color.cyan : Color.red;

        var ls = go.transform.localScale;
        ls.x = Mathf.Abs(ls.x) * (side == Side.Left ? 1f : -1f);
        go.transform.localScale = ls;

        // Следующее место
        y -= 1.5f;
        if (y < -halfH + 1f)
        {
            y = halfH - 1f;
            x += (side == Side.Left ? +1.6f : -1.6f);
        }

        spawned++;
        if (spawned <= 3) SafeLog($"  + {key} ({type}) at {pos}");
    }

    SafeLog($"SpawnArmy({side}) done. Spawned={spawned}");
}


    // ---------- helpers ----------
    private void SafeLog(string msg)
    {
        if (verboseLogs)
            Debug.Log($"[ArmySpawner] {msg}");
    }
}
