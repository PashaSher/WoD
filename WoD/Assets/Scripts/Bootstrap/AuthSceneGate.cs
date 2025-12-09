using System;
using System.Threading.Tasks;
using Firebase.Auth;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Centralized scene router based on authentication state.
/// Attach to both RegistrationWindow and MainMenu (or to a bootstrap object marked DontDestroyOnLoad).
/// Ensures no ping-pong by only switching if current scene doesn't match required one.
/// </summary>
public class AuthSceneGate : MonoBehaviour
{
	[SerializeField] private string registrationSceneName = "RegistrationWindow";
	[SerializeField] private string mainMenuSceneName     = "MainMenu";
	[SerializeField] private bool requireEmailVerified    = false; // if true, email must be verified unless Google provider

	private async void Start()
	{
		// Give Firebase a frame if bootstrap runs elsewhere first
		await Task.Yield();

		string current = SceneManager.GetActiveScene().name;
		var auth = FirebaseAuth.DefaultInstance;
		var user = auth != null ? auth.CurrentUser : null;

		bool shouldBeInMain = await ShouldEnterMainMenu(user);

		if (shouldBeInMain && current != mainMenuSceneName)
		{
			SceneManager.LoadScene(mainMenuSceneName);
			return;
		}

		if (!shouldBeInMain && current != registrationSceneName)
		{
			SceneManager.LoadScene(registrationSceneName);
			return;
		}
	}

	private static async Task<bool> ShouldEnterMainMenu(FirebaseUser user)
	{
		if (user == null) return false;
		try { await user.ReloadAsync(); } catch { /* ignore */ }

		// If any provider is Google — treat as eligible
		bool viaGoogle = false;
		try
		{
			foreach (var info in user.ProviderData)
			{
				if (info != null && string.Equals(info.ProviderId, "google.com", StringComparison.OrdinalIgnoreCase))
				{
					viaGoogle = true;
					break;
				}
			}
		}
		catch { viaGoogle = false; }

		// Email verification optional here; many flows use email-less providers
		bool emailVerified = false;
		try { emailVerified = user.IsEmailVerified; } catch { }

		// Enter main if verified email or Google-linked; adjust as needed
		return viaGoogle || emailVerified;
	}
}









