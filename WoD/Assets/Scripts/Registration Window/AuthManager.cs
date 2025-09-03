// Assets/Scripts/Registration Window/AuthManager.cs
using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using System.Threading.Tasks;


using Firebase;
using Firebase.Auth;

#if UNITY_ANDROID && USE_GOOGLE_SIGNIN
using Google; // из плагина Google Sign-In for Unity
#endif

public class AuthManager : MonoBehaviour
{
    [Header("Email UI (optional)")]
    [SerializeField] private GameObject emailPanel;        // панель с полями email/password
    [SerializeField] private TMP_InputField emailInput;    // Email
    [SerializeField] private TMP_InputField passwordInput; // Password
    [SerializeField] private TMP_Text statusText;          // опционально — вывод статуса

    [Header("Config")]
    [SerializeField] private string mainMenuSceneName = "MainMenu";
    [Tooltip("Only if you use Google Sign-In. Take Web client ID from Firebase Console → Project settings → General → Your apps.")]
    [SerializeField] private string webClientId = "";

    private FirebaseAuth auth;
    private bool firebaseReady = false;

    private void Log(string msg)
    {
        if (statusText) statusText.text = msg;
        Debug.Log("[Auth] " + msg);
    }

    private async void Awake()
    {
      if (emailPanel) emailPanel.SetActive(false);

    var dep = await FirebaseApp.CheckAndFixDependenciesAsync();
    if (dep != DependencyStatus.Available)
    {
        Log("Firebase dependencies not resolved: " + dep);
        return;
    }

    auth = FirebaseAuth.DefaultInstance;
    firebaseReady = true;
    Log("Firebase ready");

    await EnsureValidSessionAsync();   // <- ждём проверку
    }

    // ===================== PUBLIC UI HANDLERS =====================

    // Показать панель Email/Password
    public void OnEmailButton()
    {
        if (!firebaseReady) { Log("Firebase not ready"); return; }
        if (emailPanel) emailPanel.SetActive(true);
    }

    // Создать или войти по Email/Password
    public async void OnEmailCreateOrSignIn()
    {
        if (!firebaseReady) { Log("Firebase not ready"); return; }

        string email = emailInput ? emailInput.text.Trim() : "";
        string pass = passwordInput ? passwordInput.text : "";

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pass))
        {
            Log("Enter email and password");
            return;
        }

        // 1) Пытаемся создать нового пользователя
        try
        {
            AuthResult createResult = await auth.CreateUserWithEmailAndPasswordAsync(email, pass);
            FirebaseUser user = createResult.User;
            Log("Registered: " + user.Email);
            OnAuthSuccess();
            return;
        }
        catch (Exception ex)
        {
            // Если уже существует — войдём
            if (ex is FirebaseException fex &&
                (AuthError)fex.ErrorCode == AuthError.EmailAlreadyInUse)
            {
                try
                {
                    AuthResult signInResult = await auth.SignInWithEmailAndPasswordAsync(email, pass);
                    FirebaseUser user = signInResult.User;
                    Log("Signed in: " + user.Email);
                    OnAuthSuccess();
                    return;
                }
                catch (Exception e2)
                {
                    Log("Email sign-in error: " + e2.Message);
                    return;
                }
            }
            else
            {
                Log("Create error: " + ex.Message);
                return;
            }
        }
    }

    // Google Sign-In (Android). Требует плагин Google Sign-In и символ компиляции USE_GOOGLE_SIGNIN
    public async void OnGoogleButton()
    {
#if UNITY_ANDROID && USE_GOOGLE_SIGNIN
        if (!firebaseReady) { Log("Firebase not ready"); return; }
        if (string.IsNullOrEmpty(webClientId))
        {
            Log("Set Web Client ID in inspector");
            return;
        }

        GoogleSignIn.Configuration = new GoogleSignInConfiguration
        {
            WebClientId   = webClientId, // строго Web client ID
            RequestIdToken = true,
            RequestEmail   = true,
            UseGameSignIn  = false
        };

        try
        {
            Log("Opening Google account chooser...");
            var gUser = await GoogleSignIn.DefaultInstance.SignIn();
            if (gUser == null) { Log("Google sign-in canceled"); return; }

            var credential = GoogleAuthProvider.GetCredential(gUser.IdToken, null);
            AuthResult res = await auth.SignInWithCredentialAsync(credential);
            FirebaseUser user = res.User;

            Log("Signed in: " + user.Email);
            OnAuthSuccess();
        }
        catch (Exception e)
        {
            Log("Google sign-in error: " + e.Message);
        }
#else
        Log("Google Sign-In not enabled. Import Google Sign-In plugin and add USE_GOOGLE_SIGNIN to Scripting Define Symbols.");
#endif
    }

    // Выход (по желанию, повесь на кнопку в MainMenu)
    public void SignOut()
    {
        if (auth != null) auth.SignOut();
#if UNITY_ANDROID && USE_GOOGLE_SIGNIN
        try { GoogleSignIn.DefaultInstance.SignOut(); } catch { }
#endif
        Log("Signed out");
    }

    // ===================== INTERNAL =====================

    private void OnAuthSuccess()
    {
        // при желании можно сохранить userId/email в PlayerPrefs
        LoadMainMenu();
    }

    private void LoadMainMenu()
    {
        Log("Loading scene: " + mainMenuSceneName);
        SceneManager.LoadScene(mainMenuSceneName);
    }
    
      private async Task EnsureValidSessionAsync()
    {
       var user = auth.CurrentUser;
       if (user == null)
    {
        Log("No saved session");
        return;                        // async Task: просто return;
    }

    try
    {
        await user.ReloadAsync();
        var _ = await user.TokenAsync(true); // принудительно обновляем ID токен

        Log("Session valid: " + (user.Email ?? user.UserId));
        LoadMainMenu();
    }
    catch (FirebaseException fex)
    {
        Log($"Session invalid [{(AuthError)fex.ErrorCode}]. Clearing local auth.");
        auth.SignOut();
    }
    catch (Exception e)
    {
        Log("Session check error: " + e.Message);
        auth.SignOut();
    }
   }
}
