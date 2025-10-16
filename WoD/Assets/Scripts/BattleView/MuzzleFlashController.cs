using System.Collections;
using UnityEngine;

/// <summary>
/// Enables a child named "MuzzleFlash" (or a provided reference)
/// for a short duration when asked. Attach to a Unit root.
/// </summary>
public class MuzzleFlashController : MonoBehaviour
{
	[SerializeField] private GameObject muzzleFlashObject;
	[SerializeField] private float defaultDurationSeconds = 0.5f;

	private Coroutine flashCoroutine;

	private void Awake()
	{
		if (muzzleFlashObject == null)
		{
			// find by name in children (including inactive)
			var all = GetComponentsInChildren<Transform>(true);
			for (int i = 0; i < all.Length; i++)
			{
				if (all[i] != null && all[i].name == "MuzzleFlash")
				{
					muzzleFlashObject = all[i].gameObject;
					break;
				}
			}
		}

		// ensure hidden at start
		SetActiveSafe(false);
	}

	public void PlayFlash(float? durationSeconds = null)
	{
		float d = durationSeconds.HasValue ? Mathf.Max(0f, durationSeconds.Value) : Mathf.Max(0f, defaultDurationSeconds);
		if (muzzleFlashObject == null || d <= 0f) return;

		if (flashCoroutine != null) StopCoroutine(flashCoroutine);
		flashCoroutine = StartCoroutine(FlashRoutine(d));
	}

	private IEnumerator FlashRoutine(float duration)
	{
		SetActiveSafe(true);
		yield return new WaitForSeconds(duration);
		SetActiveSafe(false);
		flashCoroutine = null;
	}

	private void SetActiveSafe(bool value)
	{
		if (muzzleFlashObject != null && muzzleFlashObject.activeSelf != value)
			muzzleFlashObject.SetActive(value);
	}
}


