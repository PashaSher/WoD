// Assets/Scripts/Login/LoginController.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

using Firebase;
using Firebase.Auth;
using Firebase.Database;

public class LoginController : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TMP_InputField emailInput;
    [SerializeField] private TMP_InputField passwordInput;
    [SerializeField] private Button loginButton;
	[SerializeField] private Button googleButton; // Assign in Inspector for "Continue with Google"
    [SerializeField] private TMP_Text statusText;

    [Header("Config")]
    [SerializeField] private string nextSceneName = "MainMenu";
    [SerializeField] private bool sendVerifyEmailIfNeeded = true;
	// If you use the Google Sign-In Unity plugin, set your Web Client ID here (from Firebase console).
	[SerializeField] private string googleWebClientId = "";
	// Choose which Google flow to use (requires corresponding plugin in project and scripting define symbols):
	// - USE_GOOGLE_PLAY_GAMES for Google Play Games plugin
	// - USE_GOOGLE_SIGNIN for Google Sign-In plugin
	[SerializeField] private bool preferPlayGamesOnAndroid = true;

    private FirebaseAuth auth;
    private DatabaseReference db;

    // ---------- INIT ----------
    private async void Start()
    {
        SetInteractable(false);
        SetStatus("Initializing Firebase...");

        try
        {
            // Wait for the single, global initialization
            await FirebaseBootstrapper.EnsureInitializedAsync();

            auth = FirebaseAuth.DefaultInstance;
            db   = FirebaseDatabase.DefaultInstance.RootReference;

            // restore saved fields (if assigned)
            if (emailInput != null)
                emailInput.text = PlayerPrefs.GetString("saved_email", "");
            if (passwordInput != null)
                passwordInput.text = PlayerPrefs.GetString("saved_password", "");

            // (re)subscribe button safely
            if (loginButton != null)
            {
                loginButton.onClick.RemoveListener(OnLoginButton);
                loginButton.onClick.AddListener(OnLoginButton);
            }
			if (googleButton != null)
			{
				googleButton.onClick.RemoveListener(OnGoogleLoginButton);
				googleButton.onClick.AddListener(OnGoogleLoginButton);
			}

            SetStatus("Ready.");

			// Auto-skip to main menu if already authenticated and eligible
			if (await TryAutoEnterAsync())
				return;

			SetInteractable(true);
        }
        catch (Exception e)
        {
            Fail("Init failed: " + e.Message);
        }
    }

    private void OnDestroy()
    {
        if (loginButton != null)
            loginButton.onClick.RemoveListener(OnLoginButton);
		if (googleButton != null)
			googleButton.onClick.RemoveListener(OnGoogleLoginButton);
    }

    // Public method for OnClick() in Inspector
    public void OnLoginButton()
    {
        Debug.Log("[Login] Button clicked");
        _ = OnLoginClicked(); // fire-and-forget
    }

	// Public method for Google button in Inspector
	public void OnGoogleLoginButton()
	{
		Debug.Log("[Login] Google button clicked");
		_ = SignInWithGoogleAsync(); // fire-and-forget
	}

    // ---------- LOGIN FLOW ----------
    private async Task OnLoginClicked()
    {
        SetInteractable(false);
        SetStatus("Checking fields...");

        if (emailInput == null || passwordInput == null)
        {
            Fail("Email/Password inputs not linked.");
            return;
        }

        string email = emailInput.text?.Trim() ?? "";
        string pass  = passwordInput.text ?? "";

        if (string.IsNullOrEmpty(email))
        {
            Fail("Email is empty.");
            return;
        }
        if (string.IsNullOrEmpty(pass))
        {
            Fail("Password is empty.");
            return;
        }
        if (auth == null || db == null)
        {
            Fail("Firebase not initialized.");
            return;
        }

        try
        {
            SetStatus("Signing in...");
            var cred = await auth.SignInWithEmailAndPasswordAsync(email, pass);
            var user = cred.User;
            if (user == null)
            {
                Fail("Signin failed: user is null.");
                return;
            }

            // Refresh user info and check verification
            await user.ReloadAsync();
            bool verified = user.IsEmailVerified;

            if (!verified)
            {
                SetStatus("Email is NOT verified.");
                if (sendVerifyEmailIfNeeded)
                {
                    try
                    {
                        await user.SendEmailVerificationAsync();
                        SetStatus("Verification email sent. Check inbox, then try again.");
                    }
                    catch (Exception eSend)
                    {
                        SetStatus("Failed to send verification email: " + eSend.Message);
                    }
                }
                SetInteractable(true);
                return;
            }

            // Update RTDB: set createdAt only once; update lastLoginAt each time
            try
            {
                string uid = user.UserId;

                var userSnap = await FirebaseDatabase.DefaultInstance
                    .GetReference($"users/{uid}")
                    .GetValueAsync();

                var updates = new Dictionary<string, object>
                {
                    [$"users/{uid}/emailVerified"] = true,
                    [$"users/{uid}/email"]         = user.Email ?? email,
                    [$"users/{uid}/lastLoginAt"]   = ServerValue.Timestamp
                };

                if (!userSnap.Exists || !userSnap.Child("createdAt").Exists)
                {
                    updates[$"users/{uid}/createdAt"] = ServerValue.Timestamp;
                }

                SetStatus("Updating database...");
                await db.UpdateChildrenAsync(updates);
            }
            catch (Exception dbEx)
            {
                Fail("RTDB update failed: " + dbEx.Message);
                return;
            }

            // Save locally (note: storing password in PlayerPrefs is insecure)
            PlayerPrefs.SetString("saved_email", email);
            PlayerPrefs.SetString("saved_password", pass); // keep for your current flow
            PlayerPrefs.Save();

            // Go to next scene
            SetStatus("Login success. Loading MainMenu...");
            SceneManager.LoadScene(nextSceneName);
        }
        catch (Exception ex)
        {
            Fail(ParseAuthError(ex));
        }
    }

    // ---------- HELPERS ----------
    private void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
        Debug.Log("[Login] " + msg);
    }

    private void SetInteractable(bool on)
    {
        if (loginButton   != null) loginButton.interactable   = on;
		if (googleButton  != null) googleButton.interactable  = on;
        if (emailInput    != null) emailInput.interactable    = on;
        if (passwordInput != null) passwordInput.interactable = on;
    }

    private void Fail(string msg)
    {
        SetStatus(msg);
        SetInteractable(true);
    }

    private string ParseAuthError(Exception ex)
    {
        string generic = "Signin failed: " + ex.Message;

        if (ex is FirebaseException fe)
        {
            string msg = fe.Message.ToLowerInvariant();
            if (msg.Contains("invalid-email"))     return "Invalid email format.";
            if (msg.Contains("wrong-password"))    return "Wrong password.";
            if (msg.Contains("user-not-found"))    return "User not found.";
            if (msg.Contains("user-disabled"))     return "User is disabled.";
            if (msg.Contains("too-many-requests")) return "Too many attempts. Try later.";
            return generic;
        }
        return generic;
    }

	// ---------- AUTO-ENTER ----------
	private async Task<bool> TryAutoEnterAsync()
	{
		try
		{
			var user = auth?.CurrentUser;
			if (user == null) return false;

			await user.ReloadAsync();

			bool emailVerified = false;
			try { emailVerified = user.IsEmailVerified; } catch { emailVerified = false; }

			bool viaGoogle = false;
			try
			{
				// Check if any linked provider is Google
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

			// Allow enter if email verified or Google-linked
			if (emailVerified || viaGoogle)
			{
				SetStatus("Already signed in. Loading MainMenu...");
				SceneManager.LoadScene(nextSceneName);
				return true;
			}
		}
		catch (Exception ex)
		{
			Debug.LogWarning("[Login] Auto-enter check failed: " + ex.Message);
		}
		return false;
	}

	// ---------- GOOGLE SIGN-IN ----------
	private async Task SignInWithGoogleAsync()
	{
		SetInteractable(false);
		SetStatus("Starting Google sign-in...");

		if (auth == null || db == null)
		{
			Fail("Firebase not initialized.");
			return;
		}

		try
		{
			string idToken = null;
			string accessToken = null;

#if (UNITY_ANDROID || UNITY_IOS) && USE_GOOGLE_PLAY_GAMES
			// Flow via Google Play Games plugin (requires scripting define: USE_GOOGLE_PLAY_GAMES)
			if (preferPlayGamesOnAndroid)
			{
				SetStatus("Signing in via Google Play Games...");
				{
					// Local scopes under the directive to avoid missing usings when the plugin is absent
					GooglePlayGames.BasicApi.PlayGamesClientConfiguration config =
						new GooglePlayGames.BasicApi.PlayGamesClientConfiguration.Builder()
							.RequestIdToken()
							.Build();

					GooglePlayGames.PlayGamesPlatform.InitializeInstance(config);
					GooglePlayGames.PlayGamesPlatform.Activate();
				}

				var tcs = new TaskCompletionSource<bool>();
				Social.localUser.Authenticate(success => tcs.TrySetResult(success));
				bool ok = await tcs.Task;
				if (!ok) throw new Exception("Play Games authentication failed.");

				idToken = GooglePlayGames.PlayGamesPlatform.Instance.GetIdToken();
				if (string.IsNullOrEmpty(idToken))
					throw new Exception("Play Games returned empty ID token.");
			}
			else
#endif
#if (UNITY_ANDROID || UNITY_IOS) && USE_GOOGLE_SIGNIN
			// Flow via Google Sign-In Unity plugin (requires scripting define: USE_GOOGLE_SIGNIN)
			{
				SetStatus("Signing in via Google Sign-In...");
				Google.GoogleSignIn.Configuration = new Google.GoogleSignInConfiguration
				{
					WebClientId   = googleWebClientId,
					RequestIdToken = true,
					RequestEmail   = true
				};
				Google.GoogleSignIn signIn = Google.GoogleSignIn.DefaultInstance;
				Google.GoogleSignInUser result = await signIn.SignIn();
				idToken = result.IdToken;
				accessToken = result.AuthCode; // not required for Firebase
				if (string.IsNullOrEmpty(idToken))
					throw new Exception("Google Sign-In returned empty ID token.");
			}
#else
			{
				throw new NotSupportedException("Google Sign-In not available on this build. Add plugin and define symbols.");
			}
#endif

			// Exchange token with Firebase
			var credential = GoogleAuthProvider.GetCredential(idToken, accessToken);
			var user = await auth.SignInWithCredentialAsync(credential);
			if (user == null) throw new Exception("Firebase returned null user.");

			// Update RTDB profile
			try
			{
				string uid = user.UserId;
				var updates = new Dictionary<string, object>
				{
					[$"users/{uid}/emailVerified"] = true,
					[$"users/{uid}/email"]         = user.Email ?? "",
					[$"users/{uid}/nickname"]      = user.DisplayName ?? (user.Email ?? "Player"),
					[$"users/{uid}/lastLoginAt"]   = ServerValue.Timestamp
				};

				// If no createdAt yet — set it once
				var snap = await FirebaseDatabase.DefaultInstance
					.GetReference($"users/{user.UserId}")
					.GetValueAsync();
				if (!snap.Exists || !snap.Child("createdAt").Exists)
				{
					updates[$"users/{uid}/createdAt"] = ServerValue.Timestamp;
				}

				await db.UpdateChildrenAsync(updates);
			}
			catch (Exception dbEx)
			{
				Debug.LogWarning("[Login] RTDB update after Google sign-in failed: " + dbEx.Message);
			}

			SetStatus("Google sign-in success. Loading MainMenu...");
			SceneManager.LoadScene(nextSceneName);
		}
		catch (Exception ex)
		{
			Fail("Google sign-in failed: " + ex.Message);
		}
	}
}
