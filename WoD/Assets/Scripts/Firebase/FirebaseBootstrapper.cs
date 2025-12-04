using System;
using System.Threading.Tasks;
using Firebase;

public static class FirebaseBootstrapper
{
    private static Task _initTask;
    private static readonly object _lock = new object();

    public static Task EnsureInitializedAsync()
    {
        lock (_lock)
        {
            if (_initTask != null) return _initTask;
            _initTask = InitializeInternalAsync();
            return _initTask;
        }
    }

    private static async Task InitializeInternalAsync()
    {
        var deps = await FirebaseApp.CheckAndFixDependenciesAsync();
        if (deps != DependencyStatus.Available)
            throw new Exception($"Firebase dependencies not available: {deps}");

        // Максимальный уровень логирования Firebase (поможет понять сетевые подвисания)
        try { FirebaseApp.LogLevel = LogLevel.Debug; } catch { /* ignore */ }

        // В Editor отключаем офлайн‑персистентность RTDB, чтобы избежать "липких" таймаутов/гонок при перезапусках сцен.
#if UNITY_EDITOR
        try
        {
            Firebase.Database.FirebaseDatabase.GetInstance(FirebaseApp.DefaultInstance).SetPersistenceEnabled(false);
        }
        catch { /* best-effort */ }
#endif
    }
}
