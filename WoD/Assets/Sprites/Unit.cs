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
        [SerializeField] public bool   isStationary;   // из UnitStats.kind == Stationary
        [SerializeField] public bool   isPassive;      // из UnitStats.kind == Passive

    // ===== Firing/Projectile =====
    [Header("Firing")]
    public float  fireRate;         // выстрелов в секунду
    [Range(0f,1f)] public float accuracy;       // 1 = идеально, 0 = большой разброс
    public float  accuracySpread;   // макс. отклонение цели (мировые ед.)
    public ProjectileStats projectileStats; // ссылка на настройки снаряда

    // ===== Refs =====
    [Header("Refs (optional)")]
    [SerializeField] private Transform   visual;   // child "Visual"
    [SerializeField] private Rigidbody2D rb;       // optional
    [SerializeField] private bool        invertFacingX = false; // tick on prefabs that face left by default

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
    private bool  _isDying;                    // предотвращает двойной вызов Die()

    // ===== Animation/RTDB flags =====
    private bool _movingFromRtdb;              // последний принятый из RTDB флаг "moving"

    // ====== Public API ======
    public void Init(string type, UnitStats stats)
    {
        unitType = type;
        if (stats != null)
        {
                // классификация
                isStationary = (stats.kind == UnitStats.UnitKind.Stationary);
                isPassive    = (stats.kind == UnitStats.UnitKind.Passive);

            health      = stats.health;
            maxHP       = stats.health;
            damage      = stats.damage;
            attackRange = stats.attackRange;
                moveSpeed   = isStationary ? 0f : stats.moveSpeed;
            fireRate    = Mathf.Max(0.01f, stats.fireRate);
            accuracy    = Mathf.Clamp01(stats.accuracy);
            accuracySpread = Mathf.Max(0f, stats.accuracySpread);
            projectileStats = stats.projectileStats;
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
        if (this != null) // объект мог быть уничтожен, пока ждали await
            await PushCombatState(includeMaxHP: _maxHpDirty);
        _maxHpDirty = false;

        // подписка на состояние
        if (this != null && stateRef != null)
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
        // Сразу отправим в RTDB фактическое HP, чтобы оба клиента увидели обновление до уничтожения
        _ = PushCombatState(includeMaxHP: false);
        if (health == 0) Die();
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

    // === Facing control ===
    public void FaceTowardsX(float targetX)
    {
        if (isStationary) return; // стационарные не поворачиваются
        var vis = visual != null ? visual : transform.Find("Visual");
        if (vis == null) return;
        float dir = (targetX >= transform.position.x) ? 1f : -1f; // 1 -> right, -1 -> left
        var ls = vis.localScale;
        float required = dir;
        if (invertFacingX) required = -required;
        float absX = Mathf.Abs(ls.x);
        float newSign = required >= 0f ? 1f : -1f;
        float curSign = ls.x >= 0f ? 1f : -1f;
        if (curSign != newSign)
        {
            ls.x = absX * newSign;
            vis.localScale = ls;
            _forceCombatPush = true; // push facing via combat snapshot
        }
    }

    // Read-only access for animation and UI
    public bool IsAttacking => attacking;
    public bool IsMovingFromRTDB => _movingFromRtdb;

    /// <summary>Явленно изменить maxHP и отправить в RTDB (редкий случай).</summary>
    public void SetMaxHP(int newMax)
    {
        if (newMax <= 0 || newMax == maxHP) return;
        maxHP = newMax;
        if (health > maxHP) health = maxHP;
        _maxHpDirty = true;
        _forceCombatPush = true;
        _ = PushCombatState(includeMaxHP: true);
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
            _ = PushCombatState(includeMaxHP: false); // maxHP не шлём тут
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

    private async System.Threading.Tasks.Task PushCombatState(bool includeMaxHP)
    {
        // Если объект уже уничтожен — выходим, чтобы избежать MissingReferenceException
        if (this == null) return;
        if (stateRef == null) return;

        var dict = new Dictionary<string, object>
        {
            ["hp"]        = health,
            // Не вычисляем facing, если объект уже уничтожается
            ["facing"]    = (this != null) ? GetFacing() : 1,
            ["attacking"] = attacking,
            ["updatedAt"] = ServerValue.Timestamp
        };
        if (includeMaxHP) dict["maxHP"] = maxHP;

        try { await stateRef.UpdateChildrenAsync(dict); } catch { }
        try { await unitRef.Child("updatedAt").SetValueAsync(ServerValue.Timestamp); } catch { }
    }

    private async void Die()
    {
        if (_isDying) return;
        _isDying = true;
        // Кэшируем ссылку на GameObject до await — объект может быть уничтожен по событию RTDB
        var go = this ? this.gameObject : null;
        // Удаляем узел юнита в RTDB — это событие услышит другой клиент и удалит у себя объект
        try
        {
            if (unitRef != null)
                await unitRef.RemoveValueAsync();
        }
        catch { /* best-effort */ }
        // Локально уничтожаем объект (не ждём обратной подписки)
        if (go) Destroy(go);
    }

    // ====== Inbound sync: consume movement + combat ======
    private async void OnRemoteStateChanged(object sender, ValueChangedEventArgs e)
    {
        // Объект мог быть уже уничтожен по событию RTDB удаления — просто игнорируем колбэк
        if (this == null) return;
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

        // Если HP == 0:
        // - на НЕ-владельце сразу удалим объект локально, чтобы избежать "фантома"
        // - на владельце ждём удаление узла RTDB (обрабатывается отдельно)
        if (health == 0)
        {
            try
            {
                // Владелец отвечает за удаление узла RTDB, чтобы не оставался "призрак"
                if (IsThisDeviceOwner() && stateRef != null)
                {
                    // Удаляем весь юнит, не только state
                    if (unitRef != null)
                        await unitRef.RemoveValueAsync();
                }
            }
            catch { /* best-effort */ }
            try { Destroy(gameObject); } catch { }
            return;
        }

        if (visual != null)
        {
            var ls = visual.localScale;
            float sign = (facing >= 0 ? 1f : -1f);
            if (invertFacingX) sign = -sign;
            ls.x = Mathf.Abs(ls.x) * sign;
            visual.localScale = ls;
        }

        // Движение/позиция для НЕ-владельца:
        // - обычные юниты: воспроизводим плавное движение при moving=true
        // - стационарные: игнорируем плавное движение, но ВСЕГДА применяем фиксацию позиции (телепорт),
        //   чтобы корректно обновлять результат расстановки соперника
        if (!IsThisDeviceOwner())
        {
            bool moving = ToBool(s.Child("moving").Value, _movingFromRtdb);
            float x = ToFloat(s.Child("x").Value, transform.position.x);
            float y = ToFloat(s.Child("y").Value, transform.position.y);
            Vector3 target = new Vector3(x, y, transform.position.z);

            if (!isStationary && moving)
            {
                if (moveCo != null) StopCoroutine(moveCo);
                moveCo = StartCoroutine(MoveTo(target, moveSpeed)); // скорость из статов
            }
            else
            {
                if (moveCo != null) StopCoroutine(moveCo);
                moveCo = null;
                transform.position = target; // фиксация позиции (для стационарных или moving=false)
            }

            // хранить флаг для синхронизации анимации
            _movingFromRtdb = moving;
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
			var cur  = transform.position;
			var next = (Vector3)Vector2.MoveTowards(cur, target, speed * Time.deltaTime);

			// Уважать пассивные препятствия: не позволяем проходить сквозь стены
			bool blocked = false;
			var hits = Physics2D.LinecastAll(cur, next);
			if (hits != null && hits.Length > 0)
			{
				for (int i = 0; i < hits.Length; i++)
				{
					try
					{
						var go = hits[i].collider ? hits[i].collider.gameObject : null;
						if (!go) continue;
						var u = go.GetComponentInParent<Unit>();
						if (u != null && u.isPassive)
						{
							Vector3 dir = (next - cur).normalized;
							transform.position = hits[i].point - (Vector2)(dir * 0.02f);
							blocked = true;
							break;
						}
					}
					catch { }
				}
			}
			if (blocked) break;

			transform.position = next;
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
        int sign = (vis.localScale.x >= 0f) ? 1 : -1;
        if (invertFacingX) sign = -sign;
        return sign;
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
            if (vis == null) return 1;
            int sign = (vis.localScale.x < 0f) ? -1 : 1;
            if (invertFacingX) sign = -sign;
            return sign;
        }
    }
}
