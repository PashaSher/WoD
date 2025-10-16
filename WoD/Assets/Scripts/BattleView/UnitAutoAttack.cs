using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Database;
using UnityEngine;

/// <summary>
/// Автоатака: висит на ребёнке "Attack" префаба Unit_Root.
/// Пока для собственного юнита moving == false — периодически сканирует ближайших врагов
/// (юниты с противоположным флагом host) в радиусе Unit.attackRange.
/// При обнаружении цели выставляет Unit.attacking = true (и сбрасывает в false, если целей нет).
/// Флаг записывается в RTDB через существующую логику Unit.PushCombatState.
/// </summary>
public class UnitAutoAttack : MonoBehaviour
{
    [Header("Scan")]
    [SerializeField] private float scanIntervalSeconds = 0.25f;
    [SerializeField] private bool  drawDebugGizmos = false;

    private Unit unit;                                // владелец (ищем в родителях)
    private float nextScanTime;
    private float nextShotTime;

    // RTDB moving state for THIS unit
    private DatabaseReference stateRef;               // .../sessions/{sid}/{branch}/{unitKey}/state
    private bool hasMovingCache;
    private bool movingCache;

    private void Awake()
    {
        unit = GetComponentInParent<Unit>();
    }

    private void OnEnable()
    {
        TryAttachMovingListener();
    }

    private void OnDisable()
    {
        if (stateRef != null)
        {
            stateRef.Child("moving").ValueChanged -= OnMovingValueChanged;
        }
    }

    private void Update()
    {
        if (Time.time < nextScanTime) return;
        nextScanTime = Time.time + scanIntervalSeconds;

        if (!EnsureStateRef()) return;

        // Сканируем, только если наш юнит НЕ в движении по мнению RTDB
        if (!hasMovingCache || movingCache)
        {
            // пока движется — гарантированно не атакуем
            SetAttacking(false);
            return;
        }

        // Если уже атакуем и пора стрелять — создаём снаряд (и реплицируем в RTDB)
        if (IsAttacking() && Time.time >= nextShotTime)
        {
            nextShotTime = Time.time + Mathf.Max(0.01f, 1f / Mathf.Max(0.01f, unit.fireRate));
            TryFireProjectile();
        }

        TryScanAndAttack();
    }

    private void TryScanAndAttack()
    {
        if (unit == null) return;
        float range = Mathf.Max(0.01f, unit.attackRange > 0 ? unit.attackRange : 1.5f);

        Unit closestEnemy = null;
        float closestSqr = float.PositiveInfinity;

        var allUnits = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
        Vector3 myPos = unit.transform.position;
        bool myHost = unit.host;

        foreach (var other in allUnits)
        {
            if (other == null || other == unit) continue;
            if (other.host == myHost) continue; // ищем только противоположную сторону

            float sqr = (other.transform.position - myPos).sqrMagnitude;
            if (sqr < closestSqr)
            {
                closestSqr = sqr;
                closestEnemy = other;
            }
        }

        if (closestEnemy != null && closestSqr <= range * range)
        {
            SetAttacking(true);
        }
        else
        {
            SetAttacking(false);
        }
    }

    private void SetAttacking(bool value)
    {
        if (unit == null) return;
        unit.SetAttacking(value); // дальше Unit сам пушит в RTDB с дросселем
    }

    private bool IsAttacking()
    {
        // Используем внутреннее состояние Unit через отражение недоступно; полагаемся на публичный флаг через CombatSnapshot
        // Здесь считаем, что Unit.PushCombatState пошлёт актуальный флаг, а мы просто повторяем каденс
        return true; // каденс управляется TryScanAndAttack -> SetAttacking(true)/false
    }

    private void TryFireProjectile()
    {
        if (unit == null || unit.projectileStats == null) return;
        if (string.IsNullOrEmpty(unit.sessionId) || string.IsNullOrEmpty(unit.unitKey)) return;

        // вычисляем старт по facing: чуть правее/левее центра визуала
        var vis = unit.transform.Find("Visual");
        Vector3 basePos = vis ? vis.position : unit.transform.position;
        int facing = unit.FacingDebug >= 0 ? 1 : -1;
        Vector3 start = basePos + new Vector3(0.25f * facing, 0.1f, 0);

        // цель — ближайший враг в радиусе attackRange, с разбросом по accuracy
        Unit targetUnit = FindClosestEnemyWithin(unit.attackRange);
        if (!targetUnit) return;
        Vector3 target = targetUnit.transform.position;

        // применим разброс: чем ниже accuracy, тем выше отклонение
        float spreadFactor = (1f - Mathf.Clamp01(unit.accuracy)) * Mathf.Max(0f, unit.accuracySpread);
        if (spreadFactor > 0f)
        {
            target += new Vector3(UnityEngine.Random.Range(-spreadFactor, spreadFactor), UnityEngine.Random.Range(-spreadFactor, spreadFactor), 0f);
        }

        // визуальный эффект выстрела (короткая вспышка у дула)
        var flashCtrl = unit != null ? unit.GetComponent<MuzzleFlashController>() : null;
        if (flashCtrl != null)
        {
            flashCtrl.PlayFlash(0.5f);
        }

        // создаём ноду снаряда в RTDB: /sessions/{sid}/{branch}/projectiles/{autoKey}
        string branch = unit.host ? "hostArmy" : "clientArmy";
        var root = FirebaseDatabase.DefaultInstance.RootReference;
        var projRoot = root.Child("sessions").Child(unit.sessionId).Child(branch).Child("projectiles");
        var newRef = projRoot.Push();
        string key = newRef.Key;

        var payload = new Dictionary<string, object>
        {
            ["ownerKey"] = unit.unitKey,
            ["ownerBranch"] = branch,
            ["host"] = unit.host,
            ["type"] = unit.unitType,
            ["startX"] = (double)start.x,
            ["startY"] = (double)start.y,
            ["targetX"] = (double)target.x,
            ["targetY"] = (double)target.y,
            ["speed"] = (double)unit.projectileStats.speed,
            ["damage"] = unit.projectileStats.damage,
            ["penetration"] = unit.projectileStats.penetration,
            ["splash"] = (double)unit.projectileStats.splashRadius,
            ["scaleX"] = (double)unit.projectileStats.scale.x,
            ["scaleY"] = (double)unit.projectileStats.scale.y,
            ["createdAt"] = ServerValue.Timestamp
        };
        newRef.SetValueAsync(payload);

        // Локально отрисуем снаряд
        SpawnLocalProjectile(key, start, target, createdByLocal: true);
    }

    private Unit FindClosestEnemyWithin(float range)
    {
        var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
        Unit best = null;
        float bestSqr = float.PositiveInfinity;
        Vector3 my = unit.transform.position;
        foreach (var u in all)
        {
            if (!u || u.host == unit.host) continue;
            float sqr = (u.transform.position - my).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr; best = u;
            }
        }
        if (best && bestSqr <= range * range) return best;
        return null;
    }

    private void SpawnLocalProjectile(string key, Vector2 start, Vector2 target, bool createdByLocal)
    {
        if (unit == null || unit.projectileStats == null) return;
        var go = new GameObject($"Projectile_{key}");
        go.transform.position = start;
        var proj = go.AddComponent<Projectile>();
        proj.Init(unit, unit.projectileStats, key, start, target, createdByLocal);

        // привяжем ref, если хотим позже удалять
        var refPath = FirebaseDatabase.DefaultInstance.RootReference
            .Child("sessions").Child(unit.sessionId)
            .Child(unit.host ? "hostArmy" : "clientArmy")
            .Child("projectiles").Child(key);
        proj.BindRef(refPath);
    }

    private void TryAttachMovingListener()
    {
        if (!EnsureStateRef()) return;
        stateRef.Child("moving").ValueChanged -= OnMovingValueChanged;
        stateRef.Child("moving").ValueChanged += OnMovingValueChanged;

        // первичная подгрузка
        _ = stateRef.Child("moving").GetValueAsync().ContinueWith(t =>
        {
            if (t.IsCompleted)
            {
                hasMovingCache = true;
                movingCache = ParseBool(t.Result?.Value);
            }
        });
    }

    private void OnMovingValueChanged(object sender, ValueChangedEventArgs e)
    {
        hasMovingCache = true;
        movingCache = ParseBool(e.Snapshot?.Value);
    }

    private bool EnsureStateRef()
    {
        if (stateRef != null) return true;
        if (unit == null) return false;
        if (string.IsNullOrEmpty(unit.sessionId) || string.IsNullOrEmpty(unit.unitKey)) return false;

        string branch = unit.host ? "hostArmy" : "clientArmy";
        stateRef = FirebaseDatabase.DefaultInstance.RootReference
            .Child("sessions").Child(unit.sessionId)
            .Child(branch).Child(unit.unitKey).Child("state");

        TryAttachMovingListener();
        return true;
    }

    private static bool ParseBool(object v)
    {
        if (v is bool b) return b;
        if (v is long l) return l != 0;
        if (v is int i) return i != 0;
        if (v is string s)
        {
            if (bool.TryParse(s, out var bs)) return bs;
            if (long.TryParse(s, out var ls)) return ls != 0;
        }
        return false;
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawDebugGizmos) return;
        var u = GetComponentInParent<Unit>();
        if (!u) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(u.transform.position, Mathf.Max(0.01f, u.attackRange));
    }
}


