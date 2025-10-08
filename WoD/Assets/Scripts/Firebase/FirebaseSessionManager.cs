using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Auth;
using Firebase.Database;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class FirebaseSessionManager : MonoBehaviour
{
    public static FirebaseSessionManager Instance { get; private set; }

    [Header("Config")]
    [SerializeField] private string sessionsPath = "sessions";
    [SerializeField] private string mainMenuSceneName = "MainMenu";
    public string MainMenuSceneName => mainMenuSceneName;

    // UI для сообщения "сессию закрыл другой игрок"
    private GameObject remoteClosedPanel;
    private TMP_Text   remoteClosedText;
    private Button     goToMenuButton;

    private bool modalShown;

    private FirebaseAuth auth;
    private DatabaseReference sessionRef;
    private string sessionId;
    private bool isHost;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        auth = FirebaseAuth.DefaultInstance;
        SceneManager.sceneLoaded += OnSceneLoaded;   // чтобы заново привязывать UI при смене сцены (см. Binder ниже)
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (sessionRef != null) sessionRef.ValueChanged -= OnSessionChanged;
    }

    private void OnSceneLoaded(Scene s, LoadSceneMode m)
    {
        // сбрасываем модалку при смене сцены (если была показана)
        modalShown = false;
    }

    /// <summary>Вызывай из Matchmaker после join/create.</summary>
    public void Configure(string sid, bool hostRole)
    {
        sessionId = sid;
        isHost = hostRole;

        if (sessionRef != null) sessionRef.ValueChanged -= OnSessionChanged;
        sessionRef = FirebaseDatabase.DefaultInstance.GetReference(sessionsPath).Child(sessionId);
        sessionRef.ValueChanged += OnSessionChanged;
    }

    /// <summary>Привязка UI текущей сцены (вызывается Binder-ом в сцене).</summary>
    public void BindUI(GameObject panel, TMP_Text text, Button menuBtn)
    {
        remoteClosedPanel = panel;
        remoteClosedText  = text;
        goToMenuButton    = menuBtn;
        modalShown = false;

        if (goToMenuButton != null)
        {
            goToMenuButton.onClick.RemoveAllListeners();
            goToMenuButton.onClick.AddListener(() =>
            {
                SceneManager.LoadScene(mainMenuSceneName);
            });
        }
        if (remoteClosedPanel != null) remoteClosedPanel.SetActive(false);
    }

    private void OnSessionChanged(object sender, ValueChangedEventArgs e)
    {
        if (e.DatabaseError != null) return;

        var snap = e.Snapshot;

        // Узел удалён — показать сообщение
        if (!snap.Exists)
        {
            ShowRemoteClosed(isHost ? "Client left the session" : "Host closed the session");
            return;
        }

        bool open = true;
        var openChild = snap.Child("sessionOpen");
        if (openChild.Exists && openChild.Value != null)
            open = Convert.ToBoolean(openChild.Value);

        string closedByUid  = snap.Child("closedByUid").Value?.ToString();
        string closedByRole = snap.Child("closedByRole").Value?.ToString();

        string myUid = auth.CurrentUser != null ? auth.CurrentUser.UserId : "";

        // Если сессию закрыли (и это сделал не я) — показать модалку.
        if (!open && !string.IsNullOrEmpty(closedByUid) && closedByUid != myUid)
        {
            string who = closedByRole == "host" ? "Host" : "Client";
            ShowRemoteClosed($"{who} closed the session");
        }
    }

    private void ShowRemoteClosed(string message)
    {
        if (modalShown) return;
        modalShown = true;

        if (remoteClosedPanel == null) return; // В этой сцене нет модалки — просто игнор.
        remoteClosedPanel.SetActive(true);
        if (remoteClosedText != null) remoteClosedText.text = message;
    }

    /// <summary>Выходит из сессии: ставит флаг, пишет кто закрыл, удаляет узел.</summary>
    public async Task LeaveSessionAsync()
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            SceneManager.LoadScene(mainMenuSceneName);
            return;
        }

        var r = FirebaseDatabase.DefaultInstance.GetReference(sessionsPath).Child(sessionId);
        string uid = auth.CurrentUser != null ? auth.CurrentUser.UserId : "unknown";

        var updates = new Dictionary<string, object>
        {
            ["sessionOpen"] = false,
            ["closedByUid"] = uid,
            ["closedByRole"] = isHost ? "host" : "client",
            ["closedAt"] = ServerValue.Timestamp
        };

        try { await r.UpdateChildrenAsync(updates); } catch (Exception ex) { Debug.LogWarning(ex); }
        await Task.Delay(600); // дать второму клиенту поймать событие
        try { await r.RemoveValueAsync(); } catch (Exception ex) { Debug.LogWarning(ex); }
    }

    /// <summary>Закрыть/удалить сессию и перейти в главное меню.</summary>
    public async Task LeaveSessionAndGoToMenuAsync()
    {
        try { await LeaveSessionAsync(); }
        catch (Exception ex) { Debug.LogWarning(ex); }
        SceneManager.LoadScene(mainMenuSceneName);
    }
}
