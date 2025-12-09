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
	private bool _dying;
	private float _spawnTime;

	// Взрыв/эффект уничтожения
	private SpriteRenderer _explosionRenderer;
	private GameObject _explosionGo;

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
		_spawnTime = Time.time;
		// One-time init log (safe, lightweight)
		try
		{
			Debug.Log($"[SPProjectile] Init owner={(owner ? owner.name : "null")}, start={transform.position}, target={this.target}, speed={(stats ? stats.speed : 0f)}, dmg={(stats ? stats.damage : 0)}, splash={(stats ? stats.splashRadius : 0f)}, sprite={(stats && stats.sprite ? stats.sprite.name : "null")}");
		}
		catch { }
	}

	private void Update()
	{
		if (!stats || _hitApplied || _dying) return;

		// Страховка по времени жизни
		if (stats.maxLifetime > 0f && (Time.time - _spawnTime) >= stats.maxLifetime)
		{
			BeginDeath();
			return;
		}
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
					if (u.isPassive) { try { Debug.Log($"[SPProjectile] Hit passive '{u.name}' -> destroy"); } catch {} BeginDeath(); return; }
					// Если это одиночный снаряд (без сплэша) — наносим урон прямо по Unit, которого задел Linecast.
					// Это надёжнее, чем пересчитывать ближайшего к точке попадания (у юнитов центр transform может быть далеко от хитбокса).
					if (stats != null && stats.splashRadius <= 0f)
					{
						_hitApplied = true;
						int dmg = Mathf.Max(1, stats.damage);
						u.TakeDamage(dmg);
						try { Debug.Log($"[SPProjectile] Hit '{u.name}' at {hits[i].point} -> dmg={dmg}"); } catch {}
					}
					else
					{
						// Сплэш — рассчитаем урон по радиусу
						ApplyDamageAt(hits[i].point);
						try { Debug.Log($"[SPProjectile] Hit '{u.name}' at {hits[i].point}"); } catch {}
					}
					BeginDeath();
					return;
				}
				catch { }
			}
		}

		transform.position = to;
		_prevPos = transform.position;
		if (Vector2.Distance(transform.position, target) <= 0.02f) OnArrived();
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
			// одиночная цель — ближайший в малом радиусе (берём центр коллайдера, если есть)
			float bestSqr = float.PositiveInfinity;
			Unit best = null;
			Vector2 bestCenter = point;
			foreach (var u in all)
			{
				try
				{
					if (!u || (owner && u.host == owner.host)) continue;
					if (u.isPassive) continue;
					// центр хитбокса (если есть), иначе transform.position
					Vector3 center3 = u.transform.position;
					var vis = u.transform.Find("Visual");
					if (vis)
					{
						var col = vis.GetComponent<Collider2D>();
						if (!col)
						{
							var cols = vis.GetComponentsInChildren<Collider2D>(true);
							if (cols != null && cols.Length > 0) col = cols[0];
						}
						if (col) center3 = col.bounds.center;
					}
					Vector2 center = new Vector2(center3.x, center3.y);
					float sqr = (center - point).sqrMagnitude;
					if (sqr < bestSqr) { bestSqr = sqr; best = u; }
				}
				catch { }
			}
			if (best && bestSqr <= 0.3f * 0.3f) best.TakeDamage(Mathf.Max(1, stats.damage));
		}
	}

	private void OnArrived()
	{
		try { Debug.Log($"[SPProjectile] Arrived at {transform.position}"); } catch {}
		if (!_hitApplied) ApplyDamageAt(transform.position);
		BeginDeath();
	}

	private void BeginDeath()
	{
		if (_dying) return;
		_dying = true;

		// отключаем спрайт снаряда сразу
		if (spriteRenderer != null) spriteRenderer.enabled = false;

		// рисуем спрайт взрыва поверх (как в MP)
		if (stats != null && stats.destroySprite != null)
		{
			if (_explosionRenderer == null)
			{
				_explosionGo = new GameObject("Explosion");
				_explosionGo.transform.position = transform.position;
				_explosionGo.transform.rotation = Quaternion.identity;
				_explosionGo.transform.localScale = Vector3.one;
				_explosionRenderer = _explosionGo.AddComponent<SpriteRenderer>();
				if (spriteRenderer != null)
				{
					_explosionRenderer.sortingLayerID = spriteRenderer.sortingLayerID;
					_explosionRenderer.sortingOrder = spriteRenderer.sortingOrder + 1;
					_explosionRenderer.color = spriteRenderer.color;
				}
				_explosionRenderer.flipX = false;
				_explosionRenderer.flipY = false;
			}
			else
			{
				_explosionGo.transform.position = transform.position;
				_explosionGo.transform.rotation = Quaternion.identity;
				_explosionRenderer.flipX = false;
				_explosionRenderer.flipY = false;
			}
			_explosionRenderer.sprite = stats.destroySprite;
			_explosionRenderer.enabled = true;
		}

		// маштаб вспышки
		if (stats != null && stats.destroyScale != Vector2.zero)
		{
			float sx = Mathf.Abs(stats.destroyScale.x);
			float sy = Mathf.Abs(stats.destroyScale.y);
			if (_explosionGo != null)
				_explosionGo.transform.localScale = new Vector3(sx, sy, 1f);
			else
				transform.localScale = new Vector3(sx, sy, 1f);
		}

		StartCoroutine(DeathRoutine());
	}

	private System.Collections.IEnumerator DeathRoutine()
	{
		float dur = (stats != null && stats.destroyDuration > 0f) ? stats.destroyDuration : 1f;
		yield return new WaitForSeconds(dur);
		if (_explosionGo) Destroy(_explosionGo);
		if (this) Destroy(gameObject);
	}
}




