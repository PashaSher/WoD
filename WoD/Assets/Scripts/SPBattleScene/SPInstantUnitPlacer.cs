using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Single-player instant unit placer:
/// - Spawns the specified units immediately on the configured player side (left/right)
/// - No timers, no interactive placement
/// - Ends placement phase right after the last unit is placed so the battle starts
/// </summary>
public class SPInstantUnitPlacer : MonoBehaviour
{
	[Header("Prefabs")]
	[SerializeField] private GameObject unitRootPrefab;

	[Header("Stats (1 asset per type)")]
	[SerializeField] private List<UnitStats> unitStatsList;

	[Header("References")]
	[SerializeField] private Transform unitsParent;

	[Header("Placement")]
	[SerializeField] private bool playerOnLeft = true;   // true: LEFT side, false: RIGHT side
	[SerializeField] private bool playerIsBlue = false;  // true: BLUE, false: BLACK (default SP is black)
	[SerializeField] private float xPadding = 2.0f;
	[SerializeField] private float yTopPadding = 1.0f;
	[SerializeField] private float rowStep = 1.5f;
	[SerializeField] private float colStep = 1.6f;

	[Header("Units to spawn")]
	[SerializeField] private List<UnitEntry> units = new List<UnitEntry>();

	[Header("Debug")]
	[SerializeField] private bool verboseLogs = true;

	private readonly Dictionary<UnitType, UnitStats> statsByType = new Dictionary<UnitType, UnitStats>();

	[Serializable]
	private struct UnitEntry
	{
		public UnitType type;
		public int count;
	}

	private void Awake()
	{
		SafeLog("Awake() start");

		// Ensure Units parent
		if (unitsParent == null)
		{
			var go = GameObject.Find("Units") ?? new GameObject("Units");
			unitsParent = go.transform;
			SafeLog("Units parent auto-created/attached");
		}

		// Build stats map
		statsByType.Clear();
		if (unitStatsList != null)
		{
			for (int i = 0; i < unitStatsList.Count; i++)
			{
				var s = unitStatsList[i];
				if (s == null) continue;
				if (!statsByType.ContainsKey(s.unitType))
					statsByType.Add(s.unitType, s);
			}
		}

		// Apply SP config so other systems (labels/colors/orientation) are consistent
		SPBattleConfig.PlayerOnLeft = playerOnLeft;
		SPBattleConfig.PlayerIsBlue = playerIsBlue;
		// Treat the local player as HOST when on the left; otherwise as CLIENT.
		// Other systems infer side by host/client flag.
		Globalflags.ifHost = playerOnLeft;

		// We want no interactive placement or ready timers in SP instant mode.
		// If auto-bootstrapped managers exist, disable them.
		TryDisablePlacementAndTimers();

		SafeLog($"Awake() done. Stats found: {statsByType.Count}, playerOnLeft={playerOnLeft}, playerIsBlue={playerIsBlue}");
	}

	private void Start()
	{
		EnsureInputForWorldClicks(); // safe for clickables/drag; harmless otherwise

		if (unitRootPrefab == null)
		{
			Debug.LogError("[SPInstantUnitPlacer] UnitRootPrefab not set.");
			return;
		}
		if (unitRootPrefab.scene.IsValid())
		{
			Debug.LogError("[SPInstantUnitPlacer] UnitRootPrefab references a scene object. Drag a prefab asset.");
			return;
		}
		if (units == null || units.Count == 0)
		{
			Debug.LogWarning("[SPInstantUnitPlacer] No units configured to spawn.");
		}

		// Pause combat logic during the instant spawn (just in case)
		BattlePlacementState.BeginPlacement();
		SpawnConfiguredUnits();
		// Immediately start the battle
		BattlePlacementState.EndPlacement();
		SafeLog("Placement ended → battle starts");
	}

	private void SpawnConfiguredUnits()
	{
		var cam = Camera.main;
		if (cam == null)
		{
			Debug.LogError("[SPInstantUnitPlacer] Camera.main == null");
			return;
		}

		float halfH = cam.orthographicSize;
		float halfW = halfH * cam.aspect;

		float startX = playerOnLeft ? (-halfW + Mathf.Max(0f, xPadding)) : (halfW - Mathf.Max(0f, xPadding));
		float y = halfH - Mathf.Max(0f, yTopPadding);
		int spawned = 0;

		for (int e = 0; e < (units?.Count ?? 0); e++)
		{
			var entry = units[e];
			if (entry.count <= 0) continue;
			for (int i = 0; i < entry.count; i++)
			{
				Vector3 pos = new Vector3(startX, y, 0f);
				var prefabToUse = GetPrefabForType(entry.type);
				var go = Instantiate(prefabToUse, pos, Quaternion.identity, unitsParent);

				// Visual
				var visualTr = go.transform.Find("Visual");
				if (visualTr == null)
				{
					Debug.LogError("[SPInstantUnitPlacer] 'Visual' child NOT found in Unit_Root prefab.");
					Destroy(go);
					continue;
				}

				var anim = visualTr.GetComponent<Animator>();
				var renderers = visualTr.GetComponentsInChildren<SpriteRenderer>(true);
				SpriteRenderer primarySr = null;
				if (renderers != null && renderers.Length > 0)
				{
					primarySr = renderers[0];
				}
				else
				{
					primarySr = visualTr.GetComponent<SpriteRenderer>();
					if (primarySr == null) primarySr = visualTr.gameObject.AddComponent<SpriteRenderer>();
					renderers = new SpriteRenderer[] { primarySr };
				}

				UnitStats stats = null;
				statsByType.TryGetValue(entry.type, out stats);
				if (stats != null)
				{
					if (anim != null && stats.animatorOverride != null)
					{
						anim.runtimeAnimatorController = stats.animatorOverride;
						anim.enabled = true;
					}
					else
					{
						if (anim != null && anim.runtimeAnimatorController != null)
							anim.enabled = true;
						else if (anim != null)
							anim.enabled = false;
					}
				}

				// Unit component
				var unit = go.GetComponent<Unit>();
				if (unit != null)
				{
					if (stats != null) unit.Init(entry.type.ToString(), stats);
					else               unit.unitType = entry.type.ToString();
					unit.host = Globalflags.ifHost; // mark local side by host/client mapping
				}

				// Tint and sorting
				Color tint = SPBattleConfig.GetTint(true);
				for (int r = 0; r < renderers.Length; r++)
				{
					var sr = renderers[r];
					if (sr == null) continue;
					sr.enabled = true;
					sr.color = tint;
					if (sr.sortingOrder < 5) sr.sortingOrder = 5;
				}

				// Facing: player on left looks right; on right looks left
				try
				{
					float lookDir = playerOnLeft ? +1f : -1f;
					if (unit != null) unit.FaceTowardsX(unit.transform.position.x + lookDir);
					else
					{
						// Fallback: flip local scale on Visual
						var s = visualTr.localScale;
						s.x = (playerOnLeft ? -Mathf.Abs(s.x) : Mathf.Abs(s.x));
						visualTr.localScale = s;
					}
				}
				catch { }

				// Optional ring color (cosmetic)
				try
				{
					var ring = go.transform.Find("SelectionRing")?.GetComponent<SpriteRenderer>();
					if (ring != null) ring.color = (playerIsBlue ? Color.blue : Color.cyan);
				}
				catch { }

				// Avoid MP behaviours in SP
				var mpMover = visualTr.GetComponent<UnitDragMover>();
				if (mpMover != null) mpMover.enabled = false;
				var mpAutoAttack = visualTr.GetComponent<UnitAutoAttack>();
				if (mpAutoAttack != null) mpAutoAttack.enabled = false;

				// Ensure Collider2D for clicks (optional)
				var col2d = visualTr.GetComponent<Collider2D>();
				if (col2d == null)
				{
					col2d = visualTr.gameObject.AddComponent<CircleCollider2D>();
					var cc = col2d as CircleCollider2D;
					if (cc != null)
					{
						cc.isTrigger = true;
						cc.radius = 0.4f;
					}
				}

				// SP components
				if (anim != null && visualTr.GetComponent<SPAnimatorFlags>() == null)
					visualTr.gameObject.AddComponent<SPAnimatorFlags>();
				if (visualTr.GetComponent<SPUnitAutoAttack>() == null)
					visualTr.gameObject.AddComponent<SPUnitAutoAttack>();
				if (visualTr.GetComponent<SPAttackEvents>() == null)
					visualTr.gameObject.AddComponent<SPAttackEvents>();
				if (visualTr.GetComponent<SPUnitDragMover>() == null)
					visualTr.gameObject.AddComponent<SPUnitDragMover>();

				go.name = $"{entry.type}_SP_{spawned}";
				spawned++;

				// Next position
				y -= Mathf.Max(0.1f, rowStep);
				if (y < -halfH + Mathf.Max(0.1f, yTopPadding))
				{
					y = halfH - Mathf.Max(0.1f, yTopPadding);
					startX += (playerOnLeft ? +Mathf.Max(0.1f, colStep) : -Mathf.Max(0.1f, colStep));
				}
			}
		}

		SafeLog($"Spawned {spawned} units (instant).");
	}

	private void TryDisablePlacementAndTimers()
	{
		// Placement manager (interactive) — we don't need it
		try
		{
#if UNITY_6000_0_OR_NEWER || UNITY_2023_1_OR_NEWER
			var placement = UnityEngine.Object.FindFirstObjectByType<BattlePlacementManager>(FindObjectsInactive.Include);
#else
			var placement = UnityEngine.Object.FindObjectOfType<BattlePlacementManager>(true);
#endif
			if (placement != null && placement.gameObject != null)
			{
				placement.gameObject.SetActive(false);
				SafeLog("Disabled BattlePlacementManager");
			}
		}
		catch { }

		// Ready/timer manager — hide overlay to avoid countdowns in SP instant mode
		try
		{
#if UNITY_6000_0_OR_NEWER || UNITY_2023_1_OR_NEWER
			var brm = UnityEngine.Object.FindFirstObjectByType<BattleReadyManager>(FindObjectsInactive.Include);
#else
			var brm = UnityEngine.Object.FindObjectOfType<BattleReadyManager>(true);
#endif
			if (brm != null && brm.gameObject != null)
			{
				brm.gameObject.SetActive(false);
				SafeLog("Disabled BattleReadyManager");
			}
		}
		catch { }
	}

	// Ensure there is an EventSystem and Physics2D raycaster so IPointer* handlers work on 2D objects
	private void EnsureInputForWorldClicks()
	{
		// EventSystem
		if (EventSystem.current == null)
		{
			var esGo = new GameObject("EventSystem");
			esGo.AddComponent<EventSystem>();
			esGo.AddComponent<StandaloneInputModule>();
		}
		// Physics2DRaycaster on main camera
		var cam = Camera.main;
		if (cam != null && cam.GetComponent<Physics2DRaycaster>() == null)
		{
			cam.gameObject.AddComponent<Physics2DRaycaster>();
		}
	}

	private GameObject GetPrefabForType(UnitType type)
	{
		if (statsByType.TryGetValue(type, out var s) && s != null && s.unitPrefab != null)
			return s.unitPrefab;
		var res = Resources.Load<GameObject>($"Units/{type}");
		if (res != null) return res;
		res = Resources.Load<GameObject>($"Units/{type}_Prefab");
		if (res != null) return res;
		return unitRootPrefab;
	}

	private void SafeLog(string msg)
	{
		if (verboseLogs)
			Debug.Log($"[SPInstantUnitPlacer] {msg}");
	}
}





