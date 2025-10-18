using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class UnitSelectable : MonoBehaviour
{
    public float selectionSize = 1.2f;
    [SerializeField] private LineRenderer lr;
    [SerializeField] private BoxCollider2D box;

    private void Awake()
    {
        if (!lr)
        {
            var go = new GameObject("Selection");
            go.transform.SetParent(transform, false);
            lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.positionCount = 4;
            lr.widthMultiplier = 0.04f;
            lr.enabled = false;
        }
        EnsureCollider();
        Redraw();
        SyncColliderToSelection();
    }

    private void OnValidate()
    {
        EnsureCollider();
        Redraw();
        SyncColliderToSelection();
    }

    public void SetSelected(bool v) { if (lr) lr.enabled = v; }
    public void SetSize(float s) { selectionSize = s; Redraw(); SyncColliderToSelection(); }

    private void Redraw()
    {
        if (!lr) return;
        float h = selectionSize * 0.5f;
        lr.SetPosition(0, new Vector3(-h, -h, 0));
        lr.SetPosition(1, new Vector3(-h,  h, 0));
        lr.SetPosition(2, new Vector3( h,  h, 0));
        lr.SetPosition(3, new Vector3( h, -h, 0));
    }

    private void EnsureCollider()
    {
        if (!box)
        {
            box = GetComponent<BoxCollider2D>();
            if (!box) box = gameObject.AddComponent<BoxCollider2D>();
        }
        // selection is a UI-like area; use trigger so physics doesn't push units
        box.isTrigger = true;
        box.offset = Vector2.zero;
    }

    private void SyncColliderToSelection()
    {
        if (!box) return;
        box.size = new Vector2(selectionSize, selectionSize);
    }
}
