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

        TryScanAndAttack();
    }

    private void TryScanAndAttack()
    {
        if (unit == null) return;
        float range = Mathf.Max(0.01f, unit.attackRange > 0 ? unit.attackRange : 1.5f);

        Unit closestEnemy = null;
        float closestSqr = float.PositiveInfinity;

        var allUnits = FindObjectsOfType<Unit>();
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


