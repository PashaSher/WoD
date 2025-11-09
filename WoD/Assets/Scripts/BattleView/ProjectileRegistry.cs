using System.Collections.Generic;

public static class ProjectileRegistry
{
    private static readonly Dictionary<string, Projectile> _byKey = new Dictionary<string, Projectile>();
    private static readonly HashSet<string> _createdLocally = new HashSet<string>();

    public static bool Contains(string key)
    {
        return !string.IsNullOrEmpty(key) && _byKey.ContainsKey(key);
    }

    public static void Register(string key, Projectile projectile, bool createdLocally)
    {
        if (string.IsNullOrEmpty(key) || projectile == null) return;
        _byKey[key] = projectile;
        if (createdLocally) _createdLocally.Add(key);
    }

    public static bool WasCreatedLocally(string key)
    {
        return !string.IsNullOrEmpty(key) && _createdLocally.Contains(key);
    }

    public static void Unregister(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _byKey.Remove(key);
        _createdLocally.Remove(key);
    }
}







