using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

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

        // В SP запрещаем выбор/клик по юниту во время его движения
        var scene = SceneManager.GetActiveScene();
        if (scene.name == "SPBattleScene")
        {
            var spFlags = GetComponentInParent<SPAnimatorFlags>();
            if (spFlags != null && spFlags.IsMoving) return;
        }

        Debug.Log(
            $"[TAP] {unit.gameObject.name} | key={unit.unitKey} | type={unit.unitType} | host={unit.host} | pos={unit.transform.position}"
        );
    }
}
