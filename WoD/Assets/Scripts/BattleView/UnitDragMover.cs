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

    private DatabaseReference stateRef; // лениво инициализируем

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

    // Попытка собрать ссылку, если её ещё нет
    private bool EnsureStateRef()
    {
        if (stateRef != null) return true;
        if (unit == null) return false;
        if (string.IsNullOrEmpty(unit.sessionId) ||
            string.IsNullOrEmpty(unit.unitKey))
        {
            // ещё не готово — спавнер не успел заполнить
            return false;
        }

        var root   = FirebaseDatabase.DefaultInstance.RootReference;
        var branch = unit.host ? "hostArmy" : "clientArmy";
        stateRef = root.Child("sessions").Child(unit.sessionId)
                       .Child(branch).Child(unit.unitKey).Child("state");
        return true;
    }

    private bool CanControl() => unit && Globalflags.ifHost == unit.host;

    public void OnPointerDown(PointerEventData e)
    {
        if (!CanControl()) return;
        if (!EnsureStateRef())
        {
            Debug.LogWarning("[UnitDragMover] Firebase refs not ready yet (sessionId/unitKey).");
            return;
        }

        dragging   = true;
        startWorld = ScreenToWorld(e.position);
        currWorld  = startWorld;

        line.enabled = true;
        line.SetPosition(0, unit.transform.position);
        line.SetPosition(1, unit.transform.position);
    }

    public void OnDrag(PointerEventData e)
    {
        if (!dragging) return;
        currWorld = ScreenToWorld(e.position);

        line.SetPosition(0, unit.transform.position);
        line.SetPosition(1, new Vector3(currWorld.x, currWorld.y, unit.transform.position.z));
    }

    public void OnPointerUp(PointerEventData e)
    {
        if (!dragging) return;
        dragging = false;
        line.enabled = false;

        if (!EnsureStateRef()) return; // безопасность

        var target = ScreenToWorld(e.position);
        if (Vector2.Distance(target, unit.transform.position) < minDragDistance) return;

        int facing = (target.x >= unit.transform.position.x) ? 1 : -1;

        // ОДНА запись: конечные координаты + moving=true
        var updates = new Dictionary<string, object>
        {
            ["x"] = target.x,
            ["y"] = target.y,
            ["facing"] = facing,
            ["moving"] = true,
            ["updatedAt"] = ServerValue.Timestamp
        };
        stateRef.UpdateChildrenAsync(updates);

        // локально едем и по прибытии ставим moving=false
        StopAllCoroutines();
        StartCoroutine(MoveToAndFinish(target, unit.moveSpeed));
    }

    private IEnumerator MoveToAndFinish(Vector3 target, float speed)
    {
        const float stopDist = 0.02f;
        target.z = unit.transform.position.z;

        while (Vector2.Distance(unit.transform.position, target) > stopDist)
        {
            unit.transform.position = Vector2.MoveTowards(unit.transform.position, target, speed * Time.deltaTime);
            yield return null;
        }
        unit.transform.position = target;

        if (stateRef != null)
        {
            var done = new Dictionary<string, object>
            {
                ["moving"] = false,
                ["updatedAt"] = ServerValue.Timestamp
            };
            stateRef.UpdateChildrenAsync(done);
        }
    }

    private Vector3 ScreenToWorld(Vector2 screenPos)
    {
        float z = Mathf.Abs(cam.transform.position.z - unit.transform.position.z);
        var wp = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, z));
        wp.z = unit.transform.position.z;
        return wp;
    }
}
