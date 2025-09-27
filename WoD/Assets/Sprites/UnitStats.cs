using UnityEngine;
// using <твой namespace с UnitType/UnitPrices>;  // добавь, если они в namespace

[CreateAssetMenu(fileName = "UnitStats", menuName = "Units/Unit Stats", order = 0)]
public class UnitStats : ScriptableObject
{
    public UnitType unitType;     // из UnitTypes.cs
    public int health = 100;
    public int damage = 10;
    public float attackRange = 4f;
    public float moveSpeed = 2.5f;

    [Header("Visual")]
    public Sprite sprite; 
    public AnimatorOverrideController animatorOverride;

}
