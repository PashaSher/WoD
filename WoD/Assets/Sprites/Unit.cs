using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Database;
using UnityEngine;

public class Unit : MonoBehaviour
{
    // ===== Identity (runtime) =====
    [Header("Runtime (identity)")]
    [SerializeField] public string unitKey;    // e.g. "Rifleman_0"
    [SerializeField] public string sessionId;  // session id
    [SerializeField] public bool   host;       // true -> hostArmy, false -> clientArmy

    // ===== Stats =====
    [Header("Stats")]
    public string unitType;     // "Rifleman" / "Grenader" / ...
    public int    health;
    public int    damage;
    public float  attackRange;
    public float  moveSpeed;
    public int    maxHP;

    // ===== Refs =====
    [Header("Refs (optional)")]
    [SerializeField] private Transform   visual;   // child "Visual"
    [SerializeField] private Rigidbody2D rb;       // optional

    // ===== Firebase =====
    private DatabaseReference unitRef;   // .../sessions/{sid}/{branch}/{unitKey}
    private DatabaseReference stateRef;  // .../state

    // ===== Local move playback for remote commands =====
    private Coroutine moveCo;

    // ===== Combat sync (hp/attacking/facing only) =====
    private struct CombatSnapshot
    {
        public int  hp;
        public int  facing;     // 1 / -1
        public bool attacking;
    }

    private CombatSnapshot _combatLast;
    private float _nextCombatSyncTime;
    private bool  _forceCombatPush;
    private bool  _maxHpDirty;                 // push maxHP once at start or on explicit change
    private const float COMBAT_SYNC_INTERVAL = 0.10f; // push at most 10 Hz

    // ====== Public API ======
    public void Init(string type, UnitStats stats)
    {
        unitType = type;
        if (stats != null)
        {
            health      = stats.health;
            maxHP       = stats.health;
            damage      = stats.damage;
            attackRange = stats.attackRange;
            moveSpeed   = stats.moveSpeed;
        }

        if (visual == null) visual = transform.Find("Visual");
        if (rb == null)     rb     = GetComponent<Rigidbody2D>();

        _combatLast  = CaptureCombat();
        _maxHpDirty  = true;       // отправим maxHP один раз после бинда к RTDB
        _forceCombatPush = true;   // инициализирующий пуш
    }

    public async void SetFirebaseContextAndPush(string sessionId, bool host, string unitKey)
    {
        this.sessionId = sessionId;
        this.host      = host;
        this.unitKey   = unitKey;

        string branch = host ? "hostArmy" : "clientArmy";
        unitRef = FirebaseDatabase.DefaultInstance.RootReference
            .Child("sessions").Child(sessionId)
            .Child(branch).Child(unitKey);

        stateRef = unitRef.Child("state");

        // метаданные (без координат!)
        var meta = new Dictionary<string, object>
        {
            ["type"]      = unitType,
            ["host"]      = host,
            ["sessionId"] = sessionId,
            ["createdAt"] = ServerValue.Timestamp,
            ["updatedAt"] = ServerValue.Timestamp
        };
        await unitRef.UpdateChildrenAsync(meta);

        // первичный боевой state (включая maxHP один раз)
        PushCombatState(includeMaxHP: _maxHpDirty);
        _maxHpDirty = false;

        // подписка на состояние
        stateRef.ValueChanged += OnRemoteStateChanged;
    }

    private void OnDestroy()
    {
        if (stateRef != null) stateRef.ValueChanged -= OnRemoteStateChanged;
    }

    // ====== Gameplay (hp / attack flags) ======
    private bool attacking;

    public void TakeDamage(int amount)
    {
        health = Mathf.Max(0, health - Mathf.Abs(amount));
        _forceCombatPush = true;
        if (health == 0) Destroy(gameObject);
    }

    public void Heal(int amount)
    {
        health = Mathf.Clamp(health + Mathf.Abs(amount), 0, maxHP);
        _forceCombatPush = true;
    }

    public void StartAttack() => SetAttacking(true);
    public void StopAttack()  => SetAttacking(false);
    public void SetAttacking(bool value)
    {
        if (attacking != value)
        {
            attacking = value;
            _forceCombatPush = true;
        }
    }

    /// <summary>Явленно изменить maxHP и отправить в RTDB (редкий случай).</summary>
    public void SetMaxHP(int newMax)
    {
        if (newMax <= 0 || newMax == maxHP) return;
        maxHP = newMax;
        if (health > maxHP) health = maxHP;
        _maxHpDirty = true;
        _forceCombatPush = true;
        PushCombatState(includeMaxHP: true);
        _maxHpDirty = false;
    }

    // ====== Outbound combat sync (no position/moving!) ======
    private void Update()
    {
        if (stateRef == null) return;

        var cur = CaptureCombat();
        bool changed = HasCombatChanged(cur, _combatLast);

        if (_forceCombatPush || (changed && Time.time >= _nextCombatSyncTime))
        {
            PushCombatState(includeMaxHP: false); // maxHP не шлём тут
            _combatLast = cur;
            _nextCombatSyncTime = Time.time + COMBAT_SYNC_INTERVAL;
            _forceCombatPush = false;
        }
    }

    private CombatSnapshot CaptureCombat() => new CombatSnapshot
    {
        hp        = health,
        facing    = GetFacing(),
        attacking = attacking
    };

    private static bool HasCombatChanged(CombatSnapshot a, CombatSnapshot b) =>
        a.hp != b.hp || a.facing != b.facing || a.attacking != b.attacking;

    private async void PushCombatState(bool includeMaxHP)
    {
        if (stateRef == null) return;

        var dict = new Dictionary<string, object>
        {
            ["hp"]        = health,
            ["facing"]    = GetFacing(),
            ["attacking"] = attacking,
            ["updatedAt"] = ServerValue.Timestamp
        };
        if (includeMaxHP) dict["maxHP"] = maxHP;

        await stateRef.UpdateChildrenAsync(dict);
        await unitRef.Child("updatedAt").SetValueAsync(ServerValue.Timestamp);
    }

    // ====== Inbound sync: consume movement + combat ======
    private void OnRemoteStateChanged(object sender, ValueChangedEventArgs e)
    {
        if (e.DatabaseError != null || e.Snapshot == null || !e.Snapshot.Exists) return;

        var s = e.Snapshot;

        // Всегда применяем боевые поля
        int   rHp    = ToInt  (s.Child("hp").Value, health);
        int   rMaxHP = ToInt  (s.Child("maxHP").Value, maxHP);
        int   facing = ToInt  (s.Child("facing").Value, GetFacing());
        bool  rAtk   = ToBool (s.Child("attacking").Value, attacking);

        health    = Mathf.Clamp(rHp, 0, rMaxHP);
        maxHP     = (rMaxHP > 0) ? rMaxHP : maxHP;
        attacking = rAtk;

        if (visual != null)
        {
            var ls = visual.localScale;
            ls.x = Mathf.Abs(ls.x) * (facing >= 0 ? 1f : -1f);
            visual.localScale = ls;
        }

        // Движение воспроизводим только если ЭТО устройство не владелец юнита
        if (!IsThisDeviceOwner())
        {
            bool moving = ToBool(s.Child("moving").Value, false);
            float x = ToFloat(s.Child("x").Value, transform.position.x);
            float y = ToFloat(s.Child("y").Value, transform.position.y);
            Vector3 target = new Vector3(x, y, transform.position.z);

            if (moving)
            {
                if (moveCo != null) StopCoroutine(moveCo);
                moveCo = StartCoroutine(MoveTo(target, moveSpeed)); // скорость из статов
            }
            else
            {
                if (moveCo != null) StopCoroutine(moveCo);
                moveCo = null;
                transform.position = target; // фиксация позиции
            }
        }

        // обновляем локальный combat-снимок, чтобы не слать обратно то же самое
        _combatLast = CaptureCombat();
    }

    private bool IsThisDeviceOwner() => Globalflags.ifHost == host;

    private IEnumerator MoveTo(Vector3 target, float speed)
    {
        const float stopDist = 0.02f;
        while (Vector2.Distance(transform.position, target) > stopDist)
        {
            transform.position = Vector2.MoveTowards(transform.position, target, speed * Time.deltaTime);
            yield return null;
        }
        transform.position = target;
        moveCo = null;
        // ВАЖНО: здесь ничего не пишем в RTDB; инициатор сам установит moving=false.
    }

    // ===== Helpers =====
    private int GetFacing()
    {
        var vis = visual != null ? visual : transform.Find("Visual");
        if (vis == null) return 1;
        return (vis.localScale.x >= 0f) ? 1 : -1;
    }

    private Vector2 RBVel()
    {
        if (rb == null) return Vector2.zero;
#if UNITY_6000_0_OR_NEWER || UNITY_2023_3_OR_NEWER
        return rb.linearVelocity;
#else
        return rb.velocity;
#endif
    }

    private static float ToFloat(object v, float def)
    {
        try { return v == null ? def : Convert.ToSingle(v); }
        catch { return def; }
    }

    private static int ToInt(object v, int def)
    {
        try { return v == null ? def : Convert.ToInt32(v); }
        catch { return def; }
    }

    private static bool ToBool(object v, bool def)
    {
        try { return v == null ? def : Convert.ToBoolean(v); }
        catch { return def; }
    }

    // ===== Debug getters =====
    public Vector2 PosDebug     => (Vector2)transform.position;
    public bool    MovingDebug  => RBVel().sqrMagnitude > 0.01f;
    public int     FacingDebug
    {
        get
        {
            var vis = visual != null ? visual : transform.Find("Visual");
            return (vis != null && vis.localScale.x < 0f) ? -1 : 1;
        }
    }
}
