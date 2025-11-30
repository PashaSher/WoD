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
    private DatabaseReference hostArmyRef;
    private DatabaseReference clientArmyRef;
    private readonly Dictionary<string, GameObject> unitByKey = new(); // ключ формата "host:{key}" / "client:{key}"

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
        if (!string.IsNullOrEmpty(sessionId))
        {
            hostArmyRef = root.Child("sessions").Child(sessionId).Child("hostArmy");
            clientArmyRef = root.Child("sessions").Child(sessionId).Child("clientArmy");
        }

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
        // Подпишемся только после первичной загрузки, чтобы обработать удаления в реальном времени
        AttachRemovalListeners();
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
            var prefabToUse = GetPrefabForType(type);
            var go = Instantiate(prefabToUse, pos, Quaternion.identity, unitsParent);

            // --- ВИЗУАЛ И СТАТЫ ---
            var visualTr = go.transform.Find("Visual");
            if (visualTr == null)
            {
                Debug.LogError("[ArmySpawner] 'Visual' child NOT found in Unit_Root prefab.");
                Destroy(go);
                continue;
            }

            // Гарантируем наличие рендереров на визуале
            var anim = visualTr.GetComponent<Animator>(); // может быть null
            var renderers = visualTr.GetComponentsInChildren<SpriteRenderer>(true);
            SpriteRenderer sr = null;
            if (renderers != null && renderers.Length > 0)
            {
                sr = renderers[0];
            }
            else
            {
                // ни одного рендера внутри Visual — добавим на сам Visual
                sr = visualTr.GetComponent<SpriteRenderer>() ?? visualTr.gameObject.AddComponent<SpriteRenderer>();
                renderers = new SpriteRenderer[] { sr };
            }

            Sprite appliedSprite = null;
            UnitStats stats = null;
            statsByType.TryGetValue(type, out stats);

            if (stats != null)
            {
                // если есть override-контроллер — используем анимации
                if (anim != null && stats.animatorOverride != null)
                {
                    anim.runtimeAnimatorController = stats.animatorOverride;
                    anim.enabled = true;
                }
                else
                {
                    // Нет override из UnitStats. В бою оставляем визуал из ПРЕФАБА.
                    // Если у префаба уже назначен контроллер — используем его;
                    // иначе ничего не подменяем спрайтом из UnitStats (sprite используется только в магазине).
                    if (anim != null && anim.runtimeAnimatorController != null)
                        {
                        anim.enabled = true;
                        }
                    else if (anim != null)
                    {
                        anim.enabled = false;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"[ArmySpawner] No stats for type={type} (key={key}). Using prefab-only defaults.");
            }

            // Инициализировать компонент Unit: всегда проставляем unitType,
            // а если есть статсы — прокинем их, чтобы HP/урон корректно инициализировались.
            var unit = go.GetComponent<Unit>();
            if (unit != null)
            {
                if (stats != null) unit.Init(type.ToString(), stats);
                else               unit.unitType = type.ToString(); // хотя бы корректный meta.type в RTDB
            }

            // Гарантируем видимость поверх фона + цвет на ВСЕХ рендерах в Visual
            var tint = isHostBranch ? Color.black : Color.blue;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                r.enabled = true;
                r.color = tint;
                if (r.sortingOrder < 5) r.sortingOrder = 5;
            }

            // Зеркалим ТОЛЬКО визуал (арт), не корень с коллайдерами
            var s = visualTr.localScale;
            s.x = Mathf.Abs(s.x) * (side == Side.Right ? 1f : -1f);
            visualTr.localScale = s;

            // Метаданные в RTDB (best-effort)
            var unitMeta = go.GetComponent<Unit>();
            if (unitMeta != null)
            {
                try
                {
                    unitMeta.SetFirebaseContextAndPush(sessionId, isHostBranch, key);
                    // гарантируем кликабельность
                     EnsureClickable(go);
                    // Аниматор-синхронизатор (подтянет Animator из Visual)
                    if (!go.GetComponent<UnitAnimator>())
                        go.AddComponent<UnitAnimator>();
                }

                catch (Exception ex) { Debug.LogWarning($"[ArmySpawner] meta write skip for '{key}': {ex.Message}"); }
            }

            // Цвет кольца (если есть)
            var ring = go.transform.Find("SelectionRing")?.GetComponent<SpriteRenderer>();
            if (ring != null) ring.color = isHostBranch ? Color.cyan : Color.red;

            // Имя для наглядности
            go.name = $"{type}_{key}";
            SafeLog($"  + {go.name} at {pos} (sprite='{appliedSprite?.name}')");

            // Привяжем к карте для быстрых удалений по событию RTDB
            string mapKey = (isHostBranch ? "host" : "client") + ":" + key;
            if (!unitByKey.ContainsKey(mapKey))
                unitByKey.Add(mapKey, go);
            else
                unitByKey[mapKey] = go;

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

    private void EnsureClickable(GameObject go)
{
    var visualTr = go.transform.Find("Visual");
    if (!visualTr) return;

    // Коллайдер (для тапа)
    var poly = visualTr.GetComponent<PolygonCollider2D>();
    var box  = visualTr.GetComponent<BoxCollider2D>();
    if (!poly && !box)
    {
        // проще: прямоугольник
        box = visualTr.gameObject.AddComponent<BoxCollider2D>();
        box.isTrigger = true;
        // подстроим размер под спрайт, если есть
        var sr = visualTr.GetComponent<SpriteRenderer>();
        if (sr && sr.sprite)
            box.size = sr.sprite.bounds.size;
    }
    else
    {
        if (poly) poly.isTrigger = true;
        if (box)  box.isTrigger  = true;
    }

        // Обработчик перетаскивания/записи конечной точки — только для НЕ стационарных
        var u = go.GetComponent<Unit>();
        bool allowDrag = (u != null && !u.isStationary && u.moveSpeed > 0.01f);
        if (allowDrag)
        {
    if (!visualTr.GetComponent<UnitDragMover>())
        visualTr.gameObject.AddComponent<UnitDragMover>();
        }
}


    // ---------- helpers ----------
    private GameObject GetPrefabForType(UnitType type)
    {
        // 1) Префаб из UnitStats, если указан
        if (statsByType.TryGetValue(type, out var s) && s != null && s.unitPrefab != null)
            return s.unitPrefab;

        // 2) Fallback: Resources/Units/{Type} или {Type}_Prefab
        var res = Resources.Load<GameObject>($"Units/{type}");
        if (res != null) return res;
        res = Resources.Load<GameObject>($"Units/{type}_Prefab");
        if (res != null) return res;

        // 3) Базовый префаб
        return unitRootPrefab;
    }
    private void SafeLog(string msg)
    {
        if (verboseLogs)
            Debug.Log($"[ArmySpawner] {msg}");
    }

    // ---------- RTDB listeners for deletions ----------
    private void AttachRemovalListeners()
    {
        try
        {
            if (hostArmyRef != null)
            {
                hostArmyRef.ChildRemoved -= OnHostUnitRemoved;
                hostArmyRef.ChildRemoved += OnHostUnitRemoved;
            }
            if (clientArmyRef != null)
            {
                clientArmyRef.ChildRemoved -= OnClientUnitRemoved;
                clientArmyRef.ChildRemoved += OnClientUnitRemoved;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ArmySpawner] AttachRemovalListeners exception: {ex.Message}");
        }
    }

    private void OnDestroy()
    {
        try
        {
            if (hostArmyRef != null) hostArmyRef.ChildRemoved -= OnHostUnitRemoved;
            if (clientArmyRef != null) clientArmyRef.ChildRemoved -= OnClientUnitRemoved;
        }
        catch { }
    }

    private void OnHostUnitRemoved(object sender, ChildChangedEventArgs e) => HandleUnitRemoved("host", e);
    private void OnClientUnitRemoved(object sender, ChildChangedEventArgs e) => HandleUnitRemoved("client", e);

    private void HandleUnitRemoved(string branch, ChildChangedEventArgs e)
    {
        try
        {
            var key = e?.Snapshot?.Key;
            if (string.IsNullOrEmpty(key)) return;
            string mapKey = $"{branch}:{key}";
            if (unitByKey.TryGetValue(mapKey, out var go))
            {
                if (go) Destroy(go);
                unitByKey.Remove(mapKey);
                SafeLog($"Removed unit '{mapKey}' by RTDB delete.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ArmySpawner] HandleUnitRemoved exception: {ex.Message}");
        }
    }
}
