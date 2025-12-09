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

    [Header("Projectile")]
    [SerializeField] private Transform projectileSpawn; // опциональная точка вылета (Inspector)
    [SerializeField] private Vector3 startOffset = new Vector3(0.25f, 0.1f, 0f); // fallback оффсет

    [Header("Cadence")]
    [SerializeField] private float firstShotDelaySeconds = 0.2f; // задержка перед первым выстрелом при начале атаки

    private Unit unit;                                // владелец (ищем в родителях)
    private float nextScanTime;
    private float nextShotTime;
    private bool  wasAttacking;                       // для детекта начала атаки

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
        // Подписки включаем только для владельца юнита
        if (unit != null && Globalflags.ifHost != unit.host) return;
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
		// Во время расстановки — не сканируем и не стреляем
		if (BattlePlacementState.IsPlacementActive)
		{
			SetAttacking(false);
			wasAttacking = false;
			return;
		}
		// Пока менеджер боя не активен или оба игрока не готовы — стоим
		if (!BattleReadyManager.Active || !BattleReadyManager.BothReady)
		{
			SetAttacking(false);
			wasAttacking = false;
			return;
		}
        // Исполняем логику только на стороне владельца, чтобы не дублировать снаряды
		if (unit != null && Globalflags.ifHost != unit.host) return;
        if (Time.time < nextScanTime) return;
        nextScanTime = Time.time + scanIntervalSeconds;

        if (!EnsureStateRef()) return;

        // Сканируем, только если наш юнит НЕ в движении по мнению RTDB.
        // Если кеш ещё не прогрет (hasMovingCache == false) — считаем, что НЕ движется, чтобы не блокировать старт боя.
        bool isMoving = hasMovingCache ? movingCache : false;
        if (isMoving)
        {
            // пока движется — гарантированно не атакуем
            SetAttacking(false);
            // сбрасываем флаг локального состояния, чтобы при окончании движения сработала задержка первого выстрела
            wasAttacking = false;
            return;
        }

        // Сначала сканируем и обновляем состояние (включая установку задержки первого выстрела)
        TryScanAndAttack();
		// Обычно стрельба вызывается событием анимации (Animation Event).
		// Если валидного события нет — используем программный фолбэк по каденсу.
		if (useCadenceFallback && IsAttacking() && Time.time >= nextShotTime)
		{
			nextShotTime = Time.time + Mathf.Max(0.01f, 1f / Mathf.Max(0.01f, unit != null ? unit.fireRate : 1f));
			TryFireProjectile();
		}
    }

	// Текущая выбранная цель (с проверкой LOS)
	private Unit currentTarget;
	private bool useCadenceFallback; // если нет валидного AnimationEvent("AnimEvent_Fire")
	private bool animEventsSanitized;

	private void Start()
	{
		TryDetectOrSanitizeAnimEvents();
	}

	private void TryDetectOrSanitizeAnimEvents()
	{
		if (animEventsSanitized) return;
		animEventsSanitized = true;
		try
		{
			if (unit == null) unit = GetComponentInParent<Unit>();
			if (unit == null) return;
			Animator animator = null;
			var vis = unit.transform.Find("Visual");
			if (vis) animator = vis.GetComponent<Animator>();
			if (!animator) animator = unit.GetComponentInChildren<Animator>(true);
			if (!animator || animator.runtimeAnimatorController == null) { useCadenceFallback = true; return; }
			bool hasFireEvent = false;
			var clips = animator.runtimeAnimatorController.animationClips;
			if (clips == null || clips.Length == 0) { useCadenceFallback = true; return; }
			for (int i = 0; i < clips.Length; i++)
			{
				var clip = clips[i];
				if (!clip) continue;
				var events = clip.events;
				if (events == null || events.Length == 0) continue;
				var filtered = new List<AnimationEvent>(events.Length);
				for (int j = 0; j < events.Length; j++)
				{
					var ev = events[j];
					if (string.IsNullOrEmpty(ev.functionName)) continue; // выбрасываем пустые, чтобы не было ошибки в редакторе
					if (string.Equals(ev.functionName, "AnimEvent_Fire", StringComparison.Ordinal)) hasFireEvent = true;
					filtered.Add(ev);
				}
				if (filtered.Count != events.Length)
				{
					try { clip.events = filtered.ToArray(); } catch { }
				}
			}
			useCadenceFallback = !hasFireEvent;
		}
		catch
		{
			useCadenceFallback = true;
		}
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
        int myFacing = unit != null ? (unit.FacingDebug >= 0 ? 1 : -1) : 1;

        foreach (var other in allUnits)
        {
			try
        {
            if (other == null || other == unit) continue;
				// может быть уничтожен между проверками — повторная проверка ниже
				if (other == null) continue;
            if (other.host == myHost) continue; // ищем только противоположную сторону
			// пассивные объекты не являются целями
			if (other.isPassive) continue;
            // Для стационарных — цели только впереди по направлению взгляда
            if (unit != null && unit.isStationary)
            {
                float dx;
                try { dx = other.transform.position.x - myPos.x; } catch { dx = 0f; }
                if (myFacing > 0 && dx < 0f) continue;
                if (myFacing < 0 && dx > 0f) continue;
            }

				Vector3 pos;
				try { pos = other.transform.position; } catch { continue; }
			// Линия видимости: если между нами пассивное препятствие — цель недоступна
			if (IsLineBlockedByPassive(myPos, pos)) continue;

				float sqr = (pos - myPos).sqrMagnitude;
            if (sqr < closestSqr)
            {
                closestSqr = sqr;
                closestEnemy = other;
            }
			}
			catch { /* объект мог быть уничтожен в этот кадр */ }
        }

        bool shouldAttack = (closestEnemy != null && closestSqr <= range * range);

        // При начале атаки разворачиваемся лицом к цели
        if (shouldAttack && !wasAttacking && closestEnemy != null)
        {
			// цель могла быть уничтожена — безопасно читаем позицию
			float tx;
			try { tx = closestEnemy.transform.position.x; }
			catch { tx = unit.transform.position.x; }
			unit.FaceTowardsX(tx);
            // при входе в атаку даём время на анимацию/изготовку
            nextShotTime = Time.time + Mathf.Max(0f, firstShotDelaySeconds);
        }

        SetAttacking(shouldAttack);
        wasAttacking = shouldAttack;
		currentTarget = shouldAttack ? closestEnemy : null;
    }

    private void SetAttacking(bool value)
    {
        if (unit == null) return;
        unit.SetAttacking(value); // дальше Unit сам пушит в RTDB с дросселем
    }

    private bool IsAttacking()
    {
		// Стреляем ТОЛЬКО если бой действительно активен и юнит в атакующем состоянии
		if (BattlePlacementState.IsPlacementActive) return false;
		if (!BattleReadyManager.Active || !BattleReadyManager.BothReady) return false;
		return unit != null && unit.IsAttacking;
    }

    private void TryFireProjectile()
    {
		// Доп. защита от выстрелов во время расстановки/ожидания
		if (BattlePlacementState.IsPlacementActive) return;
		if (!BattleReadyManager.Active || !BattleReadyManager.BothReady) return;
		if (unit == null || unit.projectileStats == null) return;
		if (string.IsNullOrEmpty(unit.sessionId) || string.IsNullOrEmpty(unit.unitKey)) return;

        // Выбор цели: сохраняем приоритет юнитов над стенами — никогда не целимся в пассивные
		Unit targetUnit = null;
		if (currentTarget != null && IsValidTarget(currentTarget, unit.attackRange))
		{
			targetUnit = currentTarget;
		}
		else
		{
			targetUnit = FindPreferredEnemyWithinLOS(unit.attackRange);
			currentTarget = targetUnit;
		}
		if (!targetUnit) return;

        // Точка вылета: приоритет — заданная в Inspector, затем авто-поиск "MuzzleFlash", затем fallback к Visual с оффсетом по facing
        Transform muzzle = projectileSpawn;
        if (!muzzle)
        {
            // пробуем найти любой дочерний Transform с названием "MuzzleFlash" (включая неактивные)
            var allChildren = unit.transform.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < allChildren.Length; i++)
            {
                var t = allChildren[i];
                if (t != null && t.name == "MuzzleFlash") { muzzle = t; break; }
            }
        }

        Vector3 basePos;
        if (muzzle)
        {
            basePos = muzzle.position;
        }
        else
        {
            var vis = unit.transform.Find("Visual");
            basePos = vis ? vis.position : unit.transform.position;
        }

        int facing = unit.FacingDebug >= 0 ? 1 : -1;
        Vector3 start = basePos;
        if (!muzzle)
        {
            start += new Vector3(startOffset.x * facing, startOffset.y, startOffset.z);
        }

        // Перед выстрелом разворачиваемся к цели
		try
		{
			unit.FaceTowardsX(targetUnit.transform.position.x);
		}
		catch
		{
			// цель могла исчезнуть — отменяем выстрел
			return;
		}
        Vector3 target = GetTargetPoint(targetUnit);

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

		// Host-авторитет: только HOST создаёт реальный снаряд и реплицирует его
		bool iAmOwner = (Globalflags.ifHost == unit.host);
		if (iAmOwner)
		{
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

			// Локально отрисуем снаряд (авторитетный на HOST)
			SpawnLocalProjectile(key, start, target, createdByLocal: true);
		}
		else
		{
			// Не владелец данного юнита на этом клиенте:
			// НИЧЕГО не спауним локально — ждём авторитетную запись снаряда из RTDB от владельца.
			// Это устраняет рассинхрон направления (разные цели на разных клиентах).
		}
    }

    private Unit FindClosestEnemyWithin(float range)
    {
        var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
        Unit best = null;
        float bestSqr = float.PositiveInfinity;
        Vector3 my = unit.transform.position;
        foreach (var u in all)
        {
			try
        {
            if (!u || u.host == unit.host) continue;
				Vector3 pos;
				try { pos = u.transform.position; } catch { continue; }
				float sqr = (pos - my).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr; best = u;
            }
			}
			catch { }
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

	// Визуальный снаряд без БД/ключа и без нанесения урона (используется на клиенте)
	private void SpawnVisualOnly(Vector2 start, Vector2 target)
	{
		if (unit == null || unit.projectileStats == null) return;
		var key = $"local_{unit.unitKey}_{DateTime.UtcNow.Ticks}";
		var go = new GameObject($"Projectile_{key}");
		go.transform.position = start;
		var proj = go.AddComponent<Projectile>();
		// createdByLocal=false, чтобы исключить любую попытку локального урономоделирования
		proj.Init(unit, unit.projectileStats, key, start, target, createdByLocal: false);
		// Регистрируем визуал у репликатора, чтобы при приходе RTDB он привязал ref и не создавал дубликат
		try { ProjectileReplicator.RegisterLocalVisual(unit.unitKey, proj, start, target); } catch {}
		// Без BindRef сейчас — привяжется при приходе узла RTDB
	}

	// Метод для Animation Event
	public void AnimEvent_Fire()
	{
		// Во время расстановки/ожидания — не стреляем
		if (BattlePlacementState.IsPlacementActive) return;
		if (!BattleReadyManager.Active || !BattleReadyManager.BothReady) return;
		// Доп. защита: если юнит в движении по RTDB — не стрелять даже при событии
		if (hasMovingCache && movingCache) return;
		// Чтобы не стрелять вне режима атаки — уважаем cadence из сканирования
		if (!IsAttacking()) return;
		// Переносим каденс на анимацию: ограничим частоту по fireRate, чтобы дизайнер мог ставить частые события
		if (Time.time < nextShotTime) return;
		nextShotTime = Time.time + Mathf.Max(0.01f, 1f / Mathf.Max(0.01f, unit != null ? unit.fireRate : 1f));
		TryFireProjectile();
	}

	private Vector3 GetTargetPoint(Unit target)
	{
		if (target == null) return Vector3.zero;
		// Пытаемся взять центр коллайдера Visual; иначе центр спрайта; иначе позицию корня
		Transform vis = target.transform.Find("Visual");
		Collider2D bestCol = null;
		if (vis)
		{
			bestCol = vis.GetComponent<Collider2D>();
			if (!bestCol)
			{
				var cols = vis.GetComponentsInChildren<Collider2D>(true);
				if (cols != null && cols.Length > 0) bestCol = cols[0];
			}
			if (!bestCol)
			{
				var sr = vis.GetComponent<SpriteRenderer>();
				if (sr && sr.sprite)
				{
					var b = sr.bounds;
					return new Vector3(b.center.x, b.center.y, target.transform.position.z);
				}
			}
		}
		if (bestCol != null)
		{
			var b = bestCol.bounds;
			return new Vector3(b.center.x, b.center.y, target.transform.position.z);
		}
		return target.transform.position;
	}

	private bool IsLineBlockedByPassive(Vector3 a, Vector3 b)
	{
		var hits = Physics2D.LinecastAll(a, b);
		if (hits == null || hits.Length == 0) return false;
		for (int i = 0; i < hits.Length; i++)
		{
			try
			{
				var go = hits[i].collider ? hits[i].collider.gameObject : null;
				if (!go) continue;
				var u = go.GetComponentInParent<Unit>();
				if (u != null && u.isPassive) return true;
			}
			catch { }
		}
		return false;
	}

	private bool IsValidTarget(Unit other, float range)
	{
		if (!other) return false;
		if (other.host == unit.host) return false;
		if (other.isPassive) return false;
		Vector3 myPos = unit.transform.position;
		Vector3 pos;
		try { pos = other.transform.position; } catch { return false; }
		if ((pos - myPos).sqrMagnitude > range * range) return false;
		// для стационарных — только вперёд
		if (unit.isStationary)
		{
			int myFacing = unit.FacingDebug >= 0 ? 1 : -1;
			float dx = pos.x - myPos.x;
			if (myFacing > 0 && dx < 0f) return false;
			if (myFacing < 0 && dx > 0f) return false;
		}
		// не выбираем цель, если между нами пассивное препятствие
		if (IsLineBlockedByPassive(myPos, pos)) return false;
		return true;
	}

	private Unit FindPreferredEnemyWithinLOS(float range)
	{
		var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
		Unit best = null;
		float bestSqr = float.PositiveInfinity;
		Vector3 my = unit.transform.position;
		int myFacing = unit.FacingDebug >= 0 ? 1 : -1;
		for (int i = 0; i < all.Length; i++)
		{
			var u = all[i];
			try
			{
				if (!u || u.host == unit.host) continue;
				if (u.isPassive) continue;
				Vector3 pos;
				try { pos = u.transform.position; } catch { continue; }
				if (IsLineBlockedByPassive(my, pos)) continue;
				if (unit.isStationary)
				{
					float dx = pos.x - my.x;
					if (myFacing > 0 && dx < 0f) continue;
					if (myFacing < 0 && dx > 0f) continue;
				}
				float sqr = (pos - my).sqrMagnitude;
				if (sqr < bestSqr && sqr <= range * range)
				{
					bestSqr = sqr;
					best = u;
				}
			}
			catch { }
		}
		return best;
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


