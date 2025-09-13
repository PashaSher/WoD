using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem; // New Input System
#endif

public class Backtoreg : MonoBehaviour
{
    [SerializeField] private string sceneName = "RegistrationWindow";

    public void OnBack()
    {
        SceneManager.LoadScene(sceneName);
    }

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        // ESC на ПК; на большинстве устройств Back мапится как "Cancel"/Escape
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            SceneManager.LoadScene(sceneName);

        // Дополнительно: кнопка B (Gamepad)
        if (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame)
            SceneManager.LoadScene(sceneName);
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.Escape))
            SceneManager.LoadScene(sceneName);
#endif
    }
}
