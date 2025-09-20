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

    /// <summary> Добавить юнита: создаёт индексированное имя type_i </summary>
    public async Task<string> AddUnitAsync(UnitType type)
    {
        Debug.Log($"[FAS] AddUnit to '{ArmyPath}' type={type}");
        // читаем текущее число, чтобы выдать следующий индекс
        var snap = await FirebaseDatabase.DefaultInstance
            .GetReference(ArmyPath)
            .GetValueAsync();

        int nextIndex = 0;
        if (snap.Exists)
        {
            foreach (var child in snap.Children)
            {
                // child.Key format: sniper_0 etc.
                if (child.Key.StartsWith(type.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    var parts = child.Key.Split('_');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int idx))
                        nextIndex = Math.Max(nextIndex, idx + 1);
                }
            }
        }

        string key = $"{type}_{nextIndex}";
        var unit = new Dictionary<string, object>
        {
            { "type", type.ToString() },
            { "createdAt", ServerValue.Timestamp }
        };

        await Root.Child(ArmyPath).Child(key).SetValueAsync(unit);
        await UpdateUpdatedAt();
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

        var snap = await Root.Child(ArmyPath).GetValueAsync();
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

    /// <summary> Подписка на изменения армии (для live-обновления UI) </summary>
    public void ListenArmyChanges(Action onChanged)
    {
        // отписка на всякий случай
        StopArmyChanges();

        _armyRef = FirebaseDatabase.DefaultInstance.GetReference(ArmyPath);
        _armyHandler = (s, e) =>
        {
            // можно оставить лог для дебага
            Debug.Log($"[FAS] Army ValueChanged exists={e.Snapshot != null && e.Snapshot.Exists}");
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
            _armyHandler = null;
            _armyRef = null;
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
