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
	[SerializeField] private string movingBool = "moving";
	[SerializeField] private string speedFloat = "Speed";
	[SerializeField] private string attackingBool = "attack";
	[SerializeField] private string attackTrigger = "";
	[SerializeField] private string hitTrigger = "";
	[SerializeField] private string dieTrigger = "";

	[SerializeField] private float movingThreshold = 0.01f;
	[SerializeField] private bool  alsoInferSpeedFromMotion = false;
	[SerializeField] private bool  useStateCrossfadeFallback = false;
	[SerializeField] private string idleState    = "idel";
	[SerializeField] private string moveState    = "moving";
	[SerializeField] private string attackState  = "attack";

	private Animator animator;
	private Transform unitRoot;
	private Unit unit;
	private Vector3 lastPos;
	private string unitName;
	private bool lastMoving;

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
				// Include MP aliases (typos used in some controllers): "Atacing"
				string[] candidates = { "Attacking", "IsAttacking", "attacking", "isAttacking", "attack", "Atacing", "atacing", "Atack", "atack", "Shoot", "shoot", "Fire", "fire" };
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
			lastMoving = speed > movingThreshold;
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
		if (!(a || b || c) && useStateCrossfadeFallback)
		{
			// No param matched → try to crossfade by state name (does not modify controller asset)
			var target = on ? moveState : idleState;
			TryCrossFade(target, 0.05f);
		}
		Debug.Log($"[SPAnimatorFlags] '{unitName}' SetMoving({on}) -> applied: movingBool={a} moving={b} Moving={c}");
		lastMoving = on;
	}

	public void SetSpeed(float value)
	{
		if (animator == null) return;
		bool a = SetFloatIfExists(speedFloat, value);
		bool b = SetFloatIfExists("Speed", value);
		bool c = SetFloatIfExists("speed", value);
		if (verboseLogs || a || b || c)
		{
			Debug.Log($"[SPAnimatorFlags] '{unitName}' SetSpeed({value:F3}) -> applied: SpeedName={a} Speed={b} speed={c}");
		}
	}

	public void SetAttacking(bool on)
	{
		if (animator == null) return;
		bool a = SetBoolIfExists(attackingBool, on);
		bool b = SetBoolIfExists("Attacking", on);
		bool c = SetBoolIfExists("attacking", on);
		bool d = SetBoolIfExists("attack", on);
		// Независимо от наличия параметров, попробуем мягко перейти в нужный клип по имени
		// (это не меняет контроллер и не влияет на MP — работает только в SP).
		if (useStateCrossfadeFallback)
		{
			var target = on ? attackState : idleState;
			bool ok = TryCrossFade(target, 0.05f);
			Debug.Log($"[SPAnimatorFlags] '{unitName}' CrossFade('{target}') => {ok}");
		}
		Debug.Log($"[SPAnimatorFlags] '{unitName}' SetAttacking({on}) -> applied: attackingName={a} Attacking={b} attacking={c} attack={d}");
	}

	public void TriggerAttack()
	{
		if (animator == null) return;
		// Try explicit trigger name first
		if (!string.IsNullOrEmpty(attackTrigger) && HasParamTrigger(attackTrigger))
		{
			animator.SetTrigger(attackTrigger);
			Debug.Log($"[SPAnimatorFlags] '{unitName}' TriggerAttack() -> '{attackTrigger}'");
			return;
		}
		// Probe common trigger names like MP
		string[] candidates = { "Attack", "attack", "Shoot", "shoot", "Fire", "fire" };
		for (int i = 0; i < candidates.Length; i++)
		{
			var tr = candidates[i];
			if (HasParamTrigger(tr))
			{
				animator.SetTrigger(tr);
				Debug.Log($"[SPAnimatorFlags] '{unitName}' TriggerAttack() -> '{tr}' (auto)");
				return;
			}
		}
		Debug.Log($"[SPAnimatorFlags] '{unitName}' TriggerAttack() -> no trigger found on Animator");
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
	
	public bool IsMoving => lastMoving;

	private bool TryCrossFade(string stateName, float duration)
	{
		if (string.IsNullOrEmpty(stateName) || animator == null) return false;
		if (verboseLogs) Debug.Log($"[SPAnimatorFlags] '{unitName}' TryCrossFade('{stateName}', {duration:F2})");
		int h1 = Animator.StringToHash(stateName);
		int h2 = Animator.StringToHash("Base Layer." + stateName);
		if (animator.HasState(0, h1))
		{
			animator.CrossFade(h1, duration);
			if (verboseLogs) Debug.Log($"[SPAnimatorFlags] '{unitName}' CrossFade OK by '{stateName}'");
			return true;
		}
		if (animator.HasState(0, h2))
		{
			animator.CrossFade(h2, duration);
			if (verboseLogs) Debug.Log($"[SPAnimatorFlags] '{unitName}' CrossFade OK by 'Base Layer.{stateName}'");
		 return true;
		}
		if (verboseLogs) Debug.Log($"[SPAnimatorFlags] '{unitName}' CrossFade FAIL, state not found");
		return false;
	}
}


