using UnityEngine;
using UnityEngine.SceneManagement;

public class LoadSPArmyCreation : MonoBehaviour
{
    [SerializeField] private string sceneName = "SPArmyCreation";

    // Assign this method to the Button's OnClick() event
    public void LoadScene()
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogError("[LoadSPArmyCreation] Scene name is empty.");
            return;
        }

        SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
    }
}


