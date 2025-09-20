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

    [Header("Flow")]
    [SerializeField] private string nextSceneName = "BattleScene";
    [SerializeField] private string backSceneName = "MainMenu"; // сцена для кнопки Back

    private DatabaseReference root;
    private DatabaseReference sessionRef;
    private DatabaseReference myReadyRef;
    private DatabaseReference enemyReadyRef;

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

    private void Awake()
    {
        if (readyButton) readyButton.onClick.AddListener(OnReadyClicked);
        if (waitPanel) waitPanel.SetActive(false);
    }

    private async void Start()
    {
        // 1) Загрузка параметров сессии
        GameSession.Load();
        sessionId = GameSession.SessionId;
        isHost    = Globalflags.ifHost;

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

        myReadyRef    = sessionRef.Child(myBranch).Child("ready");
        enemyReadyRef = sessionRef.Child(enemyBranch).Child("ready");

        // 2) На старте гарантируем отсутствие "хвостов"
        await SafeRemove(myReadyRef);           // удаляем узел ready целиком
        _ = ArmOnDisconnectCleanup();           // включаем авто-чистку на разрыв

        // 3) Подтягиваем ник соперника
        _ = LoadEnemyIdentity();
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
    }

    private async void OnReadyClicked()
    {
        readyButton.interactable = false;

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
