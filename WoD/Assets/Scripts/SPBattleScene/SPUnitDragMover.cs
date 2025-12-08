using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Локальное перетаскивание юнита для одиночного режима (без RTDB).
/// Разрешено после завершения расстановки.
/// Вешай на объект с коллайдером (обычно child "Visual").
/// </summary>
public class SPUnitDragMover : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
	[SerializeField] private float minDragDistance = 0.05f;
	[SerializeField] private float stopDistance    = 0.02f;

	private Unit unit;
	private LineRenderer line;
	private Camera cam;

	private bool dragging;
	private Vector3 startWorld, currWorld;

	// вспомогательное состояние блокировки линией препятствия
	private bool _blocked;
	private Vector3 _blockedPoint;

	void Awake()
	{
		cam  = Camera.main;
		unit = GetComponentInParent<Unit>();

		// Используем существующий LineRenderer, если он уже есть (например, из MP-скрипта)
		line = gameObject.GetComponent<LineRenderer>();
		if (line == null)
			line = gameObject.AddComponent<LineRenderer>();
		if (line != null)
		{
			line.enabled = false;
			line.positionCount = 2;
			line.useWorldSpace = true;
			line.startWidth = 0.03f;
			line.endWidth   = 0.03f;
			line.numCornerVertices = 2;
			line.numCapVertices    = 2;
		}
	}

	private bool CanControl() => unit && unit.moveSpeed > 0.01f;

	public void OnPointerDown(PointerEventData e)
	{
		// Во время расстановки — управление запрещено
		if (BattlePlacementState.IsPlacementActive) return;
		if (!CanControl()) return;

		dragging   = true;
		startWorld = ScreenToWorld(e.position);
		currWorld  = startWorld;

		line.enabled = true;
		line.SetPosition(0, unit.transform.position);
		line.SetPosition(1, unit.transform.position);
	}

	public void OnDrag(PointerEventData e)
	{
		if (BattlePlacementState.IsPlacementActive) return;
		if (!dragging) return;
		currWorld = ScreenToWorld(e.position);

		_blocked = false;
		_blockedPoint = currWorld;
		// Проверим, не пересекает ли линия пассивное препятствие
		var hits = Physics2D.LinecastAll(unit.transform.position, currWorld);
		if (hits != null && hits.Length > 0)
		{
			for (int i = 0; i < hits.Length; i++)
			{
				try
				{
					var go = hits[i].collider ? hits[i].collider.gameObject : null;
					if (!go) continue;
					var u = go.GetComponentInParent<Unit>();
					if (u != null && u.isPassive)
					{
						_blocked = true;
						_blockedPoint = hits[i].point;
						break;
					}
				}
				catch { }
			}
		}

		line.SetPosition(0, unit.transform.position);
		var end = _blocked ? _blockedPoint : new Vector3(currWorld.x, currWorld.y, unit.transform.position.z);
		line.SetPosition(1, new Vector3(end.x, end.y, unit.transform.position.z));
	}

	public void OnPointerUp(PointerEventData e)
	{
		if (BattlePlacementState.IsPlacementActive) return;
		if (!dragging) return;
		dragging = false;
		line.enabled = false;

		var target = ScreenToWorld(e.position);
		if (_blocked) target = _blockedPoint;
		if (Vector2.Distance(target, unit.transform.position) < minDragDistance) return;

		StopAllCoroutines();
		StartCoroutine(MoveToAndFinish(target, unit.moveSpeed));
	}

	private IEnumerator MoveToAndFinish(Vector3 target, float speed)
	{
		target.z = unit.transform.position.z;
		while (Vector2.Distance(unit.transform.position, target) > stopDistance)
		{
			var cur  = unit.transform.position;
			var next = (Vector3)Vector2.MoveTowards(cur, target, speed * Time.deltaTime);
			// Блокировка препятствием
			bool blocked = false;
			var hits = Physics2D.LinecastAll(cur, next);
			if (hits != null && hits.Length > 0)
			{
				for (int i = 0; i < hits.Length; i++)
				{
					try
					{
						var go = hits[i].collider ? hits[i].collider.gameObject : null;
						if (!go) continue;
						var u = go.GetComponentInParent<Unit>();
						if (u != null && u.isPassive)
						{
							Vector3 dir = (next - cur).normalized;
							unit.transform.position = hits[i].point - (Vector2)(dir * 0.02f);
							blocked = true;
							break;
						}
					}
					catch { }
				}
			}
			if (blocked) break;
			unit.transform.position = next;
			yield return null;
		}
	}

	private Vector3 ScreenToWorld(Vector2 screenPos)
	{
		float z = Mathf.Abs((cam ? cam.transform.position.z : -10f) - unit.transform.position.z);
		var wp = (cam ? cam : Camera.main).ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, z));
		wp.z = unit.transform.position.z;
		return wp;
	}
}


