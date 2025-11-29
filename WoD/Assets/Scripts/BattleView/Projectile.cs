using System.Collections;
using System.Collections.Generic;
using Firebase.Database;
using UnityEngine;

/// <summary>
/// Клиентская визуализация и воспроизведение снаряда по данным RTDB.
/// Не создаёт записи сам (этим занимается владелец через UnitAutoAttack),
/// только читает и движется из start → target со скоростью из ProjectileStats.
/// По достижении цели проверяет попадание и применяет урон (только если этот клиент — владелец? нет, урон снимает тот, кто достиг).
/// После завершения удаляет ноду снаряда в RTDB, если она ещё существует и если наш клиент создал её.
/// </summary>
public class Projectile : MonoBehaviour
{
    [SerializeField] private SpriteRenderer spriteRenderer;
    [SerializeField] private ProjectileStats stats;

    // RTDB identity
    private Unit owner;
    private string projectileKey; // уникальный ключ в /projectiles
    private DatabaseReference projRef;   // .../sessions/{sid}/projectiles/{key}

    private Vector3 start;
    private Vector3 target;
    private bool initialized;
    private bool createdByLocal;  // чтобы только создатель удалял узел
    private Vector3 _prevPos;
    private bool _hitApplied;     // единичное нанесение урона этим снарядом
    private bool _dying;          // запущена анимация уничтожения
    private Coroutine _deathRoutine;
    private SpriteRenderer _explosionRenderer;
	private GameObject _explosionGo;

	// Цель-юнит может исчезнуть к моменту столкновения (умер/удалён).
	// В таком случае снаряд просто летит до точки и исчезает — без урона и без ошибок.
	private static bool IsAlive(Unit u)
	{
		try
		{
			if (u == null) return false;
			if (!u) return false; // Unity destroyed check
			if (!u.isActiveAndEnabled) return false;
			return u.health > 0;
		}
		catch { return false; }
	}

    public void Init(Unit owner, ProjectileStats stats, string key, Vector2 startPos, Vector2 targetPos, bool createdByLocal)
    {
        this.owner = owner;
        this.stats = stats;
        this.projectileKey = key;
        this.createdByLocal = createdByLocal;
        start = new Vector3(startPos.x, startPos.y, owner.transform.position.z);
        target = new Vector3(targetPos.x, targetPos.y, owner.transform.position.z);

        if (!spriteRenderer)
            spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
        if (stats && stats.sprite)
            spriteRenderer.sprite = stats.sprite;

        // применим маштаб, если задан
        if (stats != null)
        {
            if (stats.scale == Vector2.zero)
                transform.localScale = Vector3.one;
            else
                transform.localScale = new Vector3(stats.scale.x, stats.scale.y, 1f);
        }

        transform.position = start;
        _prevPos = start;
        initialized = true;
    }

    public void BindRef(DatabaseReference projRef)
    {
        this.projRef = projRef;
    }

    private void Update()
    {
        if (!initialized || stats == null) return;
        if (_hitApplied || _dying) return; // уже нанесли урон/умираем — не двигаемся

        float step = stats.speed * Time.deltaTime;

        // Continuous collision check along movement path
        Vector2 from = _prevPos;
        Vector2 to   = Vector2.MoveTowards((Vector2)transform.position, (Vector2)target, step);

        // Поворот по направлению движения
        Vector2 dir = to - (Vector2)transform.position;
        if (dir.sqrMagnitude > 0.0001f)
        {
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
        }

		// Нанесение урона выполняется только на HOST, независимо от того, кто создавал локально объект
		if (Globalflags.ifHost)
        {
            // Sweep linecast from previous to next pos; check any enemy Unit collider
			var hits = Physics2D.LinecastAll(from, to);
            if (hits != null && hits.Length > 0)
            {
                for (int i = 0; i < hits.Length; i++)
                {
                    try
                {
                    var go = hits[i].collider ? hits[i].collider.gameObject : null;
                    if (!go) continue;
                    var unitHit = go.GetComponentInParent<Unit>();
                    if (!unitHit) continue;
						// Если цель уже умерла/удалена — игнорируем попадание и летим дальше
						if (!IsAlive(unitHit)) continue;
						// Пассивные объекты блокируют снаряд, но не получают урон
						if (unitHit.isPassive)
						{
							OnLocalHitCleanup();
							return;
						}
                        // мог быть уничтожен между кадрами
                        bool sameSide;
                        try { sameSide = (owner != null && unitHit.host == owner.host); } catch { continue; }
                        if (sameSide) continue; // ignore friendlies

					// Apply damage centered at impact point; if AoE didn't hit anyone, ensure direct hit gets damage
					Vector2 impactPoint = hits[i].point;
					TryApplyDamageAtPoint(impactPoint);
					if (!_hitApplied)
					{
							// Ещё раз проверим, что цель жива непосредственно перед нанесением урона
							if (IsAlive(unitHit))
						unitHit.TakeDamage(Mathf.Max(1, stats.damage));
						_hitApplied = true;
					}
					OnLocalHitCleanup();
                    return;
                    }
                    catch { /* цель могла исчезнуть в этот же кадр */ }
                }
            }
        }

        transform.position = to;
        _prevPos = transform.position;

        if (Vector2.Distance(transform.position, target) <= 0.02f)
        {
            OnArrived();
        }
    }

    private async void OnLocalHitCleanup()
    {
		// Удаление узла БД — только на HOST (источник авторитетного состояния)
		if (Globalflags.ifHost && projRef != null)
        {
            await projRef.RemoveValueAsync();
        }
        BeginDeath();
    }

    private async void OnArrived()
    {
		if (Globalflags.ifHost)
        {
            if (!_hitApplied)
            {
                TryApplyDamageAtPoint(target);
            }
			if (projRef != null)
            {
                await projRef.RemoveValueAsync();
            }
            BeginDeath();
        }
        else
        {
			// Не HOST: ждём удаление с RTDB, но если уже прибыли — просто уничтожим локально
            BeginDeath();
        }
    }

	private void TryApplyDamageAtPoint(Vector2 point)
    {
        if (owner == null || stats == null) return;
        if (_hitApplied) return; // уже нанесли урон ранее

		var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);

		// Если splashRadius > 0 — наносим урон всем врагам в радиусе с линейным затуханием
		if (stats.splashRadius > 0f)
		{
			float radius = Mathf.Max(0.01f, stats.splashRadius);
			int baseDamage = Mathf.Max(1, stats.damage);
			bool anyHit = false;
			foreach (var u in all)
			{
				try
			{
				if (!u || u.host == owner.host) continue;
				if (!IsAlive(u)) continue;
					Vector2 pos;
					try { pos = (Vector2)u.transform.position; } catch { continue; }
					float dist = Vector2.Distance(pos, point);
				if (dist > radius) continue;
				float t = 1f - (dist / radius); // 1 в центре, 0 на краю
				int dmg = Mathf.RoundToInt(baseDamage * Mathf.Clamp01(t));
				if (dmg <= 0) continue;
				if (IsAlive(u)) u.TakeDamage(dmg);
				anyHit = true;
				}
				catch { }
			}
			if (anyHit) _hitApplied = true;
			return;
		}

		// Иначе — поведение как раньше: один ближайший в малом радиусе
		float hitRadius = Mathf.Max(0.1f, 0.3f);
		Unit best = null;
		float bestSqr = float.PositiveInfinity;
		foreach (var u in all)
		{
			try
		{
			if (!u || u.host == owner.host) continue; // только враги
				if (u.isPassive) continue; // препятствия не получают урон
				if (!IsAlive(u)) continue;
				Vector2 pos;
				try { pos = (Vector2)u.transform.position; } catch { continue; }
				float sqr = (pos - point).sqrMagnitude;
			if (sqr < bestSqr)
			{
				bestSqr = sqr; best = u;
			}
			}
			catch { }
		}
		if (best != null && bestSqr <= hitRadius * hitRadius)
		{
			_hitApplied = true;
			if (IsAlive(best))
			best.TakeDamage(Mathf.Max(1, stats.damage));
		}
    }

    public void BeginDeath()
    {
        if (_dying) return;
        _dying = true;

        // отключаем спрайт снаряда сразу
        if (spriteRenderer != null) spriteRenderer.enabled = false;

        // рисуем спрайт взрыва отдельным рендерером, если задан
        if (stats != null && stats.destroySprite != null)
        {
			// Создаём обособленный объект взрыва в мировых координатах, чтобы он всегда был "вверх" (без поворотов и флипов)
			if (_explosionRenderer == null)
			{
				_explosionGo = new GameObject("Explosion");
				_explosionGo.transform.position = transform.position;
				_explosionGo.transform.rotation = Quaternion.identity; // всегда смотрит вверх
				_explosionGo.transform.localScale = Vector3.one;       // без наследования флипа
				_explosionRenderer = _explosionGo.AddComponent<SpriteRenderer>();
				if (spriteRenderer != null)
				{
					_explosionRenderer.sortingLayerID = spriteRenderer.sortingLayerID;
					_explosionRenderer.sortingOrder = spriteRenderer.sortingOrder + 1;
					_explosionRenderer.color = spriteRenderer.color;
				}
				// Явно отключаем флипы, чтобы не переворачивался
				_explosionRenderer.flipX = false;
				_explosionRenderer.flipY = false;
			}
			else
			{
				// Обновим позицию на случай, если BeginDeath вызван при движении
				_explosionGo.transform.position = transform.position;
				_explosionGo.transform.rotation = Quaternion.identity;
				_explosionRenderer.flipX = false;
				_explosionRenderer.flipY = false;
			}
			_explosionRenderer.sprite = stats.destroySprite;
			_explosionRenderer.enabled = true;
        }

        // применим маштаб вспышки, если задан
        if (stats != null && stats.destroyScale != Vector2.zero)
        {
			// Положительный масштаб для избежания переворотов
			float sx = Mathf.Abs(stats.destroyScale.x);
			float sy = Mathf.Abs(stats.destroyScale.y);
			if (_explosionGo != null)
				_explosionGo.transform.localScale = new Vector3(sx, sy, 1f);
			else
				transform.localScale = new Vector3(sx, sy, 1f);
        }

        if (_deathRoutine != null) StopCoroutine(_deathRoutine);
        _deathRoutine = StartCoroutine(DeathRoutine());
    }

    private IEnumerator DeathRoutine()
    {
        float dur = (stats != null && stats.destroyDuration > 0f) ? stats.destroyDuration : 1f;
        yield return new WaitForSeconds(dur);
		if (_explosionGo) Destroy(_explosionGo);
        if (this) Destroy(gameObject);
    }
}


