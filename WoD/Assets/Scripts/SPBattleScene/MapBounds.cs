using UnityEngine;

/// <summary>
/// Defines world-space rectangular bounds of the playable map.
/// Place this on an empty GameObject in the scene and set size/center.
/// </summary>
public class MapBounds : MonoBehaviour
{
	public static MapBounds Instance { get; private set; }

	[Header("World Rect (center + size)")]
	[SerializeField] private Vector2 center = Vector2.zero;
	[SerializeField] private Vector2 size = new Vector2(20f, 12f);

	private void Awake()
	{
		if (Instance == null) Instance = this;
		else if (Instance != this) Destroy(gameObject);
	}

	public Rect WorldRect
	{
		get
		{
			float w = Mathf.Max(0.01f, size.x);
			float h = Mathf.Max(0.01f, size.y);
			return new Rect(center.x - w * 0.5f, center.y - h * 0.5f, w, h);
		}
	}

	public static bool TryGet(out MapBounds mb)
	{
		mb = Instance != null ? Instance : FindObjectOfType<MapBounds>();
		if (mb != null && Instance == null) Instance = mb;
		return mb != null;
	}

	// Clamp arbitrary point inside world rect with optional margin from edges
	public static Vector3 ClampPoint(Vector3 p, float margin = 0f)
	{
		if (!TryGet(out var mb)) return p;
		var r = mb.WorldRect;
		float minX = r.xMin + margin;
		float maxX = r.xMax - margin;
		float minY = r.yMin + margin;
		float maxY = r.yMax - margin;
		p.x = Mathf.Clamp(p.x, minX, maxX);
		p.y = Mathf.Clamp(p.y, minY, maxY);
		return p;
	}

	// Clamp camera center so that full viewport stays inside map bounds
	public static Vector3 ClampCameraCenter(Camera cam, Vector3 desiredCenter, float margin = 0f)
	{
		if (cam == null || !TryGet(out var mb)) return desiredCenter;
		var r = mb.WorldRect;
		float halfH = cam.orthographicSize;
		float halfW = halfH * cam.aspect;
		float minX = r.xMin + halfW + margin;
		float maxX = r.xMax - halfW - margin;
		float minY = r.yMin + halfH + margin;
		float maxY = r.yMax - halfH - margin;
		desiredCenter.x = Mathf.Clamp(desiredCenter.x, minX, maxX);
		desiredCenter.y = Mathf.Clamp(desiredCenter.y, minY, maxY);
		return desiredCenter;
	}
}


