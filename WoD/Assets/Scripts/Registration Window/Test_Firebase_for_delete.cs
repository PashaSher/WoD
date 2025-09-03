using System;
using System.Collections.Generic;
using Firebase;
using Firebase.Auth;
using Firebase.Database;
using Firebase.Functions;
using Firebase.Extensions;
using UnityEngine;

public class FirebaseSanity : MonoBehaviour
{
    void Start()
    {
        FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(t =>
        {
            if (t.Result != DependencyStatus.Available)
            {
                Debug.LogError("Firebase deps: " + t.Result);
                return;
            }

            Debug.Log("Firebase deps OK");
            var auth = FirebaseAuth.DefaultInstance;

            // 1) Auth (anonymous)
            auth.SignInAnonymouslyAsync().ContinueWithOnMainThread(authTask =>
            {
                if (authTask.IsFaulted)
                {
                    Debug.LogException(authTask.Exception);
                    return;
                }

                Debug.Log("Auth OK, uid=" + auth.CurrentUser.UserId);

                // 2) Realtime DB: write + read back
                var db = FirebaseDatabase.DefaultInstance;
                string path = "smoke_test/" + SystemInfo.deviceUniqueIdentifier;
                string stamp = DateTime.UtcNow.ToString("o");

                db.RootReference.Child(path).SetValueAsync(stamp).ContinueWithOnMainThread(w =>
                {
                    if (w.IsFaulted) { Debug.LogException(w.Exception); return; }
                    Debug.Log("DB write OK: " + path + " = " + stamp);

                    db.RootReference.Child(path).GetValueAsync().ContinueWithOnMainThread(r =>
                    {
                        if (r.IsFaulted) { Debug.LogException(r.Exception); return; }
                        Debug.Log("DB read OK: " + r.Result?.Value);
                    });
                });

                // 3) (опционально) Cloud Functions: ping
                try
                {
                    var functions = FirebaseFunctions.DefaultInstance;
                    functions.GetHttpsCallable("ping")
                        .CallAsync(new Dictionary<string, object> { { "t", stamp } })
                        .ContinueWithOnMainThread(f =>
                        {
                            if (f.IsFaulted) Debug.LogWarning("Functions ping skipped: " + f.Exception?.Message);
                            else Debug.Log("Functions OK: " + (f.Result.Data ?? "null"));
                        });
                }
                catch (Exception e) { Debug.LogWarning("Functions not set up: " + e.Message); }
            });
        });
    }
}
