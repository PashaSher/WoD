using System;
using Firebase;

/// <summary>
/// Sets a deterministic DatabaseUrl for Firebase BEFORE any Database DefaultInstance is created.
/// Ensures all platforms (Editor/Android) use the same RTDB endpoint.
/// </summary>
public static class FirebaseConfigInitializer
{
	// IMPORTANT: set to your RTDB URL (from Firebase Console → Realtime Database → Data)
	// Example: https://your-project-default-rtdb.firebasedatabase.app
	private const string DatabaseUrl = "https://war-of-drawings-default-rtdb.firebasedatabase.app";

	[UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void ApplyDatabaseUrl()
	{
		try
		{
			var app = FirebaseApp.DefaultInstance;
			if (app != null && !string.IsNullOrWhiteSpace(DatabaseUrl))
			{
				var current = app.Options.DatabaseUrl?.ToString() ?? "<null>";
				// Only set if different, and as early as possible
				if (!string.Equals(current, DatabaseUrl, StringComparison.OrdinalIgnoreCase))
				{
					app.Options.DatabaseUrl = new Uri(DatabaseUrl);
				}
			}
		}
		catch
		{
			// best-effort; if DefaultInstance not ready yet, Bootstrapper will still use project defaults
		}
	}
}


