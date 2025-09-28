using UnityEngine;
using UnityEngine.EventSystems;

public class UnitPointer : MonoBehaviour, IPointerDownHandler
{
    private Unit unit;  // твой скрипт Unit на корне

    private void Awake()
    {
        // Найдём ближайший Unit наверху и кешируем
        unit = GetComponentInParent<Unit>();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (unit == null) return;

        Debug.Log(
            $"[TAP] {unit.gameObject.name} | key={unit.unitKey} | type={unit.unitType} | host={unit.host} | pos={unit.transform.position}"
        );
    }
}
