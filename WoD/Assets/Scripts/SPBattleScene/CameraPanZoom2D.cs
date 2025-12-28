using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Pan the orthographic camera by dragging (mouse or one finger) and zoom with pinch (two fingers) or mouse wheel.
/// If a MapBounds component exists in the scene, the camera center is clamped to its world rect.
/// Attach this script to the main orthographic camera in your SP scene.
/// </summary>
public class CameraPanZoom2D : MonoBehaviour
{
	[Header("Pan")]
	[SerializeField] private bool  enablePan       = true;
	[SerializeField] private float panSpeed        = 1.0f;
	[SerializeField] private bool  blockPanOverUI  = false;   // set true if you want UI to block panning
	[SerializeField] private float clampMargin     = 0.1f;    // keep a small border margin when clamping
	[SerializeField] private bool  blockPanWhenPointerOverUnit = true; // do not pan when drag starts on a Unit (e.g., drawing its path)

	[Header("Zoom (Orthographic)")]
	[SerializeField] private bool  enableZoom      = true;
	[SerializeField] private float minOrthoSize    = 3.0f;
	[SerializeField] private float maxOrthoSize    = 40.0f;
	[SerializeField] private float zoomSensitivity = 1.0f;    // >1 = stronger zoom per pinch/scroll step

	private Camera  cam;
	private bool    dragging;
	private Vector3 prevWorld;
	private bool    panLockedUntilRelease; // set when press begins over a Unit

	private void Awake()
	{
		cam = GetComponent<Camera>();
		if (cam == null) cam = Camera.main;
	}

	private bool IsPointerOverUI()
	{
		if (!blockPanOverUI) return false;
		if (EventSystem.current == null) return false;
#if UNITY_EDITOR || UNITY_STANDALONE
		return EventSystem.current.IsPointerOverGameObject();
#else
		// On touch devices pass the finger id to detect UI hits correctly
		for (int i = 0; i < Input.touchCount; i++)
		{
			if (EventSystem.current.IsPointerOverGameObject(Input.GetTouch(i).fingerId))
				return true;
		}
		return false;
#endif
	}

	private bool IsPointerOverUnit(Vector2 screenPos)
	{
		if (!blockPanWhenPointerOverUnit) return false;
		try
		{
			Vector3 w = cam != null ? cam.ScreenToWorldPoint(screenPos) : (Vector3)screenPos;
			var hits = Physics2D.OverlapPointAll(new Vector2(w.x, w.y));
			if (hits == null || hits.Length == 0) return false;
			for (int i = 0; i < hits.Length; i++)
			{
				var go = hits[i] ? hits[i].gameObject : null;
				if (!go) continue;
				// Detect Unit or common handlers on the visual
				if (go.GetComponentInParent<Unit>() != null) return true;
				if (go.GetComponentInParent<SPUnitDragMover>() != null) return true;
				if (go.GetComponentInParent<UnitPointer>() != null) return true;
			}
		}
		catch { }
		return false;
	}

	private void Update()
	{
		if (cam == null) return;
#if UNITY_EDITOR || UNITY_STANDALONE
		if (enablePan)  HandleMousePan();
		if (enableZoom) HandleMouseZoom();
#else
		HandleTouch();
#endif
	}

	// World-space threshold that corresponds to ~0.75 pixel; helps to kill clamp jitter at edges
	private float NoMoveThreshold()
	{
		if (cam == null || Screen.height <= 0) return 0.0005f;
		float worldPerPixel = (cam.orthographicSize * 2f) / Mathf.Max(1, Screen.height);
		return Mathf.Max(0.00025f, worldPerPixel * 0.75f);
	}

	private void HandleMousePan()
	{
		if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))
		{
			if (!IsPointerOverUI())
			{
				// If down starts over a unit — lock pan until button release
				if (IsPointerOverUnit(Input.mousePosition))
				{
					panLockedUntilRelease = true;
					return;
				}
				dragging = true;
				prevWorld = cam.ScreenToWorldPoint(Input.mousePosition);
			}
		}
		else if ((Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2)) && dragging)
		{
			if (panLockedUntilRelease) return;
			Vector3 cur = cam.ScreenToWorldPoint(Input.mousePosition);
			Vector3 delta = cur - prevWorld;
			if (delta.sqrMagnitude > 0f)
			{
				Vector3 desired = cam.transform.position - delta * Mathf.Max(0.01f, panSpeed);
				var applied = ApplyClampedPosition(desired);
				// If clamp blocked movement (very small applied delta), prevent jitter by consuming gesture without shifting camera
				float thr = NoMoveThreshold();
				if (applied.sqrMagnitude <= (thr * thr))
				{
					prevWorld = cur;
					return;
				}
				prevWorld = cam.ScreenToWorldPoint(Input.mousePosition);
			}
		}
		else if (Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Input.GetMouseButtonUp(2))
		{
			dragging = false;
			panLockedUntilRelease = false;
		}
	}

	private void HandleMouseZoom()
	{
		if (panLockedUntilRelease) return; // don't zoom while unit drag is active
		float scroll = Input.mouseScrollDelta.y;
		if (Mathf.Abs(scroll) < 0.0001f) return;
		float oldSize = cam.orthographic ? cam.orthographicSize : 5f;
		float newSize = Mathf.Clamp(oldSize * Mathf.Pow(0.9f, scroll * 10f * zoomSensitivity), minOrthoSize, maxOrthoSize);
		// If at bounds, skip to avoid micro jitter
		if (Mathf.Abs(newSize - oldSize) < 1e-5f) return;
		Vector3 before = cam.ScreenToWorldPoint(Input.mousePosition);
		cam.orthographic = true;
		cam.orthographicSize = newSize;
		Vector3 after = cam.ScreenToWorldPoint(Input.mousePosition);
		Vector3 delta = before - after;
		_ = ApplyClampedPosition(cam.transform.position + new Vector3(delta.x, delta.y, 0f));
	}

	private void HandleTouch()
	{
		int tc = Input.touchCount;
		if (tc == 0) { dragging = false; return; }

		if (tc == 1 && enablePan)
		{
			Touch t = Input.GetTouch(0);
			if (t.phase == TouchPhase.Began)
			{
				if (!IsPointerOverUI())
				{
					// Lock pan if touch begins over a unit (so unit can be dragged to draw path)
					if (IsPointerOverUnit(t.position))
					{
						panLockedUntilRelease = true;
						return;
					}
					dragging = true;
					prevWorld = cam.ScreenToWorldPoint(t.position);
				}
			}
			else if ((t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary) && dragging)
			{
				if (panLockedUntilRelease) return;
				Vector3 cur = cam.ScreenToWorldPoint(t.position);
				Vector3 delta = cur - prevWorld;
				if (delta.sqrMagnitude > 0f)
				{
					var applied = ApplyClampedPosition(cam.transform.position - delta * Mathf.Max(0.01f, panSpeed));
					float thr = NoMoveThreshold();
					if (applied.sqrMagnitude <= (thr * thr))
					{
						prevWorld = cur;
						return;
					}
					prevWorld = cam.ScreenToWorldPoint(t.position);
				}
			}
			else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
			{
				dragging = false;
				panLockedUntilRelease = false;
			}
		}
		else if (tc >= 2 && enableZoom)
		{
			dragging = false;
			Touch t0 = Input.GetTouch(0);
			Touch t1 = Input.GetTouch(1);

			Vector2 p0Prev = t0.position - t0.deltaPosition;
			Vector2 p1Prev = t1.position - t1.deltaPosition;

			float prevDist = Vector2.Distance(p0Prev, p1Prev);
			float currDist = Vector2.Distance(t0.position, t1.position);
			if (prevDist > 0.0001f && currDist > 0.0001f)
			{
				float scale = prevDist > 0f ? (prevDist / currDist) : 1f; // >1 => zoom in
				float oldSize = cam.orthographicSize;
				float newSize = Mathf.Clamp(oldSize * Mathf.Pow(scale, 1f * zoomSensitivity), minOrthoSize, maxOrthoSize);
				if (Mathf.Abs(newSize - oldSize) < 1e-5f) return;

				Vector2 mid = (t0.position + t1.position) * 0.5f;
				Vector3 before = cam.ScreenToWorldPoint(new Vector3(mid.x, mid.y, 0f));
				cam.orthographic = true;
				cam.orthographicSize = newSize;
				Vector3 after = cam.ScreenToWorldPoint(new Vector3(mid.x, mid.y, 0f));
				Vector3 delta = before - after;
				_ = ApplyClampedPosition(cam.transform.position + new Vector3(delta.x, delta.y, 0f));
			}
		}
	}

	// Applies clamped position and returns the actual applied delta (newPos - oldPos)
	private Vector3 ApplyClampedPosition(Vector3 desired)
	{
		Vector3 oldPos = cam.transform.position;
		Vector3 target = desired;
		if (MapBounds.TryGet(out var _))
		{
			target = MapBounds.ClampCameraCenter(cam, desired, clampMargin);
		}
		// Snap tiny decimals to avoid micro jitter at edges
		target.x = Mathf.Abs(target.x) < 1e-6f ? 0f : target.x;
		target.y = Mathf.Abs(target.y) < 1e-6f ? 0f : target.y;
		target.z = cam.transform.position.z;
		cam.transform.position = target;
		return target - oldPos;
	}
}


