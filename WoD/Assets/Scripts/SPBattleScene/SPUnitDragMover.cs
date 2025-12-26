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

	private SPAnimatorFlags animFlags;

	private void EnsureAnimFlags()
	{
		if (animFlags != null) return;
		animFlags = GetComponent<SPAnimatorFlags>() ?? GetComponentInChildren<SPAnimatorFlags>() ?? GetComponentInParent<SPAnimatorFlags>();
		if (animFlags != null) Debug.Log($"[SPUnitDragMover] Found SPAnimatorFlags on '{unit?.name}'");
	}

	void Awake()
	{
		cam  = Camera.main;
		unit = GetComponentInParent<Unit>();

		// Создаём отдельный LineRenderer для линии перетаскивания, чтобы не конфликтовать с флагом
		var traceGo = transform.Find("TraceLineLR") ? transform.Find("TraceLineLR").gameObject : null;
		if (traceGo == null)
		{
			traceGo = new GameObject("TraceLineLR");
			traceGo.transform.SetParent(transform, false);
		}
		line = traceGo.GetComponent<LineRenderer>();
		if (line == null) line = traceGo.AddComponent<LineRenderer>();
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

		EnsureAnimFlags();
	}

	private bool CanControl() => unit && unit.moveSpeed > 0.01f;

	public void OnPointerDown(PointerEventData e)
	{
		// Во время расстановки — управление запрещено
		if (BattlePlacementState.IsPlacementActive) return;
		if (!CanControl()) return;
		EnsureAnimFlags();
		// Разрешаем ретаргет во время движения — не блокируем нажатие при IsMoving
		Debug.Log($"[SPUnitDragMover] Down on '{unit?.name}' at screen={e.position}");

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
		// Повернуться в сторону цели
		try { unit.FaceTowardsX(target.x); } catch {}
		Debug.Log($"[SPUnitDragMover] Up on '{unit?.name}', target={target}, blocked={_blocked}");

		StopAllCoroutines();
		StartCoroutine(MoveToAndFinish(target, unit.moveSpeed));
	}

	private IEnumerator MoveToAndFinish(Vector3 target, float speed)
	{
		EnsureAnimFlags();
		// Перед началом движения — немедленно отменим атаку и переключим анимацию/флаги в режим движения
		try
		{
			var auto = GetComponent<SPUnitAutoAttack>() ?? GetComponentInChildren<SPUnitAutoAttack>() ?? GetComponentInParent<SPUnitAutoAttack>();
			if (auto != null) auto.ForceStopAttack();
		}
		catch { }
		try { unit?.SetAttacking(false); } catch { }
		if (animFlags != null)
		{
			animFlags.SetAttacking(false);
			animFlags.SetMoving(true);
			animFlags.SetSpeed(Mathf.Max(0.01f, speed));
		}
		Debug.Log($"[SPUnitDragMover] Move start '{unit?.name}' speed={speed} -> {target}");
		target.z = unit.transform.position.z;
		while (Vector2.Distance(unit.transform.position, target) > stopDistance)
		{
			var cur  = unit.transform.position;
			var next = (Vector3)Vector2.MoveTowards(cur, target, speed * Time.deltaTime);
			if (animFlags != null) animFlags.SetSpeed(Mathf.Max(0.01f, speed));
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
		if (animFlags != null)
		{
			animFlags.SetSpeed(0f);
			animFlags.SetMoving(false);
		}
		Debug.Log($"[SPUnitDragMover] Move end '{unit?.name}' at {unit?.transform.position}");
	}

	private Vector3 ScreenToWorld(Vector2 screenPos)
	{
		float z = Mathf.Abs((cam ? cam.transform.position.z : -10f) - unit.transform.position.z);
		var wp = (cam ? cam : Camera.main).ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, z));
		wp.z = unit.transform.position.z;
		return wp;
	}
}


