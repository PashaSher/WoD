using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Database;
using UnityEngine;

/// <summary>
/// Спавнит юнитов из Firebase-сессии. Всегда инстансит один базовый префаб Unit_Root,
/// а внешний вид/анимации и характеристики берёт из UnitStats по типу юнита.
/// ВАЖНО: в префабе Unit_Root должен быть дочерний объект "Visual" с SpriteRenderer
/// (и опционально Animator).
/// В UnitStats должны быть поля: unitType, sprite (Sprite), animatorOverride (AnimatorOverrideController, опц.).
/// </summary>
public class ArmySpawner : MonoBehaviour
{
    [Header("Prefabs")]
    [SerializeField] private GameObject unitRootPrefab;   // базовый префаб Unit_Root

    [Header("Stats (1 asset per type)")]
    [SerializeField] private List<UnitStats> unitStatsList; // Rifleman/Grenader/Sniper/Tank

    [Header("References")]
    [SerializeField] private Transform unitsParent;   // контейнер "Units" (если не задан — создадим)
    [SerializeField] private bool ifHost;             // роль (может подхватиться из Globalflags)
    [SerializeField] private string sessionId;        // ID сессии (может подхватиться из GameSession)

    [Header("Debug")]
    [SerializeField] private bool verboseLogs = true; // подробные логи

    private DatabaseReference root;
    private readonly Dictionary<UnitType, UnitStats> statsByType = new();

    private enum Side { Left, Right }

    // ---------- lifecycle ----------
    private void Awake()
    {
        SafeLog("Awake() start");

        // контекст — если есть
        try
        {
            GameSession.Load();
            if (!string.IsNullOrEmpty(GameSession.SessionId))
                sessionId = GameSession.SessionId;
        }
        catch { /* ignore */ }

        try { ifHost = Globalflags.ifHost; } catch { /* ignore */ }

        // контейнер Units
        if (unitsParent == null)
        {
            var go = GameObject.Find("Units") ?? new GameObject("Units");
            unitsParent = go.transform;
            SafeLog("Units parent auto-created/attached");
        }

        // словарь статов по типу
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
        SafeLog($"Start() | sessionId='{sessionId}', prefab='{(unitRootPrefab ? unitRootPrefab.name : "NULL")}', stats={statsByType.Count}");

        root = FirebaseDatabase.DefaultInstance.RootReference;

        if (unitRootPrefab == null)
        {
            Debug.LogError("[ArmySpawner] UnitRootPrefab не задан — спавн невозможен.");
            return;
        }
        // защита от ссылки на объект сцены вместо ассета префаба
        if (unitRootPrefab.scene.IsValid())
        {
            Debug.LogError("[ArmySpawner] UnitRootPrefab указывает на объект в сцене. Перетащи ПРЕФАБ (синий кубик) из Project.");
            return;
        }
        if (string.IsNullOrEmpty(sessionId))
        {
            Debug.LogError("[ArmySpawner] sessionId пуст — укажи ID сессии.");
            return;
        }

        await LoadAndSpawn();
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

        bool isHostBranch = (side == Side.Left); // hostArmy слева
        int spawned = 0;

        int valid = 0;
        foreach (var c in armySnap.Children)
            if (c.HasChildren && c.HasChild("type")) valid++;

        SafeLog($"SpawnArmy({side}) begin at x={x:F2}, yStart={y:F2} | nodes={armySnap.ChildrenCount}, units(with type)={valid}");

        foreach (var child in armySnap.Children)
        {
            string key = child.Key;

            if (!child.HasChildren || !child.HasChild("type")) continue;

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

            // --- ВИЗУАЛ И СТАТЫ ---
            var visualTr = go.transform.Find("Visual");
            if (visualTr == null)
            {
                Debug.LogError("[ArmySpawner] 'Visual' child NOT found in Unit_Root prefab.");
                Destroy(go);
                continue;
            }

            // гарантируем наличие SpriteRenderer
            var sr   = visualTr.GetComponent<SpriteRenderer>() ?? visualTr.gameObject.AddComponent<SpriteRenderer>();
            var anim = visualTr.GetComponent<Animator>(); // может быть null

            Sprite appliedSprite = null;
            if (statsByType.TryGetValue(type, out var stats) && stats != null)
            {
                // если есть override-контроллер — используем анимации
                if (anim != null && stats.animatorOverride != null)
                {
                    anim.runtimeAnimatorController = stats.animatorOverride;
                    anim.enabled = true;
                }
                else
                {
                    if (anim != null) { anim.runtimeAnimatorController = null; anim.enabled = false; }
                    if (stats.sprite != null) { sr.sprite = stats.sprite; appliedSprite = stats.sprite; }
                }

                // Инициализировать компонент Unit статами
                var unit = go.GetComponent<Unit>();
                if (unit != null) unit.Init(type.ToString(), stats);
            }
            else
            {
                Debug.LogWarning($"[ArmySpawner] No stats for type={type} (key={key}). Visual will stay NONE.");
            }

            // гарантируем видимость поверх фона
            sr.enabled = true;
            sr.color = Color.white;
            if (sr.sortingOrder < 5) sr.sortingOrder = 5;

            // Зеркалим ТОЛЬКО визуал (арт), не корень с коллайдерами
            var s = visualTr.localScale;
            s.x = Mathf.Abs(s.x) * (side == Side.Right ? -1f : 1f);
            visualTr.localScale = s;

            // Метаданные в RTDB (best-effort)
            var unitMeta = go.GetComponent<Unit>();
            if (unitMeta != null)
            {
                try { unitMeta.SetFirebaseContextAndPush(sessionId, isHostBranch, key); }
                catch (Exception ex) { Debug.LogWarning($"[ArmySpawner] meta write skip for '{key}': {ex.Message}"); }
            }

            // Цвет кольца (если есть)
            var ring = go.transform.Find("SelectionRing")?.GetComponent<SpriteRenderer>();
            if (ring != null) ring.color = isHostBranch ? Color.cyan : Color.red;

            // Имя для наглядности
            go.name = $"{type}_{key}";
            SafeLog($"  + {go.name} at {pos} (sprite='{appliedSprite?.name}')");

            // Следующее место
            y -= 1.5f;
            if (y < -halfH + 1f)
            {
                y = halfH - 1f;
                x += (side == Side.Left ? +1.6f : -1.6f);
            }

            spawned++;
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
