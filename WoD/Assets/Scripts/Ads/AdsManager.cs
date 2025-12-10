using System;
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

	private InterstitialAd interstitialAd;

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
		MobileAds.Initialize(_ =>
		{
			LoadInterstitial();
		});
	}

	private AdRequest CreateRequest()
	{
		return new AdRequest();
	}

	private void LoadInterstitial()
	{
		if (string.IsNullOrEmpty(interstitialAdUnitId)) return;
		InterstitialAd.Load(interstitialAdUnitId, CreateRequest(), (ad, error) =>
		{
			if (error != null || ad == null)
			{
				interstitialAd = null;
				return;
			}
			interstitialAd = ad;
		});
	}

	public bool ShowInterstitial(Action onClosed)
	{
		if (interstitialAd == null)
		{
			onClosed?.Invoke();
			LoadInterstitial();
			return false;
		}

		interstitialAd.OnAdFullScreenContentClosed += () =>
		{
			try { interstitialAd?.Destroy(); } catch { }
			interstitialAd = null;
			LoadInterstitial();
			onClosed?.Invoke();
		};
		interstitialAd.OnAdFullScreenContentFailed += _ =>
		{
			interstitialAd = null;
			LoadInterstitial();
			onClosed?.Invoke();
		};

		interstitialAd.Show();
		return true;
	}
}


