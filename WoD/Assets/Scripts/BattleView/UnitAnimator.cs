using UnityEngine;
using System.Collections.Generic;

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

	// Try to resolve an animator parameter name:
	// 1) exact match for preferred
	// 2) exact match for any alias
	// 3) case-insensitive match vs preferred/aliases among existing parameters
	private static string ResolveAnimatorParam(Animator a, string preferred, params string[] aliases)
	{
		if (!a) return null;
		if (HasParam(a, preferred)) return preferred;
		if (aliases != null)
		{
			for (int i = 0; i < aliases.Length; i++)
			{
				var alias = aliases[i];
				if (!string.IsNullOrEmpty(alias) && HasParam(a, alias)) return alias;
			}
		}

		// case-insensitive scan
		var candidates = new List<string>();
		candidates.Add(preferred);
		if (aliases != null) candidates.AddRange(aliases);

		var ps = a.parameters;
		for (int i = 0; i < ps.Length; i++)
		{
			var p = ps[i];
			if (p == null || string.IsNullOrEmpty(p.name)) continue;
			for (int c = 0; c < candidates.Count; c++)
			{
				if (!string.IsNullOrEmpty(candidates[c]) &&
					string.Equals(p.name, candidates[c], System.StringComparison.OrdinalIgnoreCase))
				{
					return p.name; // use actual casing from controller
				}
			}
		}
		return null;
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
			// Auto-detect parameter names to tolerate controller variants (e.g., "Moving", "Atacing")
			string resolvedMoving = ResolveAnimatorParam(animator, movingParam, "Moving");
			if (!string.IsNullOrEmpty(resolvedMoving)) movingParam = resolvedMoving;

			string resolvedAttack = ResolveAnimatorParam(animator, attackParam, "Attack", "Attacking", "Atacing");
			if (!string.IsNullOrEmpty(resolvedAttack)) attackParam = resolvedAttack;

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


