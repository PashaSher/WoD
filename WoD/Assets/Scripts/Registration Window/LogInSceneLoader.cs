using UnityEngine;
using UnityEngine.SceneManagement; // для работы со сценами

public class LoginSceneLoader : MonoBehaviour
{
    // функция для кнопки "Create Account"
    public void LoadCreateAccountScene()
    {
        SceneManager.LoadScene("LogInScene"); // имя должно совпадать с названием сцены в папке Scenes
    }
}
