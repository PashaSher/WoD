using UnityEngine;
using UnityEngine.SceneManagement;

public class GoToGameWithFriend : MonoBehaviour
{
	[SerializeField] private string sceneName = "GamewithFriend";

	public void OnClick()
	{
		if (string.IsNullOrEmpty(sceneName)) return;
		SceneManager.LoadScene(sceneName);
	}
}


