using UnityEngine;
using UnityEngine.SceneManagement;

public class LoadSPBattleScene : MonoBehaviour
{
    [SerializeField] private string sceneName = "SPBattleScene";

    // Assign to Button->OnClick
    public void LoadScene()
    {
        if (!SPArmyState.HasSelection)
        {
            Debug.LogWarning("[LoadSPBattleScene] No SP selection saved. Did you confirm selection?");
        }
        SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
    }
}





