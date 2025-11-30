using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Firebase.Auth;
using Firebase.Database;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Detects when one side has no units left, shows result panel, and updates stats.
/// Attach this to any object in the battle scene. Assign UI refs in Inspector.
/// </summary>
public class BattleEndManager : MonoBehaviour
{
	[Header("UI")]
	[SerializeField] private GameObject resultPanel;     // Panel to show/hide
	[SerializeField] private TMP_Text   resultText;      // "Ты победил" / "Ты проиграл"
	[SerializeField] private Button     toMenuButton;    // Button to go to main menu

	[Header("Config")] 
	[SerializeField] private float checkIntervalSeconds = 0.25f;
	[SerializeField] private string sessionsPath = "sessions"; // RTDB path for sessions/armies

	private float nextCheckTime;
	private bool finished;
	private bool hadAnyUnits; // станет true, когда хотя бы один юнит появится в сцене

	private FirebaseAuth auth;

	private void Awake()
	{
		auth = FirebaseAuth.DefaultInstance;
		if (resultPanel) resultPanel.SetActive(false);
		if (toMenuButton != null)
		{
			toMenuButton.onClick.RemoveAllListeners();
			toMenuButton.onClick.AddListener(OnGoToMenuClicked);
		}
	}

	private void Update()
	{
		if (finished) return;
		if (Time.time < nextCheckTime) return;
		nextCheckTime = Time.time + checkIntervalSeconds;

		EvaluateBattleState();
	}

	private void EvaluateBattleState()
	{
		// Count alive units by host flag (exclude passive obstacles)
		int hostAlive = 0;
		int clientAlive = 0;
		var allUnits = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
		foreach (var u in allUnits)
		{
			try
		{
			if (u == null) continue;
				// препятствия не учитываются в условиях победы/поражения
				bool isPassive;
				try { isPassive = u.isPassive; } catch { isPassive = false; }
				if (isPassive) continue;
				// может быть уничтожен между проверкой и чтением свойства — ловим и игнорируем
				int hp;
				try { hp = u.health; } catch { continue; }
				if (hp > 0)
			{
					bool isHost;
					try { isHost = u.host; } catch { continue; }
					if (isHost) hostAlive++; else clientAlive++;
			}
			}
			catch { }
		}

		// Запомним, что юниты в принципе были в сцене (чтобы не показывать ничью при старте)
		if (!hadAnyUnits && (hostAlive > 0 || clientAlive > 0)) hadAnyUnits = true;

		// Пока обе стороны живы — продолжаем бой
		if (hostAlive > 0 && clientAlive > 0) return;

		// Если обе стороны 0, но юнитов ещё не было — это старт, игнорируем
		if (!hadAnyUnits && hostAlive == 0 && clientAlive == 0) return;

		bool iAmHost = Globalflags.ifHost;
		bool localWins;
		bool isDraw = (hostAlive == 0 && clientAlive == 0);

		if (isDraw)
		{
			localWins = false; // draw — не плюсуем победу никому
			ShowResult("Ничья");
			finished = true;
			return;
		}

		// One side has 0
		bool hostWins = (hostAlive > 0 && clientAlive == 0);
		localWins = iAmHost ? hostWins : !hostWins;
		ShowResult(localWins ? "Ты победил" : "Ты проиграл");
		_ = TryUpdateWins(localWins);
		finished = true;
	}

	private void ShowResult(string text)
	{
		if (resultText) resultText.text = text;
		if (resultPanel) resultPanel.SetActive(true);
	}

	private async Task TryUpdateWins(bool won)
	{
		if (!won) return; // проигравшему ничего не пишем
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
		catch (Exception ex)
		{
			Debug.LogWarning($"[BattleEndManager] wins update failed: {ex.Message}");
		}
	}

	private void OnGoToMenuClicked()
	{
		// Используем имеющийся менеджер для корректного выхода из сессии
		if (FirebaseSessionManager.Instance != null)
		{
			_ = FirebaseSessionManager.Instance.LeaveSessionAndGoToMenuAsync();
		}
		else
		{
			// Fallback — просто загрузим сцену MainMenu, если менеджера нет
			SceneManager.LoadScene("MainMenu");
		}
	}
}


