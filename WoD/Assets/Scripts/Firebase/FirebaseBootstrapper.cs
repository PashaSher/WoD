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

        // Если нужно — раскомментируй и перенеси сюда настройки ДО первого доступа к DefaultInstance:
        // using Firebase.Database;
        // Firebase.Database.FirebaseDatabase.GetInstance(FirebaseApp.DefaultInstance).SetPersistenceEnabled(false);
    }
}
