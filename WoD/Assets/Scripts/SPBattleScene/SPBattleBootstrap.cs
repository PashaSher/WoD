using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class SPBattleBootstrap
{
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void Init()
	{
		// Инициализируем только в SPBattleScene
		var scene = SceneManager.GetActiveScene();
		if (scene.name != "SPBattleScene") return;

		// В SP всегда играем за ЧЁРНЫХ (host, левая сторона)
		SPBattleConfig.PlayerOnLeft = true;
		SPBattleConfig.PlayerIsBlue = false;
		Globalflags.ifHost = true;

		// Автоматически добавим менеджер расстановки (UI + клик-расстановка)
		try
		{
			var go = new GameObject("SPPlacementManager(Auto)");
			go.AddComponent<BattlePlacementManager>();
		}
		catch { }

		// Итоги боя (win/lose) — без RTDB
		try
		{
			var go = new GameObject("SPBattleEnd(Auto)");
			go.AddComponent<SPBattleEndManager>();
		}
		catch { }

		// Постоянный бэйдж «you are black/blue» — показываем только во время расстановки
		try
		{
			var go = new GameObject("SPPlacementBadge");
			go.AddComponent<SPPlacementSideLabel>();
		}
		catch { }
	}
}


