using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Single-player enemy spawner: generates a random enemy army within the same points budget
/// and places units randomly on the RIGHT half of the map.
/// </summary>
public class SPEnemySpawner : MonoBehaviour
{
	[Header("Prefabs")]
	[SerializeField] private GameObject unitRootPrefab;

	[Header("Stats (1 asset per type)")]
	[SerializeField] private List<UnitStats> unitStatsList;

	[Header("References")]
	[SerializeField] private Transform unitsParent;

	[Header("Generation")]
	[SerializeField] private UnitType[] allowedTypes = new[] { UnitType.Rifleman, UnitType.Grenader, UnitType.Sniper, UnitType.Tank };
	[SerializeField] private float spawnMargin = 1.2f; // keep off edges a little
	[SerializeField] private int maxPlacementAttempts = 10;
	[SerializeField] private float minSpacing = 0.8f; // simple non-overlap guard

	[Header("Debug")]
	[SerializeField] private bool verboseLogs = true;

	private readonly Dictionary<UnitType, UnitStats> statsByType = new();
	private readonly List<Vector3> placedPositions = new();

	private void Awake()
	{
		SafeLog("Awake() start");

		if (unitsParent == null)
		{
			var go = GameObject.Find("Units") ?? new GameObject("Units");
			unitsParent = go.transform;
			SafeLog("Units parent auto-created/attached");
		}

		statsByType.Clear();
		if (unitStatsList != null)
		{
			foreach (var s in unitStatsList)
			{
				if (s == null) continue;
				if (!statsByType.ContainsKey(s.unitType))
					statsByType.Add(s.unitType, s);
			}
		}
		SafeLog($"Awake() done. Stats found: {statsByType.Count}");
	}

	private void Start()
	{
		EnsureBattleEndManagerPresent();
		if (unitRootPrefab != null && unitRootPrefab.scene.IsValid())
		{
			Debug.LogError("[SPEnemySpawner] UnitRootPrefab references a scene object. Drag a prefab asset.");
			return;
		}

		if (!SPArmyState.TryGetSelection(out var _, out var startingPoints))
		{
			SafeLog("No SP selection found; using default budget 100.");
			startingPoints = 100;
		}

		var enemyCounts = GenerateRandomArmy(startingPoints);
		SpawnEnemy(enemyCounts);
	}

	private Dictionary<UnitType, int> GenerateRandomArmy(int budget)
	{
		var counts = new Dictionary<UnitType, int>();
		if (allowedTypes == null || allowedTypes.Length == 0) return counts;

		// compute min cost among allowed
		int minCost = int.MaxValue;
		foreach (var t in allowedTypes)
		{
			if (!UnitPrices.Cost.TryGetValue(t, out var c)) continue;
			if (c < minCost) minCost = c;
		}
		if (minCost == int.MaxValue) return counts;

		int safety = 10000; // guard against infinite loops
		while (budget >= minCost && --safety > 0)
		{
			// pick a random affordable type
			var affordable = new List<UnitType>();
			for (int i = 0; i < allowedTypes.Length; i++)
			{
				var t = allowedTypes[i];
				if (UnitPrices.Cost.TryGetValue(t, out var c) && c <= budget)
					affordable.Add(t);
			}
			if (affordable.Count == 0) break;

			var pick = affordable[Random.Range(0, affordable.Count)];
			int price = UnitPrices.Cost[pick];
			budget -= price;
			counts[pick] = counts.TryGetValue(pick, out var cur) ? cur + 1 : 1;
		}

		SafeLog($"Generated enemy army: {Describe(counts)}");
		return counts;
	}

	private void SpawnEnemy(Dictionary<UnitType, int> counts)
	{
		var cam = Camera.main;
		if (cam == null)
		{
			Debug.LogError("[SPEnemySpawner] Camera.main == null");
			return;
		}

		float halfH = cam.orthographicSize;
		float halfW = halfH * cam.aspect;

		placedPositions.Clear();

		int spawned = 0;
		foreach (var kv in counts)
		{
			var type = kv.Key;
			int num = kv.Value;
			for (int i = 0; i < num; i++)
			{
				// random position on RIGHT half
				Vector3 pos;
				int attempts = 0;
				do
				{
					float x = Random.Range(+0.1f * halfW, halfW - spawnMargin);
					float y = Random.Range(-halfH + spawnMargin, halfH - spawnMargin);
					pos = new Vector3(x, y, 0f);
					attempts++;
				}
				while (attempts < maxPlacementAttempts && !IsFarEnough(pos, minSpacing));
				placedPositions.Add(pos);

				var prefabToUse = GetPrefabForType(type);
				if (prefabToUse == null)
				{
					Debug.LogError("[SPEnemySpawner] No prefab to spawn (unitRootPrefab and fallbacks missing).");
					continue;
				}
				var go = Instantiate(prefabToUse, pos, Quaternion.identity, unitsParent);

				// Setup Visual
				var visualTr = go.transform.Find("Visual");
				if (visualTr == null)
				{
					Debug.LogError("[SPEnemySpawner] 'Visual' child NOT found in Unit prefab.");
					Destroy(go);
					continue;
				}

				var anim = visualTr.GetComponent<Animator>();
				var renderers = visualTr.GetComponentsInChildren<SpriteRenderer>(true);
				SpriteRenderer sr = null;
				if (renderers != null && renderers.Length > 0)
				{
					sr = renderers[0];
				}
				else
				{
					sr = visualTr.GetComponent<SpriteRenderer>() ?? visualTr.gameObject.AddComponent<SpriteRenderer>();
					renderers = new SpriteRenderer[] { sr };
				}

				if (statsByType.TryGetValue(type, out var stats) && stats != null)
				{
					if (anim != null && stats.animatorOverride != null)
					{
						anim.runtimeAnimatorController = stats.animatorOverride;
						anim.enabled = true;
					}
					else if (anim != null && anim.runtimeAnimatorController != null)
					{
						anim.enabled = true;
					}
					else if (anim != null)
					{
						anim.enabled = false;
					}
				}

				var unit = go.GetComponent<Unit>();
				if (unit != null)
				{
					if (statsByType.TryGetValue(type, out var stats2) && stats2 != null)
						unit.Init(type.ToString(), stats2);
					else
						unit.unitType = type.ToString();
					unit.host = false; // enemy side
				}

				foreach (var r in renderers)
				{
					if (!r) continue;
					r.enabled = true;
					r.color = Color.blue;
					if (r.sortingOrder < 5) r.sortingOrder = 5;
				}

				// Face LEFT (positive scale X if default faces right)
				var s = visualTr.localScale;
				s.x = Mathf.Abs(s.x);
				visualTr.localScale = s;

				// Disable multiplayer auto-attack in SP to avoid conflicts
				var mpAutoAttack = visualTr.GetComponent<UnitAutoAttack>();
				if (mpAutoAttack != null) mpAutoAttack.enabled = false;

				// Animator flags for SP (move/attack triggers) - add BEFORE mover if mover existed
				if (anim != null && visualTr.GetComponent<SPAnimatorFlags>() == null)
				{
					visualTr.gameObject.AddComponent<SPAnimatorFlags>();
				}
				// Auto-attack scanner (SP)
				if (visualTr.GetComponent<SPUnitAutoAttack>() == null)
					visualTr.gameObject.AddComponent<SPUnitAutoAttack>();
				// Animator event bridge for firing via animation
				if (visualTr.GetComponent<SPAttackEvents>() == null)
					visualTr.gameObject.AddComponent<SPAttackEvents>();

				var ring = go.transform.Find("SelectionRing")?.GetComponent<SpriteRenderer>();
				if (ring != null) ring.color = Color.blue;

				go.name = $"{type}_EN_{spawned}";
				spawned++;
			}
		}
		SafeLog($"Spawned {spawned} enemy units.");
	}

	private void EnsureBattleEndManagerPresent()
	{
		try
		{
			if (FindObjectOfType<SPBattleEndManager>() == null)
			{
				var go = new GameObject("SPBattleEnd(Auto-FromEnemySpawner)");
				go.AddComponent<SPBattleEndManager>();
				SafeLog("SPBattleEndManager auto-attached by SPEnemySpawner");
			}
		}
		catch { }
	}

	private bool IsFarEnough(Vector3 pos, float minDist)
	{
		for (int i = 0; i < placedPositions.Count; i++)
		{
			if ((placedPositions[i] - pos).sqrMagnitude < (minDist * minDist)) return false;
		}
		return true;
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

	private string Describe(Dictionary<UnitType, int> counts)
	{
		var parts = new List<string>();
		foreach (var kv in counts) if (kv.Value > 0) parts.Add($"{kv.Key}:{kv.Value}");
		return parts.Count == 0 ? "empty" : string.Join(", ", parts);
	}

	private void SafeLog(string msg)
	{
		if (verboseLogs)
			Debug.Log($"[SPEnemySpawner] {msg}");
	}
}


