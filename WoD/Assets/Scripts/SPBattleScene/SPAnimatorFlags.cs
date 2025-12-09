using System.Linq;
using UnityEngine;

/// <summary>
/// Local animator flags/triggers for SP (no RTDB).
/// Attach to 'Visual' that has an Animator.
/// </summary>
public class SPAnimatorFlags : MonoBehaviour
{
	[Header("Debug")]
	[SerializeField] private bool verboseLogs = false;
	[SerializeField] private float debugIntervalSeconds = 1.0f;
	private float _nextDebugTime;

	[Header("Behavior")]
	[SerializeField] private bool mirrorAttackingFromUnit = true;

	[Header("Parameter names (auto-detected if empty)")]
	[SerializeField] private string movingBool = "";
	[SerializeField] private string speedFloat = "";
	[SerializeField] private string attackingBool = "";
	[SerializeField] private string attackTrigger = "";
	[SerializeField] private string hitTrigger = "";
	[SerializeField] private string dieTrigger = "";

	[SerializeField] private float movingThreshold = 0.01f;
	[SerializeField] private bool  alsoInferSpeedFromMotion = false;

	private Animator animator;
	private Transform unitRoot;
	private Unit unit;
	private Vector3 lastPos;
	private string unitName;

	private void Awake()
	{
		animator = GetComponent<Animator>();
		unit = GetComponentInParent<Unit>();
		unitRoot = unit ? unit.transform : transform.root;
		unitName = unitRoot ? unitRoot.name : gameObject.name;
		if (unitRoot != null) lastPos = unitRoot.position;

		AutoDetectParams();

		if (animator == null)
		{
			Debug.LogWarning($"[SPAnimatorFlags] No Animator on '{name}' (unit='{unitName}')");
		}
		else
		{
			if (verboseLogs)
			{
				try
				{
					var all = string.Join(", ", animator.parameters.Select(p => $"{p.name}:{p.type}"));
					Debug.Log($"[SPAnimatorFlags] Animator on '{unitName}'. Params: {all}");
					Debug.Log($"[SPAnimatorFlags] Selected params on '{unitName}': moving='{movingBool}', speed='{speedFloat}', attacking='{attackingBool}', attackTr='{attackTrigger}', hitTr='{hitTrigger}', dieTr='{dieTrigger}'");
				}
				catch { }
			}
		}
	}

	private void AutoDetectParams()
	{
		if (animator == null) return;
		try
		{
			var ps = animator.parameters;
			if (ps == null || ps.Length == 0) return;

			if (string.IsNullOrEmpty(movingBool))
			{
				string[] candidates = { "Moving", "IsMoving", "moving", "isMoving", "Walk", "Walking", "Run", "Running" };
				var p = ps.FirstOrDefault(x => x.type == AnimatorControllerParameterType.Bool && candidates.Contains(x.name));
				if (p != null) movingBool = p.name;
			}
			if (string.IsNullOrEmpty(speedFloat))
			{
				string[] candidates = { "Speed", "speed", "Velocity", "velocity" };
				var p = ps.FirstOrDefault(x => x.type == AnimatorControllerParameterType.Float && candidates.Contains(x.name));
				if (p != null) speedFloat = p.name;
			}
			if (string.IsNullOrEmpty(attackingBool))
			{
				string[] candidates = { "Attacking", "IsAttacking", "attacking", "isAttacking", "attack" };
				var p = ps.FirstOrDefault(x => x.type == AnimatorControllerParameterType.Bool && candidates.Contains(x.name));
				if (p != null) attackingBool = p.name;
			}
			if (string.IsNullOrEmpty(attackTrigger))
			{
				string[] candidates = { "Attack", "attack", "Shoot", "shoot", "Fire", "fire" };
				var p = ps.FirstOrDefault(x => x.type == AnimatorControllerParameterType.Trigger && candidates.Contains(x.name));
				if (p != null) attackTrigger = p.name;
			}
			if (string.IsNullOrEmpty(hitTrigger))
			{
				string[] candidates = { "Hit", "hit", "Hurt", "hurt" };
				var p = ps.FirstOrDefault(x => x.type == AnimatorControllerParameterType.Trigger && candidates.Contains(x.name));
				if (p != null) hitTrigger = p.name;
			}
			if (string.IsNullOrEmpty(dieTrigger))
			{
				string[] candidates = { "Die", "die", "Death", "death" };
				var p = ps.FirstOrDefault(x => x.type == AnimatorControllerParameterType.Trigger && candidates.Contains(x.name));
				if (p != null) dieTrigger = p.name;
			}
		}
		catch { }
	}

	private void Update()
	{
		if (animator == null || unitRoot == null) return;

		// Optional: derive speed to feed Speed float; moving bool may also be kept in sync
		if (alsoInferSpeedFromMotion)
		{
			var pos = unitRoot.position;
			var delta = (Vector2)(pos - lastPos);
			var speed = delta.magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
			if (!string.IsNullOrEmpty(speedFloat) && HasParamFloat(speedFloat)) animator.SetFloat(speedFloat, speed);
			if (!string.IsNullOrEmpty(movingBool) && HasParamBool(movingBool)) animator.SetBool(movingBool, speed > movingThreshold);

			if (verboseLogs && Time.time >= _nextDebugTime)
			{
				_nextDebugTime = Time.time + Mathf.Max(0.1f, debugIntervalSeconds);
				Debug.Log($"[SPAnimatorFlags] '{unitName}' speed={speed:F3}, moving={(speed > movingThreshold)} (set '{movingBool}', '{speedFloat}')");
			}
			lastPos = pos;
		}

		// Mirror Unit.attacking if available
		if (mirrorAttackingFromUnit && unit != null && !string.IsNullOrEmpty(attackingBool) && HasParamBool(attackingBool))
		{
			bool isAttacking = false;
			try { isAttacking = unit.IsAttacking; } catch { }
			animator.SetBool(attackingBool, isAttacking);
			if (verboseLogs && Time.time >= _nextDebugTime)
			{
				_nextDebugTime = Time.time + Mathf.Max(0.1f, debugIntervalSeconds);
				Debug.Log($"[SPAnimatorFlags] '{unitName}' attacking={isAttacking} (set '{attackingBool}')");
			}
		}
	}

	// Public API for other SP scripts
	public void SetMoving(bool on)
	{
		if (animator == null) return;
		bool a = SetBoolIfExists(movingBool, on);
		bool b = SetBoolIfExists("moving", on);
		bool c = SetBoolIfExists("Moving", on);
		Debug.Log($"[SPAnimatorFlags] '{unitName}' SetMoving({on}) -> applied: movingBool={a} moving={b} Moving={c}");
	}

	public void SetSpeed(float value)
	{
		if (animator == null) return;
		bool a = SetFloatIfExists(speedFloat, value);
		bool b = SetFloatIfExists("Speed", value);
		bool c = SetFloatIfExists("speed", value);
		Debug.Log($"[SPAnimatorFlags] '{unitName}' SetSpeed({value:F3}) -> applied: SpeedName={a} Speed={b} speed={c}");
	}

	public void SetAttacking(bool on)
	{
		if (animator == null) return;
		bool a = SetBoolIfExists(attackingBool, on);
		bool b = SetBoolIfExists("Attacking", on);
		bool c = SetBoolIfExists("attacking", on);
		bool d = SetBoolIfExists("attack", on);
		Debug.Log($"[SPAnimatorFlags] '{unitName}' SetAttacking({on}) -> applied: attackingName={a} Attacking={b} attacking={c} attack={d}");
	}

	public void TriggerAttack()
	{
		if (animator == null) return;
		if (!string.IsNullOrEmpty(attackTrigger) && HasParamTrigger(attackTrigger))
		{
			animator.SetTrigger(attackTrigger);
			if (verboseLogs) Debug.Log($"[SPAnimatorFlags] '{unitName}' TriggerAttack() -> '{attackTrigger}'");
		}
	}

	public void TriggerHit()
	{
		if (animator == null) return;
		if (!string.IsNullOrEmpty(hitTrigger) && HasParamTrigger(hitTrigger))
		{
			animator.SetTrigger(hitTrigger);
			if (verboseLogs) Debug.Log($"[SPAnimatorFlags] '{unitName}' TriggerHit() -> '{hitTrigger}'");
		}
	}

	public void TriggerDie()
	{
		if (animator == null) return;
		if (!string.IsNullOrEmpty(dieTrigger) && HasParamTrigger(dieTrigger))
		{
			animator.SetTrigger(dieTrigger);
			if (verboseLogs) Debug.Log($"[SPAnimatorFlags] '{unitName}' TriggerDie() -> '{dieTrigger}'");
		}
	}

	private bool HasParamBool(string name)
	{
		try { foreach (var p in animator.parameters) if (p.name == name && p.type == AnimatorControllerParameterType.Bool) return true; } catch { }
		return false;
	}
	private bool HasParamFloat(string name)
	{
		try { foreach (var p in animator.parameters) if (p.name == name && p.type == AnimatorControllerParameterType.Float) return true; } catch { }
		return false;
	}
	private bool HasParamTrigger(string name)
	{
		try { foreach (var p in animator.parameters) if (p.name == name && p.type == AnimatorControllerParameterType.Trigger) return true; } catch { }
		return false;
	}

	private bool SetBoolIfExists(string name, bool value)
	{
		if (string.IsNullOrEmpty(name)) return false;
		if (!HasParamBool(name)) return false;
		animator.SetBool(name, value);
		return true;
	}

	private bool SetFloatIfExists(string name, float value)
	{
		if (string.IsNullOrEmpty(name)) return false;
		if (!HasParamFloat(name)) return false;
		animator.SetFloat(name, value);
		return true;
	}
}


