using System;
using System.Collections;
using System.Threading.Tasks;
using Firebase.Auth;
using Firebase.Database;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Ждёт готовности обоих игроков перед началом боя.
/// Локальный игрок помечается "готов" после завершения расстановки (или сразу, если пропустил).
/// Если второй игрок не готов в течение timeoutSeconds, победа присуждается готовому игроку,
/// инкрементируется счётчик побед и сессия закрывается.
/// </summary>
public class BattleReadyManager : MonoBehaviour
{
	private static BattleReadyManager s_instance;
	public static bool BothReady { get; private set; }
	public static bool Active { get; private set; }
	private static bool s_localReadyRequested; // выставляется PlacementManager-ом при Skip/Finish
	public static void SignalLocalReady()
	{
		EnsureInstance();
		s_localReadyRequested = true;
		Debug.Log("[BRM] SignalLocalReady() received");
	}

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void ResetState()
	{
		// Жёсткий сброс перед загрузкой любой сцены
		BothReady = false;
		Active = false;
		s_localReadyRequested = false;
	}

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void AutoBootstrap()
	{
		// Создаём менеджер только в боевой сцене
		if (IsBattleContext())
			EnsureInstance();
	}

	private static void EnsureInstance()
	{
		if (!IsBattleContext()) return;
		if (s_instance && s_instance.isActiveAndEnabled) return;
		// Попробуем найти в сцене
#if UNITY_6000_0_OR_NEWER || UNITY_2023_1_OR_NEWER
		var existing = UnityEngine.Object.FindFirstObjectByType<BattleReadyManager>();
#else
		var existing = UnityEngine.Object.FindObjectOfType<BattleReadyManager>();
#endif
		if (existing)
		{
			s_instance = existing;
			return;
		}
		// Создадим новый GameObject с компонентом, если его не было
		var go = new GameObject("BattleReadyManager(Auto)");
		s_instance = go.AddComponent<BattleReadyManager>();
	}

	private static bool IsBattleContext()
	{
		try
		{
			var sceneName = SceneManager.GetActiveScene().name ?? "";
			if (sceneName.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
#if UNITY_6000_0_OR_NEWER || UNITY_2023_1_OR_NEWER
			var spawner = UnityEngine.Object.FindFirstObjectByType<ArmySpawner>();
#else
			var spawner = UnityEngine.Object.FindObjectOfType<ArmySpawner>();
#endif
			return spawner != null;
		}
		catch { return false; }
	}

	[SerializeField] private float timeoutSeconds = 30f;

	[Header("Timer UI Style")]
	[SerializeField] private TMP_FontAsset timerFont;
	[SerializeField] private Color        timerColor = Color.white;
	[SerializeField] private int          timerFontSize = 48;

	[Header("Role toast")]
	[SerializeField] private float        roleToastSeconds = 2.0f;
	[SerializeField] private TMP_FontAsset roleFont;
	[SerializeField] private Color        roleColor = Color.yellow;
	[SerializeField] private int          roleFontSize = 36;
	private TextMeshProUGUI roleToast;

	private DatabaseReference myReadyRef;
	private DatabaseReference enemyReadyRef;
	private DatabaseReference enemyBranchRef;

	private bool isHost;
	private string sessionId;
	private string role => isHost ? "HOST" : "CLIENT";

	private bool myReadyPosted;
	private bool enemyReady;
	private bool enemyReadyKnown;

	private float countdown;
	private const float SMALL_EPS = 0.001f;
	private long enemyReadyAtMs; // серверное время, когда соперник стал готов
	private bool waitingForEnemyAnnounced;
	private bool waitingForMeAnnounced;

	// UI overlay
	private Canvas canvas;
	private TextMeshProUGUI centerText;

	private FirebaseAuth auth;

	private void Awake()
	{
		s_instance = this;
		Active = true;
		Debug.Log("[BRM] Awake()");
		auth = FirebaseAuth.DefaultInstance;
		GameSession.Load();
		sessionId = GameSession.SessionId;
		isHost = Globalflags.ifHost;
		Debug.Log($"[BRM] Session='{sessionId}', role={role}, timeout={timeoutSeconds}s");
		BindRefs();
		BuildOverlay();
		ShowRoleToastOnce();
		BothReady = false; // сброс на всякий случай
		countdown = timeoutSeconds;
	}

	private void BindRefs()
	{
		if (string.IsNullOrEmpty(sessionId)) return;
		string myBranch = isHost ? "hostArmy" : "clientArmy";
		string enemyBranch = isHost ? "clientArmy" : "hostArmy";
		var baseRef = FirebaseDatabase.DefaultInstance.RootReference
			.Child("sessions").Child(sessionId);
		myReadyRef = baseRef.Child(myBranch).Child("battleReady");
		enemyBranchRef = baseRef.Child(enemyBranch);
		enemyReadyRef = enemyBranchRef.Child("battleReady");
		enemyReadyRef.ValueChanged += OnEnemyReadyChanged;
		Debug.Log($"[BRM] BindRefs() myReadyRef=/sessions/{sessionId}/{myBranch}/battleReady, enemyReadyRef=/sessions/{sessionId}/{enemyBranch}/battleReady");
	}

	private void OnDestroy()
	{
		if (enemyReadyRef != null) enemyReadyRef.ValueChanged -= OnEnemyReadyChanged;
		Debug.Log("[BRM] OnDestroy(): listeners detached");
		Active = false;
	}

	private void BuildOverlay()
	{
		// Создаём некликабельный оверлей с таймером по центру
		var go = new GameObject("ReadyOverlay");
		canvas = go.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		go.AddComponent<GraphicRaycaster>();

		var textGo = new GameObject("CenterTimer");
		textGo.transform.SetParent(go.transform, false);
		centerText = textGo.AddComponent<TextMeshProUGUI>();
		centerText.alignment = TextAlignmentOptions.Center;
		centerText.fontSize = Mathf.Max(10, timerFontSize);
		centerText.color = timerColor;
		if (timerFont != null) centerText.font = timerFont;
		centerText.raycastTarget = false;
		var rt = (RectTransform)centerText.transform;
		rt.anchorMin = new Vector2(0.25f, 0.45f);
		rt.anchorMax = new Vector2(0.75f, 0.55f);
		rt.offsetMin = Vector2.zero;
		rt.offsetMax = Vector2.zero;

		UpdateCenterText();
		Debug.Log("[BRM] Overlay created");
	}

	private void ShowRoleToastOnce()
	{
		try
		{
			if (!canvas) return;
			var go = new GameObject("RoleToast");
			go.transform.SetParent(canvas.transform, false);
			roleToast = go.AddComponent<TextMeshProUGUI>();
			roleToast.alignment = TextAlignmentOptions.Center;
			roleToast.fontSize = Mathf.Max(10, roleFontSize);
			// Цвет по роли: HOST -> чёрный, CLIENT -> синий
			roleToast.color = Globalflags.ifHost ? Color.black : Color.blue;
			// Шрифт: берём из Timer Font, если Role Font не задан
			var effectiveFont = roleFont != null ? roleFont : timerFont;
			if (effectiveFont != null) roleToast.font = effectiveFont;
			roleToast.raycastTarget = false;
			var rt = (RectTransform)roleToast.transform;
			rt.anchorMin = new Vector2(0.25f, 0.82f);
			rt.anchorMax = new Vector2(0.75f, 0.92f);
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;

			string text = Globalflags.ifHost ? "You are Black" : "You are Blue";
			roleToast.text = text;
			StartCoroutine(HideRoleToastAfter(roleToastSeconds));
		}
		catch { /* best-effort */ }
	}

	private IEnumerator HideRoleToastAfter(float seconds)
	{
		yield return new WaitForSeconds(Mathf.Max(0.1f, seconds));
		if (roleToast) roleToast.enabled = false;
	}

	private void Update()
	{
		// 1) Постим свою готовность ТОЛЬКО по явному сигналу (Skip или завершена расстановка)
		if (!myReadyPosted && s_localReadyRequested)
		{
			myReadyPosted = true;
			Debug.Log("[BRM] Local is READY → posting to RTDB");
			_ = PostMyReadyAsync();
		}

		// 2) Если оба готовы — убираем оверлей и разрешаем бой
		if (myReadyPosted && enemyReady && !BothReady)
		{
			// Перед стартом боя один раз насильно синхронизируем позиции из RTDB,
			// чтобы учесть последнюю расстановку у обоих игроков.
			_ = ResyncAllUnitsPositionsAsync();

			BothReady = true;
			// Создаём (или активируем) таймер боя
#if UNITY_6000_0_OR_NEWER || UNITY_2023_1_OR_NEWER
			var timer = UnityEngine.Object.FindFirstObjectByType<BattleTimerManager>();
#else
			var timer = UnityEngine.Object.FindObjectOfType<BattleTimerManager>();
#endif
			if (timer == null)
			{
				var goT = new GameObject("BattleTimerManager(Auto)");
				timer = goT.AddComponent<BattleTimerManager>();
			}
			// Прокинем стиль из этого инспектора, если задан
			try { timer.SetStyle(timerFont, timerFontSize); } catch { /* best-effort */ }

			if (canvas) canvas.enabled = false;
			Debug.Log("[BRM] Both players ready → battle starts (positions resync requested)");
			return;
		}

		// 3A) Я ГОТОВ, соперник НЕТ — я жду соперника, у меня идёт таймер ожидания
		if (myReadyPosted && !enemyReady && !BothReady)
		{
			if (!waitingForEnemyAnnounced)
			{
				waitingForEnemyAnnounced = true;
				waitingForMeAnnounced = false;
				Debug.Log("[BRM] Waiting for ENEMY to be ready… countdown running");
			}
			countdown = Mathf.Max(0f, countdown - Time.deltaTime);
			UpdateCenterText();

			if (countdown <= 0f)
			{
				// Соперник не готов — присуждаем победу локальному игроку и закрываем сессию
				Debug.Log("[BRM] Timeout reached while waiting for enemy. AwardLocalWinAndCloseAsync()");
				_ = AwardLocalWinAndCloseAsync();
			}
		}
		// 3B) СОПЕРНИК ГОТОВ, я НЕТ — у меня показывается таймер «осталось N секунд»
		else if (!myReadyPosted && enemyReady && !BothReady)
		{
			if (!waitingForMeAnnounced)
			{
				waitingForMeAnnounced = true;
				waitingForEnemyAnnounced = false;
				Debug.Log("[BRM] Enemy is ready. Waiting for ME to finish placement… countdown running");
			}
			countdown = Mathf.Max(0f, countdown - Time.deltaTime);
			UpdateCenterText();

			if (countdown <= SMALL_EPS)
			{
				// Я не успел — закрываем сессию, победа у соперника
				Debug.Log("[BRM] Timeout reached while I am not ready. CloseAsTimeoutLoserAsync()");
				_ = CloseAsTimeoutLoserAsync();
			}
		}
		else
		{
			UpdateCenterText();
		}
	}

	/// <summary>
	/// Один раз при старте боя принудительно применяем координаты из RTDB state/x,y для всех юнитов.
	/// Это страхует случаи, когда один клиент не успел обработать события ValueChanged во время расстановки.
	/// </summary>
	private async Task ResyncAllUnitsPositionsAsync()
	{
		try
		{
			if (string.IsNullOrEmpty(sessionId)) return;
			var baseRef = FirebaseDatabase.DefaultInstance.RootReference.Child("sessions").Child(sessionId);
			var hostTask = baseRef.Child("hostArmy").GetValueAsync();
			var clientTask = baseRef.Child("clientArmy").GetValueAsync();
			await System.Threading.Tasks.Task.WhenAll(hostTask, clientTask);

			ApplyArmySnapshot(hostTask.Result, isHostBranch: true);
			ApplyArmySnapshot(clientTask.Result, isHostBranch: false);
			Debug.Log("[BRM] ResyncAllUnitsPositionsAsync: applied");
		}
		catch (Exception ex)
		{
			Debug.LogWarning($"[BRM] ResyncAllUnitsPositionsAsync failed: {ex.Message}");
		}
	}

	private void ApplyArmySnapshot(DataSnapshot armySnap, bool isHostBranch)
	{
		if (armySnap == null || !armySnap.Exists) return;
#if UNITY_2022_2_OR_NEWER
		var units = UnityEngine.Object.FindObjectsByType<Unit>(UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None);
#else
		var units = UnityEngine.Object.FindObjectsOfType<Unit>(true);
#endif
		foreach (var child in armySnap.Children)
		{
			if (!child.HasChild("state")) continue;
			string key = child.Key;
			var state = child.Child("state");
			double x = ToDouble(state.Child("x").Value, double.NaN);
			double y = ToDouble(state.Child("y").Value, double.NaN);
			int facing = ToInt(state.Child("facing").Value, 1);
			if (double.IsNaN(x) || double.IsNaN(y)) continue;

			// найдём локальный объект по (host,key)
			for (int i = 0; i < units.Length; i++)
			{
				var u = units[i];
				if (!u) continue;
				if (u.host != isHostBranch) continue;
				if (!string.Equals(u.unitKey, key, StringComparison.Ordinal)) continue;

				try
				{
					u.transform.position = new UnityEngine.Vector3((float)x, (float)y, u.transform.position.z);
					// Применим facing, если задан
					float lookDir = facing >= 0 ? +1f : -1f;
					u.FaceTowardsX(u.transform.position.x + lookDir);
				}
				catch { /* ignore */ }
				break;
			}
		}
	}

	private static int ToInt(object v, int def)
	{
		try { return v == null ? def : Convert.ToInt32(v); } catch { return def; }
	}
	private static double ToDouble(object v, double def)
	{
		try { return v == null ? def : Convert.ToDouble(v); } catch { return def; }
	}

	private async Task PostMyReadyAsync()
	{
		if (myReadyRef == null) return;
		try
		{
			Debug.Log("[BRM] RTDB: Set my battleReady=true");
			await myReadyRef.SetValueAsync(true);
			Debug.Log("[BRM] RTDB: Set my readyAt=ServerValue.Timestamp");
			await myReadyRef.Parent.Child("readyAt").SetValueAsync(ServerValue.Timestamp);
		}
		catch (Exception ex) { Debug.LogWarning($"PostMyReady failed: {ex.Message}"); }
	}

	private void OnEnemyReadyChanged(object sender, ValueChangedEventArgs e)
	{
		if (!e.Snapshot.Exists) { enemyReady = false; UpdateCenterText(); return; }
		try
		{
			enemyReady = e.Snapshot.Value is bool b && b;
			enemyReadyKnown = true;
			Debug.Log($"[BRM] enemyReady changed → {enemyReady}");

			// Считаем старт таймера по серверному времени readyAt на ветке соперника
			if (enemyReady && enemyBranchRef != null)
			{
				_ = enemyBranchRef.Child("readyAt").GetValueAsync().ContinueWith(t =>
				{
					try
					{
						if (t.IsCompleted && t.Result != null && t.Result.Exists && t.Result.Value != null)
						{
							long ms = Convert.ToInt64(t.Result.Value);
							enemyReadyAtMs = ms;
							// Пересчитаем исходное значение обратного отсчёта так, чтобы оно было одинаковым на обоих клиентах
							// Здесь используем локальное время с момента прихода ready, дальше таймер идёт локально.
							countdown = Mathf.Max(0f, timeoutSeconds);
							Debug.Log($"[BRM] enemy readyAt(ms) = {enemyReadyAtMs}, countdown set to {countdown}s");
						}
					}
					catch { /* ignore */ }
				});
			}
		}
		catch { enemyReady = false; }
		UpdateCenterText();
	}

	private async Task AwardLocalWinAndCloseAsync()
	{
		// Ещё раз перепроверим, что соперник не стал READY в последний момент
		try
		{
			if (enemyReadyKnown && enemyReady)
			{
				Debug.Log("[BRM] Abort timeout: enemy became ready (cached)");
				return;
			}
			if (enemyReadyRef != null)
			{
				var snap = await enemyReadyRef.GetValueAsync();
				bool rtReady = (snap != null && snap.Exists && snap.Value is bool b && b);
				if (rtReady)
				{
					Debug.Log("[BRM] Abort timeout: enemy is ready per RTDB");
					return;
				}
			}
		}
		catch { /* ignore: best-effort */ }

		// Чтобы не вызывалось повторно
		if (BothReady) return;
		BothReady = true;

		// +1 победа локальному игроку
		try
		{
			var user = auth?.CurrentUser;
			if (user != null)
			{
				Debug.Log("[BRM] Incrementing local wins");
				var winsRef = FirebaseDatabase.DefaultInstance.RootReference
					.Child("users").Child(user.UserId).Child("wins");
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
		}
		catch (Exception ex) { Debug.LogWarning($"wins increment failed: {ex.Message}"); }

		// Закрываем/удаляем сессию и уходим в меню
		try
		{
			Debug.Log("[BRM] Closing session as winner");
			if (FirebaseSessionManager.Instance != null)
				await FirebaseSessionManager.Instance.LeaveSessionAndGoToMenuAsync();
		}
		catch (Exception ex) { Debug.LogWarning($"close session failed: {ex.Message}"); }
	}

	private async Task CloseAsTimeoutLoserAsync()
	{
		if (BothReady) return;
		BothReady = true;
		try
		{
			Debug.Log("[BRM] Leaving session as timeout loser");
			if (FirebaseSessionManager.Instance != null)
				await FirebaseSessionManager.Instance.LeaveSessionAndGoToMenuAsync();
		}
		catch (Exception ex) { Debug.LogWarning($"leave session failed: {ex.Message}"); }
	}

	private void UpdateCenterText()
	{
		if (!centerText) return;
		if (BothReady)
		{
			centerText.text = "";
			return;
		}

		if (!myReadyPosted && enemyReady)
		{
			int secs = Mathf.CeilToInt(Mathf.Max(0f, countdown));
			centerText.text = $"You have {secs}s to finish deployment";
			return;
		}

		if (enemyReady)
		{
			centerText.text = "Both ready. Battle starts…";
		}
		else
		{
			int secs = Mathf.CeilToInt(countdown);
			centerText.text = $"Waiting for opponent: {secs}s";
		}
	}
}


