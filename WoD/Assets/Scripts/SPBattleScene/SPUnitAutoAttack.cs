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

	private Unit unit;
	private float nextScanTime;
	private float nextShotTime;
	private bool  wasAttacking;
	private Unit  currentTarget;

	private void Awake()
	{
		unit = GetComponentInParent<Unit>();
	}

	private void Update()
	{
		// Во время расстановки — не атакуем
		if (BattlePlacementState.IsPlacementActive) { SetAttacking(false); wasAttacking = false; return; }
		if (unit == null || unit.moveSpeed < 0.001f) { return; }
		if (Time.time < nextScanTime) return;
		nextScanTime = Time.time + Mathf.Max(0.05f, scanIntervalSeconds);

		TryScan();

		// Программная каденса без анимационных событий
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
		for (int i = 0; i < all.Length; i++)
		{
			var other = all[i];
			try
			{
				if (!other || other == unit) continue;
				if (other.host == unit.host) continue; // только враги
				if (other.isPassive) continue;
				Vector3 pos = other.transform.position;
				// простая LOS: не стрелять через пассивные объекты
				if (IsLineBlockedByPassive(my, pos)) continue;
				float sqr = (pos - my).sqrMagnitude;
				if (sqr < bestSqr) { bestSqr = sqr; best = other; }
			}
			catch { }
		}
		bool shouldAttack = (best != null && bestSqr <= range * range);
		if (shouldAttack && !wasAttacking && best != null)
		{
			float tx; try { tx = best.transform.position.x; } catch { tx = unit.transform.position.x; }
			unit.FaceTowardsX(tx);
			nextShotTime = Time.time + Mathf.Max(0f, firstShotDelaySeconds);
		}
		currentTarget = shouldAttack ? best : null;
		SetAttacking(shouldAttack);
		wasAttacking = shouldAttack;
	}

	private void TryFire()
	{
		if (unit == null || unit.projectileStats == null) return;
		if (!unit.IsAttacking) return;

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
				if (u != null && u.isPassive) return true;
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
}


