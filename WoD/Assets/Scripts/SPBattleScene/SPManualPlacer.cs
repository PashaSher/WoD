using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Manual placement for Single Player:
/// - Only places player's (BLACK/host) units by clicking on player's half
/// - No timers, no RTDB, no ready UI; battle starts immediately after last placement
/// - Uses global BattlePlacementState to pause/resume combat logic
/// </summary>
public class SPManualPlacer : MonoBehaviour
{
	[Header("UI Style (minimal helper)")]
	[SerializeField] private Font  uiFont;
	[SerializeField] private Color helperColor = Color.black;
	[SerializeField] private int   helperFontSize = 64;

	[Header("Placement bounds/padding")]
	[SerializeField] private float xPad = 0.2f;
	[SerializeField] private float yPad = 0.5f;

	[Header("Debug")]
	[SerializeField] private bool verboseLogs = true;

	private Camera cam;
	private float halfW;
	private float halfH;

	private readonly List<Unit> myUnits = new List<Unit>();
	private int placeIndex = -1;
	private bool isPlacing;

	private GameObject helperCanvasGo;
	private Text helperText;

	private void Awake()
	{
		cam = Camera.main;
		ComputeBounds();

		// In SP we play as BLACK/host (left side)
		SPBattleConfig.PlayerOnLeft = true;
		SPBattleConfig.PlayerIsBlue = false;
		Globalflags.ifHost = true;

		// Turn off legacy placement/timer managers if auto-added
		TryDisableLegacyManagers();

		// Freeze combat immediately on scene load, BEFORE units spawn,
		// so auto-атака не начнётся до окончания расстановки.
		BattlePlacementState.BeginPlacement();
		if (verboseLogs) Debug.Log("[SPManualPlacer] Placement phase started at Awake()");
	}

	private void Start()
	{
		EnsureInputForWorldClicks();
		StartCoroutine(WaitUnitsAndBegin());
	}

	private IEnumerator WaitUnitsAndBegin()
	{
		// Wait until SPArmySpawner (or equivalent) has spawned my (host) units
		for (;;)
		{
#if UNITY_2022_2_OR_NEWER
			var all = UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
			var all = UnityEngine.Object.FindObjectsOfType<Unit>(true);
#endif
			int own = 0;
			for (int i = 0; i < all.Length; i++)
			{
				var u = all[i];
				if (!u) continue;
				if (u.host) own++;
			}
			if (own > 0) break;
			yield return null;
		}
		BeginPlacement();
	}

	private void BeginPlacement()
	{
		BattlePlacementState.BeginPlacement();
		SafeLog("[SPManualPlacer] BeginPlacement");

		// Collect only own (host) units. Passive first (as obstacles), then others.
		myUnits.Clear();
#if UNITY_2022_2_OR_NEWER
		var all = UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
		var all = UnityEngine.Object.FindObjectsOfType<Unit>(true);
#endif
		var passives = new List<Unit>();
		var others = new List<Unit>();
		for (int i = 0; i < all.Length; i++)
		{
			var u = all[i];
			if (!u) continue;
			if (!u.host) continue; // only player's black units
			bool isPassive = false;
			try { isPassive = u.isPassive; } catch { isPassive = false; }
			if (isPassive) passives.Add(u); else others.Add(u);
		}
		myUnits.AddRange(passives);
		myUnits.AddRange(others);

		placeIndex = 0;
		isPlacing = true;
		EnsureHelper(true);
		UpdateHelperText();
		SafeLog($"[SPManualPlacer] Units to place: {myUnits.Count}");
	}

	private void Update()
	{
		if (!isPlacing) return;
		if (placeIndex < 0 || placeIndex >= myUnits.Count)
		{
			FinishPlacement();
			return;
		}

		Vector3 world;
		if (TryGetClickWorld(out world))
		{
			world = ClampToMyHalf(world);
			if (IsPlacementBlockedFor(myUnits[placeIndex], world, out var whyBlocked))
			{
				ShowToastOnce(whyBlocked ?? "Invalid placement");
				return;
			}
			PlaceCurrent(world);
		}
	}

	private void PlaceCurrent(Vector3 world)
	{
		var u = myUnits[placeIndex];
		if (!u) { placeIndex++; UpdateHelperText(); return; }

		u.transform.position = world;
		// Face towards enemy: host (left) looks right
		try { u.FaceTowardsX(u.transform.position.x + 1f); } catch { }

		placeIndex++;
		UpdateHelperText();
		if (placeIndex >= myUnits.Count)
			FinishPlacement();
	}

	private void FinishPlacement()
	{
		isPlacing = false;
		BattlePlacementState.EndPlacement(); // battle starts now
		EnsureHelper(false);
		SafeLog("[SPManualPlacer] FinishPlacement → battle starts");
	}

	private void ComputeBounds()
	{
		if (!cam) cam = Camera.main;
		if (!cam) return;
		halfH = cam.orthographicSize;
		halfW = halfH * cam.aspect;
	}

	private Vector3 ClampToMyHalf(Vector3 pos)
	{
		// Player is host (left half)
		pos.x = Mathf.Min(pos.x, 0f);
		pos.x = Mathf.Clamp(pos.x, -halfW + Mathf.Max(0f, xPad), halfW - Mathf.Max(0f, xPad));
		pos.y = Mathf.Clamp(pos.y, -halfH + Mathf.Max(0f, yPad), halfH - Mathf.Max(0f, yPad));
		return pos;
	}

	private bool IsPlacementBlockedFor(Unit placing, Vector3 world, out string reason)
	{
		const float minSpacing = 0.6f;
		const float obstaclePad = 0.5f;
		reason = null;
#if UNITY_2022_2_OR_NEWER
		var all = UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
		var all = UnityEngine.Object.FindObjectsOfType<Unit>(true);
#endif
		for (int i = 0; i < all.Length; i++)
		{
			var u = all[i];
			if (!u) continue;
			if (u == placing) continue;
			Vector3 pos;
			try { pos = u.transform.position; } catch { continue; }
			float dist = Vector2.Distance(pos, world);
			bool isPassive = false;
			try { isPassive = u.isPassive; } catch { isPassive = false; }
			if (isPassive)
			{
				if (dist < obstaclePad) { reason = "Cannot place on obstacles"; return true; }
				continue;
			}
			if (dist < minSpacing) { reason = "Too close to another unit"; return true; }
		}

		// Extra: don't drop directly on passive colliders
		var hits = Physics2D.OverlapPointAll(new Vector2(world.x, world.y));
		if (hits != null && hits.Length > 0)
		{
			for (int i = 0; i < hits.Length; i++)
			{
				try
				{
					var go = hits[i].gameObject;
					if (!go) continue;
					var u = go.GetComponentInParent<Unit>();
					bool isPassive = false;
					try { isPassive = (u != null && u.isPassive); } catch { isPassive = false; }
					if (isPassive) { reason = "Cannot place on obstacles"; return true; }
				}
				catch { }
			}
		}
		return false;
	}

	private void EnsureHelper(bool on)
	{
		if (!on)
		{
			if (helperCanvasGo) Destroy(helperCanvasGo);
			helperCanvasGo = null;
			helperText = null;
			return;
		}
		if (helperCanvasGo) { UpdateHelperText(); return; }
		helperCanvasGo = new GameObject("SPManualPlacementHelper");
		var canvas = helperCanvasGo.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		helperCanvasGo.AddComponent<GraphicRaycaster>();
		var scaler = helperCanvasGo.AddComponent<CanvasScaler>();
		scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		scaler.referenceResolution = new Vector2(1280, 720);

		var textGo = new GameObject("Hint");
		textGo.transform.SetParent(helperCanvasGo.transform, false);
		helperText = textGo.AddComponent<Text>();
		helperText.alignment = TextAnchor.MiddleCenter;
		helperText.font = uiFont != null ? uiFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
		helperText.fontSize = Mathf.Max(10, helperFontSize);
		helperText.color = helperColor;
		var rt = (RectTransform)helperText.transform;
		rt.anchorMin = new Vector2(0.15f, 0.02f);
		rt.anchorMax = new Vector2(0.85f, 0.12f);
		rt.offsetMin = Vector2.zero;
		rt.offsetMax = Vector2.zero;

		UpdateHelperText();
	}

	private void UpdateHelperText()
	{
		if (!helperText) return;
		if (!isPlacing || placeIndex < 0 || placeIndex >= myUnits.Count)
		{
			helperText.text = "";
			return;
		}
		var u = myUnits[placeIndex];
		string name = (u != null && !string.IsNullOrEmpty(u.unitType)) ? u.unitType : "Unit";
		helperText.color = Color.black; // требование: чёрным цветом
		helperText.text = $"Placing: {name}";
	}

	private GameObject toastGo;
	private Text toastText;
	private Coroutine toastCo;
	private void ShowToastOnce(string msg, float seconds = 1.0f)
	{
		if (!helperCanvasGo) EnsureHelper(true);
		if (!toastGo)
		{
			toastGo = new GameObject("Toast");
			toastGo.transform.SetParent(helperCanvasGo.transform, false);
			toastText = toastGo.AddComponent<Text>();
			toastText.alignment = TextAnchor.MiddleCenter;
			toastText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			toastText.fontSize = 28;
			toastText.color = Color.red;
			var rt = (RectTransform)toastGo.transform;
			rt.anchorMin = new Vector2(0.2f, 0.80f);
			rt.anchorMax = new Vector2(0.8f, 0.90f);
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;
		}
		toastText.text = msg;
		if (toastCo != null) StopCoroutine(toastCo);
		toastCo = StartCoroutine(HideToastAfter(seconds));
	}
	private IEnumerator HideToastAfter(float s)
	{
		yield return new WaitForSeconds(Mathf.Max(0.1f, s));
		if (toastText) toastText.text = "";
	}

	private bool TryGetClickWorld(out Vector3 world)
	{
#if ENABLE_INPUT_SYSTEM
		if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
		{
			var pos = Mouse.current.position.ReadValue();
			world = ScreenToWorld(new Vector3(pos.x, pos.y, 0f));
			return true;
		}
		if (Touchscreen.current != null)
		{
			var touch = Touchscreen.current.primaryTouch;
			if (touch != null && touch.press.wasPressedThisFrame)
			{
				var pos = touch.position.ReadValue();
				world = ScreenToWorld(new Vector3(pos.x, pos.y, 0f));
				return true;
			}
		}
#else
		if (Input.GetMouseButtonDown(0))
		{
			world = ScreenToWorld(Input.mousePosition);
			return true;
		}
		if (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)
		{
			world = ScreenToWorld(Input.GetTouch(0).position);
			return true;
		}
#endif
		world = Vector3.zero;
		return false;
	}

	private Vector3 ScreenToWorld(Vector3 screen)
	{
		if (!cam) cam = Camera.main;
		if (!cam) return Vector3.zero;
		float z = Mathf.Abs(cam.transform.position.z);
		var w = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, z));
		w.z = 0f;
		return w;
	}

	private void TryDisableLegacyManagers()
	{
		// Disable interactive MP placement manager (with prompts/RTDB)
		try
		{
#if UNITY_6000_0_OR_NEWER || UNITY_2023_1_OR_NEWER
			var placement = UnityEngine.Object.FindFirstObjectByType<BattlePlacementManager>(FindObjectsInactive.Include);
#else
			var placement = UnityEngine.Object.FindObjectOfType<BattlePlacementManager>(true);
#endif
			if (placement != null && placement.gameObject != null)
				placement.gameObject.SetActive(false);
		}
		catch { }
		// Disable "ready/timer" manager
		try
		{
#if UNITY_6000_0_OR_NEWER || UNITY_2023_1_OR_NEWER
			var brm = UnityEngine.Object.FindFirstObjectByType<BattleReadyManager>(FindObjectsInactive.Include);
#else
			var brm = UnityEngine.Object.FindObjectOfType<BattleReadyManager>(true);
#endif
			if (brm != null && brm.gameObject != null)
				brm.gameObject.SetActive(false);
		}
		catch { }
	}

	private void EnsureInputForWorldClicks()
	{
		if (EventSystem.current == null)
		{
			var esGo = new GameObject("EventSystem");
			esGo.AddComponent<EventSystem>();
			esGo.AddComponent<StandaloneInputModule>();
		}
		var c = Camera.main;
		if (c != null && c.GetComponent<Physics2DRaycaster>() == null)
			c.gameObject.AddComponent<Physics2DRaycaster>();
	}

	private void SafeLog(string msg)
	{
		if (verboseLogs) Debug.Log(msg);
	}
}


