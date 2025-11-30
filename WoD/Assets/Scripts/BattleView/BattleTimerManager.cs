using System;
using TMPro;
using UnityEngine;

/// <summary>
/// Shows a non-interactive top-center battle timer once the fight starts.
/// When it reaches zero, determines the winner by total remaining HP of all
/// alive non-passive units and finishes the battle.
/// </summary>
public class BattleTimerManager : MonoBehaviour
{
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void AutoAttach()
	{
		try
		{
			var sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
			bool looksLikeBattle = sceneName.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0
#if UNITY_6000_0_OR_NEWER || UNITY_2023_1_OR_NEWER
				|| UnityEngine.Object.FindFirstObjectByType<ArmySpawner>() != null;
#else
				|| UnityEngine.Object.FindObjectOfType<ArmySpawner>() != null;
#endif
			if (!looksLikeBattle) return;
#if UNITY_6000_0_OR_NEWER || UNITY_2023_1_OR_NEWER
			var existing = UnityEngine.Object.FindFirstObjectByType<BattleTimerManager>();
#else
			var existing = UnityEngine.Object.FindObjectOfType<BattleTimerManager>();
#endif
			if (existing != null) return;
			var go = new GameObject("BattleTimerManager(Auto)");
			go.AddComponent<BattleTimerManager>();
		}
		catch { /* best-effort */ }
	}

	[Header("Timer")]
	[SerializeField] private float        battleDurationSeconds = 120f;
	[SerializeField] private TMP_FontAsset timerFont;
	[SerializeField] private int          timerFontSize = 48;

	private Canvas _canvas;
	private TextMeshProUGUI _timerText;
	private bool _running;
	private float _timeLeft;

	private void Awake()
	{
		BuildOverlay();
		HideTimer();
	}

	private void Update()
	{
		// Start counting when both are ready
		if (!_running && BattleReadyManager.BothReady)
		{
			StartTimer();
		}

		if (!_running) return;

		_timeLeft = Mathf.Max(0f, _timeLeft - Time.deltaTime);
		UpdateTimerText();

		if (_timeLeft <= 0f)
		{
			_running = false;
			HideTimer();
			EvaluateWinnerByHpAndFinish();
		}
	}

	private void BuildOverlay()
	{
		var go = new GameObject("BattleTimerOverlay");
		_canvas = go.AddComponent<Canvas>();
		_canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		go.AddComponent<UnityEngine.UI.CanvasScaler>().uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
		go.AddComponent<UnityEngine.UI.GraphicRaycaster>(); // harmless; text itself is non-interactable

		var textGo = new GameObject("BattleTimerText");
		textGo.transform.SetParent(go.transform, false);
		_timerText = textGo.AddComponent<TextMeshProUGUI>();
		_timerText.alignment = TextAlignmentOptions.Center;
		_timerText.fontSize = Mathf.Max(10, timerFontSize);
		if (timerFont != null) _timerText.font = timerFont;
		_timerText.raycastTarget = false; // not interactable

		// Top center band
		var rt = (RectTransform)_timerText.transform;
		rt.anchorMin = new Vector2(0.35f, 0.90f);
		rt.anchorMax = new Vector2(0.65f, 0.995f);
		rt.offsetMin = Vector2.zero;
		rt.offsetMax = Vector2.zero;

		// Color by local role: HOST -> black, CLIENT -> blue
		_timerText.color = Globalflags.ifHost ? Color.black : Color.blue;

		UpdateTimerText();
	}

	private void StartTimer()
	{
		_timeLeft = Mathf.Max(0f, battleDurationSeconds);
		_running = _timeLeft > 0f;
		if (_canvas) _canvas.enabled = true;
		UpdateTimerText();
	}

	private void HideTimer()
	{
		if (_canvas) _canvas.enabled = false;
	}

	private void UpdateTimerText()
	{
		if (_timerText == null) return;

		int secs = Mathf.CeilToInt(Mathf.Max(0f, _timeLeft));
		int minutes = secs / 60;
		int seconds = secs % 60;
		_timerText.text = string.Format("{0:00}:{1:00}", minutes, seconds);
	}

	/// <summary>
	/// Optionally override runtime font and size from BattleReadyManager.
	/// </summary>
	public void SetStyle(TMP_FontAsset font, int size)
	{
		if (font != null) timerFont = font;
		if (size > 0) timerFontSize = size;
		if (_timerText != null)
		{
			if (timerFont != null) _timerText.font = timerFont;
			_timerText.fontSize = Mathf.Max(10, timerFontSize);
		}
	}

	private void EvaluateWinnerByHpAndFinish()
	{
		try
		{
			int hostHp = 0;
			int clientHp = 0;
			var units = UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
			for (int i = 0; i < units.Length; i++)
			{
				var u = units[i];
				if (u == null) continue;
				bool isPassive;
				try { isPassive = u.isPassive; } catch { isPassive = false; }
				if (isPassive) continue;
				int hp;
				try { hp = u.health; } catch { continue; }
				if (hp <= 0) continue;
				bool isHost;
				try { isHost = u.host; } catch { continue; }
				if (isHost) hostHp += hp; else clientHp += hp;
			}

			bool isDraw = (hostHp == clientHp);
			bool hostWins = hostHp > clientHp;
			bool iAmHost = Globalflags.ifHost;
			bool localWins = isDraw ? false : (iAmHost ? hostWins : !hostWins);

			var end = FindObjectOfType<BattleEndManager>();
			if (end != null)
			{
				end.FinishByTimeoutHp(localWins, isDraw);
			}
		}
		catch (Exception ex)
		{
			Debug.LogWarning($"[BattleTimerManager] evaluate winner failed: {ex.Message}");
		}
	}
}


