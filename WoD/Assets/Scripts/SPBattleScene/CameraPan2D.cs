using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Simple 2D camera pan by dragging (touch or mouse).
/// Clamp movement to MapBounds so the camera stays within the big map.
/// </summary>
public class CameraPan2D : MonoBehaviour
{
	[SerializeField] private float dragSensitivity = 1.0f;
	[SerializeField] private float uiDragBlockRadius = 0f; // optional, not used if zero
	[SerializeField] private float clampMargin = 0.1f;

	private Camera cam;
	private bool dragging;
	private Vector3 prevWorld;

	private void Awake()
	{
		cam = GetComponent<Camera>();
		if (cam == null) cam = Camera.main;
	}

	private bool IsPointerOverUI()
	{
		if (EventSystem.current == null) return false;
		return EventSystem.current.IsPointerOverGameObject();
	}

	private void Update()
	{
#if UNITY_EDITOR || UNITY_STANDALONE
		HandleMouse();
#else
		HandleTouch();
#endif
	}

	private void HandleMouse()
	{
		if (cam == null) return;
		if (Input.GetMouseButtonDown(0))
		{
			if (!IsPointerOverUI())
			{
				dragging = true;
				prevWorld = cam.ScreenToWorldPoint(Input.mousePosition);
			}
		}
		else if (Input.GetMouseButton(0) && dragging)
		{
			Vector3 cur = cam.ScreenToWorldPoint(Input.mousePosition);
			Vector3 delta = (cur - prevWorld);
			if (delta.sqrMagnitude > 0f)
			{
				Vector3 desired = cam.transform.position - delta * Mathf.Max(0.01f, dragSensitivity);
				desired = MapBounds.ClampCameraCenter(cam, desired, clampMargin);
				desired.z = cam.transform.position.z;
				cam.transform.position = desired;
				prevWorld = cam.ScreenToWorldPoint(Input.mousePosition);
			}
		}
		else if (Input.GetMouseButtonUp(0))
		{
			dragging = false;
		}
	}

	private void HandleTouch()
	{
		if (cam == null) return;
		if (Input.touchCount == 1)
		{
			var t = Input.GetTouch(0);
			if (t.phase == TouchPhase.Began)
			{
				if (!IsPointerOverUI())
				{
					dragging = true;
					prevWorld = cam.ScreenToWorldPoint(t.position);
				}
			}
			else if ((t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary) && dragging)
			{
				Vector3 cur = cam.ScreenToWorldPoint(t.position);
				Vector3 delta = (cur - prevWorld);
				if (delta.sqrMagnitude > 0f)
				{
					Vector3 desired = cam.transform.position - delta * Mathf.Max(0.01f, dragSensitivity);
					desired = MapBounds.ClampCameraCenter(cam, desired, clampMargin);
					desired.z = cam.transform.position.z;
					cam.transform.position = desired;
					prevWorld = cam.ScreenToWorldPoint(t.position);
				}
			}
			else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
			{
				dragging = false;
			}
		}
		else
		{
			dragging = false;
		}
	}
}


