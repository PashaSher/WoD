using System.Threading.Tasks;
using Firebase.Auth;
using Firebase.Database;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Single-player battle end manager: detects when one side has no units left,
/// shows a simple result UI ("You Win"/"You Lose") and a button to return to Main Menu.
/// No RTDB usage.
/// </summary>
public class SPBattleEndManager : MonoBehaviour
{
	[Header("Debug")]
	[SerializeField] private bool verboseLogs = false;

	[Header("UI (optional, will be auto-created if left empty)")]
	[SerializeField] private GameObject resultPanel;
	[SerializeField] private TMP_Text   resultText;
	[SerializeField] private Button     toMenuButton;

	[Header("Config")]
	[SerializeField] private float checkIntervalSeconds = 0.25f;
	[SerializeField] private string mainMenuSceneName = "MainMenu";

	private float nextCheckTime;
	private bool finished;
	private bool leavingToMenu;
	private bool hadAnyUnits;
	private FirebaseAuth auth;

	private void Awake()
	{
		if (verboseLogs) Debug.Log("[SPBattleEnd] Awake()");
		try { auth = FirebaseAuth.DefaultInstance; } catch { auth = null; }
		EnsureResultUI();
		HidePanel();
		WireButton();
	}

	private void Update()
	{
		if (finished) return;
		if (Time.time < nextCheckTime) return;
		nextCheckTime = Time.time + Mathf.Max(0.05f, checkIntervalSeconds);
		EvaluateBattleState();
	}

	private void EvaluateBattleState()
	{
		int hostAlive = 0;
		int clientAlive = 0;
		var allUnits = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
		if (verboseLogs) Debug.Log($"[SPBattleEnd] Evaluate start. units={allUnits?.Length ?? 0}");
		for (int i = 0; i < allUnits.Length; i++)
		{
			var u = allUnits[i];
			if (!u) continue;
			try
			{
				if (u.isPassive) continue;
				if (u.health <= 0) continue;
				if (u.host) hostAlive++; else clientAlive++;
			}
			catch { }
		}

		if (!hadAnyUnits && (hostAlive > 0 || clientAlive > 0)) hadAnyUnits = true;
		if (verboseLogs) Debug.Log($"[SPBattleEnd] Count hostAlive={hostAlive} clientAlive={clientAlive} hadAnyUnits={hadAnyUnits}");

		// Both have units → continue
		if (hostAlive > 0 && clientAlive > 0) return;

		// If nobody spawned yet, ignore 0/0 at start
		if (!hadAnyUnits && hostAlive == 0 && clientAlive == 0) return;

		// Both sides dead → local lose (no draw flow)
		if (hostAlive == 0 && clientAlive == 0)
		{
			if (verboseLogs) Debug.Log("[SPBattleEnd] Both zero after units existed -> Lose");
			ShowResult("You Lose");
			finished = true;
			return;
		}

		// В SP мониторим только ЧЁРНЫХ (host) как игрока.
		bool localWins = (hostAlive > 0 && clientAlive == 0);
		if (verboseLogs) Debug.Log($"[SPBattleEnd] hostAlive={hostAlive} clientAlive={clientAlive} => localWins={localWins}");
		ShowResult(localWins ? "You Win" : "You Lose");
		_ = TryUpdateWins(localWins);
		finished = true;
	}

	private void ShowResult(string text)
	{
		Debug.Log($"[SPBattleEnd] ShowResult: {text}");
		if (resultText != null)
		{
			resultText.text = text;
			// В SP всегда играем за ЧЁРНЫХ
			resultText.color = Color.black;
		}
		if (resultPanel != null) resultPanel.SetActive(true);
	}

	private void HidePanel()
	{
		if (resultPanel != null) resultPanel.SetActive(false);
	}

	private async Task TryUpdateWins(bool won)
	{
		if (!won) return;
		try
		{
			var user = auth?.CurrentUser;
			if (user == null) return;

			var root = FirebaseDatabase.DefaultInstance.RootReference;
			var winsRef = root.Child("users").Child(user.UserId).Child("wins");

			await winsRef.RunTransaction(mutable =>
			{
				long cur = 0;
				try
				{
					if (mutable.Value is long l) cur = l;
					else if (mutable.Value is int i) cur = i;
					else if (mutable.Value is string s && long.TryParse(s, out var ls)) cur = ls;
				}
				catch { cur = 0; }
				mutable.Value = cur + 1;
				return TransactionResult.Success(mutable);
			});
		}
		catch (System.Exception ex)
		{
			Debug.LogWarning($"[SPBattleEnd] wins update failed: {ex.Message}");
		}
	}

	private void WireButton()
	{
		if (toMenuButton == null) return;
		toMenuButton.onClick.RemoveAllListeners();
		toMenuButton.onClick.AddListener(OnGoToMenuClicked);
		var label = toMenuButton.GetComponentInChildren<TMP_Text>(true);
		if (label != null) label.text = "Main Menu";
	}

	private void EnsureResultUI()
	{
		// If all references assigned in Inspector — respect them
		if (resultPanel != null && resultText != null && toMenuButton != null) return;

		// Create minimal Canvas overlay with text and button
		var canvasGo = new GameObject("SPResultCanvas");
		var canvas = canvasGo.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		canvas.pixelPerfect = true;
		canvas.overrideSorting = true;
		canvas.sortingOrder = 32767; // максимально поверх всего
		canvasGo.AddComponent<CanvasScaler>();
		canvasGo.AddComponent<GraphicRaycaster>();

		var panelGo = new GameObject("ResultPanel");
		panelGo.transform.SetParent(canvasGo.transform, false);
		var panelRt = panelGo.AddComponent<RectTransform>();
		panelRt.anchorMin = new Vector2(0, 0);
		panelRt.anchorMax = new Vector2(1, 1);
		panelRt.offsetMin = Vector2.zero;
		panelRt.offsetMax = Vector2.zero;
		var img = panelGo.AddComponent<Image>();
		img.color = new Color(0f, 0f, 0f, 0.25f);

		// Centered result text
		var textGo = new GameObject("ResultText");
		textGo.transform.SetParent(panelGo.transform, false);
		var textRt = textGo.AddComponent<RectTransform>();
		textRt.anchorMin = new Vector2(0.5f, 0.6f);
		textRt.anchorMax = new Vector2(0.5f, 0.6f);
		textRt.sizeDelta = new Vector2(600, 120);
		resultText = textGo.AddComponent<TextMeshProUGUI>();
		resultText.alignment = TextAlignmentOptions.Center;
		resultText.fontSize = 64;
		resultText.text = "";
		resultText.color = Color.white;

		// Button
		var btnGo = new GameObject("ToMainMenuButton");
		btnGo.transform.SetParent(panelGo.transform, false);
		var btnRt = btnGo.AddComponent<RectTransform>();
		btnRt.anchorMin = new Vector2(0.5f, 0.4f);
		btnRt.anchorMax = new Vector2(0.5f, 0.4f);
		btnRt.sizeDelta = new Vector2(260, 72);
		var btnImg = btnGo.AddComponent<Image>();
		btnImg.color = new Color(1f, 1f, 1f, 0.9f);
		toMenuButton = btnGo.AddComponent<Button>();

		var btnLabelGo = new GameObject("Label");
		btnLabelGo.transform.SetParent(btnGo.transform, false);
		var btnLabelRt = btnLabelGo.AddComponent<RectTransform>();
		btnLabelRt.anchorMin = new Vector2(0, 0);
		btnLabelRt.anchorMax = new Vector2(1, 1);
		btnLabelRt.offsetMin = Vector2.zero;
		btnLabelRt.offsetMax = Vector2.zero;
		var btnLabel = btnLabelGo.AddComponent<TextMeshProUGUI>();
		btnLabel.alignment = TextAlignmentOptions.Center;
		btnLabel.fontSize = 36;
		btnLabel.text = "Main Menu";
		btnLabel.color = Color.black;

		resultPanel = panelGo;
	}

	private void OnGoToMenuClicked()
	{
		if (leavingToMenu) return;
		leavingToMenu = true;
		if (toMenuButton != null) toMenuButton.interactable = false;

		void Proceed()
		{
			SceneManager.LoadScene(string.IsNullOrEmpty(mainMenuSceneName) ? "MainMenu" : mainMenuSceneName);
		}

		System.Collections.IEnumerator Fallback()
		{
			float t = 0f;
			const float timeout = 8f;
			while (t < timeout)
			{
				t += Time.unscaledDeltaTime;
				yield return null;
			}
			Proceed();
		}
		StartCoroutine(Fallback());

		if (AdsManager.Instance != null)
		{
			AdsManager.Instance.ShowInterstitial(Proceed);
		}
		else
		{
			Proceed();
		}
	}
}


