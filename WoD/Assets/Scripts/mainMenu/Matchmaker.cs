using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Auth;
using Firebase.Database;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class Matchmaker : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Button playButton;
    [SerializeField] private GameObject waitingPanel;     // панель "Wait for joiners..."
    [SerializeField] private TMP_Text waitingText;        // текст статуса ожидания

    [Header("Config")]
    [SerializeField] private string sessionsRoot = "sessions";
    [SerializeField] private string nextSceneName = "ArmyCreationScene";
    [SerializeField] private int   joinRetryCount = 4;    // сколько попыток забрать открытую сессию
    [SerializeField] private float joinRetryDelay = 0.25f;

    private FirebaseAuth auth;
    private DatabaseReference db;
    private string myUid => auth?.CurrentUser?.UserId;
    private string createdSessionId;
    private bool isWaiting;
    private bool sceneLoading;

    private EventHandler<ValueChangedEventArgs> sessionListener;

    private async void Start()
    {
        ToggleUI(false);
        SetWaiting(false, "");

        try
        {
            await FirebaseBootstrapper.EnsureInitializedAsync();
            auth = FirebaseAuth.DefaultInstance;
            db   = FirebaseDatabase.DefaultInstance.RootReference;

            if (auth.CurrentUser == null)
                throw new Exception("Not signed in. Go through login first.");

            if (playButton != null)
            {
                playButton.onClick.RemoveListener(OnPlayClicked);
                playButton.onClick.AddListener(OnPlayClicked);
            }

            ToggleUI(true);
        }
        catch (Exception e)
        {
            Debug.LogError("[Matchmaker] Init failed: " + e.Message);
            ToggleUI(false);
            SetWaiting(true, "Init error: " + e.Message);
        }
    }

    private void OnDestroy()
    {
        if (playButton != null)
            playButton.onClick.RemoveListener(OnPlayClicked);

        DetachSessionListener();

        // Если мы создали сессию и ещё ждём — корректно удалим её
        if (isWaiting && !string.IsNullOrEmpty(createdSessionId))
        {
            // fire-and-forget
            _ = TryCleanupMyOpenSession(createdSessionId);
        }
    }

    private void ToggleUI(bool enabled)
    {
        if (playButton) playButton.interactable = enabled;
    }

    private void SetWaiting(bool on, string msg)
    {
        isWaiting = on;
        if (waitingPanel) waitingPanel.SetActive(on);
        if (waitingText) waitingText.text = msg ?? "";
    }

    private void OnPlayClicked()
    {
        _ = HandlePlayClicked();
    }

    private async Task HandlePlayClicked()
    {
        ToggleUI(false);

        // 1) Try join any open session as Client
        for (int i = 0; i < joinRetryCount; i++)
        {
            var joined = await TryJoinOpenSession();
            if (joined) { ToggleUI(true); return; }
            await Task.Delay(TimeSpan.FromSeconds(joinRetryDelay));
        }

        // 2) If no open session to join — create one and wait
        await CreateSessionAndWait();
        ToggleUI(true);
    }

    // --- Try to join any open session (atomic) ---
    private async Task<bool> TryJoinOpenSession()
{
    try
    {
        var query = FirebaseDatabase.DefaultInstance
            .GetReference(sessionsRoot)
            .OrderByChild("sessionOpen")
            .EqualTo(true)
            .LimitToFirst(1);

        var snap = await query.GetValueAsync();
        if (!snap.Exists)
        {
            Debug.Log("[Matchmaker] No open sessions.");
            return false;
        }

        // Take first open session id
        string sessionId = null;
        foreach (var child in snap.Children)
        {
            sessionId = child.Key;
            break;
        }
        if (string.IsNullOrEmpty(sessionId))
            return false;

        var sessionRef = FirebaseDatabase.DefaultInstance.GetReference($"{sessionsRoot}/{sessionId}");

        // Atomically claim as client and close sessionOpen
        var txnSnap = await sessionRef.RunTransaction(mutable =>
        {
            var dict = mutable.Value as Dictionary<string, object>;
            if (dict == null) dict = new Dictionary<string, object>();

            bool open = dict.ContainsKey("sessionOpen") && dict["sessionOpen"] is bool b && b;
            bool hasClient = dict.ContainsKey("clientUid") && dict["clientUid"] != null && dict["clientUid"].ToString() != "";

            // Abort if already closed or already has client
            if (!open || hasClient)
                return TransactionResult.Abort();

            dict["clientUid"]   = myUid;
            dict["sessionOpen"] = false;
            dict["updatedAt"]   = ServerValue.Timestamp;

            mutable.Value = dict;
            return TransactionResult.Success(mutable);
        });

        // В Unity флага "Committed" нет; проверяем по фото результата
        bool success =
            txnSnap != null &&
            txnSnap.Exists &&
            (txnSnap.Child("clientUid").Value?.ToString() == myUid) &&
            (txnSnap.Child("sessionOpen").Value is bool closed && closed == false);

        if (!success)
        {
            Debug.Log("[Matchmaker] Session was taken by someone else, retrying...");
            return false;
        }

        // Success → we are Client. Load scene
        GameSession.SessionId = sessionId;
        GameSession.Role      = "Client";
        await GoNextScene();
        return true;
    }
    catch (Exception e)
    {
        Debug.LogWarning("[Matchmaker] TryJoinOpenSession error: " + e.Message);
        return false;
    }
}

    // --- Create my own session and wait for a client ---
    private async Task CreateSessionAndWait()
    {
        try
        {
            // Create new session with push id
            var newRef = FirebaseDatabase.DefaultInstance.GetReference(sessionsRoot).Push();
            string sessionId = newRef.Key;

            var data = new Dictionary<string, object>
            {
                { "sessionOpen", true },
                { "hostUid",     myUid },
                { "clientUid",   "" },
                { "createdAt",   ServerValue.Timestamp },
                { "updatedAt",   ServerValue.Timestamp }
            };

            await newRef.UpdateChildrenAsync(data);
            createdSessionId = sessionId;

            // UI wait
            SetWaiting(true, "Wait for joiners…");

            // Attach listener: when sessionOpen becomes false (and clientUid present) → go next
            var refToListen = FirebaseDatabase.DefaultInstance.GetReference($"{sessionsRoot}/{sessionId}");
            sessionListener = (s, e) =>
            {
                if (sceneLoading) return;
                if (e.DatabaseError != null) return;
                if (!e.Snapshot.Exists) return;

                bool open = e.Snapshot.Child("sessionOpen").Value is bool b && b;
                string client = e.Snapshot.Child("clientUid").Value?.ToString() ?? "";

                if (!open && !string.IsNullOrEmpty(client))
                {
                    // Someone joined my session
                    GameSession.SessionId = sessionId;
                    GameSession.Role      = "Host";
                    _ = GoNextScene();
                }
            };
            refToListen.ValueChanged += sessionListener;
        }
        catch (Exception e)
        {
            Debug.LogError("[Matchmaker] CreateSessionAndWait error: " + e.Message);
            SetWaiting(true, "Error: " + e.Message);
        }
    }

    private async Task GoNextScene()
    {
        if (sceneLoading) return;
        sceneLoading = true;

        // UI off, stop listening
        SetWaiting(false, "");
        DetachSessionListener();

        await Task.Yield();
        SceneManager.LoadScene(nextSceneName);
    }

    private void DetachSessionListener()
    {
        if (!string.IsNullOrEmpty(createdSessionId) && sessionListener != null)
        {
            var r = FirebaseDatabase.DefaultInstance.GetReference($"{sessionsRoot}/{createdSessionId}");
            r.ValueChanged -= sessionListener;
            sessionListener = null;
        }
    }

    private async Task TryCleanupMyOpenSession(string sessionId)
    {
        try
        {
            var r = FirebaseDatabase.DefaultInstance.GetReference($"{sessionsRoot}/{sessionId}");
            var snap = await r.GetValueAsync();
            if (snap.Exists)
            {
                bool open = snap.Child("sessionOpen").Value is bool b && b;
                string host = snap.Child("hostUid").Value?.ToString() ?? "";
                if (open && host == myUid)
                {
                    await r.RemoveValueAsync();
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Matchmaker] Cleanup error: " + e.Message);
        }
    }
}
