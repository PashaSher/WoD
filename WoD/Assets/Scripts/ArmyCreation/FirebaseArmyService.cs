// FirebaseArmyService.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Database;
using UnityEngine;


public class FirebaseArmyService : MonoBehaviour
{
    [Header("Session / Host-Client")]
    [SerializeField] private string sessionId; // заполни из твоей логики
    [SerializeField] private bool ifHost = true; // Globalflags.ifHost можно пробросить сюда

    public string SessionId
    {
        get => sessionId;
        set => sessionId = value;
    }

    public bool IfHost
    {
        get => ifHost;
        set => ifHost = value;
    }
    public string OpponentArmyPath =>
     $"sessions/{sessionId}/{(ifHost ? "clientArmy" : "hostArmy")}";
    private DatabaseReference Root => FirebaseDatabase.DefaultInstance.RootReference;

    private string ArmyPath =>
        $"sessions/{sessionId}/{(ifHost ? "hostArmy" : "clientArmy")}";

    private DatabaseReference _opponentRef;
    private EventHandler<ValueChangedEventArgs> _opponentHandler;
    private DatabaseReference _armyRef;
    private EventHandler<ValueChangedEventArgs> _armyHandler;

    private DatabaseReference _enemyRef;
    private EventHandler<ValueChangedEventArgs> _enemyHandler;

    // Connection diagnostics
    private DatabaseReference _infoConnectedRef;
    private EventHandler<ValueChangedEventArgs> _infoConnectedHandler;

    /// <summary> Добавить юнита: создаёт индексированное имя type_i </summary>
    public async Task<string> AddUnitAsync(UnitType type)
{
    Debug.Log($"[FAS] AddUnit to '{ArmyPath}' type={type} (sid='{sessionId}', ifHost={ifHost})");

    // На всякий случай убеждаемся, что соединение не в оффлайне (актуально для Editor)
    try { FirebaseDatabase.DefaultInstance.GoOnline(); Debug.Log("[FAS] GoOnline()"); } catch (Exception ex) { Debug.LogWarning($"[FAS] GoOnline failed: {ex.Message}"); }

    var sw = System.Diagnostics.Stopwatch.StartNew();

    // 1) Читаем армию с таймаутом, чтобы видеть зависания в Editor
    var getArmyTask = FirebaseDatabase.DefaultInstance.GetReference(ArmyPath).GetValueAsync();
    var getArmyFinished = await System.Threading.Tasks.Task.WhenAny(getArmyTask, System.Threading.Tasks.Task.Delay(5000));
    DataSnapshot snap = null;
    if (getArmyFinished == getArmyTask)
    {
        snap = await getArmyTask;
        Debug.Log($"[FAS] Current army exists={snap.Exists}, children={(snap.Exists ? (int)snap.ChildrenCount : 0)} at '{ArmyPath}' (dt={sw.ElapsedMilliseconds}ms)");
    }
    else
    {
        Debug.LogWarning($"[FAS] GetValueAsync('{ArmyPath}') timeout after {sw.ElapsedMilliseconds}ms — will proceed with index=0");
        snap = null;
    }

    int nextIndex = 0;
    if (snap != null && snap.Exists)
    {
        foreach (var child in snap.Children)
        {
            if (child.Key.StartsWith(type.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                var parts = child.Key.Split('_');
                if (parts.Length == 2 && int.TryParse(parts[1], out int idx))
                    nextIndex = Math.Max(nextIndex, idx + 1);
            }
        }
    }

    // Если чтение армии не удалось (timeout), используем уникальный ключ, чтобы не затирать существующие записи.
    // Иначе — привычный формат type_i.
    string key;
    if (snap == null)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rnd = new System.Random().Next(100, 999);
        key = $"{type}_{ts}_{rnd}";
        Debug.Log($"[FAS] Using unique fallback key '{key}' due to snapshot timeout");
    }
    else
    {
        key = $"{type}_{nextIndex}";
    }
    var unit = new Dictionary<string, object>
    {
        { "type", type.ToString() },
        { "createdAt", ServerValue.Timestamp },
        { "sessionId", sessionId },   // <-- НОВОЕ
        { "host", ifHost }            // <-- НОВОЕ (владелец-сторона)
    };

    // 2) Пишем юнита с таймаутом и подробным временем
    sw.Restart();
    var setTask = Root.Child(ArmyPath).Child(key).SetValueAsync(unit);
    var setFinished = await System.Threading.Tasks.Task.WhenAny(setTask, System.Threading.Tasks.Task.Delay(5000));
    if (setFinished != setTask)
    {
        Debug.LogWarning($"[FAS] SetValueAsync('{ArmyPath}/{key}') timeout after 5000ms");
        // дождёмся ошибки, чтобы всплыла в catch вызывающему коду
        await setTask;
    }
    Debug.Log($"[FAS] SetValueAsync done for '{ArmyPath}/{key}' (dt={sw.ElapsedMilliseconds}ms)");
    await UpdateUpdatedAt();
    Debug.Log("[FAS] updatedAt pushed");

    // Подтвердим запись и выведем лог (для отладки проблем в Editor)
    try
    {
        sw.Restart();
        var confirmTask = Root.Child(ArmyPath).Child(key).GetValueAsync();
        var confirmFinished = await System.Threading.Tasks.Task.WhenAny(confirmTask, System.Threading.Tasks.Task.Delay(5000));
        if (confirmFinished == confirmTask)
        {
            var confirm = await confirmTask;
            Debug.Log($"[FAS] Confirm add '{key}': exists={confirm.Exists} path='/{ArmyPath}/{key}' (dt={sw.ElapsedMilliseconds}ms)");
        }
        else
        {
            Debug.LogWarning($"[FAS] Confirm add '{key}' timeout after 5000ms");
        }
    }
    catch (Exception ex) { Debug.LogWarning($"[FAS] Confirm add failed: {ex.Message}"); }
    return key;
}


    public async Task<Dictionary<UnitType, int>> GetEnemyCountsAsync()
    {
        var result = new Dictionary<UnitType, int>();
        foreach (UnitType t in Enum.GetValues(typeof(UnitType))) result[t] = 0;

        var snap = await Root.Child(OpponentArmyPath).GetValueAsync();
        if (!snap.Exists) return result;

        foreach (var child in snap.Children)
        {
            if (child.HasChild("type"))
            {
                var tStr = child.Child("type").Value?.ToString();
                if (Enum.TryParse<UnitType>(tStr, out var t))
                    result[t]++;
            }
        }
        return result;
    }

    private void Awake()
    {
        // Если Matchmaker уже сохранил контекст — подцепим его
        GameSession.Load();
        if (!string.IsNullOrEmpty(GameSession.SessionId))
            SessionId = GameSession.SessionId;

        IfHost = Globalflags.ifHost;

        Debug.Log($"[FAS] Awake: sessionId='{sessionId}', IfHost={ifHost}");

        // Диагностика соединения с RTDB
        try
        {
            _infoConnectedRef = FirebaseDatabase.DefaultInstance.GetReference(".info/connected");
            _infoConnectedHandler = (s, e) =>
            {
                bool connected = false;
                try { connected = e.Snapshot != null && e.Snapshot.Value is bool b && b; } catch { connected = false; }
                Debug.Log($"[FAS] .info/connected = {connected}");
            };
            _infoConnectedRef.ValueChanged += _infoConnectedHandler;
        }
        catch (Exception ex) { Debug.LogWarning($"[FAS] .info/connected subscribe failed: {ex.Message}"); }
        Debug.Log($"[FAS] ArmyPath={ArmyPath}");
        Debug.Log($"[FAS] OpponentArmyPath={OpponentArmyPath}");
    }

    /// <summary> Удаляет последний (с максимальным индексом) юнит данного типа </summary>
    public async Task<string> RemoveUnitAsync(UnitType type)
    {
        Debug.Log($"[FAS] RemoveUnit from '{ArmyPath}' type={type}");
        var refPath = Root.Child(ArmyPath);
        var snap = await refPath.GetValueAsync();
        if (!snap.Exists) return string.Empty;

        string lastKey = string.Empty;
        int lastIdx = -1;

        foreach (var child in snap.Children)
        {
            if (child.Key.StartsWith(type.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                var parts = child.Key.Split('_');
                if (parts.Length == 2 && int.TryParse(parts[1], out int idx))
                {
                    if (idx > lastIdx)
                    {
                        lastIdx = idx;
                        lastKey = child.Key;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(lastKey)) return string.Empty;

        await refPath.Child(lastKey).RemoveValueAsync();
        await UpdateUpdatedAt();
        return lastKey;
    }

    /// <summary> Считает текущие количества по типам </summary>
    public async Task<Dictionary<UnitType, int>> GetCountsAsync()
    {
        var result = new Dictionary<UnitType, int>();
        foreach (UnitType t in Enum.GetValues(typeof(UnitType)))
            result[t] = 0;

        Debug.Log($"[FAS] GetCountsAsync from '{ArmyPath}'");
        var snap = await Root.Child(ArmyPath).GetValueAsync();
        Debug.Log($"[FAS] GetCountsAsync snap.Exists={snap.Exists}, children={(snap.Exists ? (int)snap.ChildrenCount : 0)}");
        if (!snap.Exists) return result;

        foreach (var child in snap.Children)
        {
            if (child.HasChild("type"))
            {
                var tStr = child.Child("type").Value?.ToString();
                if (Enum.TryParse<UnitType>(tStr, out var t))
                    result[t]++;
            }
        }
        Debug.Log($"[FAS] GetCountsAsync result: " +
            $"Rifleman={result[UnitType.Rifleman]}, Grenader={result[UnitType.Grenader]}, " +
            $"Sniper={result[UnitType.Sniper]}, Tank={result[UnitType.Tank]}");
        return result;
    }

    /// <summary> Подписка на изменения армии (для live-обновления UI) </summary>
    public void ListenArmyChanges(Action onChanged)
    {
        // отписка на всякий случай
        StopArmyChanges();

        _armyRef = FirebaseDatabase.DefaultInstance.GetReference(ArmyPath);
        _armyHandler = (s, e) =>
        {
            // можно оставить лог для дебага
            var exists = e.Snapshot != null && e.Snapshot.Exists;
            int children = exists ? (int)e.Snapshot.ChildrenCount : 0;
            // выведем список ключей для наглядности
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (exists)
            {
                foreach (var ch in e.Snapshot.Children)
                {
                    try { sb.Append(ch.Key).Append(' '); } catch { }
                }
            }
            Debug.Log($"[FAS] Army ValueChanged at '{ArmyPath}': exists={exists}, children={children}, keys=[{sb}]");
            onChanged?.Invoke();
        };
        _armyRef.ValueChanged += _armyHandler;
    }

    private async Task UpdateUpdatedAt()
    {
        await Root.Child($"sessions/{sessionId}/updatedAt")
            .SetValueAsync(ServerValue.Timestamp);
    }

    public void StopArmyChanges()
    {
        if (_armyRef != null && _armyHandler != null)
        {
            _armyRef.ValueChanged -= _armyHandler;
            Debug.Log("[FAS] StopArmyChanges()");
            _armyHandler = null;
            _armyRef = null;
        }
        if (_infoConnectedRef != null && _infoConnectedHandler != null)
        {
            _infoConnectedRef.ValueChanged -= _infoConnectedHandler;
            _infoConnectedHandler = null;
            _infoConnectedRef = null;
        }
    }

    public void StopEnemyChanges()
    {
        if (_enemyRef != null && _enemyHandler != null)
        {
            _enemyRef.ValueChanged -= _enemyHandler;
            _enemyHandler = null;
            _enemyRef = null;
        }
    }
    public void ListenOpponentChanges(Action onChanged)
    {
      StopOpponentChanges();

      _opponentRef = FirebaseDatabase.DefaultInstance.GetReference(OpponentArmyPath);
      _opponentHandler = (s, e) => { onChanged?.Invoke(); };
      _opponentRef.ValueChanged += _opponentHandler;
    }
    
    public void StopOpponentChanges()
    {
      if (_opponentRef != null && _opponentHandler != null)
      {
        _opponentRef.ValueChanged -= _opponentHandler;
        _opponentRef = null;
        _opponentHandler = null;
      }
   }
    private void OnDestroy()
    {
        StopArmyChanges();
        StopEnemyChanges();
    }
}
