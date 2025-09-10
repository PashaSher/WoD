using UnityEngine;
using UnityEngine.SceneManagement; // для работы со сценами

public class SceneLoader : MonoBehaviour
{
    // функция для кнопки "Create Account"
    public void LoadCreateAccountScene()
    {
        SceneManager.LoadScene("CreateAccountWindow"); // имя должно совпадать с названием сцены в папке Scenes
    }
}
