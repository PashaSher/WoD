using UnityEngine;

/// <summary>
/// Bridges Unit gameplay state to Animator parameters on child "Visual".
/// Expects Animator with parameters: "Moving" (bool), "Shooting" (bool), "Speed" (float).
/// Works even if Animator is missing (failsafe no-op).
/// </summary>
public class UnitAnimator : MonoBehaviour
{
    [SerializeField] private string movingParam  = "Moving";
    [SerializeField] private string shootingParam = "Shooting";
    [SerializeField] private string speedParam   = "Speed";

    private Unit unit;
    private Animator animator;
    private Transform visual;

    private Vector3 lastPos;

    private void Awake()
    {
        unit = GetComponent<Unit>();
        visual = transform.Find("Visual");
        if (visual)
            animator = visual.GetComponent<Animator>();
        lastPos = transform.position;
    }

    private void Update()
    {
        if (!animator) return;

        // Velocity magnitude for simple speed param
        float speed = (transform.position - lastPos).magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
        lastPos = transform.position;

        bool isMoving = speed > 0.01f;
        bool isShooting = unit != null && unit.IsAttacking;

        animator.SetBool(movingParam, isMoving);
        animator.SetBool(shootingParam, isShooting);
        animator.SetFloat(speedParam, speed);
    }
}


