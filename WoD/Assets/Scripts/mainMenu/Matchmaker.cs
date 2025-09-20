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
    [SerializeField] private GameObject waitingPanel;      // панель "Wait for joiners…"
    [SerializeField] private TMP_Text waitingText;         // текст статуса ожидания
    [SerializeField] private Button cancelButton;          // кнопка Cancel на панели ожидания

    [Header("Config")]
    [SerializeField] private string sessionsRoot = "sessions";
    [SerializeField] private string nextSceneName = "ArmyCreationScene";
    [SerializeField] private int joinRetryCount = 8;     // попытки забрать открытую сессию
    [SerializeField] private float joinRetryDelay = 0.3f; // задержка между попытками (сек)

    private FirebaseAuth auth;
    private DatabaseReference db;
    private string myUid => auth?.CurrentUser?.UserId;

    private string createdSessionId;     // если мы Host, хранит push-id созданной сессии
    private bool isWaiting;              // показана ли панель ожидания (актуально для Host)
    private bool sceneLoading;

    private EventHandler<ValueChangedEventArgs> sessionListener;

    private async void Start()
    {
        TogglePlay(false);
        SetWaiting(false, "");

        try
        {
            await FirebaseBootstrapper.EnsureInitializedAsync();
            auth = FirebaseAuth.DefaultInstance;
            db = FirebaseDatabase.DefaultInstance.RootReference;

            if (auth.CurrentUser == null)
                throw new Exception("Not signed in. Go through login first.");

            if (playButton != null)
            {
                playButton.onClick.RemoveListener(OnPlayClicked);
                playButton.onClick.AddListener(OnPlayClicked);
            }

            if (cancelButton != null)
            {
                cancelButton.onClick.RemoveAllListeners();
                cancelButton.onClick.AddListener(OnCancelClicked);
            }

            TogglePlay(true);
        }
        catch (Exception e)
        {
            Debug.LogError("[Matchmaker] Init failed: " + e.Message);
            TogglePlay(false);
            SetWaiting(true, "Init error: " + e.Message);
        }
    }

    private void OnDestroy()
    {
        if (playButton != null)
            playButton.onClick.RemoveListener(OnPlayClicked);
        if (cancelButton != null)
            cancelButton.onClick.RemoveListener(OnCancelClicked);

        DetachSessionListener();

        // Если мы создали сессию и ещё ждём — удалим её
        if (isWaiting && !string.IsNullOrEmpty(createdSessionId))
        {
            _ = TryCleanupMyOpenSession(createdSessionId);
        }
    }

    // --- UI helpers ---
    private void TogglePlay(bool enabled)
    {
        if (playButton) playButton.interactable = enabled && !isWaiting;
    }

    private void SetWaiting(bool on, string msg)
    {
        isWaiting = on;
        if (waitingPanel) waitingPanel.SetActive(on);
        if (waitingText) waitingText.text = msg ?? "";
        // Пока ждём — play неактивна, cancel активна
        TogglePlay(false);
        if (cancelButton) cancelButton.gameObject.SetActive(on);
        if (cancelButton) cancelButton.interactable = on;
    }

    // --- Button handlers ---
    private void OnPlayClicked()
    {
        _ = HandlePlayClicked();
    }

    private async void OnCancelClicked()
    {
        // Прячем окно ожидания, снимаем листенер
        SetWaiting(false, "");
        DetachSessionListener();
        GameSession.Clear();

        // Если мы Host и сессия ещё открыта — удалим её
        if (!string.IsNullOrEmpty(createdSessionId))
        {
            await TryCleanupMyOpenSession(createdSessionId);
            createdSessionId = null;
        }

        // Вернём Play
        TogglePlay(true);
    }

    // --- Flow ---
    private async Task HandlePlayClicked()
    {
        TogglePlay(false);

        // 1) Try join any open session as Client
        for (int i = 0; i < joinRetryCount; i++)
        {
            var joined = await TryJoinOpenSession();
            if (joined) { TogglePlay(true); return; }
            await Task.Delay(TimeSpan.FromSeconds(joinRetryDelay));
        }

        // 2) If no open session to join — create one and wait (Host)
        await CreateSessionAndWait();
        // Play остаётся выключенной пока ждём; Cancel — включена
    }

    // --- Try to join any open session (atomic) ---
    // --- Try to join any open session (robust) ---
    // --- Try to join any open session (robust) ---
    private async Task<bool> TryJoinOpenSession()
    {
        string TAG = "[Matchmaker/Join]";
        try
        {
            Debug.Log($"{TAG} myUid={myUid ?? "<null>"}; query open sessions...");

            var query = FirebaseDatabase.DefaultInstance
                .GetReference(sessionsRoot)
                .OrderByChild("sessionOpen")
                .EqualTo(true)
                .LimitToFirst(25);

            var snap = await query.GetValueAsync();
            Debug.Log($"{TAG} snap.Exists={snap.Exists}, children={snap.ChildrenCount}");

            if (!snap.Exists)
            {
                Debug.Log($"{TAG} No open sessions.");
                return false;
            }

            // Пройдёмся по всем узлам и выведем их поля
            foreach (var child in snap.Children)
            {
                string id = child.Key;
                object openV = child.Child("sessionOpen").Value;
                object hostV = child.Child("hostUid").Value;
                object clientV = child.Child("clientUid").Value;

                Debug.Log($"{TAG} session[{id}] " +
                          $"sessionOpen={Val(openV)} hostUid={Val(hostV)} clientUid={Val(clientV)}");
            }

            // Кандидаты
            var candidates = new List<(string id, string host)>();
            foreach (var child in snap.Children)
            {
                string id = child.Key;
                string host = child.Child("hostUid").Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(id))
                {
                    Debug.Log($"{TAG} skip: empty id");
                    continue;
                }
                if (string.IsNullOrEmpty(host))
                {
                    Debug.Log($"{TAG} skip {id}: host is empty");
                    continue;
                }
                if (host == myUid)
                {
                    Debug.Log($"{TAG} skip {id}: my own session (host==me)");
                    continue;
                }

                candidates.Add((id, host));
            }

            Debug.Log($"{TAG} candidates count={candidates.Count}");
            if (candidates.Count == 0)
            {
                Debug.Log($"{TAG} Only my sessions found or none valid.");
                return false;
            }

            // Пробуем по очереди
            foreach (var (sessionId, hostUid) in candidates)
            {
                Debug.Log($"{TAG} try join session={sessionId} (host={hostUid}) ...");
                var sessionRef = FirebaseDatabase.DefaultInstance.GetReference($"{sessionsRoot}/{sessionId}");

                var txnSnap = await sessionRef.RunTransaction(mutable =>
                {
                    // ВНИМАНИЕ: это выполняется на ворк-потоке, но Debug.Log допустим
                    var dict = mutable.Value as Dictionary<string, object> ?? new Dictionary<string, object>();

                    bool open = dict.TryGetValue("sessionOpen", out var o) && o is bool b && b;
                    string host = dict.TryGetValue("hostUid", out var h) ? (h?.ToString() ?? "") : "";
                    string client = dict.TryGetValue("clientUid", out var c) ? (c?.ToString() ?? "") : "";

                    Debug.Log($"{TAG} TXN(before) id={sessionId} open={open} host={host} client={client}");

                    // нельзя подключаться к себе, к закрытой или уже с клиентом
                    if (!open || !string.IsNullOrEmpty(client) || string.IsNullOrEmpty(host) || host == myUid)
                    {
                        Debug.Log($"{TAG} TXN(abort) id={sessionId} reason=" +
                                  $"open={open}, client='{client}', host='{host}', myUid='{myUid}'");
                        return TransactionResult.Abort();
                    }

                    dict["clientUid"] = myUid;                // <-- ставим себя
                    dict["sessionOpen"] = false;                // <-- закрываем
                    dict["updatedAt"] = ServerValue.Timestamp;

                    mutable.Value = dict;
                    Debug.Log($"{TAG} TXN(commit) id={sessionId} set clientUid={myUid}, sessionOpen=false");
                    return TransactionResult.Success(mutable);
                });

                // Что получилось после транзакции
                string afterClient = txnSnap.Child("clientUid").Value?.ToString();
                object afterOpenV = txnSnap.Child("sessionOpen").Value;
                bool afterOpen = (afterOpenV is bool bo) && bo;

                Debug.Log($"{TAG} txn result id={sessionId}: clientUid={Val(afterClient)}, sessionOpen={Val(afterOpenV)}");

                bool success =
                    txnSnap != null &&
                    txnSnap.Exists &&
                    afterClient == myUid &&
                    afterOpen == false;

                if (success)
                {
                    Debug.Log($"{TAG} SUCCESS join session={sessionId} as Client.");
                    PersistContext(sessionId, /*isHost:*/ false);
                    FirebaseSessionManager.Instance?.Configure(sessionId, /*isHost:*/ false);
                    await GoNextScene();
                    return true;
                }

                Debug.Log($"{TAG} not joined {sessionId} (probably raced); trying next...");
            }

            Debug.Log($"{TAG} Could not join any candidate; will create my own.");
            return false;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{TAG} ERROR: {e}");
            return false;
        }
    }

    private static string Val(object v)
    {
        if (v == null) return "<null>";
        return $"({v.GetType().Name}) {v}";
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
            //{ "clientUid",   "" },
            { "createdAt",   ServerValue.Timestamp },
            { "updatedAt",   ServerValue.Timestamp }
        };

            await newRef.UpdateChildrenAsync(data);
            createdSessionId = sessionId;
            PersistContext(sessionId, /*isHost:*/ true);
            // ⬇️ ДОБАВЛЕНО: зарегистрировать сессию в менеджере (мы — хост)
            FirebaseSessionManager.Instance?.Configure(sessionId, /*isHost:*/ true);

            // UI wait
            SetWaiting(true, "Wait for joiners…");

            // Listen: when sessionOpen becomes false and clientUid present → go next
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
                    PersistContext(sessionId, /*isHost:*/ true);
                    // ⬇️ ОБЕЗОПАСИМСЯ: ещё раз конфигурируем перед переходом (на случай, если сцены грузятся быстро)
                    FirebaseSessionManager.Instance?.Configure(sessionId, /*isHost:*/ true);

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
    private void PersistContext(string sessionId, bool isHost)
    {
    // в оперативной памяти
    GameSession.SessionId = sessionId;
    GameSession.Role      = isHost ? "Host" : "Client";
    Globalflags.ifHost    = isHost;

    // в PlayerPrefs (на случай перезапуска)
    GameSession.Save(sessionId, isHost);
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
