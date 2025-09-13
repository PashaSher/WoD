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
    private bool isReady;

    private void Awake()
    {
        if (createButton != null)
            createButton.onClick.AddListener(OnCreateClicked);

        // Сразу блокируем UI до инициализации
        ToggleUI(false);
        SetStatus("Initializing Firebase...");
    }

    private async void Start()
    {
        try
        {
            // Ждём ЕДИНУЮ инициализацию (см. FirebaseBootstrapper из прошлого сообщения)
            await FirebaseBootstrapper.EnsureInitializedAsync();

            // Теперь спокойно берём инстансы
            auth = FirebaseAuth.DefaultInstance;
            db   = FirebaseDatabase.DefaultInstance.RootReference;

            isReady = true;
            SetStatus("Ready.");
            ToggleUI(true);
        }
        catch (Exception e)
        {
            isReady = false;
            SetStatus("Firebase init failed: " + e.Message);
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
        Debug.Log("[AuthManager] " + msg);
    }

    private void ToggleUI(bool enabled)
    {
        if (createButton)   createButton.interactable   = enabled;
        if (nicknameInput)  nicknameInput.interactable  = enabled;
        if (emailInput)     emailInput.interactable     = enabled;
        if (passwordInput)  passwordInput.interactable  = enabled;
    }

    private bool TryGetValidatedInputs(out string nick, out string email, out string pass, out string error)
    {
        nick = email = pass = null;
        error = null;

        string n = nicknameInput ? nicknameInput.text.Trim() : "";
        string e = emailInput ? emailInput.text.Trim() : "";
        string p = passwordInput ? passwordInput.text : "";

        if (string.IsNullOrEmpty(n))       { error = "Enter nickname."; return false; }
        if (string.IsNullOrEmpty(e))       { error = "Enter email.";    return false; }

        var rx = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        if (!rx.IsMatch(e))                { error = "Email format is invalid."; return false; }

        if (string.IsNullOrEmpty(p) || p.Length < 6)
        { error = "Password must be at least 6 characters."; return false; }

        nick = n; email = e; pass = p;
        return true;
    }

    private void OnCreateClicked()
    {
        if (!isReady)
        {
            SetStatus("Firebase is not ready yet.");
            return;
        }

        if (!TryGetValidatedInputs(out var nick, out var email, out var pass, out var err))
        {
            SetStatus(err);
            return;
        }

        RegisterAsync(nick, email, pass).Forget();
    }

    private async Task RegisterAsync(string nick, string email, string pass)
    {
        ToggleUI(false);

        try
        {
            SetStatus("Creating account...");
            var cred = await auth.CreateUserWithEmailAndPasswordAsync(email, pass);
            var user = cred.User ?? throw new Exception("User is null after create.");

            // Профиль (ник)
            await user.UpdateUserProfileAsync(new UserProfile { DisplayName = nick });

            // RTDB профиль
            SetStatus("Saving profile in database...");
            string uid = user.UserId;
            var data = new Dictionary<string, object>
            {
                { "nickname",       nick },
                { "email",          email },
                { "emailVerified",  false },
                { "createdAt",      ServerValue.Timestamp } // при регистрации — корректно
            };
            await db.Child("users").Child(uid).UpdateChildrenAsync(data);

            // Письмо подтверждения
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
                    case AuthError.EmailAlreadyInUse:     return "Email already in use.";
                    case AuthError.InvalidEmail:          return "Invalid email.";
                    case AuthError.WeakPassword:          return "Weak password (min 6).";
                    case AuthError.MissingEmail:          return "Email is missing.";
                    case AuthError.MissingPassword:       return "Password is missing.";
                    case AuthError.NetworkRequestFailed:  return "Network error. Check connection.";
                    default:                              return $"Auth error: {authErr}";
                }
            }
            catch
            {
                // иногда ErrorCode не приводится к AuthError
            }
        }
        return ex.Message;
    }
}

static class TaskExt
{
    public static async void Forget(this Task t)
    {
        try { await t; }
        catch (Exception e) { Debug.LogException(e); }
    }
}
