using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

public class BackToMainMenu : MonoBehaviour
{
    [SerializeField] private string mainMenuSceneName = "MainMenu";

    public async void OnBackClicked()
    {
        if (FirebaseSessionManager.Instance != null)
            await FirebaseSessionManager.Instance.LeaveSessionAsync();

        SceneManager.LoadScene(mainMenuSceneName);
    }
}
