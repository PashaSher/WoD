using UnityEngine;

/// <summary>
/// Bridge between Animator Events and SP auto-attack logic.
/// Attach to the 'Visual' object that has the Animator.
/// In the attack animation clip, add events:
///  - AnimEvent_Fire  (at the muzzle-flash frame)
///  - AnimEvent_AttackEnd (at the end of the clip)
/// </summary>
public class SPAttackEvents : MonoBehaviour
{
	private SPUnitAutoAttack autoAttack;
	private SPAnimatorFlags  animFlags;
	private Animator animator;
	private Unit unit;

	// Fallback: work even if clips have no Animation Events (no asset changes needed)
	[SerializeField] private bool  autoFallbackIfNoEvents = false;
	[SerializeField] private string attackStateName = "attack";
	[SerializeField] private float fallbackFireNormalizedTime = 0.3f;	// when to fire inside attack state [0..1]
	[SerializeField] private float fallbackMaxAttackDuration = 1.0f;	// safety end if state loops/never ends
	[SerializeField] private bool  debugLogs = false;

	private bool prevInAttack;
	private float attackEnterTime;

	void Awake()
	{
		autoAttack = GetComponent<SPUnitAutoAttack>();
		if (autoAttack == null) autoAttack = GetComponentInParent<SPUnitAutoAttack>();
		animFlags  = GetComponent<SPAnimatorFlags>() ?? GetComponentInParent<SPAnimatorFlags>();
		animator   = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
		unit       = GetComponentInParent<Unit>();
	}

	// Called by Animation Event
	public void AnimEvent_Fire()
	{
		var unit = GetComponentInParent<Unit>();
		Debug.Log($"[SPAttackEvents] AnimEvent_Fire -> '{(unit ? unit.name : name)}'");
		if (autoAttack != null) autoAttack.FireNow();
	}

	// Called by Animation Event at the end of the attack animation
	public void AnimEvent_AttackEnd()
	{
		var unit = GetComponentInParent<Unit>();
		Debug.Log($"[SPAttackEvents] AnimEvent_AttackEnd -> '{(unit ? unit.name : name)}'");
		if (autoAttack != null) autoAttack.OnAttackAnimationEnd();
		else if (animFlags != null) animFlags.SetAttacking(false);
	}

	void Update()
	{
		if (!autoFallbackIfNoEvents) return;
		if (animator == null || autoAttack == null) return;

		var state = animator.GetCurrentAnimatorStateInfo(0);
		bool inAttack = state.IsName(attackStateName);

		// Entered attack state
		if (inAttack && !prevInAttack)
		{
			attackEnterTime = Time.time;
			if (debugLogs)
			{
				var n = unit ? unit.name : name;
				Debug.Log($"[SPAttackEvents] '{n}' enter '{attackStateName}'");
			}
		}

		// While in attack state, simulate events if clips have none
		if (inAttack && unit != null && unit.IsAttacking)
		{
			// Fire at certain normalized time
			if (state.normalizedTime >= Mathf.Clamp01(fallbackFireNormalizedTime) && state.normalizedTime < 1.2f)
			{
				autoAttack.FireNow(); // internally idempotent per attack
			}
			// End by state end or time guard
			if (state.normalizedTime >= 0.99f || (Time.time - attackEnterTime) >= Mathf.Max(0.1f, fallbackMaxAttackDuration))
			{
				autoAttack.OnAttackAnimationEnd();
			}
		}

		// Exited attack state without event
		if (!inAttack && prevInAttack && unit != null && unit.IsAttacking)
		{
			autoAttack.OnAttackAnimationEnd();
		}

		prevInAttack = inAttack;
	}
}


