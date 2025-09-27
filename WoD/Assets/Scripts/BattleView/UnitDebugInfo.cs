using TMPro;
using UnityEngine;

[DefaultExecutionOrder(1000)]
public class UnitDebugInfo : MonoBehaviour
{
    [SerializeField] private Unit unit;
    [SerializeField] private TextMeshPro tmp;         // 3D TextMeshPro
    [SerializeField] private string format =
        "{0}\n(x:{1:0.0}, y:{2:0.0})\nHP:{3}\nMoving:{4}\nFacing:{5}";

    [Header("Placement")]
    [SerializeField] private Vector3 localOffset = new Vector3(0, 1.2f, 0);
    [SerializeField] private Color   color = new Color(1f, 0.95f, 0.1f);

    private Transform labelRoot;

    private void Awake()
    {
        if (!unit) unit = GetComponent<Unit>();

        // найдём/создадим ребёнка DebugInfo
        var t = transform.Find("DebugInfo");
        if (!t)
        {
            var go = new GameObject("DebugInfo");
            go.transform.SetParent(transform, false);
            t = go.transform;
        }
        labelRoot = t;
        labelRoot.localPosition = localOffset;

        // найдём/создадим TextMeshPro
        tmp = labelRoot.GetComponent<TextMeshPro>();
        if (!tmp)
        {
            tmp = labelRoot.gameObject.AddComponent<TextMeshPro>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 2.2f;
            tmp.color = color;
            tmp.raycastTarget = false;
        }
    }

    private void LateUpdate()
    {
        if (!unit || !tmp) return;

        var p = unit.PosDebug;
        tmp.text = string.Format(
            format,
            string.IsNullOrEmpty(unit.unitType) ? "Unit" : unit.unitType,
            p.x, p.y,
            unit.health,
            unit.MovingDebug ? "true" : "false",
            unit.FacingDebug
        );

        // повернуть ярлык к камере (удобнее читать)
        var cam = Camera.main;
        if (cam) labelRoot.forward = cam.transform.forward;
    }
}
