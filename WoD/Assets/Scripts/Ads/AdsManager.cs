using System;
using System.Collections.Generic;
using GoogleMobileAds.Api;
using UnityEngine;

public class AdsManager : MonoBehaviour
{
	public static AdsManager Instance { get; private set; }

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void Bootstrap()
	{
		// Ensure AdsManager exists even if the first loaded scene doesn't contain it (e.g., MP entry)
		if (Instance != null) return;
		var go = new GameObject(nameof(AdsManager));
		go.AddComponent<AdsManager>();
	}

#if UNITY_ANDROID
	[SerializeField] private string interstitialAdUnitId = "ca-app-pub-2638490693624676/7356227323";
#elif UNITY_IOS
	[SerializeField] private string interstitialAdUnitId = "";
#else
	[SerializeField] private string interstitialAdUnitId = "";
#endif
	[SerializeField] private float waitForReadyTimeoutSec = 5f;
	[SerializeField] private float postFocusDelaySec = 0.6f;
	[SerializeField] private float resumeReloadDelaySec = 0.8f;

	private InterstitialAd interstitialAd;
	private readonly Queue<Action> mainThreadActions = new Queue<Action>();
	private bool interstitialReady;
	private bool interstitialShowing;
	private bool deferredShowPrimed;
	public bool HasDeferredInterstitial => deferredShowPrimed;
	private System.Action pendingCallback;
	// New flow controls
	private bool hasMainMenuLoadedOnce;
	private bool returnToMenuFromBattle;

	private void Awake()
	{
		if (Instance != null)
		{
			Destroy(gameObject);
			return;
		}
		Instance = this;
		DontDestroyOnLoad(gameObject);
	}

	private void Start()
	{
		// Гарантируем, что события SDK приходят в главном потоке Unity
		MobileAds.RaiseAdEventsOnUnityMainThread = true;
		MobileAds.Initialize(_ =>
		{
			Debug.Log("[AdsManager] MobileAds initialized");
			// Больше не загружаем креатив автоматически на старте приложения.
			// Загрузка выполняется при входе в Main Menu (не впервые).
		});
	}

	private void Update()
	{
		// Execute queued actions on main thread (e.g., after ad closes)
		if (mainThreadActions.Count == 0) return;
		lock (mainThreadActions)
		{
			while (mainThreadActions.Count > 0)
			{
				var a = mainThreadActions.Dequeue();
				try { a?.Invoke(); } catch (Exception ex) { Debug.LogWarning("[AdsManager] main-thread action error: " + ex.Message); }
			}
		}

		// If we previously got APP_NOT_FOREGROUND, try showing once app is focused
		if (deferredShowPrimed && Application.isFocused && !interstitialShowing && interstitialAd != null && interstitialAd.CanShowAd())
		{
			deferredShowPrimed = false;
			StartCoroutine(ShowAfterFocusDelay(pendingCallback));
		}
	}

	private void RunOnMainThread(Action action)
	{
		if (action == null) return;
		lock (mainThreadActions)
		{
			mainThreadActions.Enqueue(action);
		}
	}

	/// <summary>
	/// Пометить, что мы выходим в меню из сцены боя (SP/MP).
	/// Реклама будет показана на загрузке Main Menu (если это не первый вход).
	/// </summary>
	public void FlagReturnToMainMenuFromBattle()
	{
		returnToMenuFromBattle = true;
	}

	/// <summary>
	/// Вызвать при загрузке сцены Main Menu.
	///  - Первый вход после запуска приложения — пропускаем рекламу.
	///  - Любые последующие входы — загружаем креатив.
	///  - Если отмечен переход из боя — пытаемся показать (не блокируя меню).
	/// </summary>
	public void OnMainMenuLoaded()
	{
		bool isFirstLoad = !hasMainMenuLoadedOnce;
		hasMainMenuLoadedOnce = true;

		// Начиная со второго входа в Main Menu — запрашиваем загрузку интерстициала
		if (!isFirstLoad && interstitialAd == null)
		{
			LoadInterstitial();
		}

		// Показываем только если пришли из боя, и это не первый вход в меню
		if (!isFirstLoad && returnToMenuFromBattle)
		{
			// Сбрасываем флаг сразу, чтобы избежать повторов при быстрых пересценах
			returnToMenuFromBattle = false;
			StartCoroutine(ShowOnMenuIfReadyRoutine());
		}
	}

	private System.Collections.IEnumerator ShowOnMenuIfReadyRoutine()
	{
		// Неблокирующая попытка показа: ждём недолго и показываем, если готово
		float t = 0f;
		float timeout = Mathf.Clamp(waitForReadyTimeoutSec, 0.2f, 3.5f);
		while (t < timeout)
		{
			if (Application.isFocused && interstitialAd != null && interstitialAd.CanShowAd())
			{
				ShowNow(null);
				yield break;
			}
			t += Time.unscaledDeltaTime;
			yield return null;
		}
		// Если не успели — оставим креатив загруженным для следующего раза (без отложенного показа)
	}

	/// <summary>
	/// Если ранее показ был отклонён из‑за отсутствия фокуса — попробовать показать ещё раз.
	/// Используется, например, при входе в MainMenu.
	/// </summary>
	public void TryShowDeferredInterstitial()
	{
		if (!deferredShowPrimed) return;
		StartCoroutine(TryShowDelayedRoutine(pendingCallback));
	}

	private System.Collections.IEnumerator TryShowDelayedRoutine(System.Action onClosed)
	{
		float t = 0f;
		float timeout = Mathf.Max(0.5f, waitForReadyTimeoutSec) + Mathf.Max(0.1f, postFocusDelaySec);
		while (t < timeout)
		{
			if (Application.isFocused && interstitialAd != null && interstitialAd.CanShowAd())
			{
				deferredShowPrimed = false;
				yield return ShowAfterFocusDelay(onClosed);
				yield break;
			}
			t += Time.unscaledDeltaTime;
			yield return null;
		}
		deferredShowPrimed = false;
		// не дождались — продолжаем без показа
		RunOnMainThread(onClosed);
	}

	private System.Collections.IEnumerator ShowAfterFocusDelay(System.Action onClosed)
	{
		// небольшая пауза после возврата фокуса/смены сцены, чтобы Activity полностью активировалась
		float t = 0f;
		while (t < Mathf.Max(0.1f, postFocusDelaySec))
		{
			t += Time.unscaledDeltaTime;
			yield return null;
		}
		// для отложенного показа в меню колбэк уже отработал — показываем без него
		ShowNow(null);
	}

	private void OnApplicationFocus(bool hasFocus)
	{
		if (hasFocus && deferredShowPrimed && pendingCallback != null)
		{
			StartCoroutine(TryShowDelayedRoutine(pendingCallback));
		}
	}

	private void OnApplicationPause(bool paused)
	{
		if (paused)
		{
			// Уходим в фон: текущий interstitial может стать невалидным — сбросим и перезагрузим после резюма
			Debug.Log("[AdsManager] OnApplicationPause(true) — drop current interstitial");
			interstitialShowing = false;
			deferredShowPrimed = false;
			pendingCallback = null;
			try { interstitialAd?.Destroy(); } catch { }
			interstitialAd = null;
			interstitialReady = false;
		}
		else
		{
			// Возврат из фона: через небольшую паузу перезагрузим креатив
			Debug.Log("[AdsManager] OnApplicationPause(false) — schedule reload");
			StartCoroutine(ReloadAfterResumeRoutine());
		}
	}

	private System.Collections.IEnumerator ReloadAfterResumeRoutine()
	{
		// Дождёмся фокуса и небольшую паузу, чтобы Activity стабильно вернулась
		float t = 0f;
		float delay = Mathf.Max(0.2f, resumeReloadDelaySec);
		while (!Application.isFocused || t < delay)
		{
			t += Time.unscaledDeltaTime;
			yield return null;
		}
		LoadInterstitial();
		// Если ранее ставили отложенный показ — попробуем аккуратно показать (без колбэка навигации)
		if (deferredShowPrimed)
		{
			StartCoroutine(TryShowDelayedRoutine(null));
		}
	}

	private AdRequest CreateRequest()
	{
		return new AdRequest();
	}

	private void LoadInterstitial()
	{
		if (string.IsNullOrEmpty(interstitialAdUnitId)) return;
		Debug.Log("[AdsManager] Loading interstitial...");
		InterstitialAd.Load(interstitialAdUnitId, CreateRequest(), (ad, error) =>
		{
			if (error != null || ad == null)
			{
				Debug.LogWarning("[AdsManager] Interstitial load failed: " + error);
				interstitialAd = null;
				interstitialReady = false;
				return;
			}
			interstitialAd = ad;
			interstitialReady = interstitialAd.CanShowAd();
			Debug.Log("[AdsManager] Interstitial loaded");
		});
	}

	public bool ShowInterstitial(Action onClosed)
	{
		// сохранить колбэк, чтобы можно было вызвать после отложенного показа
		pendingCallback = onClosed;

		if (interstitialShowing) { return true; }

		// Если не готов — подождём немного загрузку и фокус
		if (!Application.isFocused || interstitialAd == null || !interstitialAd.CanShowAd() || !interstitialReady)
		{
			StartCoroutine(WaitAndShow(pendingCallback));
			return true;
		}

		ShowNow(pendingCallback);
		return true;
	}

	private System.Collections.IEnumerator WaitAndShow(Action onClosed)
	{
		float timeout = Mathf.Max(0.5f, waitForReadyTimeoutSec);
		float t = 0f;
		while (t < timeout)
		{
			if (Application.isFocused && interstitialAd != null && interstitialAd.CanShowAd())
			{
				ShowNow(onClosed);
				yield break;
			}
			t += Time.unscaledDeltaTime;
			yield return null;
		}
		// Не дождались — продолжаем без рекламы, но сразу перезагрузим для следующего раза
		RunOnMainThread(onClosed);
		// Пометим отложенный показ: как только креатив догрузится и будет фокус — покажем в MainMenu без колбэка
		deferredShowPrimed = true;
		// Колбэк уже отработал — сбросим, чтобы не вызывался повторно
		pendingCallback = null;
		if (interstitialAd == null) LoadInterstitial();
	}

	private void ShowNow(Action onClosed)
	{
		if (interstitialAd == null || !interstitialAd.CanShowAd())
		{
			RunOnMainThread(onClosed);
			LoadInterstitial();
			return;
		}

		interstitialShowing = true;

		interstitialAd.OnAdFullScreenContentOpened += () =>
		{
			Debug.Log("[AdsManager] Interstitial opened");
		};
		interstitialAd.OnAdFullScreenContentClosed += () =>
		{
			try { interstitialAd?.Destroy(); } catch { }
			interstitialAd = null;
			interstitialReady = false;
			interstitialShowing = false;
			LoadInterstitial();
			// завершить сценарий после закрытия рекламы
			RunOnMainThread(() => { StartCoroutine(InvokeNextFrame(pendingCallback)); pendingCallback = null; });
		};
		interstitialAd.OnAdFullScreenContentFailed += (adError) =>
		{
			try
			{
				Debug.LogWarning($"[AdsManager] Interstitial show failed: code={adError?.GetCode()} domain={adError?.GetDomain()} msg={adError?.GetMessage()}");
				// If app-not-in-foreground, defer showing to when we regain focus (e.g., on next scene)
				string msg = adError?.GetMessage() ?? "";
				if (adError?.GetCode() == 3 || msg.IndexOf("not in foreground", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					// запланировать отложенный показ и НЕМЕДЛЕННО продолжить поток (уйти в меню)
					deferredShowPrimed = true;
					interstitialAd = null;
					interstitialReady = false;
					interstitialShowing = false;
					LoadInterstitial();
					var cb = pendingCallback;
					pendingCallback = null;
					if (cb != null) RunOnMainThread(() => { StartCoroutine(InvokeNextFrame(cb)); });
					// фактический показ случится позже (в меню), через TryShowDeferredInterstitial()
					return;
				}
			}
			catch { Debug.LogWarning("[AdsManager] Interstitial show failed"); }
			interstitialAd = null;
			interstitialReady = false;
			interstitialShowing = false;
			LoadInterstitial();
			// Для прочих ошибок продолжаем без показа
			var cb2 = pendingCallback;
			pendingCallback = null;
			if (cb2 != null) RunOnMainThread(() => { StartCoroutine(InvokeNextFrame(cb2)); });
		};

		// Показ со следующего кадра и только при фокусе
		StartCoroutine(ShowRoutine());
		Debug.Log("[AdsManager] Interstitial show requested");
	}

	private System.Collections.IEnumerator InvokeNextFrame(Action action)
	{
		yield return null;
		RunOnMainThread(action);
	}

	private System.Collections.IEnumerator ShowRoutine()
	{
		// дождаться завершения обработчика клика
		yield return null;
		// дождаться фокуса
		float t = 0f;
		while (!Application.isFocused && t < 1.5f)
		{
			t += Time.unscaledDeltaTime;
			yield return null;
		}
		// финальная проверка
		if (interstitialAd != null && interstitialAd.CanShowAd())
		{
			interstitialAd.Show();
		}
		else
		{
			Debug.Log("[AdsManager] Interstitial lost before show");
			interstitialShowing = false;
			LoadInterstitial();
		}
	}
}


