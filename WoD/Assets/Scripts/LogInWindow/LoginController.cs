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
    [SerializeField] private TMP_Text statusText;

    [Header("Config")]
    [SerializeField] private string nextSceneName = "MainMenu";
    [SerializeField] private bool sendVerifyEmailIfNeeded = true;

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

            SetStatus("Ready.");
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
    }

    // Public method for OnClick() in Inspector
    public void OnLoginButton()
    {
        Debug.Log("[Login] Button clicked");
        _ = OnLoginClicked(); // fire-and-forget
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
}
