using System;
using System.Threading.Tasks;
using Firebase.Auth;
using Firebase.Database;
using UnityEngine;
using UnityEngine.SceneManagement;

public class DeleteAccountController : MonoBehaviour
{
	[SerializeField] private string registrationSceneName = "RegistrationWindow";
	[SerializeField] private bool preferPlayGamesOnAndroid = true;
	[SerializeField] private string googleWebClientId = ""; // OAuth 2.0 Web client ID (for Google Sign-In reauth)

	public void OnDeleteAccountButton()
	{
		_ = DeleteAccountFlowAsync();
	}

	private async Task DeleteAccountFlowAsync()
	{
		try
		{
			try { await FirebaseBootstrapper.EnsureInitializedAsync(); } catch { /* best-effort */ }

			var auth = FirebaseAuth.DefaultInstance;
			var user = auth != null ? auth.CurrentUser : null;
			var root = FirebaseDatabase.DefaultInstance.RootReference;

			// Попробуем заранее выполнить переавторизацию (если требуется "recent login", это повысит шанс успешного удаления)
			try
			{
				if (user != null)
				{
					Debug.Log("[DeleteAccount] Trying pre-reauth...");
					bool reauthed = await TryReauthenticateAsync(auth);
					Debug.Log("[DeleteAccount] Pre-reauth result: " + reauthed);
				}
			}
			catch (Exception preReauthEx)
			{
				Debug.LogWarning("[DeleteAccount] Pre-reauth failed: " + preReauthEx.Message);
			}

			// 1) Очистка данных в RTDB (best-effort)
			if (user != null)
			{
				try { await root.Child("users").Child(user.UserId).RemoveValueAsync(); } catch { /* ignore */ }
			}

			// 2) Удаление аккаунта в Auth (с переавторизацией при необходимости)
			if (user != null)
			{
				try
				{
					await user.DeleteAsync();
				}
				catch (Exception ex)
				{
					if (IsRequiresRecentLoginError(ex) && await TryReauthenticateAsync(auth))
					{
						await user.DeleteAsync();
					}
					else
					{
						throw;
					}
				}
			}

			// 3) Полный выход и очистка устройства
			await SignOutAndPurgeAsync();

			// 4) Переход к окну регистрации
			SceneManager.LoadScene(string.IsNullOrEmpty(registrationSceneName) ? "RegistrationWindow" : registrationSceneName);
		}
		catch (Exception e)
		{
			Debug.LogError("[DeleteAccount] Failed: " + e.Message);
		}
	}

	private static bool IsRequiresRecentLoginError(Exception ex)
	{
		try
		{
			var msg = ex.Message.ToLowerInvariant();
			return msg.Contains("requires-recent-login") || msg.Contains("recent login");
		}
		catch { return false; }
	}

	private async Task<bool> TryReauthenticateAsync(FirebaseAuth auth)
	{
		var user = auth?.CurrentUser;
		if (user == null) return false;

		// Попытка через Google (если привязан)
		bool isGoogle = false;
		try
		{
			foreach (var info in user.ProviderData)
			{
				if (info != null && string.Equals(info.ProviderId, "google.com", StringComparison.OrdinalIgnoreCase))
				{
					isGoogle = true;
					break;
				}
			}
		}
		catch { isGoogle = false; }

		Debug.Log("[DeleteAccount] Providers: google=" + isGoogle);

		if (isGoogle && await TryReauthWithGoogleAsync(user))
		{
			Debug.Log("[DeleteAccount] Reauth via Google: OK");
			return true;
		}

		// Попытка через email/password (если сохранены локально)
		string savedEmail = PlayerPrefs.GetString("saved_email", "");
		string savedPass  = PlayerPrefs.GetString("saved_password", "");
		if (!string.IsNullOrEmpty(savedEmail) && !string.IsNullOrEmpty(savedPass))
		{
			try
			{
				var cred = EmailAuthProvider.GetCredential(savedEmail, savedPass);
				await user.ReauthenticateAsync(cred);
				Debug.Log("[DeleteAccount] Reauth via email/password: OK");
				return true;
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[DeleteAccount] Email reauth failed: " + ex.Message);
			}
		}

		Debug.LogWarning("[DeleteAccount] No suitable reauth method available.");
		return false;
	}

	private async Task<bool> TryReauthWithGoogleAsync(FirebaseUser user)
	{
		try
		{
			string idToken = null;
			string accessToken = null;

#if (UNITY_ANDROID || UNITY_IOS) && USE_GOOGLE_PLAY_GAMES
			if (preferPlayGamesOnAndroid)
			{
				GooglePlayGames.BasicApi.PlayGamesClientConfiguration config =
					new GooglePlayGames.BasicApi.PlayGamesClientConfiguration.Builder()
						.RequestIdToken()
						.Build();
				GooglePlayGames.PlayGamesPlatform.InitializeInstance(config);
				GooglePlayGames.PlayGamesPlatform.Activate();

				var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
				Social.localUser.Authenticate(success => tcs.TrySetResult(success));
				bool ok = await tcs.Task;
				if (!ok) throw new Exception("Google Play Games reauth failed.");

				idToken = GooglePlayGames.PlayGamesPlatform.Instance.GetIdToken();
				if (string.IsNullOrEmpty(idToken))
					throw new Exception("Empty ID token from Google Play Games.");
			}
			else
#endif
#if (UNITY_ANDROID || UNITY_IOS) && USE_GOOGLE_SIGNIN
			{
				if (string.IsNullOrWhiteSpace(googleWebClientId))
				{
					throw new Exception("Google Sign-In requires Web Client ID. Please set googleWebClientId in the component.");
				}
				Google.GoogleSignIn.Configuration = new Google.GoogleSignInConfiguration
				{
					WebClientId   = googleWebClientId.Trim(),
					RequestIdToken = true,
					RequestEmail   = true
				};
				var signIn = Google.GoogleSignIn.DefaultInstance;
				var result = await signIn.SignIn();
				idToken = result.IdToken;
				accessToken = result.AuthCode;
				if (string.IsNullOrEmpty(idToken))
					throw new Exception("Empty ID token from Google Sign-In.");
			}
#else
			{
				throw new NotSupportedException("Google reauth is not available on this build.");
			}
#endif

			var credential = GoogleAuthProvider.GetCredential(idToken, accessToken);
			await user.ReauthenticateAsync(credential);
			return true;
		}
		catch (Exception ex)
		{
			Debug.LogWarning("[DeleteAccount] Google reauth failed: " + ex.Message);
			return false;
		}
	}

	private async Task SignOutAndPurgeAsync()
	{
#if (UNITY_ANDROID || UNITY_IOS) && USE_GOOGLE_PLAY_GAMES
		try { GooglePlayGames.PlayGamesPlatform.Instance?.SignOut(); } catch { }
#endif
#if (UNITY_ANDROID || UNITY_IOS) && USE_GOOGLE_SIGNIN
		try
		{
			var gi = Google.GoogleSignIn.DefaultInstance;
			gi.SignOut();
			gi.Disconnect();
		}
		catch { }
#endif
		try { FirebaseAuth.DefaultInstance.SignOut(); } catch { }
		try
		{
			PlayerPrefs.DeleteKey("saved_email");
			PlayerPrefs.DeleteKey("saved_password");
			PlayerPrefs.Save();
		}
		catch { }
	}
}


