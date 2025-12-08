using UnityEngine;

/// <summary>
/// Упрощённый снаряд для одиночного режима: без RTDB, с локальным уроном.
/// </summary>
public class SPProjectile : MonoBehaviour
{
	[SerializeField] private SpriteRenderer spriteRenderer;
	[SerializeField] private ProjectileStats stats;

	private Unit owner;
	private Vector3 target;
	private Vector3 _prevPos;
	private bool _hitApplied;

	public void Init(Unit owner, ProjectileStats stats, Vector2 start, Vector2 target)
	{
		this.owner = owner;
		this.stats = stats;
		this.target = new Vector3(target.x, target.y, owner ? owner.transform.position.z : 0f);
		if (!spriteRenderer) spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
		if (stats && stats.sprite) spriteRenderer.sprite = stats.sprite;
		if (stats && stats.scale != Vector2.zero)
			transform.localScale = new Vector3(Mathf.Abs(stats.scale.x), Mathf.Abs(stats.scale.y), 1f);
		transform.position = new Vector3(start.x, start.y, this.target.z);
		_prevPos = transform.position;
	}

	private void Update()
	{
		if (!stats || _hitApplied) return;
		float step = Mathf.Max(0.01f, stats.speed) * Time.deltaTime;

		Vector2 from = _prevPos;
		Vector2 to = Vector2.MoveTowards((Vector2)transform.position, (Vector2)target, step);

		// поворот по движению
		Vector2 dir = to - (Vector2)transform.position;
		if (dir.sqrMagnitude > 0.0001f)
		{
			float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
			transform.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
		}

		// проверка попадания по пути
		var hits = Physics2D.LinecastAll(from, to);
		if (hits != null && hits.Length > 0)
		{
			for (int i = 0; i < hits.Length; i++)
			{
				try
				{
					var go = hits[i].collider ? hits[i].collider.gameObject : null;
					if (!go) continue;
					var u = go.GetComponentInParent<Unit>();
					if (!u) continue;
					if (u == owner) continue;
					if (owner && u.host == owner.host) continue; // только враги
					if (u.isPassive) { Finish(); return; }
					ApplyDamageAt(hits[i].point);
					Finish();
					return;
				}
				catch { }
			}
		}

		transform.position = to;
		_prevPos = transform.position;
		if (Vector2.Distance(transform.position, target) <= 0.02f) Finish();
	}

	private void ApplyDamageAt(Vector2 point)
	{
		if (_hitApplied || stats == null) return;
		_hitApplied = true;
		var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
		if (stats.splashRadius > 0f)
		{
			float radius = Mathf.Max(0.01f, stats.splashRadius);
			int baseDamage = Mathf.Max(1, stats.damage);
			foreach (var u in all)
			{
				try
				{
					if (!u || (owner && u.host == owner.host)) continue;
					if (u.isPassive) continue;
					float dist = Vector2.Distance(u.transform.position, point);
					if (dist > radius) continue;
					float t = 1f - (dist / radius);
					int dmg = Mathf.RoundToInt(baseDamage * Mathf.Clamp01(t));
					if (dmg > 0) u.TakeDamage(dmg);
				}
				catch { }
			}
		}
		else
		{
			// одиночная цель — ближайший в малом радиусе
			float bestSqr = float.PositiveInfinity;
			Unit best = null;
			foreach (var u in all)
			{
				try
				{
					if (!u || (owner && u.host == owner.host)) continue;
					if (u.isPassive) continue;
					float sqr = ((Vector2)u.transform.position - point).sqrMagnitude;
					if (sqr < bestSqr) { bestSqr = sqr; best = u; }
				}
				catch { }
			}
			if (best && bestSqr <= 0.3f * 0.3f) best.TakeDamage(Mathf.Max(1, stats.damage));
		}
	}

	private void Finish()
	{
		Destroy(gameObject);
	}
}


