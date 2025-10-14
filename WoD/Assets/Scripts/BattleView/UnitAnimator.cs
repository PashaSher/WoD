using UnityEngine;

/// <summary>
/// Bridges Unit gameplay state to Animator parameters on child "Visual".
/// Expects Animator parameters (configurable):
///  - moving (bool)  ← synchronized with RTDB flag "state/moving"
///  - attack (bool)  ← synchronized with RTDB flag "state/attacking"
///  - speed  (float) ← optional, purely cosmetic from local velocity
/// Works even if Animator is missing (failsafe no-op).
/// </summary>
public class UnitAnimator : MonoBehaviour
{
    [SerializeField] private string movingParam = "moving";
    [SerializeField] private string attackParam = "attack";
    [SerializeField] private string speedParam  = "speed";

    private Unit unit;
    private Animator animator;
    private Transform visual;

    private Vector3 lastPos;
    private bool hasMovingParam;
    private bool hasAttackParam;
    private bool hasSpeedParam;

    private static bool HasParam(Animator a, string name)
    {
        if (!a || string.IsNullOrEmpty(name)) return false;
        var ps = a.parameters;
        for (int i = 0; i < ps.Length; i++)
            if (ps[i] != null && ps[i].name == name) return true;
        return false;
    }

    private void Awake()
    {
        unit = GetComponent<Unit>();
        visual = transform.Find("Visual");
        if (visual)
            animator = visual.GetComponent<Animator>();
        lastPos = transform.position;

        if (animator)
        {
            hasMovingParam = HasParam(animator, movingParam);
            hasAttackParam = HasParam(animator, attackParam);
            hasSpeedParam  = HasParam(animator, speedParam);
        }
    }

    private void Update()
    {
        if (!animator) return;

        // Local speed for optional cosmetic blending
        float speed = (transform.position - lastPos).magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
        lastPos = transform.position;

        // For locally owned units drive "moving" from local motion; for remote units use RTDB flag
        bool owned = unit != null && Globalflags.ifHost == unit.host;
        bool movingFlag  = owned ? (speed > 0.1f) : (unit != null && unit.IsMovingFromRTDB);
        bool attackFlag  = unit != null && unit.IsAttacking;      // kept in sync via RTDB callbacks

        if (hasMovingParam) animator.SetBool(movingParam, movingFlag);
        if (hasAttackParam) animator.SetBool(attackParam, attackFlag);
        if (hasSpeedParam)  animator.SetFloat(speedParam, speed);
    }
}


