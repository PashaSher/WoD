using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Локальная автоатака для одиночного режима (без RTDB).
/// Сканирует ближайших врагов и стреляет с каденсом из UnitStats.
/// </summary>
public class SPUnitAutoAttack : MonoBehaviour
{
	[SerializeField] private float scanIntervalSeconds = 0.25f;
	[SerializeField] private float firstShotDelaySeconds = 0.2f;
	[SerializeField] private bool  useAnimationEvents = true;
	// Точка вылета как в MP: опционально укажем вручную; иначе найдем "MuzzleFlash"; иначе возьмём Visual + смещение
	[SerializeField] private Transform projectileSpawn;
	[SerializeField] private Vector3 startOffset = new Vector3(0.25f, 0.1f, 0f);
		[SerializeField] private bool  debugLogs = true;

	private Unit unit;
	private SPAnimatorFlags animFlags;
	private float nextScanTime;
	private float nextShotTime;
	private bool  wasAttacking;
	private Unit  currentTarget;
	private bool  firedThisAttack;

	private void Awake()
	{
		unit = GetComponentInParent<Unit>();
		animFlags = GetComponent<SPAnimatorFlags>() ?? GetComponentInChildren<SPAnimatorFlags>();
	}

	private void Update()
	{
		// Во время расстановки — не атакуем
		if (BattlePlacementState.IsPlacementActive) { if (debugLogs) Debug.Log("[SPUnitAutoAttack] Placement active -> skip"); SetAttacking(false); wasAttacking = false; return; }
		if (unit == null || unit.moveSpeed < 0.001f) { if (debugLogs) Debug.Log("[SPUnitAutoAttack] No unit or moveSpeed < eps -> skip"); return; }
		// Запрет атаки во время движения (как в MP — стреляем стоя)
		if (animFlags != null && animFlags.IsMoving) { if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' Moving -> block attack"); SetAttacking(false); wasAttacking = false; return; }
		if (Time.time < nextScanTime) { return; }
		nextScanTime = Time.time + Mathf.Max(0.05f, scanIntervalSeconds);

		TryScan();

		// Прямой выстрел только если не используем события анимации
		if (!useAnimationEvents)
			if (unit.IsAttacking && Time.time >= nextShotTime)
			{
				nextShotTime = Time.time + Mathf.Max(0.01f, 1f / Mathf.Max(0.01f, unit.fireRate));
                TryFire();
			}
	}

	private void TryScan()
	{
		float range = Mathf.Max(0.01f, unit.attackRange > 0 ? unit.attackRange : 1.5f);
		Unit best = null; float bestSqr = float.PositiveInfinity;
		var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
		Vector3 my = unit.transform.position;
		int considered = 0, blocked = 0;
		for (int i = 0; i < all.Length; i++)
		{
			var other = all[i];
			try
			{
				if (!other || other == unit) continue;
				if (other.host == unit.host) continue; // только враги
				if (other.isPassive) continue;
				considered++;
				Vector3 pos = other.transform.position;
				// простая LOS: не стрелять через пассивные объекты
				if (IsLineBlockedByPassive(my, pos)) { blocked++; continue; }
				float sqr = (pos - my).sqrMagnitude;
				if (sqr < bestSqr) { bestSqr = sqr; best = other; }
			}
			catch { }
		}
		bool shouldAttack = (best != null && bestSqr <= range * range);
		if (debugLogs)
		{
			string targetName = best ? best.name : "none";
			Debug.Log($"[SPUnitAutoAttack] '{unit.name}' scan: enemies={considered}, blockedLOS={blocked}, range={range:F2}, target={targetName}, dist={(best != null ? Mathf.Sqrt(bestSqr).ToString("F2") : "--")}, shouldAttack={shouldAttack}");
		}
		if (shouldAttack && !wasAttacking && best != null && (animFlags == null || !animFlags.IsMoving))
		{
			float tx; try { tx = best.transform.position.x; } catch { tx = unit.transform.position.x; }
			unit.FaceTowardsX(tx);
			nextShotTime = Time.time + Mathf.Max(0f, firstShotDelaySeconds);
			firedThisAttack = false;
			if (useAnimationEvents && animFlags != null)
			{
				// Запускаем анимацию атаки, сам выстрел придёт из Animation Event
				if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' start attack via animation events");
				animFlags.SetAttacking(true);
				// В SP атакуем строго по ивентам анимации; переход по bool 'attack'
				// Если контроллер использует триггер, настройте переходы под bool 'attack'
			}
			else if (!useAnimationEvents)
			{
				if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' start attack immediate (no events)");
			}
		}
		else if (shouldAttack && wasAttacking)
		{
			if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' already attacking - waiting for animation events/fire rate");
		}
		else if (shouldAttack && animFlags != null && animFlags.IsMoving)
		{
			if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' shouldAttack but moving -> blocked");
		}
		else if (!shouldAttack && wasAttacking)
		{
			if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' lost target -> stop attack");
		}
		currentTarget = shouldAttack ? best : null;
		SetAttacking(shouldAttack);
		// Держим bool 'attack' синхронным со сканером всегда (как в MP)
		if (animFlags != null) animFlags.SetAttacking(shouldAttack);
		wasAttacking = shouldAttack;
	}

	private void TryFire()
	{
		if (unit == null || unit.projectileStats == null) return;
		if (!unit.IsAttacking) return;
		if (!useAnimationEvents && animFlags != null) animFlags.TriggerAttack();

		// Точка вылета
		var vis = unit.transform.Find("Visual");
		Vector3 basePos = vis ? vis.position : unit.transform.position;
		int facing = unit.FacingDebug >= 0 ? 1 : -1;
		Vector3 start = basePos + new Vector3(0.25f * facing, 0.1f, 0f);

		// Цель — текущая или ближайшая в радиусе
		var targetU = currentTarget ? currentTarget : FindPreferredEnemyWithin(unit.attackRange);
		if (!targetU) return;
		try { unit.FaceTowardsX(targetU.transform.position.x); } catch { }

		Vector3 targetPos = GetTargetPoint(targetU);

		// добавим разброс
		float spreadFactor = (1f - Mathf.Clamp01(unit.accuracy)) * Mathf.Max(0f, unit.accuracySpread);
		if (spreadFactor > 0f)
		{
			targetPos += new Vector3(Random.Range(-spreadFactor, spreadFactor), Random.Range(-spreadFactor, spreadFactor), 0f);
		}

		var go = new GameObject($"SPProjectile_{unit.unitType}_{Time.frameCount}");
		go.transform.position = start;
		var proj = go.AddComponent<SPProjectile>();
		proj.Init(unit, unit.projectileStats, start, targetPos);
		if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' TryFire -> projectile to {targetPos}");
	}

	// Вызывается из события анимации в клипе атаки
	public void FireNow()
	{
		if (debugLogs)
		{
			var animMoving = (animFlags != null) ? animFlags.IsMoving : (bool?)null;
			Debug.Log($"[SPUnitAutoAttack] FireNow() enter: unit={(unit ? unit.name : "null")}, useEvents={useAnimationEvents}, firedThis={firedThisAttack}, isAttacking={(unit ? unit.IsAttacking : false)}, isMoving={(animMoving.HasValue ? animMoving.Value.ToString() : "n/a")}");
		}
		if (!useAnimationEvents) { TryFire(); return; }
		if (!unit || !unit.IsAttacking) return;
		if (animFlags != null && animFlags.IsMoving) return;
		// ограничение частоты выстрелов по fireRate
		if (Time.time < nextShotTime) return;
		if (unit.projectileStats == null)
		{
			if (debugLogs) Debug.LogWarning($"[SPUnitAutoAttack] FireNow() skip: '{(unit ? unit.name : name)}' has no ProjectileStats on Unit");
			return;
		}

		// Точка вылета: projectileSpawn → "MuzzleFlash" → Visual + смещение по facing
		Transform muzzle = projectileSpawn;
		if (!muzzle)
		{
			var all = unit.transform.GetComponentsInChildren<Transform>(true);
			for (int i = 0; i < all.Length; i++)
			{
				var t = all[i];
				if (t != null && t.name == "MuzzleFlash") { muzzle = t; break; }
			}
		}
		var vis = unit.transform.Find("Visual");
		Vector3 basePos = muzzle ? muzzle.position : (vis ? vis.position : unit.transform.position);
		int facing = unit.FacingDebug >= 0 ? 1 : -1;
		Vector3 start = basePos;
		if (!muzzle)
		{
			start += new Vector3(startOffset.x * facing, startOffset.y, startOffset.z);
		}
		if (debugLogs)
		{
			string src = projectileSpawn ? "projectileSpawn" : (muzzle ? "MuzzleFlash" : "Visual+offset");
			Debug.Log($"[SPUnitAutoAttack] '{unit.name}' muzzleSrc={src}, basePos={basePos}, start={start}, facing={(facing > 0 ? ">" : "<")}");
		}

		// Цель
		var targetU = currentTarget ? currentTarget : FindPreferredEnemyWithin(unit.attackRange);
		if (!targetU) { if (debugLogs) Debug.LogWarning($"[SPUnitAutoAttack] FireNow() skip: '{unit.name}' has no current target within range"); return; }
		try { unit.FaceTowardsX(targetU.transform.position.x); } catch { }
		Vector3 targetPos = GetTargetPoint(targetU);

		float spread = (1f - Mathf.Clamp01(unit.accuracy)) * Mathf.Max(0f, unit.accuracySpread);
		if (spread > 0f) targetPos += new Vector3(Random.Range(-spread, spread), Random.Range(-spread, spread), 0f);

		// Мuzzle flash как в MP
		try
		{
			var flashCtrl = unit.GetComponent<MuzzleFlashController>();
			if (flashCtrl != null)
			{
				flashCtrl.PlayFlash(0.5f);
				if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' muzzle flash played");
			}
			else if (debugLogs)
			{
				Debug.Log($"[SPUnitAutoAttack] '{unit.name}' no MuzzleFlashController (optional)");
			}
		}
		catch { }

		if (debugLogs)
		{
			var st = unit.projectileStats;
			Debug.Log($"[SPUnitAutoAttack] '{unit.name}' spawn projectile: speed={st.speed}, dmg={st.damage}, splash={st.splashRadius}, sprite={(st.sprite ? st.sprite.name : "null")}, start={start}, target={targetPos}");
		}
		var go = new GameObject($"SPProjectile_{unit.unitType}_{Time.frameCount}");
		go.transform.position = start;
		var proj = go.AddComponent<SPProjectile>();
		proj.Init(unit, unit.projectileStats, start, targetPos);
		if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' projectile GO created -> {go.name} at {start}");

		nextShotTime = Time.time + Mathf.Max(0.01f, 1f / Mathf.Max(0.01f, unit.fireRate));
		if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' FireNow -> projectile to {targetPos}");
	}

	// Вызывается из Animation Event в конце клипа атаки
	public void OnAttackAnimationEnd()
	{
		// Не завершаем атаку событием. Выход из атаки — только по сканеру (нет цели/LOS).
		if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' Attack animation end (no state change)");
	}

	private void SetAttacking(bool value) => unit.SetAttacking(value);

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
				if (u != null && u.isPassive)
				{
					if (debugLogs) Debug.Log($"[SPUnitAutoAttack] '{unit.name}' LOS blocked by passive '{u.name}'");
					return true;
				}
			}
			catch { }
		}
		return false;
	}

	private Unit FindPreferredEnemyWithin(float range)
	{
		var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
		Unit best = null; float bestSqr = float.PositiveInfinity;
		Vector3 my = unit.transform.position;
		for (int i = 0; i < all.Length; i++)
		{
			var u = all[i];
			try
			{
				if (!u || u.host == unit.host) continue;
				if (u.isPassive) continue;
				Vector3 pos = u.transform.position;
				if (IsLineBlockedByPassive(my, pos)) continue;
				float sqr = (pos - my).sqrMagnitude;
				if (sqr < bestSqr && sqr <= range * range) { bestSqr = sqr; best = u; }
			}
			catch { }
		}
		return best;
	}

	private Vector3 GetTargetPoint(Unit target)
	{
		if (!target) return Vector3.zero;
		// 1) Пытаемся найти любой Collider2D у цели (у Visual или у детей/корня)
		Collider2D col = null;
		var vis = target.transform.Find("Visual");
		if (vis)
		{
			col = vis.GetComponent<Collider2D>();
			if (!col)
			{
				var cols = vis.GetComponentsInChildren<Collider2D>(true);
				if (cols != null && cols.Length > 0) col = cols[0];
			}
		}
		if (!col)
		{
			// не нашли на Visual — берём на самом target или его детях
			col = target.GetComponent<Collider2D>();
			if (!col)
			{
				var cols = target.GetComponentsInChildren<Collider2D>(true);
				if (cols != null && cols.Length > 0) col = cols[0];
			}
		}
		if (col)
		{
			var b = col.bounds;
			return new Vector3(b.center.x, b.center.y, target.transform.position.z);
		}
		// 2) Если нет коллайдера — берём центр спрайта Visual
		if (vis)
		{
			var sr = vis.GetComponent<SpriteRenderer>();
			if (sr && sr.sprite)
			{
				var b = sr.bounds;
				return new Vector3(b.center.x, b.center.y, target.transform.position.z);
			}
		}
		// 3) Фоллбек — позиция трансформа цели
		return target.transform.position;
	}
}


