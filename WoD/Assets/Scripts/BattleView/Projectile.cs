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

        if (createdByLocal)
        {
            // Sweep linecast from previous to next pos; check any enemy Unit collider
            var hits = Physics2D.LinecastAll(from, to);
            if (hits != null && hits.Length > 0)
            {
                for (int i = 0; i < hits.Length; i++)
                {
                    var go = hits[i].collider ? hits[i].collider.gameObject : null;
                    if (!go) continue;
                    var unitHit = go.GetComponentInParent<Unit>();
                    if (!unitHit) continue;
                    if (owner != null && unitHit.host == owner.host) continue; // ignore friendlies

                    // Apply damage and destroy projectile
                    _hitApplied = true; // помечаем до нанесения, чтобы исключить двойное срабатывание
                    unitHit.TakeDamage(Mathf.Max(1, stats.damage));
                    OnLocalHitCleanup();
                    return;
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
        if (projRef != null)
        {
            await projRef.RemoveValueAsync();
        }
        BeginDeath();
    }

    private async void OnArrived()
    {
        if (createdByLocal)
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
            // Не владелец: ждём удаление с RTDB, но если уже прибыли — просто уничтожим локально
            BeginDeath();
        }
    }

    private void TryApplyDamageAtPoint(Vector2 point)
    {
        if (owner == null || stats == null) return;
        if (_hitApplied) return; // уже нанесли урон ранее

        // простая проверка попадания: найти ближайший вражеский Unit в небольшом радиусе
        float hitRadius = Mathf.Max(0.1f, stats.splashRadius > 0 ? stats.splashRadius : 0.3f);

        var all = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
        Unit best = null;
        float bestSqr = float.PositiveInfinity;
        foreach (var u in all)
        {
            if (!u || u.host == owner.host) continue; // только враги
            float sqr = ((Vector2)u.transform.position - point).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr; best = u;
            }
        }

        if (best != null && bestSqr <= hitRadius * hitRadius)
        {
            _hitApplied = true;
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
            if (_explosionRenderer == null)
            {
                var go = new GameObject("Explosion");
                go.transform.SetParent(transform, false);
                _explosionRenderer = go.AddComponent<SpriteRenderer>();
                if (spriteRenderer != null)
                {
                    _explosionRenderer.sortingLayerID = spriteRenderer.sortingLayerID;
                    _explosionRenderer.sortingOrder = spriteRenderer.sortingOrder + 1;
                    _explosionRenderer.color = spriteRenderer.color;
                    _explosionRenderer.flipX = spriteRenderer.flipX;
                    _explosionRenderer.flipY = spriteRenderer.flipY;
                }
            }
            _explosionRenderer.sprite = stats.destroySprite;
            _explosionRenderer.enabled = true;
        }

        // применим маштаб вспышки, если задан
        if (stats != null && stats.destroyScale != Vector2.zero)
        {
            transform.localScale = new Vector3(stats.destroyScale.x, stats.destroyScale.y, 1f);
        }

        if (_deathRoutine != null) StopCoroutine(_deathRoutine);
        _deathRoutine = StartCoroutine(DeathRoutine());
    }

    private IEnumerator DeathRoutine()
    {
        float dur = (stats != null && stats.destroyDuration > 0f) ? stats.destroyDuration : 1f;
        yield return new WaitForSeconds(dur);
        if (this) Destroy(gameObject);
    }
}


