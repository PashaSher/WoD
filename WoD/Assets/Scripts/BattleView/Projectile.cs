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

        transform.position = start;
        initialized = true;
    }

    public void BindRef(DatabaseReference projRef)
    {
        this.projRef = projRef;
    }

    private void Update()
    {
        if (!initialized || stats == null) return;

        float step = stats.speed * Time.deltaTime;
        transform.position = Vector2.MoveTowards(transform.position, target, step);

        if (Vector2.Distance(transform.position, target) <= 0.02f)
        {
            OnArrived();
        }
    }

    private async void OnArrived()
    {
        if (createdByLocal)
        {
            TryApplyDamageAtPoint(target);
            if (projRef != null)
            {
                await projRef.RemoveValueAsync();
            }
            if (this) Destroy(gameObject);
        }
        else
        {
            // Не владелец: ждём удаление с RTDB, но если уже прибыли — просто уничтожим локально
            if (this) Destroy(gameObject);
        }
    }

    private void TryApplyDamageAtPoint(Vector2 point)
    {
        if (owner == null || stats == null) return;

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
            best.TakeDamage(Mathf.Max(1, stats.damage));
        }
    }
}


