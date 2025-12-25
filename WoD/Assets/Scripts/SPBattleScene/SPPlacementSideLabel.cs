using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Небольшой оверлей «You are black/blue», видимый только во время расстановки.
/// Создаётся автоматически в SPBattleBootstrap.
/// </summary>
public class SPPlacementSideLabel : MonoBehaviour
{
	private Canvas canvas;
	private TextMeshProUGUI label;

	private void Awake()
	{
		canvas = gameObject.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		gameObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		gameObject.AddComponent<GraphicRaycaster>();

		var go = new GameObject("SideLabel");
		go.transform.SetParent(transform, false);
		label = go.AddComponent<TextMeshProUGUI>();
		label.raycastTarget = false;
		label.alignment = TextAlignmentOptions.MidlineLeft;
		label.fontSize = 28;
		label.color = SPBattleConfig.PlayerIsBlue ? Color.blue : Color.black;
		var rt = (RectTransform)label.transform;
		rt.anchorMin = new Vector2(0.02f, 0.94f);
		rt.anchorMax = new Vector2(0.5f, 0.995f);
		rt.offsetMin = Vector2.zero;
		rt.offsetMax = Vector2.zero;

		UpdateText();
	}

	private void Update()
	{
		if (!label) return;
		// Показываем только в фазе расстановки
		label.enabled = BattlePlacementState.IsPlacementActive;
	}

	private void UpdateText()
	{
		string colorName = SPBattleConfig.PlayerIsBlue ? "BLUE" : "BLACK";
		string side = SPBattleConfig.PlayerOnLeft ? "LEFT" : "RIGHT";
		label.text = $"You are {colorName} ({side} side)";
	}
}









