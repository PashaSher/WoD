using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Database;
using UnityEngine;
using UnityEngine.EventSystems;

public class UnitDragMover : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] private float minDragDistance = 0.05f;
    [SerializeField] private float stopDistance    = 0.02f;

    private Unit unit;
    private LineRenderer line;
    private Camera cam;

    private bool dragging;
    private Vector3 startWorld, currWorld;

    private DatabaseReference stateRef; // lazily initialized

    // cache of remote "moving" to avoid extra writes while unit is in motion
    private bool hasMovingCache;
    private bool movingCache;

    void Awake()
    {
        cam  = Camera.main;
        unit = GetComponentInParent<Unit>();

        line = gameObject.AddComponent<LineRenderer>();
        line.enabled = false;
        line.positionCount = 2;
        line.useWorldSpace = true;
        line.startWidth = 0.03f;
        line.endWidth   = 0.03f;
        line.numCornerVertices = 2;
        line.numCapVertices    = 2;
    }

    private void OnEnable()
    {
        TryAttachMovingListener();
    }

    private void OnDisable()
    {
        if (stateRef != null)
        {
            // Firebase Unity SDK uses +=/-= on ValueChanged
            stateRef.Child("moving").ValueChanged -= OnMovingValueChanged;
        }
    }

    private void TryAttachMovingListener()
    {
        if (!EnsureStateRef()) return;
        stateRef.Child("moving").ValueChanged -= OnMovingValueChanged;
        stateRef.Child("moving").ValueChanged += OnMovingValueChanged;
        // Fire a one-time read to warm cache
        _ = stateRef.Child("moving").GetValueAsync().ContinueWith(t =>
        {
            if (t.IsCompleted && t.Result != null)
            {
                var snap = t.Result;
                var mv = ParseBool(snap.Value);
                hasMovingCache = true;
                movingCache = mv;
            }
        });
    }

    private void OnMovingValueChanged(object sender, ValueChangedEventArgs e)
      {
        hasMovingCache = true;
        movingCache = ParseBool(e.Snapshot?.Value);
        // Allow retarget while moving — do not cancel local drag UI.
    }

    // Attempt to build reference if it isn't ready yet
    private bool EnsureStateRef()
    {
        if (stateRef != null) return true;
        if (unit == null) return false;
        if (string.IsNullOrEmpty(unit.sessionId) || string.IsNullOrEmpty(unit.unitKey))
        {
            return false; // spawner not ready to fill identity
        }

        var root   = FirebaseDatabase.DefaultInstance.RootReference;
        var branch = unit.host ? "hostArmy" : "clientArmy";
        stateRef = root.Child("sessions").Child(unit.sessionId)
                       .Child(branch).Child(unit.unitKey).Child("state");

        // attach listener as soon as we succeed
        TryAttachMovingListener();
        return true;
    }

	private bool CanControl() => unit && Globalflags.ifHost == unit.host && unit.moveSpeed > 0.01f;

    public void OnPointerDown(PointerEventData e)
    {
        // Во время расстановки и до момента, пока оба игрока не готовы — запретить управление
        if (BattlePlacementState.IsPlacementActive || !BattleReadyManager.BothReady) return;
        if (!CanControl()) return;
        if (!EnsureStateRef())
        {
            Debug.LogWarning("[UnitDragMover] Firebase refs not ready yet (sessionId/unitKey).");
            return;
        }

        // Allow starting drag even if moving==true (retarget while moving)
        dragging   = true;
        startWorld = ScreenToWorld(e.position);
        currWorld  = startWorld;

        line.enabled = true;
        line.SetPosition(0, unit.transform.position);
        line.SetPosition(1, unit.transform.position);
    }

    private IEnumerator BeginDragIfNotMoving(Vector2 screenPos)
    {
        var task = stateRef.Child("moving").GetValueAsync();
        while (!task.IsCompleted) yield return null;

        bool remoteMoving = ParseBool(task.Result?.Value);
        hasMovingCache = true;
        movingCache = remoteMoving;

        if (remoteMoving) yield break;

        dragging   = true;
        startWorld = ScreenToWorld(screenPos);
        currWorld  = startWorld;

        line.enabled = true;
        line.SetPosition(0, unit.transform.position);
        line.SetPosition(1, unit.transform.position);
    }

	// вспомогательное состояние блокировки линией препятствия
	private bool _blocked;
	private Vector3 _blockedPoint;

    public void OnDrag(PointerEventData e)
    {
        if (BattlePlacementState.IsPlacementActive || !BattleReadyManager.BothReady) return;
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
        if (BattlePlacementState.IsPlacementActive || !BattleReadyManager.BothReady) return;
        if (!dragging) return;
        dragging = false;
        line.enabled = false;

        if (!EnsureStateRef()) return; // safety

        var target = ScreenToWorld(e.position);
		// Если линия была заблокирована препятствием — не даём пройти «сквозь», используем ближайшую точку
		if (_blocked) target = _blockedPoint;
        if (Vector2.Distance(target, unit.transform.position) < minDragDistance) return;

        // Re-check before write to avoid race
        StartCoroutine(TryCommitMove(target));
    }

    private IEnumerator TryCommitMove(Vector3 target)
    {
		// Немедленно отменяем атаку при старте движения (и пушим в RTDB через Unit)
		try { unit?.SetAttacking(false); } catch {}

        int facing = (target.x >= unit.transform.position.x) ? 1 : -1;

        // SINGLE write: final coordinates + moving=true
        var updates = new Dictionary<string, object>
        {
            ["x"] = target.x,
            ["y"] = target.y,
            ["facing"] = facing,
            ["moving"] = true,
            ["updatedAt"] = ServerValue.Timestamp
        };
        stateRef.UpdateChildrenAsync(updates);

        // locally move and on arrival set moving=false
        StopAllCoroutines();
        StartCoroutine(MoveToAndFinish(target, unit.moveSpeed));
        yield break;
    }

    private IEnumerator MoveToAndFinish(Vector3 target, float speed)
    {
        const float stopDist = 0.02f;
        target.z = unit.transform.position.z;

        while (Vector2.Distance(unit.transform.position, target) > stopDist)
        {
            var cur  = unit.transform.position;
            var next = (Vector3)Vector2.MoveTowards(cur, target, speed * Time.deltaTime);

            // Блокируем движение, если на пути пассивное препятствие (стена и т.п.)
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
                            // останавливаемся чуть раньше точки столкновения
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
        // Не принуждаем к точке target — могли упереться в стену

        if (stateRef != null)
        {
            var done = new Dictionary<string, object>
            {
                ["moving"] = false,
                ["updatedAt"] = ServerValue.Timestamp
            };
            stateRef.UpdateChildrenAsync(done);
        }

        // refresh local cache
        movingCache = false;
        hasMovingCache = true;
    }

    private Vector3 ScreenToWorld(Vector2 screenPos)
    {
        float z = Mathf.Abs(cam.transform.position.z - unit.transform.position.z);
        var wp = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, z));
        wp.z = unit.transform.position.z;
        return wp;
    }

    private static bool ParseBool(object v)
    {
        // RTDB may store bool as true/false or 0/1 (Int64)
        if (v is bool b) return b;
        if (v is long l) return l != 0;
        if (v is int i) return i != 0;
        if (v is string s)
        {
            if (bool.TryParse(s, out var bs)) return bs;
            if (long.TryParse(s, out var ls)) return ls != 0;
        }
        return false;
    }
}
