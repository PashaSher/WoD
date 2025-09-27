using System;
using System.Collections.Generic;
using Firebase.Database;
using UnityEngine;

public class Unit : MonoBehaviour
{
    [Header("Runtime (identity)")]
    [SerializeField] public string unitKey;        // например, "Rifleman_0"
    [SerializeField] public string sessionId;      // ID сессии
    [SerializeField] public bool   host;           // true -> hostArmy, false -> clientArmy

    [Header("Stats")]
    public string unitType;                        // "Rifleman" / "Grenader" / ...
    public int    health;
    public int    damage;
    public float  attackRange;
    public float  moveSpeed;
    public int    maxHP;                           // добавили для state

    [Header("Refs (optional)")]
    [SerializeField] private Transform   visual;   // child "Visual" (для зеркала)
    [SerializeField] private Rigidbody2D rb;       // если двигаешь физикой

    // --- Firebase ---
    private DatabaseReference unitRef;

    // --- локальный снимок ---
    private struct Snapshot
    {
        public Vector2 pos;
        public int     hp;
        public int     facing;   // 1 / -1
        public bool    moving;
        public bool    attacking;
    }
    private Snapshot lastSent;
    private float nextSyncTime;
    private const float SYNC_INTERVAL = 0.10f; // 10 Гц

    // флаг атаки
    private bool attacking;

    // --- кросс-версионная скорость Rb2D ---
    private Vector2 RBVel()
    {
        if (rb == null) return Vector2.zero;
#if UNITY_6000_0_OR_NEWER || UNITY_2023_3_OR_NEWER
        return rb.linearVelocity;
#else
        return rb.velocity;
#endif
    }

    // ----------------------------------------------------
    // Инициализация статами (вызывает спавнер)
    // ----------------------------------------------------
    public void Init(string type, UnitStats stats)
    {
        unitType = type;
        if (stats != null)
        {
            health      = stats.health;
            maxHP       = stats.health;  // стартовое maxHP = health (можно менять в геймплее)
            damage      = stats.damage;
            attackRange = stats.attackRange;
            moveSpeed   = stats.moveSpeed;
        }

        if (visual == null) visual = transform.Find("Visual");
        if (rb == null)     rb     = GetComponent<Rigidbody2D>();

        attacking = false;
        lastSent  = Capture(); // стартовый снэпшот
    }

    // ----------------------------------------------------
    // Привязка к Firebase и первичная запись МЕТА (без hp/maxHP)
    // ----------------------------------------------------
    public async void SetFirebaseContextAndPush(string sessionId, bool host, string unitKey)
    {
        this.sessionId = sessionId;
        this.host      = host;
        this.unitKey   = unitKey;

        string branch = host ? "hostArmy" : "clientArmy";
        unitRef = FirebaseDatabase.DefaultInstance.RootReference
            .Child("sessions").Child(sessionId)
            .Child(branch).Child(unitKey);

        // Пишем только метаданные (hp/maxHP убрали отсюда)
        var meta = new Dictionary<string, object>
        {
            ["type"]      = unitType,
            ["host"]      = host,
            ["sessionId"] = sessionId,
            ["createdAt"] = ServerValue.Timestamp,
            ["updatedAt"] = ServerValue.Timestamp
        };
        await unitRef.UpdateChildrenAsync(meta);

        // первичный state
        PushState(Capture());

        // слушатель удалённых изменений state (если нужно принимать чужие правки)
        unitRef.Child("state").ValueChanged += OnRemoteStateChanged;
    }

    private void OnDestroy()
    {
        if (unitRef != null)
            unitRef.Child("state").ValueChanged -= OnRemoteStateChanged;
    }

    // ---------------------- Геймплей ---------------------
    public void TakeDamage(int amount)
    {
        health = Mathf.Max(0, health - Mathf.Abs(amount));
        nextSyncTime = 0f;
        if (health == 0) Destroy(gameObject);
    }

    public void Heal(int amount)
    {
        health = Mathf.Clamp(health + Mathf.Abs(amount), 0, maxHP);
        nextSyncTime = 0f;
    }

    public void StartAttack() => SetAttacking(true);
    public void StopAttack()  => SetAttacking(false);
    public void SetAttacking(bool value)
    {
        if (attacking != value)
        {
            attacking = value;
            nextSyncTime = 0f; // форснем ближайший пуш
        }
    }

    // ---------------------- Синхронизация ----------------
    private void Update()
    {
        if (unitRef == null) return;

        var cur = Capture();

        if (Time.time >= nextSyncTime && HasChanged(cur, lastSent))
        {
            PushState(cur);
            lastSent    = cur;
            nextSyncTime = Time.time + SYNC_INTERVAL;
        }
    }

    private Snapshot Capture()
    {
        return new Snapshot
        {
            pos       = transform.position,
            hp        = health,
            facing    = GetFacing(),
            moving    = IsMoving(),
            attacking = attacking
        };
    }

    private bool HasChanged(Snapshot a, Snapshot b)
    {
        if (a.hp        != b.hp)        return true;
        if (a.facing    != b.facing)    return true;
        if (a.moving    != b.moving)    return true;
        if (a.attacking != b.attacking) return true;
        if ((a.pos - b.pos).sqrMagnitude > 0.0004f) return true; // позиция
        return false;
    }

    private Dictionary<string, object> BuildStateDict(Snapshot s)
    {
        return new Dictionary<string, object>
        {
            // позиция
            ["x"]        = (double)s.pos.x,
            ["y"]        = (double)s.pos.y,

            // боевое состояние
            ["hp"]       = s.hp,
            ["maxHP"]    = maxHP,             // <-- теперь в state
            ["facing"]   = s.facing,          // 1 / -1
            ["moving"]   = s.moving,
            ["attacking"]= s.attacking,       // <-- флаг атаки

            // служебное
            ["updatedAt"]= ServerValue.Timestamp
        };
    }

    private async void PushState(Snapshot s)
    {
        if (unitRef == null) return;
        await unitRef.Child("state").UpdateChildrenAsync(BuildStateDict(s));
        await unitRef.Child("updatedAt").SetValueAsync(ServerValue.Timestamp);
    }

    private void OnRemoteStateChanged(object sender, ValueChangedEventArgs e)
    {
        if (e.DatabaseError != null) return;
        if (e.Snapshot == null || !e.Snapshot.Exists) return;

        float x      = ToFloat(e.Snapshot.Child("x").Value, transform.position.x);
        float y      = ToFloat(e.Snapshot.Child("y").Value, transform.position.y);
        int   rHp    = ToInt  (e.Snapshot.Child("hp").Value, health);
        int   rMaxHP = ToInt  (e.Snapshot.Child("maxHP").Value, maxHP);
        int   facing = ToInt  (e.Snapshot.Child("facing").Value, GetFacing());
        bool  moving = ToBool (e.Snapshot.Child("moving").Value, false);
        bool  rAtk   = ToBool (e.Snapshot.Child("attacking").Value, attacking);

        // применяем позицию/HP/атаку
        transform.position = new Vector3(x, y, transform.position.z);
        health = Mathf.Clamp(rHp, 0, rMaxHP);
        maxHP  = rMaxHP;
        attacking = rAtk;

        // разворот только визуала
        if (visual != null)
        {
            var s = visual.localScale;
            s.x = Mathf.Abs(s.x) * (facing >= 0 ? 1f : -1f);
            visual.localScale = s;
        }

        lastSent = Capture(); // чтобы не отослать то же самое назад
    }

    // ---------------------- Вспомогательные --------------
    private bool IsMoving()
    {
        if (rb != null) return RBVel().sqrMagnitude > 0.01f;
        return (transform.hasChanged && (transform.position - (Vector3)lastSent.pos).sqrMagnitude > 0.0001f);
    }

    private int GetFacing()
    {
        if (visual == null) return 1;
        return (visual.localScale.x >= 0f) ? 1 : -1;
    }

    private static float ToFloat(object v, float def) { return v == null ? def : Convert.ToSingle(v); }
    private static int   ToInt  (object v, int   def) { return v == null ? def : Convert.ToInt32(v); }
    private static bool  ToBool (object v, bool  def) { return v == null ? def : Convert.ToBoolean(v); }

    // ---- DEBUG getters для UnitDebugInfo ----
    public Vector2 PosDebug   => (Vector2)transform.position;
    public bool    MovingDebug => RBVel().sqrMagnitude > 0.01f;
    public int     FacingDebug
    {
        get
        {
            var vis = visual != null ? visual : transform.Find("Visual");
            return (vis != null && vis.localScale.x < 0f) ? -1 : 1;
        }
    }
}
