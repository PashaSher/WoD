using System.Threading.Tasks;
using Firebase.Database;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class ReadyUpController : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Button readyButton;
    [SerializeField] private GameObject waitPanel;
    [SerializeField] private TextMeshProUGUI waitLabel;
    [SerializeField] private TextMeshProUGUI statusText; // сюда выводим причину, почему нельзя READY

    [Header("Enemy army visibility")]
    [SerializeField] private GameObject enemyArmyRoot;   // назначь в инспекторе при наличии
    [SerializeField] private bool hideEnemyArmyOnStart = true;
    [SerializeField] private bool showEnemyNickInsteadOfArmy = true;
    [SerializeField] private TMP_FontAsset enemyNickFont;
    [SerializeField] private int enemyNickFontSize = 42;
    [SerializeField] private Color enemyNickColor = new Color(1f, 1f, 0.2f, 1f);
    [SerializeField] private Vector3 enemyNickWorldOffset = new Vector3(0f, 1.2f, 0f);

    [Header("Auto-lose timer")]
    [SerializeField] private float readyTimeoutSeconds = 60f;
    [SerializeField] private TMP_FontAsset timerFont;
    [SerializeField] private int timerFontSize = 48;

    [Header("Flow")]
    [SerializeField] private string nextSceneName = "BattleScene";
    [SerializeField] private string backSceneName = "MainMenu"; // сцена для кнопки Back

    [Header("Services (optional)")]
    [SerializeField] private FirebaseArmyService firebase; // можно не указывать — используем прямой доступ к RTDB

    private DatabaseReference root;
    private DatabaseReference sessionRef;
    private DatabaseReference myReadyRef;
    private DatabaseReference enemyReadyRef;
    private DatabaseReference myArmyRef;

    private string sessionId;
    private bool isHost;
    private string myBranch;      // "hostArmy" | "clientArmy"
    private string enemyBranch;   // противоположная ветка
    private string myUidKey;      // "hostUid" | "clientUid"
    private string enemyUidKey;   // "clientUid" | "hostUid"

    private bool myReady;
    private bool enemyReady;
    private bool subscribed;
    private bool skipDestroyCleanup; // если выходим осознанно — не чистим повторно в OnDestroy

    private string enemyUid;
    private string enemyNick = ""; // реальный ник

    // timer overlay
    private Canvas timerCanvas;
    private TextMeshProUGUI timerText;
    private float countdown;
    private bool timerActive;

    // enemy nick world label
    private TextMeshPro enemyNickLabel3D;

    private void Awake()
    {
        if (readyButton) readyButton.onClick.AddListener(OnReadyClicked);
        if (waitPanel) waitPanel.SetActive(false);
        if (statusText) statusText.text = "";

        if (hideEnemyArmyOnStart) TryHideEnemyArmy();
        BuildTimerOverlay();
        countdown = Mathf.Max(1f, readyTimeoutSeconds);
        timerActive = true;
        UpdateTimerLabel();
    }

    private async void Start()
    {
        // 1) Загрузка параметров сессии
        GameSession.Load();
        sessionId = GameSession.SessionId;
        isHost    = Globalflags.ifHost;
        if (firebase == null)
        {
#if UNITY_2022_2_OR_NEWER
            firebase = FindFirstObjectByType<FirebaseArmyService>(FindObjectsInactive.Include);
#else
            firebase = Object.FindObjectOfType<FirebaseArmyService>(true);
#endif
        }

        if (string.IsNullOrEmpty(sessionId))
        {
            Debug.LogError("ReadyUpController: SessionId is empty. Call GameSession.Save(...) first.");
            ShowWait(true, "Session not found");
            return;
        }

        myBranch     = isHost ? "hostArmy"   : "clientArmy";
        enemyBranch  = isHost ? "clientArmy" : "hostArmy";
        myUidKey     = isHost ? "hostUid"    : "clientUid";
        enemyUidKey  = isHost ? "clientUid"  : "hostUid";

        root       = FirebaseDatabase.DefaultInstance.RootReference;
        sessionRef = root.Child("sessions").Child(sessionId);
        myArmyRef  = sessionRef.Child(myBranch);

        myReadyRef    = sessionRef.Child(myBranch).Child("ready");
        enemyReadyRef = sessionRef.Child(enemyBranch).Child("ready");

        // 2) На старте гарантируем отсутствие "хвостов"
        await SafeRemove(myReadyRef);           // удаляем узел ready целиком
        _ = ArmOnDisconnectCleanup();           // включаем авто-чистку на разрыв

        // 3) Подтягиваем ник соперника
        _ = LoadEnemyIdentity();

        // 4) Следим за количеством активных юнитов и блокируем READY при 0
        await RefreshReadyAvailabilityAsync();
        if (myArmyRef != null)
        {
            myArmyRef.ValueChanged += OnMyArmyChanged;
        }
    }

    private void Update()
    {
        if (!timerActive) return;
        if (myReady) { StopTimer(); return; }

        countdown = Mathf.Max(0f, countdown - Time.deltaTime);
        UpdateTimerLabel();
        if (countdown <= 0f)
        {
            timerActive = false;
            _ = AutoLoseForNoReadyAsync();
        }
    }

    private async void OnMyArmyChanged(object sender, ValueChangedEventArgs e)
    {
        await RefreshReadyAvailabilityAsync();
    }

    private static readonly System.Collections.Generic.HashSet<UnitType> PassiveTypes
        = new System.Collections.Generic.HashSet<UnitType>
        {
            UnitType.Wall, UnitType.BarbedWire, UnitType.TankTrap, UnitType.Sandbags, UnitType.Barrier
        };

    private async System.Threading.Tasks.Task RefreshReadyAvailabilityAsync()
    {
        try
        {
            int activeCount = 0;
            // читаем армию и считаем НЕ пассивные типы
            var snap = await sessionRef.Child(myBranch).GetValueAsync();
            if (snap.Exists)
            {
                foreach (var child in snap.Children)
                {
                    if (child.HasChild("type"))
                    {
                        var tStr = child.Child("type").Value?.ToString();
                        if (!string.IsNullOrEmpty(tStr) && System.Enum.TryParse<UnitType>(tStr, out var t))
                        {
                            if (!PassiveTypes.Contains(t)) activeCount++;
                        }
                    }
                }
            }
            bool allowReady = activeCount > 0;
            if (readyButton) readyButton.interactable = allowReady;
            if (!allowReady && statusText) statusText.text = "Купите хотя бы один АКТИВНЫЙ юнит, чтобы нажать READY";
            if (allowReady && statusText) statusText.text = "";
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("RefreshReadyAvailability failed: " + ex.Message);
        }
    }

    private void CreateEnemyNickLabelAt(Vector3 worldPos)
    {
        try
        {
            if (!showEnemyNickInsteadOfArmy) return;
            if (enemyNickLabel3D != null) return;
            var go = new GameObject("EnemyNickLabel");
            go.transform.position = worldPos + enemyNickWorldOffset;
            enemyNickLabel3D = go.AddComponent<TextMeshPro>();
            if (enemyNickFont != null) enemyNickLabel3D.font = enemyNickFont;
            enemyNickLabel3D.fontSize = Mathf.Max(10, enemyNickFontSize);
            enemyNickLabel3D.color = enemyNickColor;
            enemyNickLabel3D.alignment = TextAlignmentOptions.Center;
            enemyNickLabel3D.text = string.IsNullOrWhiteSpace(enemyNick) ? "opponent" : enemyNick;

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) { mr.sortingOrder = 200; }
        }
        catch { /* best-effort */ }
    }

    private void UpdateEnemyNickLabel()
    {
        if (enemyNickLabel3D != null)
            enemyNickLabel3D.text = string.IsNullOrWhiteSpace(enemyNick) ? "opponent" : enemyNick;
    }

    private void BuildTimerOverlay()
    {
        var go = new GameObject("ReadyTimerOverlay");
        timerCanvas = go.AddComponent<Canvas>();
        timerCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        go.AddComponent<GraphicRaycaster>();

        var tgo = new GameObject("ReadyTimerText");
        tgo.transform.SetParent(go.transform, false);
        timerText = tgo.AddComponent<TextMeshProUGUI>();
        timerText.alignment = TextAlignmentOptions.Center;
        timerText.fontSize = Mathf.Max(10, timerFontSize);
        if (timerFont != null) timerText.font = timerFont;
        // Цвет по роли: HOST — чёрный, CLIENT — синий
        timerText.color = Globalflags.ifHost ? Color.black : Color.blue;
        timerText.raycastTarget = false;
        var rt = (RectTransform)timerText.transform;
        rt.anchorMin = new Vector2(0.35f, 0.90f);
        rt.anchorMax = new Vector2(0.65f, 0.995f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private void UpdateTimerLabel()
    {
        if (!timerText) return;
        int secs = Mathf.CeilToInt(countdown);
        int m = secs / 60;
        int s = secs % 60;
        timerText.text = string.Format("{0:00}:{1:00}", m, s);
    }

    private void StopTimer()
    {
        timerActive = false;
        if (timerCanvas) timerCanvas.enabled = false;
    }

    private void TryHideEnemyArmy()
    {
        try
        {
            if (enemyArmyRoot != null)
            {
                Vector3 pos = enemyArmyRoot.transform.position;
                enemyArmyRoot.SetActive(false);
                CreateEnemyNickLabelAt(pos);
                return;
            }
            var go1 = GameObject.Find("AnemyArmy");
            if (go1 != null)
            {
                Vector3 pos = go1.transform.position;
                go1.SetActive(false);
                CreateEnemyNickLabelAt(pos);
                return;
            }
            var go2 = GameObject.Find("EnemyArmy");
            if (go2 != null)
            {
                Vector3 pos = go2.transform.position;
                go2.SetActive(false);
                CreateEnemyNickLabelAt(pos);
                return;
            }
        }
        catch { /* ignore */ }
    }

    // Загружает enemyUid и enemyNick (users/.../nickname, fallback: sessions/.../nickname)
    private async Task LoadEnemyIdentity()
    {
        try
        {
            var uidSnap = await sessionRef.Child(enemyBranch).Child(enemyUidKey).GetValueAsync();
            if (uidSnap.Exists) enemyUid = uidSnap.Value?.ToString();

            // 1) users/{uid}/nickname
            if (!string.IsNullOrEmpty(enemyUid))
            {
                var nickSnap = await root.Child("users").Child(enemyUid).Child("nickname").GetValueAsync();
                if (nickSnap.Exists && !string.IsNullOrWhiteSpace(nickSnap.Value?.ToString()))
                    enemyNick = nickSnap.Value.ToString();
            }

            // 2) fallback: sessions/{sid}/{enemyBranch}/nickname
            if (string.IsNullOrWhiteSpace(enemyNick))
            {
                var nick2 = await sessionRef.Child(enemyBranch).Child("nickname").GetValueAsync();
                if (nick2.Exists && !string.IsNullOrWhiteSpace(nick2.Value?.ToString()))
                    enemyNick = nick2.Value.ToString();
            }

            if (string.IsNullOrWhiteSpace(enemyNick))
                enemyNick = "player";
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("LoadEnemyIdentity failed: " + ex.Message);
            if (string.IsNullOrWhiteSpace(enemyNick)) enemyNick = "player";
        }
        UpdateEnemyNickLabel();
    }

    private async void OnReadyClicked()
    {
        readyButton.interactable = false;
        StopTimer(); // нажал READY — авто-поражение не сработает

        // 1) Ставим свой ready = true (создаём узел)
        myReady = true;
        await SafeSetBool(myReadyRef, true);

        // 2) Проверим врага разово
        var snap = await enemyReadyRef.GetValueAsync();
        enemyReady = snap.Exists && snap.Value is bool eb && eb;

        if (enemyReady)
        {
            ProceedIfBothReady();
            return;
        }

        // 3) Ждём соперника
        ShowWait(true, $"Wait for {enemyNick}…");
        SubscribeEnemyReady();
    }

    private async Task AutoLoseForNoReadyAsync()
    {
        // Не нажал READY — считаемся проигравшими, +1 победа сопернику (если знаем его uid), закрываем сессию
        try
        {
            // Отметим победителя в сессии, чтобы второму клиенту показать корректный итог
            try
            {
                if (sessionRef != null)
                {
                    string winRole = isHost ? "client" : "host";
                    var updates = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["winnerRole"] = winRole,
                        ["endReason"] = "not_ready_timeout",
                        ["sessionOpen"] = false
                    };
                    await sessionRef.UpdateChildrenAsync(updates);
                }
            }
            catch (System.Exception ex) { Debug.LogWarning("AutoLose: mark winner failed: " + ex.Message); }

            if (!string.IsNullOrEmpty(enemyUid))
            {
                var winsRef = root.Child("users").Child(enemyUid).Child("wins");
                await winsRef.RunTransaction(mutable =>
                {
                    long cur = 0;
                    try
                    {
                        if (mutable.Value is long l) cur = l;
                        else if (mutable.Value is int i) cur = i;
                        else if (mutable.Value is string s && long.TryParse(s, out var ls)) cur = ls;
                    }
                    catch { cur = 0; }
                    mutable.Value = cur + 1;
                    return TransactionResult.Success(mutable);
                });
            }
        }
        catch (System.Exception ex) { Debug.LogWarning("AutoLose: wins update failed: " + ex.Message); }

        try
        {
            if (FirebaseSessionManager.Instance != null)
                await FirebaseSessionManager.Instance.LeaveSessionAndGoToMenuAsync();
            else
                SceneManager.LoadScene("MainMenu");
        }
        catch (System.Exception ex) { Debug.LogWarning("AutoLose: leave failed: " + ex.Message); }
    }

    private void SubscribeEnemyReady()
    {
        if (subscribed) return;
        subscribed = true;
        enemyReadyRef.ValueChanged += OnEnemyReadyChanged;
    }

    private void OnEnemyReadyChanged(object sender, ValueChangedEventArgs e)
    {
        if (!e.Snapshot.Exists) return;

        if (e.Snapshot.Value is bool b)
        {
            enemyReady = b;
            if (enemyReady)
            {
                enemyReadyRef.ValueChanged -= OnEnemyReadyChanged;
                subscribed = false;
                ProceedIfBothReady();
            }
        }
    }

    private async void ProceedIfBothReady()
    {
        if (!(myReady && enemyReady)) return;

        ShowWait(false, "");
        // Сцена боя: перед переходом отменим onDisconnect и не оставим хвостов
        await CancelOnDisconnect();
        skipDestroyCleanup = true;          // чтобы OnDestroy не трогал
        SceneManager.LoadScene(nextSceneName);
    }

    public async void OnBack() // повесь на кнопку Back
    {
        // Чистим свой ready и отменяем onDisconnect, затем уходим
        await CancelOnDisconnect();
        await SafeRemove(myReadyRef);
        skipDestroyCleanup = true;

        SceneManager.LoadScene(backSceneName);
    }

    // ===== UI =====
    private void ShowWait(bool show, string text)
    {
        if (waitPanel) waitPanel.SetActive(show);
        if (waitLabel) waitLabel.text = text;
    }

    // ===== Безопасные операции с RTDB =====
    private async Task SafeSetBool(DatabaseReference r, bool value)
    {
        if (r == null) return;
        try { await r.SetValueAsync(value); }
        catch (System.Exception ex) { Debug.LogWarning($"RTDB set failed: {ex.Message}"); }
    }

    private async Task SafeRemove(DatabaseReference r)
    {
        if (r == null) return;
        try { await r.RemoveValueAsync(); }
        catch (System.Exception ex) { Debug.LogWarning($"RTDB remove failed: {ex.Message}"); }
    }

    private async Task ArmOnDisconnectCleanup()
    {
        try { await myReadyRef.OnDisconnect().RemoveValue(); }
        catch (System.Exception ex) { Debug.LogWarning("OnDisconnect arm failed: " + ex.Message); }
    }

    private async Task CancelOnDisconnect()
    {
        try { await myReadyRef.OnDisconnect().Cancel(); }
        catch (System.Exception ex) { Debug.LogWarning("OnDisconnect cancel failed: " + ex.Message); }
    }

    // ===== Жизненный цикл =====
    private async void OnDestroy()
    {
        if (subscribed && enemyReadyRef != null)
            enemyReadyRef.ValueChanged -= OnEnemyReadyChanged;
        if (myArmyRef != null) myArmyRef.ValueChanged -= OnMyArmyChanged;

        if (!skipDestroyCleanup && myReadyRef != null)
        {
            // Если уходим неожиданно, удалим свой ready (а onDisconnect подстрахует при разрыве)
            if (myReady) await SafeRemove(myReadyRef);
        }

        if (readyButton) readyButton.onClick.RemoveListener(OnReadyClicked);
    }

    private async void OnApplicationQuit()
    {
        // Доп. страховка при закрытии приложения
        try { await SafeRemove(myReadyRef); } catch {}
        try { await CancelOnDisconnect(); } catch {}
    }
}
