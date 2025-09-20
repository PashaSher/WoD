using UnityEngine;

public static class GameSession
{
    public static string SessionId;
    public static string Role; // "Host" | "Client"

    public static void Save(string sessionId, bool isHost, string role = "")
    {
        SessionId = sessionId;
        Role = string.IsNullOrEmpty(role) ? (isHost ? "Host" : "Client") : role;

        PlayerPrefs.SetString("currentSessionId", SessionId ?? "");
        PlayerPrefs.SetInt("currentIfHost", isHost ? 1 : 0);
        PlayerPrefs.SetString("currentRole", Role);
        PlayerPrefs.Save();
    }

    public static void Load()
    {
        SessionId = PlayerPrefs.GetString("currentSessionId", "");
        Globalflags.ifHost = PlayerPrefs.GetInt("currentIfHost", 0) == 1;
        Role = PlayerPrefs.GetString("currentRole", Globalflags.ifHost ? "Host" : "Client");
    }

    public static void Clear()
    {
        SessionId = "";
        Role = "";
        Globalflags.ifHost = false;
        PlayerPrefs.DeleteKey("currentSessionId");
        PlayerPrefs.DeleteKey("currentIfHost");
        PlayerPrefs.DeleteKey("currentRole");
        PlayerPrefs.Save();
    }
}
