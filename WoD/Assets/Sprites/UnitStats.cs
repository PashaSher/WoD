using UnityEngine;
// using <твой namespace с UnitType/UnitPrices>;  // добавь, если они в namespace

[CreateAssetMenu(fileName = "UnitStats", menuName = "Units/Unit Stats", order = 0)]
public class UnitStats : ScriptableObject
{
    public enum UnitKind
    {
        Normal,
        Vehicle,
        Stationary, // не двигается, не поворачивается, стреляет только вперёд
        Passive     // не атакует (на будущее)
    }

    public UnitType unitType;     // из UnitTypes.cs
    [Header("Classification")]
    public UnitKind kind = UnitKind.Normal;

    public int health = 100;
    public int damage = 10;
    public float attackRange = 4f;
    public float moveSpeed = 2.5f;

    [Header("Combat Timing")]
    public float fireRate = 1.0f;        // выстрелов в секунду
    [Range(0f, 1f)] public float accuracy = 0.9f;  // 1 = идеально, 0 = рандом
    public float accuracySpread = 0.3f;  // максимальный разброс цели (мировые единицы)

    [Header("Visual")]
    public Sprite sprite; 
    public AnimatorOverrideController animatorOverride;

    [Header("Prefab (optional)")]
    public GameObject unitPrefab; // Если задан — спавним этот префаб вместо базового Unit_Root

    [Header("Projectile")]
    public ProjectileStats projectileStats; // настройки снаряда для этого юнита

}
