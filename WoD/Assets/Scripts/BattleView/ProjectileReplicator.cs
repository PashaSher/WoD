using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Database;
using UnityEngine;

/// <summary>
/// Глобальный репликатор снарядов: подписывается на /sessions/{sid}/projectiles
/// и создаёт локальные объекты Projectile на обоих клиентах (хост/клиент).
/// Повесь на пустой объект на сцене один раз.
/// </summary>
public class ProjectileReplicator : MonoBehaviour
{
    [SerializeField] private string sessionId;

    private DatabaseReference projRootHost;
    private DatabaseReference projRootClient;
    private readonly Dictionary<string, Projectile> spawned = new();
	private static readonly Dictionary<string, float> recentLocalFireByOwner = new();
    private class PendingVisual
    {
        public Projectile projectile;
        public Vector2 start;
        public Vector2 target;
        public float timestamp;
    }
    private static readonly Dictionary<string, List<PendingVisual>> pendingVisualsByOwner = new();

	// Вызывается на клиенте в момент события анимации для подавления дублирования визуала
	public static void MarkLocalFire(string ownerKey)
	{
		if (string.IsNullOrEmpty(ownerKey)) return;
		recentLocalFireByOwner[ownerKey] = Time.time;
	}

	// Регистрируется локальный визуальный снаряд, чтобы при приходе RTDB записи не создавать дубликат, а привязать ref
    public static void RegisterLocalVisual(string ownerKey, Projectile projectile, Vector2 start, Vector2 target)
	{
		if (string.IsNullOrEmpty(ownerKey) || projectile == null) return;
        if (!pendingVisualsByOwner.TryGetValue(ownerKey, out var list))
        {
            list = new List<PendingVisual>();
            pendingVisualsByOwner[ownerKey] = list;
        }
        list.Add(new PendingVisual { projectile = projectile, start = start, target = target, timestamp = Time.time });
        recentLocalFireByOwner[ownerKey] = Time.time; // тоже отметим время для подстраховки

        // Очистим сильно старые записи
        float cutoff = Time.time - 2f;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] == null || !list[i].projectile || list[i].timestamp < cutoff)
            {
                list.RemoveAt(i);
            }
        }
	}

    private void Awake()
    {
        TryResolveSession();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoBootstrap()
    {
        try
        {
            var sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
            bool isBattle = sceneName.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0;
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            var hasSpawner = UnityEngine.Object.FindFirstObjectByType<ArmySpawner>() != null;
            var existing = UnityEngine.Object.FindFirstObjectByType<ProjectileReplicator>();
#else
            var hasSpawner = UnityEngine.Object.FindObjectOfType<ArmySpawner>() != null;
            var existing = UnityEngine.Object.FindObjectOfType<ProjectileReplicator>();
#endif
            if ((isBattle || hasSpawner) && existing == null)
            {
                var go = new GameObject("ProjectileReplicator(Auto)");
                go.AddComponent<ProjectileReplicator>();
            }
        }
        catch { /* best-effort */ }
    }

    private void OnEnable()
    {
        Attach();
    }

    private void OnDisable()
    {
        Detach();
    }

    private void TryResolveSession()
    {
        if (!string.IsNullOrEmpty(sessionId)) return;
        try
        {
            GameSession.Load();
            sessionId = GameSession.SessionId;
        }
        catch { }
    }

    private void Attach()
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var baseRef = FirebaseDatabase.DefaultInstance.RootReference
            .Child("sessions").Child(sessionId);

        projRootHost = baseRef.Child("hostArmy").Child("projectiles");
        projRootClient = baseRef.Child("clientArmy").Child("projectiles");

        projRootHost.ChildAdded += OnChildAdded;
        projRootHost.ChildRemoved += OnChildRemoved;
        projRootClient.ChildAdded += OnChildAdded;
        projRootClient.ChildRemoved += OnChildRemoved;
    }

    private void Detach()
    {
        if (projRootHost != null)
        {
            projRootHost.ChildAdded -= OnChildAdded;
            projRootHost.ChildRemoved -= OnChildRemoved;
            projRootHost = null;
        }
        if (projRootClient != null)
        {
            projRootClient.ChildAdded -= OnChildAdded;
            projRootClient.ChildRemoved -= OnChildRemoved;
            projRootClient = null;
        }
    }

    private void OnChildAdded(object sender, ChildChangedEventArgs e)
    {
        var s = e.Snapshot; if (s == null || !s.Exists) return;
        string key = s.Key;
        string branchMark = (sender == (object)projRootHost) ? "H" : (sender == (object)projRootClient ? "C" : "U");
        string dictKey = branchMark + ":" + key;
        if (spawned.ContainsKey(dictKey)) return;

        // Если это наша ветка (hostArmy на хосте, clientArmy на клиенте) — локальный снаряд уже создан стрелком
        bool ourBranch = (sender == (object)projRootHost && Globalflags.ifHost) || (sender == (object)projRootClient && !Globalflags.ifHost);
        if (ourBranch) return;

        // прочитаем требуемые поля
        Vector2 start = new Vector2(ToFloat(s.Child("startX").Value), ToFloat(s.Child("startY").Value));
        Vector2 target = new Vector2(ToFloat(s.Child("targetX").Value), ToFloat(s.Child("targetY").Value));
        float speed = ToFloat(s.Child("speed").Value, 8f);
        int damage = ToInt(s.Child("damage").Value, 10);
        int penetration = ToInt(s.Child("penetration").Value, 0);
        float splash = ToFloat(s.Child("splash").Value, 0f);
        float scaleX = ToFloat(s.Child("scaleX").Value, 1f);
        float scaleY = ToFloat(s.Child("scaleY").Value, 1f);
        bool ownerIsHost = ToBool(s.Child("host").Value, false);
        string ownerKey = s.Child("ownerKey").Value?.ToString();

		// Если этот снаряд принадлежит нашей стороне по данным снапшота — пропускаем локальное создание,
        // т.к. владелец уже отрисовал его локально.
        bool snapshotSaysOwn = Globalflags.ifHost == ownerIsHost;
		if (snapshotSaysOwn)
		{
			return;
		}

        // Если есть локальные визуалы у этого владельца — найдём лучший матч по стартовой позиции/времени и привяжем RTDB к нему
        if (!string.IsNullOrEmpty(ownerKey) && pendingVisualsByOwner.TryGetValue(ownerKey, out var candidates) && candidates != null && candidates.Count > 0)
        {
            int bestIndex = -1;
            float bestScore = float.PositiveInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                var pv = candidates[i];
                if (pv == null || !pv.projectile) continue;
                float dist = Vector2.Distance(pv.start, start);
                // Простой скоринг: расстояние + небольшой штраф за время
                float timePenalty = Mathf.Abs(Time.time - pv.timestamp) * 0.1f;
                float score = dist + timePenalty;
                if (score < bestScore)
                {
                    bestScore = score; bestIndex = i;
                }
            }
            if (bestIndex >= 0)
            {
                var chosen = candidates[bestIndex];
                chosen.projectile.BindRef(s.Reference);
                string existingKey = branchMark + ":" + key;
                spawned[existingKey] = chosen.projectile;
                candidates.RemoveAt(bestIndex);
                // Вспышка уже проиграна локально
                return;
            }
            // Если кандидаты были, но все мёртвые — почистим список
            candidates.RemoveAll(pv => pv == null || !pv.projectile);
        }

        // Как запасной вариант: если очень недавно был локальный ивент — не создавать второй визуал
        if (!string.IsNullOrEmpty(ownerKey) && recentLocalFireByOwner.TryGetValue(ownerKey, out var t2) && (Time.time - t2) <= 0.4f)
        {
            return;
        }

        // Создаём снаряд на стороне НЕ-владельца по данным RTDB.
        // Это нужно, чтобы HOST просчитывал урон (Projectile.Update делает damage только на HOST),
        // а CLIENT видел корректный визуал от выстрела HOST.
        try
        {
            // найдём владельца по ключу (для определения стороны/ally checks)
#if UNITY_2022_2_OR_NEWER
            var units = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
#else
            var units = UnityEngine.Object.FindObjectsOfType<Unit>(true);
#endif
            Unit ownerUnit = null;
            for (int i = 0; i < units.Length; i++)
            {
                var u = units[i];
                if (!u) continue;
                if (!string.Equals(u.unitKey, ownerKey, StringComparison.Ordinal)) continue;
                if (u.host == ownerIsHost) { ownerUnit = u; break; }
            }
            if (ownerUnit == null)
            {
                Debug.LogWarning($"[PR] Owner unit '{ownerKey}' not found for projectile '{key}'. Skipping spawn.");
                return;
            }

            // Сконструируем временный ProjectileStats из снапшота
            var stats = ScriptableObject.CreateInstance<ProjectileStats>();
            stats.speed = speed;
            stats.damage = damage;
            stats.penetration = penetration;
            stats.splashRadius = splash;
            stats.scale = new Vector2(scaleX, scaleY);
            // Визуальные поля берём из статов владельца, если доступны
            if (ownerUnit != null && ownerUnit.projectileStats != null)
            {
                try
                {
                    var os = ownerUnit.projectileStats;
                    stats.sprite = os.sprite;
                    stats.destroySprite = os.destroySprite;
                    stats.destroyDuration = os.destroyDuration;
                    stats.destroyScale = os.destroyScale;
                    // если в снапшоте scale не задан (0), используем визуальный scale из ос
                    if (Mathf.Approximately(stats.scale.x, 0f) && Mathf.Approximately(stats.scale.y, 0f))
                        stats.scale = os.scale;
                }
                catch { }
            }

            // Создаём объект и инициализируем
            var go = new GameObject($"Projectile_{key}_RTDB");
            go.transform.position = start;
            var proj = go.AddComponent<Projectile>();
            proj.Init(ownerUnit, stats, key, start, target, createdByLocal: false);
            proj.BindRef(s.Reference);

            // Запомним, чтобы корректно обработать удаление
            spawned[dictKey] = proj;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ProjectileReplicator] Spawn from RTDB failed: {ex.Message}");
        }
    }

    private void OnChildRemoved(object sender, ChildChangedEventArgs e)
    {
        var s = e.Snapshot; if (s == null) return;
        string key = s.Key;
        string branchMark = (sender == (object)projRootHost) ? "H" : (sender == (object)projRootClient ? "C" : "U");
        string dictKey = branchMark + ":" + key;
        if (spawned.TryGetValue(dictKey, out var proj) && proj)
        {
            proj.BeginDeath();
        }
        spawned.Remove(dictKey);
    }

    private static float ToFloat(object v, float def = 0f)
    {
        try { return v == null ? def : Convert.ToSingle(v); } catch { return def; }
    }
    private static int ToInt(object v, int def = 0)
    {
        try { return v == null ? def : Convert.ToInt32(v); } catch { return def; }
    }

    private static bool ToBool(object v, bool def = false)
    {
        try
        {
            if (v is bool b) return b;
            if (v is long l) return l != 0;
            if (v is int i) return i != 0;
            if (v is string s)
            {
                if (bool.TryParse(s, out var bs)) return bs;
                if (long.TryParse(s, out var ls)) return ls != 0;
            }
            return def;
        }
        catch { return def; }
    }

    private Unit FindOwnerUnit(string ownerKey)
    {
        if (string.IsNullOrEmpty(ownerKey)) return null;
        var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
        foreach (var u in all)
        {
            if (u && u.unitKey == ownerKey) return u;
        }
        return null;
    }

    private IEnumerator TryPlayFlashLater(string ownerKey, float timeoutSeconds)
    {
        float deadline = Time.time + Mathf.Max(0.05f, timeoutSeconds);
        while (Time.time < deadline)
        {
            var u = FindOwnerUnit(ownerKey);
            if (u)
            {
                var ctrl = u.GetComponentInChildren<MuzzleFlashController>(true);
                if (ctrl != null)
                {
                    ctrl.PlayFlash(0.5f);
                    yield break;
                }
            }
            yield return null;
        }
    }
}


