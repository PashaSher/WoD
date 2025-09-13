using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class FirebaseSessionUIBinder : MonoBehaviour
{
    [SerializeField] private GameObject panel;
    [SerializeField] private TMP_Text text;
    [SerializeField] private Button toMenu;

    void Start()
    {
        if (FirebaseSessionManager.Instance != null)
            FirebaseSessionManager.Instance.BindUI(panel, text, toMenu);
    }
}
