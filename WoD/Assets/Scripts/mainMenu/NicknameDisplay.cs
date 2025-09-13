// Assets/Scripts/UI/NicknameDisplayRTDB.cs
using UnityEngine;
using TMPro;
using Firebase.Auth;
using Firebase.Database;

public class NicknameDisplayRTDB : MonoBehaviour
{
    [SerializeField] private TMP_Text nicknameText;     // перетащи сюда Text (TMP)
    [SerializeField] private string fallbackText = "Guest";

    private void Start()
    {
        if (nicknameText == null) nicknameText = GetComponent<TMP_Text>();
        LoadNickname();
    }

    private async void LoadNickname()
    {
        var user = FirebaseAuth.DefaultInstance.CurrentUser;
        if (user == null) { nicknameText.text = fallbackText; return; }

        // users/{uid}/nickname
        var refNick = FirebaseDatabase.DefaultInstance
            .GetReference($"users/{user.UserId}/nickname");

        try
        {
            var snap = await refNick.GetValueAsync();
            if (snap.Exists && snap.Value != null)
                nicknameText.text = snap.Value.ToString();
            else
                nicknameText.text = fallbackText;
        }
        catch
        {
            nicknameText.text = fallbackText;
        }
    }
}
