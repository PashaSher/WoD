// Assets/Scripts/Registration Window/AuthManager.cs
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

using Firebase;
using Firebase.Auth;
using Firebase.Database;
using Firebase.Extensions;

public class AuthManager : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TMP_InputField nicknameInput;
    [SerializeField] private TMP_InputField emailInput;
    [SerializeField] private TMP_InputField passwordInput;
    [SerializeField] private Button createButton;
    [SerializeField] private TMP_Text statusText;

    private FirebaseAuth auth;
    private DatabaseReference db;

    private void Awake()
    {
        if (createButton != null)
            createButton.onClick.AddListener(OnCreateClicked);
    }

    // IMPORTANT: explicitly use the non-generic IEnumerator
    private System.Collections.IEnumerator Start()
    {
        SetStatus("Initializing Firebase...");
        var depTask = FirebaseApp.CheckAndFixDependenciesAsync();
        yield return new WaitUntil(() => depTask.IsCompleted);

        if (depTask.Result == DependencyStatus.Available)
        {
            auth = FirebaseAuth.DefaultInstance;
            db   = FirebaseDatabase.DefaultInstance.RootReference;
            SetStatus("Ready.");
        }
        else
        {
            SetStatus("Firebase init failed: " + depTask.Result);
            ToggleUI(false);
        }
    }

    private void OnDestroy()
    {
        if (createButton != null)
            createButton.onClick.RemoveListener(OnCreateClicked);
    }

    private void SetStatus(string msg)
    {
        if (statusText) statusText.text = msg;
        Debug.Log(msg);
    }

    private void ToggleUI(bool enabled)
    {
        if (createButton) createButton.interactable = enabled;
        if (nicknameInput) nicknameInput.interactable = enabled;
        if (emailInput) emailInput.interactable = enabled;
        if (passwordInput) passwordInput.interactable = enabled;
    }

    private bool ValidateInputs(out string error)
    {
        error = null;
        string nick = nicknameInput ? nicknameInput.text.Trim() : "";
        string email = emailInput ? emailInput.text.Trim() : "";
        string pass = passwordInput ? passwordInput.text : "";

        if (string.IsNullOrEmpty(nick)) { error = "Enter nickname."; return false; }
        if (string.IsNullOrEmpty(email)) { error = "Enter email."; return false; }

        var rx = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        if (!rx.IsMatch(email)) { error = "Email format is invalid."; return false; }

        if (string.IsNullOrEmpty(pass) || pass.Length < 6)
        { error = "Password must be at least 6 characters."; return false; }

        return true;
    }

    private void OnCreateClicked()
    {
        if (!ValidateInputs(out string err)) { SetStatus(err); return; }
        RegisterAsync().Forget();
    }

    private async Task RegisterAsync()
    {
        ToggleUI(false);
        string nick = nicknameInput.text.Trim();
        string email = emailInput.text.Trim();
        string pass = passwordInput.text;

        try
        {
            SetStatus("Creating account...");
            var cred = await auth.CreateUserWithEmailAndPasswordAsync(email, pass);
            var user = cred.User ?? throw new Exception("User is null after create.");

            await user.UpdateUserProfileAsync(new UserProfile { DisplayName = nick });

            SetStatus("Saving profile in database...");
            string uid = user.UserId;
            var data = new Dictionary<string, object>
            {
                { "nickname", nick },
                { "email", email },
                { "emailVerified", false },
                { "createdAt", ServerValue.Timestamp }
            };
            await db.Child("users").Child(uid).UpdateChildrenAsync(data);

            SetStatus("Sending verification email...");
            await user.SendEmailVerificationAsync();

            SetStatus($"Done. Verification email sent to {email}. Please confirm it.");
        }
        catch (Exception e)
        {
            SetStatus("Error: " + MapFirebaseError(e));
        }
        finally
        {
            ToggleUI(true);
        }
    }

    private string MapFirebaseError(Exception ex)
    {
        if (ex is FirebaseException fex)
        {
            try
            {
                var authErr = (AuthError)fex.ErrorCode;
                switch (authErr)
                {
                    case AuthError.EmailAlreadyInUse: return "Email already in use.";
                    case AuthError.InvalidEmail: return "Invalid email.";
                    case AuthError.WeakPassword: return "Weak password (min 6).";
                    case AuthError.MissingEmail: return "Email is missing.";
                    case AuthError.MissingPassword: return "Password is missing.";
                    case AuthError.NetworkRequestFailed: return "Network error. Check connection.";
                }
            }
            catch { }
        }
        return ex.Message;
    }
}

static class TaskExt
{
    public static async void Forget(this Task t)
    {
        try { await t; } catch (Exception e) { Debug.LogException(e); }
    }
}
