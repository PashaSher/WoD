using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Single-player spawner: instantiates player's selected units (left side) from SPArmyState.
/// Uses UnitStats to configure visuals and behaviors similarly to multiplayer spawner.
/// </summary>
public class SPArmySpawner : MonoBehaviour
{
    [Header("Prefabs")]
    [SerializeField] private GameObject unitRootPrefab;

    [Header("Stats (1 asset per type)")]
    [SerializeField] private List<UnitStats> unitStatsList;

    [Header("References")]
    [SerializeField] private Transform unitsParent;

    [Header("Debug")]
    [SerializeField] private bool verboseLogs = true;

    private readonly Dictionary<UnitType, UnitStats> statsByType = new();

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
		EnsureInputForWorldClicks();
		EnsureBattleEndManagerPresent();
		EnsureEnemyBotPresent();
        if (unitRootPrefab == null)
        {
            Debug.LogError("[SPArmySpawner] UnitRootPrefab not set.");
            return;
        }
        if (unitRootPrefab.scene.IsValid())
        {
            Debug.LogError("[SPArmySpawner] UnitRootPrefab references a scene object. Drag a prefab asset.");
            return;
        }
        if (!SPArmyState.TryGetSelection(out var counts, out var _))
        {
            Debug.LogWarning("[SPArmySpawner] No selection found in SPArmyState.");
            return;
        }

        SpawnArmy(counts);
    }

    private void SpawnArmy(Dictionary<UnitType, int> counts)
    {
        var cam = Camera.main;
        if (cam == null)
        {
            Debug.LogError("[SPArmySpawner] Camera.main == null");
            return;
        }

        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        float startX = -halfW + 2f;
        float y = halfH - 1f;
        int spawned = 0;

        foreach (var kv in counts)
        {
            var type = kv.Key;
            int num = kv.Value;
            for (int i = 0; i < num; i++)
            {
                Vector3 pos = new Vector3(startX, y, 0f);
                var prefabToUse = GetPrefabForType(type);
                var go = Instantiate(prefabToUse, pos, Quaternion.identity, unitsParent);

                // Setup Visual
                var visualTr = go.transform.Find("Visual");
                if (visualTr == null)
                {
                    Debug.LogError("[SPArmySpawner] 'Visual' child NOT found in Unit_Root prefab.");
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

                UnitStats stats = null;
                statsByType.TryGetValue(type, out stats);
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
                        {
                            anim.enabled = true;
                        }
                        else if (anim != null)
                        {
                            anim.enabled = false;
                        }
                    }
                }

                var unit = go.GetComponent<Unit>();
                if (unit != null)
                {
                    if (stats != null) unit.Init(type.ToString(), stats);
                    else               unit.unitType = type.ToString();
                    unit.host = true; // mark as player's side for downstream systems
                }

                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    r.enabled = true;
                    r.color = Color.black;
                    if (r.sortingOrder < 5) r.sortingOrder = 5;
                }

                // Flip visual to face right (like left-side team)
                var s = visualTr.localScale;
                s.x = -Mathf.Abs(s.x);
                visualTr.localScale = s;

				var ring = go.transform.Find("SelectionRing")?.GetComponent<SpriteRenderer>();
				if (ring != null) { ring.color = Color.cyan; }

                go.name = $"{type}_SP_{spawned}";
                spawned++;

				// Disable multiplayer mover if present to avoid RTDB usage in SP.
				var mpMover = visualTr.GetComponent<UnitDragMover>();
				if (mpMover != null) mpMover.enabled = false;
				// Disable multiplayer auto-attack to prevent double handlers in SP.
				var mpAutoAttack = visualTr.GetComponent<UnitAutoAttack>();
				if (mpAutoAttack != null) mpAutoAttack.enabled = false;
				// Ensure collider for pointer events
				var col2d = visualTr.GetComponent<Collider2D>();
				if (col2d == null)
				{
					// Prefer CircleCollider2D as a simple clickable area
					col2d = visualTr.gameObject.AddComponent<CircleCollider2D>();
					var cc = col2d as CircleCollider2D;
					if (cc != null)
					{
						cc.isTrigger = true;
						cc.radius = 0.4f;
					}
				}
				// Animator flags (if Animator present) - add BEFORE mover so mover can find it in Awake
				if (anim != null)
				{
					if (visualTr.GetComponent<SPAnimatorFlags>() == null)
						visualTr.gameObject.AddComponent<SPAnimatorFlags>();
				}
				// Auto-attack scanner (SP)
				if (visualTr.GetComponent<SPUnitAutoAttack>() == null)
					visualTr.gameObject.AddComponent<SPUnitAutoAttack>();
				// Animator event bridge for firing via animation
				if (visualTr.GetComponent<SPAttackEvents>() == null)
					visualTr.gameObject.AddComponent<SPAttackEvents>();
				// Add SP mover last
				var spMover = visualTr.GetComponent<SPUnitDragMover>();
				if (spMover == null) spMover = visualTr.gameObject.AddComponent<SPUnitDragMover>();

                // Next position
                y -= 1.5f;
                if (y < -halfH + 1f)
                {
                    y = halfH - 1f;
                    startX += 1.6f;
                }
            }
        }
        SafeLog($"Spawned {spawned} units from SP selection.");
    }

	private void EnsureBattleEndManagerPresent()
	{
		try
		{
			if (FindObjectOfType<SPBattleEndManager>() == null)
			{
				var go = new GameObject("SPBattleEnd(Auto-FromSpawner)");
				go.AddComponent<SPBattleEndManager>();
				SafeLog("SPBattleEndManager auto-attached by SPArmySpawner");
			}
		}
		catch { }
	}

	private void EnsureEnemyBotPresent()
	{
		try
		{
			if (FindObjectOfType<SPEnemyBot>() == null)
			{
				var go = new GameObject("SPEnemyBot(Auto-FromSpawner)");
				go.AddComponent<SPEnemyBot>();
				SafeLog("SPEnemyBot auto-attached by SPArmySpawner");
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
			// StandaloneInputModule works with both old and new input (via back-compat)
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
            Debug.Log($"[SPArmySpawner] {msg}");
    }
}


