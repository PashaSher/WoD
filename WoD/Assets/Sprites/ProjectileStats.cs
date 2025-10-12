using UnityEngine;

[CreateAssetMenu(fileName = "ProjectileStats", menuName = "Units/Projectile Stats", order = 0)]
public class ProjectileStats : ScriptableObject
{
    [Header("Damage Model")]
    public int damage = 10;
    public int penetration = 0;          // пробитие сквозь броню/порядок
    public float splashRadius = 0f;      // >0 для AOE

    [Header("Motion")]
    public float speed = 8f;             // скорость полёта
    public float maxLifetime = 5f;       // страховка

    [Header("Visual")]
    public Sprite sprite;                // заглушка, позже заменим анимацией
	public Vector2 scale = new Vector2(1f, 1f); // маштаб спрайта (1,1 по умолчанию)
}



