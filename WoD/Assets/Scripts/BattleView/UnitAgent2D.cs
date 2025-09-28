using System.Collections.Generic;
using Firebase.Database;
using UnityEngine;

public class UnitAgent2D : MonoBehaviour
{
    [Tooltip("Если >0 — перекрывает Unit.moveSpeed")]
    public float speedOverride = 0f;
    public bool useRigidbodyMove = false;

    private Rigidbody2D rb;
    private Unit unit;                 // берём sessionId/host/unitKey и moveSpeed
    private Vector2? dest;
    private const float EPS = 0.02f;

    private void Awake()
    {
        unit = GetComponentInParent<Unit>();
        rb   = GetComponentInParent<Rigidbody2D>();
    }

    private float GetSpeed()
    {
        if (speedOverride > 0f) return speedOverride;
        return (unit != null && unit.moveSpeed > 0f) ? unit.moveSpeed : 3f;
    }

    private void Update()
    {
        if (!dest.HasValue) return;

        Vector2 pos = transform.parent ? (Vector2)transform.parent.position : (Vector2)transform.position;
        Vector2 d   = dest.Value - pos;
        float dist  = d.magnitude;

        if (dist <= EPS)
        {
            dest = null;
            StopRB();

            // Авто-пометка moving=false в RTDB
            PushMoving(false, pos);
            return;
        }

        Vector2 dir = d / Mathf.Max(dist, 1e-6f);
        float step  = GetSpeed() * Time.deltaTime;
        Vector2 next = dist <= step ? dest.Value : pos + dir * step;

        if (useRigidbodyMove && rb) rb.MovePosition(next);
        else if (transform.parent)  transform.parent.position = new Vector3(next.x, next.y, transform.parent.position.z);
        else                        transform.position       = new Vector3(next.x, next.y, transform.position.z);
    }

    public void SetDestination(Vector2 world)
    {
        dest = world;
    }

    private void StopRB()
    {
        if (!rb) return;
#if UNITY_6000_0_OR_NEWER || UNITY_2023_3_OR_NEWER
        rb.linearVelocity = Vector2.zero;
#else
        rb.velocity = Vector2.zero;
#endif
    }

    private async void PushMoving(bool moving, Vector2 at)
    {
        if (unit == null || string.IsNullOrEmpty(unit.sessionId) || string.IsNullOrEmpty(unit.unitKey)) return;
        string branch = unit.host ? "hostArmy" : "clientArmy";
        var stateRef = FirebaseDatabase.DefaultInstance.RootReference
            .Child("sessions").Child(unit.sessionId).Child(branch).Child(unit.unitKey).Child("state");

        var payload = new Dictionary<string, object>
        {
            ["moving"]   = moving,
            ["x"]        = (double)at.x,
            ["y"]        = (double)at.y,
            ["updatedAt"]= ServerValue.Timestamp
        };
        await stateRef.UpdateChildrenAsync(payload);
        await stateRef.Parent.Child("updatedAt").SetValueAsync(ServerValue.Timestamp);
    }
}
