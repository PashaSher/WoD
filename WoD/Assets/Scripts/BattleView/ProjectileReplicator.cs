using System;
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

    private void Awake()
    {
        TryResolveSession();
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

        // прочитаем требуемые поля
        Vector2 start = new Vector2(ToFloat(s.Child("startX").Value), ToFloat(s.Child("startY").Value));
        Vector2 target = new Vector2(ToFloat(s.Child("targetX").Value), ToFloat(s.Child("targetY").Value));
        float speed = ToFloat(s.Child("speed").Value, 8f);
        int damage = ToInt(s.Child("damage").Value, 10);
        int penetration = ToInt(s.Child("penetration").Value, 0);
        float splash = ToFloat(s.Child("splash").Value, 0f);

        // сделаем простые Projectiles без отдельного SO, т.к. не знаем тип — скорость уже пришла
        var ownerUnit = FindOwnerUnit(s.Child("ownerKey").Value?.ToString());
        var go = new GameObject($"Projectile_{key}");
        var proj = go.AddComponent<Projectile>();

        // сконструируем временный ProjectileStats
        var ps = ScriptableObject.CreateInstance<ProjectileStats>();
        ps.speed = speed; ps.damage = damage; ps.penetration = penetration; ps.splashRadius = splash;
        // Назначим спрайт из настроек владельца, иначе на удалённом клиенте снаряд будет невидим
        if (ownerUnit != null && ownerUnit.projectileStats != null)
        {
            ps.sprite = ownerUnit.projectileStats.sprite;
        }
        proj.Init(ownerUnit, ps, key, start, target, createdByLocal: false);
        // Привяжем ссылку прямо к снапшоту, чтобы корректно удалять из нужной ветки
        proj.BindRef(s.Reference);

        spawned[dictKey] = proj;
    }

    private void OnChildRemoved(object sender, ChildChangedEventArgs e)
    {
        var s = e.Snapshot; if (s == null) return;
        string key = s.Key;
        string branchMark = (sender == (object)projRootHost) ? "H" : (sender == (object)projRootClient ? "C" : "U");
        string dictKey = branchMark + ":" + key;
        if (spawned.TryGetValue(dictKey, out var proj) && proj)
        {
            Destroy(proj.gameObject);
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
}


