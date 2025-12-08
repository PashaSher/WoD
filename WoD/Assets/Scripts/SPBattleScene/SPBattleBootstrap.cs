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

		// 50/50 выбор стороны: слева (host) или справа (client)
		SPBattleConfig.PlayerOnLeft = Random.value < 0.5f;
		// Цвет фиксирован как в мультиплеере: слева — BLACK, справа — BLUE
		SPBattleConfig.PlayerIsBlue = !SPBattleConfig.PlayerOnLeft;
		// В существующей логике host = левая сторона
		Globalflags.ifHost = SPBattleConfig.PlayerOnLeft;

		// Автоматически добавим менеджер расстановки (UI + клик-расстановка)
		try
		{
			var go = new GameObject("SPPlacementManager(Auto)");
			go.AddComponent<BattlePlacementManager>();
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


