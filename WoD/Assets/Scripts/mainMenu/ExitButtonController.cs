// Assets/Scripts/mainMenu/ExitButtonController.cs
using UnityEngine;

public class ExitButtonController : MonoBehaviour
{
    public void ExitGame()
    {
        // В билде (Windows/Android/etc.)
        Application.Quit();

        // В редакторе Unity Application.Quit не сработает — остановим Play Mode
        #if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
        #endif
    }
}
