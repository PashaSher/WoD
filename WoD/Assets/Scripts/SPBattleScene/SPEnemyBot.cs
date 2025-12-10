using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple SP bot: periodically moves BLUE (host=false) units. 
/// Никакой боевой/целевая логики — только движение.
/// </summary>
public class SPEnemyBot : MonoBehaviour
{
	[Header("Behavior")]
	[SerializeField] private float thinkIntervalSeconds = 1.0f;
	[SerializeField] private float stepDistance = 1.0f;          // legacy default step if min/max are zero
	[SerializeField] private float stopDistance = 0.05f;          // movement epsilon
	[SerializeField] private float perUnitCooldownSeconds = 20.0f; // запрет двигаться после хода (сек)

	[Header("Step Randomization")]
	[SerializeField, Tooltip("Минимальная длина шага (мировые ед.)")]
	private float stepDistanceMin = 0.4f;
	[SerializeField, Tooltip("Максимальная длина шага (мировые ед.)")]
	private float stepDistanceMax = 1.2f;
	[SerializeField, Range(0f, 1f), Tooltip("Малая вероятность вместо ближней цели ориентироваться на самую дальнюю")]
	private float farTargetProbability = 0.10f;

		[Header("Tactics")]
		[SerializeField, Tooltip("Доля от attackRange, ниже которой юнит начинает отступать")]
		private float maintainRangeMinFactor = 0.75f;
		[SerializeField, Tooltip("Не подходить ближе этой доли от attackRange (не приближаться вообще)")]
		private float maintainRangeMaxFactor = 1.00f; // оставлено для ясности
		[SerializeField, Tooltip("Радиус вокруг, в котором 'много юнитов' считается опасным")]
		private float crowdRadius = 2.5f;
		[SerializeField, Tooltip("Порог количества врагов вокруг для побега")]
		private int crowdThreshold = 3;
		[SerializeField, Tooltip("Вероятность остаться стрелять при давлении (1.0=всегда, 0.0=всегда бежать)")]
		private float crowdStayProbability = 0.70f; // 70% остаётся, 30% убегает
		[SerializeField, Tooltip("Доля юнитов (не танков), которые будут подходить к цели, если она вне радиуса атаки")]
		private float approachFraction = 0.5f; // половина
		[SerializeField, Tooltip("Отступ от границ экрана при планировании точки назначения")]
		private float screenMargin = 0.2f;

	[Header("Debug")]
	[SerializeField] private bool verboseLogs = false;

	private float nextThinkTime;
	private readonly Dictionary<Unit, float> unitCooldownUntil = new();
	private readonly Dictionary<Unit, bool> unitApproachPref = new();
	private readonly Dictionary<Unit, float> unitBaseStep = new();

	private void Update()
	{
		// Не двигаем врагов, пока игрок расставляется
		if (BattlePlacementState.IsPlacementActive) return;
		if (Time.time < nextThinkTime) return;
		nextThinkTime = Time.time + Mathf.Max(0.25f, thinkIntervalSeconds);

		RunOneDecision();
	}

	private void RunOneDecision()
	{
		// Сдвигаем всех подходящих синих юнитов небольшим шагом
		var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
		for (int i = 0; i < all.Length; i++)
		{
			var u = all[i];
			try
			{
				if (!u) continue;
				if (u.host) continue;             // only blue
				if (u.isPassive) continue;        // ignore obstacles
				if (u.health <= 0) continue;      // dead
				if (u.moveSpeed <= 0.01f) continue;
				if (unitCooldownUntil.TryGetValue(u, out var until) && Time.time < until) continue;
				var flagsProbe = u.transform.Find("Visual")?.GetComponent<SPAnimatorFlags>() ?? u.GetComponentInChildren<SPAnimatorFlags>(true);
				if (flagsProbe != null && flagsProbe.IsMoving) continue; // уже движется

					// Цель: ближайший чёрный (для оценки дистанции).
				Vector3 from = u.transform.position;
				var nearest = FindNearestPlayerUnit(from);

					// Решение о движении:
					// 1) Если вокруг много врагов — с вероятностью (1 - crowdStayProbability) убегаем от их центра, иначе остаёмся.
					// 2) Иначе: если слишком близко к цели (< minFactor * attackRange) — отходим назад.
					// 3) Если далеко — танки всегда подходят, половина прочих подходит, чтобы войти в радиус атаки.
					Vector3 moveDir = Vector3.zero;
					bool isTank = IsTank(u);

					// толпа вокруг?
					Vector3 enemiesCentroid;
					int enemyCount = CountEnemiesAndCentroidAround(from, crowdRadius, out enemiesCentroid);
					if (enemyCount >= Mathf.Max(1, crowdThreshold))
					{
						bool flee = !isTank && (Random.value > Mathf.Clamp01(crowdStayProbability)); // танк не отступает
						if (flee)
						{
							Vector3 away = (from - enemiesCentroid);
							away.z = 0f;
							if (away.sqrMagnitude > 0.0001f) moveDir = away.normalized;
						}
					}
					else if (nearest)
					{
						float range = Mathf.Max(0.01f, u.attackRange > 0 ? u.attackRange : 1.5f);
						// Выбор опорной цели для направления: обычно ближайший, иногда — самый дальний
						var approachRef = (Random.value < Mathf.Clamp01(farTargetProbability)) ? FindFarthestPlayerUnit(from) : nearest;
						Vector3 to = approachRef ? approachRef.transform.position : nearest.transform.position;
						float dist = Vector2.Distance(from, to);
						float minDist = range * Mathf.Clamp01(maintainRangeMinFactor);

						// слишком близко -> отходим
						if (!isTank && dist < minDist)
						{
							Vector3 away = (from - to);
							away.z = 0f;
							if (away.sqrMagnitude > 0.0001f) moveDir = away.normalized;
						}
						else if (dist > range)
						{
							// цель вне радиуса — половина юнитов подходит, танк подходит всегда
							bool shouldApproach = isTank || GetApproachPreference(u);
							if (shouldApproach)
							{
								Vector3 towards = (to - from);
								towards.z = 0f;
								if (towards.sqrMagnitude > 0.0001f) moveDir = towards.normalized;
							}
						}
					}

					// Выполнить шаг, если есть направление
					if (moveDir.sqrMagnitude > 0.0001f)
					{
						// Персональная длина шага + рандомная коррекция от ситуации
						float baseStep = GetUnitBaseStep(u);
						float stepJitter = Random.Range(0.8f, 1.3f);
						float stepLen = Mathf.Max(0.01f, baseStep * stepJitter);

						// Стараться не перелетать целевой 'коридор' стрельбы: двигаемся к границе зоны атаки
						// Для подхода: останавливаться на ~[0.85..1.0]*range от цели
						// Для отхода: стремиться увеличить дистанцию как минимум до minDist
						float range = Mathf.Max(0.01f, u.attackRange > 0 ? u.attackRange : 1.5f);
						float capLen = stepLen;
						if (nearest)
						{
							var toPos = nearest.transform.position;
							float distNow = Vector2.Distance(from, toPos);
							if (Vector3.Dot(moveDir, (toPos - from)) > 0f)
							{
								// Идём к цели: ограничим шаг, чтобы не зайти слишком глубоко
								float desiredStop = range * Random.Range(0.85f, 1.00f);
								float need = Mathf.Max(0f, distNow - desiredStop);
								capLen = Mathf.Min(stepLen, need + 0.1f);
							}
							else
							{
								// Отходим от цели: стараемся выйти на безопасную дистанцию
								float minDist = range * Mathf.Clamp01(maintainRangeMinFactor);
								float need = Mathf.Max(0f, minDist - distNow);
								capLen = Mathf.Min(stepLen, need + 0.1f);
							}
						}

						if (capLen <= 0.01f) goto SkipMove; // уже достаточно

						Vector3 dest = from + moveDir * capLen;
						dest = ClampToCameraBounds(dest, screenMargin); // не выходить за экран
						dest.z = from.z;
						if (verboseLogs) Debug.Log($"[SPEnemyBot] Move '{u.name}' -> {dest} (dir={moveDir}, enemiesNear={enemyCount})");
						StartCoroutine(MoveUnit(u, dest));
						unitCooldownUntil[u] = Time.time + Mathf.Max(0.1f, perUnitCooldownSeconds);
					}

					SkipMove: ;
			}
			catch { }
		}
	}

	private static bool IsTank(Unit u)
		{
			try
			{
				return u != null && !string.IsNullOrEmpty(u.unitType) && u.unitType.Contains("Tank");
			}
			catch { return false; }
		}

	private float GetUnitBaseStep(Unit u)
	{
		if (!u) return Mathf.Max(0.01f, stepDistance);
		if (unitBaseStep.TryGetValue(u, out var s)) return s;
		float minS = (stepDistanceMin > 0.001f) ? stepDistanceMin : Mathf.Max(0.01f, stepDistance * 0.6f);
		float maxS = (stepDistanceMax > 0.001f) ? stepDistanceMax : Mathf.Max(minS + 0.01f, stepDistance * 1.4f);
		float pick = Random.Range(minS, maxS);
		unitBaseStep[u] = pick;
		return pick;
	}

	private bool GetApproachPreference(Unit u)
		{
			if (!u) return false;
			if (unitApproachPref.TryGetValue(u, out var pref)) return pref;
			bool decided = Random.value < Mathf.Clamp01(approachFraction);
			unitApproachPref[u] = decided;
			return decided;
		}

	private static Vector3 ClampToCameraBounds(Vector3 p, float margin)
		{
			var cam = Camera.main;
			if (cam == null) return p;
			float halfH = cam.orthographicSize;
			float halfW = halfH * cam.aspect;
			float minX = -halfW + margin;
			float maxX =  halfW - margin;
			float minY = -halfH + margin;
			float maxY =  halfH - margin;
			p.x = Mathf.Clamp(p.x, minX, maxX);
			p.y = Mathf.Clamp(p.y, minY, maxY);
			return p;
		}

	private Unit FindNearestPlayerUnit(Vector3 pos)
	{
		Unit best = null;
		float bestSqr = float.PositiveInfinity;
		var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
		for (int i = 0; i < all.Length; i++)
		{
			var u = all[i];
			try
			{
				if (!u) continue;
				if (!u.host) continue;       // player black only
				if (u.isPassive) continue;
				if (u.health <= 0) continue;
				float sqr = (u.transform.position - pos).sqrMagnitude;
				if (sqr < bestSqr) { bestSqr = sqr; best = u; }
			}
			catch { }
		}
		return best;
	}

	private Unit FindFarthestPlayerUnit(Vector3 pos)
	{
		Unit best = null;
		float bestSqr = -1f;
		var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
		for (int i = 0; i < all.Length; i++)
		{
			var u = all[i];
			try
			{
				if (!u) continue;
				if (!u.host) continue;       // player black only
				if (u.isPassive) continue;
				if (u.health <= 0) continue;
				float sqr = (u.transform.position - pos).sqrMagnitude;
				if (sqr > bestSqr) { bestSqr = sqr; best = u; }
			}
			catch { }
		}
		return best;
	}

	private IEnumerator MoveUnit(Unit unit, Vector3 target)
	{
		if (!unit) yield break;
		Transform tr = null;
		try { tr = unit.transform; } catch { yield break; }
		if (!tr) yield break;

		var vis = tr.Find("Visual");
		var flags = vis ? vis.GetComponent<SPAnimatorFlags>() : unit.GetComponentInChildren<SPAnimatorFlags>(true);

		// Только движение: включим анимацию ходьбы, скорость — остальное уже реализовано в других системах
		if (flags != null)
		{
			flags.SetMoving(true);
			flags.SetSpeed(Mathf.Max(0.01f, unit.moveSpeed));
		}
		try { unit.FaceTowardsX(target.x); } catch { }

		try { target.z = tr.position.z; } catch { yield break; }
		if (verboseLogs) Debug.Log($"[SPEnemyBot] Move start '{unit.name}' -> {target}");

		while (true)
		{
			if (!unit) yield break;
			try { tr = unit.transform; } catch { yield break; }
			if (!tr) yield break;

			var cur = tr.position;
			if (Vector2.Distance(cur, target) <= stopDistance) break;
			var next = (Vector3)Vector2.MoveTowards(cur, target, unit.moveSpeed * Time.deltaTime);
			// Respect passive obstacles (walls)
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
							Vector3 d = (next - cur).normalized;
							try { tr.position = hits[i].point - (Vector2)(d * 0.02f); } catch { }
							blocked = true;
							break;
						}
					}
					catch { }
				}
			}
			if (blocked) break;
			try { tr.position = next; } catch { yield break; }
			if (flags != null) flags.SetSpeed(Mathf.Max(0.01f, unit.moveSpeed));
			yield return null;
		}
		if (flags != null)
		{
			flags.SetSpeed(0f);
			flags.SetMoving(false);
		}
		if (verboseLogs) Debug.Log($"[SPEnemyBot] Move end '{unit.name}' at {unit.transform.position}");
	}

	private int CountEnemiesAndCentroidAround(Vector3 pos, float radius, out Vector3 centroid)
		{
			Vector3 sum = Vector3.zero;
			int count = 0;
			var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
			for (int i = 0; i < all.Length; i++)
			{
				var u = all[i];
				try
				{
					if (!u) continue;
					if (!u.host) continue;   // враги для синего — это чёрные
					if (u.isPassive) continue;
					if (u.health <= 0) continue;
					if (Vector2.Distance(pos, u.transform.position) <= radius)
					{
						count++;
						sum += u.transform.position;
					}
				}
				catch { }
			}
			centroid = (count > 0) ? (sum / count) : pos;
			return count;
		}
}


