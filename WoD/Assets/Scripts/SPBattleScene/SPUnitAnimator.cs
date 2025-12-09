using System.Linq;
using UnityEngine;

/// <summary>
/// Drives Animator parameters in SP by measuring transform motion.
/// Attach to the 'Visual' object that has an Animator.
/// </summary>
public class SPUnitAnimator : MonoBehaviour
{
	[SerializeField] private string movingParam = "Moving";
	[SerializeField] private string speedParam  = "Speed";
	[SerializeField] private float  movingThreshold = 0.01f;
	[SerializeField] private bool   autoFaceByVelocity = true;

	private Animator animator;
	private Transform unitRoot;
	private Vector3 lastPos;

	private void Awake()
	{
		animator = GetComponent<Animator>();
		unitRoot = transform.root;
		if (unitRoot != null) lastPos = unitRoot.position;

		// Auto-detect existing parameter names to match controller setup
		if (animator != null)
		{
			try
			{
				var parms = animator.parameters;
				if (parms != null && parms.Length > 0)
				{
					// Pick first matching moving bool
					string[] movingCandidates = new[] { "Moving", "moving", "IsMoving", "isMoving", "Walk", "Walking", "Run", "Running" };
					var mv = parms.FirstOrDefault(p => p.type == AnimatorControllerParameterType.Bool && movingCandidates.Contains(p.name));
					if (mv != null && !string.IsNullOrEmpty(mv.name)) movingParam = mv.name;

					// Pick first matching speed float
					string[] speedCandidates = new[] { "Speed", "speed", "Velocity", "velocity" };
					var sp = parms.FirstOrDefault(p => p.type == AnimatorControllerParameterType.Float && speedCandidates.Contains(p.name));
					if (sp != null && !string.IsNullOrEmpty(sp.name)) speedParam = sp.name;
				}
			}
			catch { }
		}
	}

	private void Update()
	{
		if (!unitRoot) return;

		var pos = unitRoot.position;
		var delta = (Vector2)(pos - lastPos);
		var speed = delta.magnitude / Mathf.Max(Time.deltaTime, 0.0001f);

		if (animator != null)
		{
			if (!string.IsNullOrEmpty(movingParam) && HasParamBool(movingParam)) animator.SetBool(movingParam, speed > movingThreshold);
			if (!string.IsNullOrEmpty(speedParam)  && HasParamFloat(speedParam)) animator.SetFloat(speedParam, speed);
		}

		// auto face visual by horizontal velocity
		if (autoFaceByVelocity && Mathf.Abs(delta.x) > 0.0005f)
		{
			var s = transform.localScale;
			s.x = (delta.x >= 0f) ? -Mathf.Abs(s.x) : Mathf.Abs(s.x); // right-facing uses negative X like in spawner
			transform.localScale = s;
		}

		lastPos = pos;
	}

	private bool HasParamBool(string name)
	{
		try
		{
			foreach (var p in animator.parameters) if (p.name == name && p.type == AnimatorControllerParameterType.Bool) return true;
		}
		catch { }
		return false;
	}

	private bool HasParamFloat(string name)
	{
		try
		{
			foreach (var p in animator.parameters) if (p.name == name && p.type == AnimatorControllerParameterType.Float) return true;
		}
		catch { }
		return false;
	}
}


