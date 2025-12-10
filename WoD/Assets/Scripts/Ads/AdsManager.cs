using System;
using System.Collections.Generic;
using GoogleMobileAds.Api;
using UnityEngine;

public class AdsManager : MonoBehaviour
{
	public static AdsManager Instance { get; private set; }

#if UNITY_ANDROID
	[SerializeField] private string interstitialAdUnitId = "ca-app-pub-2638490693624676/7356227323";
#elif UNITY_IOS
	[SerializeField] private string interstitialAdUnitId = "";
#else
	[SerializeField] private string interstitialAdUnitId = "";
#endif
	[SerializeField] private float waitForReadyTimeoutSec = 5f;
	[SerializeField] private float postFocusDelaySec = 0.6f;

	private InterstitialAd interstitialAd;
	private readonly Queue<Action> mainThreadActions = new Queue<Action>();
	private bool interstitialReady;
	private bool interstitialShowing;
	private bool deferredShowPrimed;
	public bool HasDeferredInterstitial => deferredShowPrimed;
	private System.Action pendingCallback;

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
			LoadInterstitial();
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


