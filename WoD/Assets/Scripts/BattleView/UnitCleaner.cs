using System.Collections;
using Firebase.Database;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Periodically cleans up "ghost" units (hp == 0) that might remain due to race/network issues.
/// - For non-owners: just destroys the local GameObject.
/// - For owners: additionally removes the unit node from RTDB, then destroys locally.
/// Auto-bootstraps in battle scenes (scene name contains "battle" or an ArmySpawner exists).
/// </summary>
public class UnitCleaner : MonoBehaviour
{
	[SerializeField] private float intervalSeconds = 1.0f;
	[SerializeField] private bool  verboseLogs = false;

	private Coroutine loopCo;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void AutoBootstrap()
	{
		try
		{
			var scene = SceneManager.GetActiveScene();
			if (!scene.IsValid()) return;

			bool looksLikeBattle = scene.name.ToLower().Contains("battle")
#if UNITY_2022_2_OR_NEWER
				|| Object.FindFirstObjectByType<ArmySpawner>(FindObjectsInactive.Include) != null;
#else
				|| Object.FindObjectOfType<ArmySpawner>(true) != null;
#endif

			if (!looksLikeBattle) return;

			if (Object.FindObjectOfType<UnitCleaner>() == null)
			{
				var go = new GameObject("UnitCleaner");
				go.AddComponent<UnitCleaner>();
			}
		}
		catch { /* best-effort */ }
	}

	private void OnEnable()
	{
		if (loopCo == null) loopCo = StartCoroutine(Loop());
	}

	private void OnDisable()
	{
		if (loopCo != null) StopCoroutine(loopCo);
		loopCo = null;
	}

	private IEnumerator Loop()
	{
		var wait = new WaitForSeconds(intervalSeconds > 0f ? intervalSeconds : 1f);
		while (true)
		{
			Tick();
			yield return wait;
		}
	}

	private void Tick()
	{
		// Работать только в активном бою (если менеджер готовности есть)
		try
		{
			if (BattleReadyManager.Active && !BattleReadyManager.BothReady)
				return; // до старта боя не трогаем
		}
		catch { /* if manager not present, proceed */ }

		Unit[] units;
#if UNITY_2022_2_OR_NEWER
		units = Object.FindObjectsByType<Unit>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
		units = Object.FindObjectsOfType<Unit>(true);
#endif
		for (int i = 0; i < units.Length; i++)
		{
			var u = units[i];
			if (!u) continue;

			bool dead = false;
			try { dead = (u.maxHP > 0 && u.health <= 0); } catch { dead = true; }
			if (!dead) continue;

			SafeLog($"Cleanup dead unit: {u.unitKey} type={u.unitType} host={u.host}");

			// Если это владелец на данном устройстве — попробуем удалить узел RTDB (best-effort).
			bool iAmOwner = false;
			try { iAmOwner = (Globalflags.ifHost == u.host); } catch { iAmOwner = false; }
			if (iAmOwner && !string.IsNullOrEmpty(u.sessionId) && !string.IsNullOrEmpty(u.unitKey))
			{
				try
				{
					string branch = u.host ? "hostArmy" : "clientArmy";
					var unitRef = FirebaseDatabase.DefaultInstance.RootReference
						.Child("sessions").Child(u.sessionId)
						.Child(branch).Child(u.unitKey);
					unitRef.RemoveValueAsync();
				}
				catch { /* ignore */ }
			}

			// Всегда удаляем локальный объект, чтобы не оставался фантом
			try { Destroy(u.gameObject); } catch { /* ignore */ }
		}
	}

	private void SafeLog(string msg)
	{
		if (verboseLogs) Debug.Log($"[UnitCleaner] {msg}");
	}
}











