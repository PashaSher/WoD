using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Database;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Появляется при загрузке сцены боя. Предлагает: "Рассавить" вручную или "Пропустить".
/// В режиме расстановки позволяет кликать по своей половине, расставляя СВОИХ юнитов по очереди.
/// Позиции пишутся в RTDB в state/x,y (moving=false), чтобы другая сторона сразу увидела расстановку.
/// </summary>
public class BattlePlacementManager : MonoBehaviour
{
	[Header("UI")]
	[SerializeField] private Canvas canvas;
	[SerializeField] private GameObject panel;
	[SerializeField] private Button   btnSkip;
	[SerializeField] private Button   btnPlace;
	[SerializeField] private Text     titleText;

	[Header("UI Style")]
	[SerializeField] private Font   uiFont;
	[SerializeField] private Color  titleColor = Color.white;
	[SerializeField] private int    titleFontSize = 36;
	[SerializeField] private Color  buttonTextColor = Color.black;
	[SerializeField] private Color  buttonBgColor   = new Color(1f, 1f, 1f, 0.9f);
	[SerializeField] private int    buttonFontSize  = 28;
	// Центры кнопок по оси X: 0.5 ± buttonsSeparation/2. Рекомендуемый диапазон: 0.10..0.40
	[SerializeField] private float  buttonsSeparation = 0.20f;
	// Геометрия кнопки (доля от ширины/высоты экрана в якорях)
	[SerializeField] private float  buttonHalfWidth  = 0.12f;
	[SerializeField] private float  buttonHalfHeight = 0.06f;
	[SerializeField] private float  buttonsY         = 0.35f;

	private Camera cam;
	private Rect allowedScreen;   // на случай, если захотим ограничить по экрану
	private float halfW;
	private float halfH;

	private readonly List<Unit> myUnits = new();
	private int placeIndex = -1;
	private bool isPlacing;

	private void Awake()
	{
		cam = Camera.main;
		ComputeBounds();
		// Принудительный режим ручной расстановки: без выбора
		try
		{
			if (canvas) Destroy(canvas.gameObject);
		}
		catch { }
		// Дождёмся появления юнитов в сцене (спавнер асинхронный), затем начнём расстановку
		StartCoroutine(WaitAndBeginPlacement());
	}

	private IEnumerator WaitAndBeginPlacement()
	{
		// Ждём, пока в сцене появятся Мои юниты (host/client)
		bool iAmHost = Globalflags.ifHost;
		for (;;)
		{
#if UNITY_2022_2_OR_NEWER
			var all = UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
			var all = UnityEngine.Object.FindObjectsOfType<Unit>(true);
#endif
			int ownCount = 0;
			for (int i = 0; i < all.Length; i++)
			{
				var u = all[i];
				if (!u) continue;
				if (u.host == iAmHost) ownCount++;
			}
			if (ownCount > 0) break;
			yield return null; // ждём следующий кадр
		}
		BeginPlacement();
	}

	private void ComputeBounds()
	{
		if (!cam)
		{
			cam = Camera.main;
			if (!cam) return;
		}
		halfH = cam.orthographicSize;
		halfW = halfH * cam.aspect;
	}

	private void TryCreateUiIfMissing()
	{
		if (canvas && panel && btnSkip && btnPlace && titleText) return;

		// Создадим простой Canvas с двумя кнопками
		canvas = new GameObject("PlacementCanvas").AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		var scaler = canvas.gameObject.AddComponent<CanvasScaler>();
		scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		scaler.referenceResolution = new Vector2(1280, 720);
		canvas.gameObject.AddComponent<GraphicRaycaster>();

		panel = new GameObject("Panel");
		panel.transform.SetParent(canvas.transform, false);
		var img = panel.AddComponent<Image>();
		img.color = new Color(0f, 0f, 0f, 0.6f);
		var rt = (RectTransform)panel.transform;
		rt.anchorMin = new Vector2(0f, 0f);
		rt.anchorMax = new Vector2(1f, 1f);
		rt.offsetMin = Vector2.zero;
		rt.offsetMax = Vector2.zero;

		// Title
		var titleGo = new GameObject("Title");
		titleGo.transform.SetParent(panel.transform, false);
		titleText = titleGo.AddComponent<Text>();
		titleText.alignment = TextAnchor.MiddleCenter;
		titleText.font = uiFont != null ? uiFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
		titleText.fontSize = Mathf.Max(10, titleFontSize);
		titleText.color = titleColor;
		var rtTitle = (RectTransform)titleGo.transform;
		rtTitle.anchorMin = new Vector2(0.15f, 0.6f);
		rtTitle.anchorMax = new Vector2(0.85f, 0.8f);
		rtTitle.offsetMin = Vector2.zero;
		rtTitle.offsetMax = Vector2.zero;

		// Buttons
		float halfSep = Mathf.Clamp01(Mathf.Abs(buttonsSeparation)) * 0.5f;
		var leftCenter  = new Vector2(Mathf.Clamp01(0.5f - halfSep), Mathf.Clamp01(buttonsY));
		var rightCenter = new Vector2(Mathf.Clamp01(0.5f + halfSep), Mathf.Clamp01(buttonsY));
		btnPlace = CreateButton(panel.transform, "Рассавить",  leftCenter);
		btnSkip  = CreateButton(panel.transform, "Пропустить", rightCenter);
	}

	private Button CreateButton(Transform parent, string text, Vector2 anchorCenter)
	{
		var go = new GameObject(text);
		go.transform.SetParent(parent, false);
		var image = go.AddComponent<Image>();
		image.color = buttonBgColor;
		var btn = go.AddComponent<Button>();
		var rt = (RectTransform)go.transform;
		rt.sizeDelta = Vector2.zero;
		rt.anchorMin = anchorCenter - new Vector2(buttonHalfWidth, buttonHalfHeight);
		rt.anchorMax = anchorCenter + new Vector2(buttonHalfWidth, buttonHalfHeight);
		rt.offsetMin = Vector2.zero;
		rt.offsetMax = Vector2.zero;

		var labelGo = new GameObject("Text");
		labelGo.transform.SetParent(go.transform, false);
		var label = labelGo.AddComponent<Text>();
		label.text = text;
		label.alignment = TextAnchor.MiddleCenter;
		label.font = uiFont != null ? uiFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
		label.color = buttonTextColor;
		label.fontSize = Mathf.Max(10, buttonFontSize);
		var rtLabel = (RectTransform)labelGo.transform;
		rtLabel.anchorMin = new Vector2(0, 0);
		rtLabel.anchorMax = new Vector2(1, 1);
		rtLabel.offsetMin = Vector2.zero;
		rtLabel.offsetMax = Vector2.zero;

		return btn;
	}

	private void ShowPrompt()
	{
		titleText.text = "Deploy your units?\nClick anywhere on YOUR half of the field.";
		panel.SetActive(true);

		btnSkip.onClick.RemoveAllListeners();
		btnPlace.onClick.RemoveAllListeners();
		btnSkip.onClick.AddListener(OnSkip);
		btnPlace.onClick.AddListener(OnPlace);
	}

	private void OnSkip()
	{
		panel.SetActive(false);
		// Сразу считаемся готовыми
		Debug.Log("[Placement] Skip pressed → ready now");
		BattleReadyManager.SignalLocalReady();
		Destroy(canvas.gameObject, 0.1f);
		// Ничего не делаем — спавн уже выполнен ArmySpawner как сейчас.
	}

	private void OnPlace()
	{
		panel.SetActive(false);
		BeginPlacement();
	}

	private void BeginPlacement()
	{
		BattlePlacementState.BeginPlacement();
		Debug.Log("[Placement] BeginPlacement");
		// Собираем ТОЛЬКО свои юниты
		myUnits.Clear();
		var all = UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
		bool iAmHost = Globalflags.ifHost;
		// Сначала собираем пассивные как препятствия, затем остальные — чтобы располагались первыми
		var passives = new List<Unit>();
		var others   = new List<Unit>();
		foreach (var u in all)
		{
			if (!u) continue;
			if (u.host != iAmHost) continue;
			bool isPassive = false;
			try { isPassive = u.isPassive; } catch { isPassive = false; }
			if (isPassive) passives.Add(u); else others.Add(u);
		}
		myUnits.AddRange(passives);
		myUnits.AddRange(others);
		placeIndex = 0;
		isPlacing = true;

		// Попросим игрока кликнуть позицию для первого юнита
		EnsureHelperOverlay(true);
		Debug.Log($"[Placement] Units to place: {myUnits.Count}");
	}

	private GameObject helper;
	private Text helperText;

	private void EnsureHelperOverlay(bool on)
	{
		if (!on)
		{
			if (helper) Destroy(helper);
			return;
		}
		if (!helper)
		{
			helper = new GameObject("PlacementHelper");
			var c = helper.AddComponent<Canvas>();
			c.renderMode = RenderMode.ScreenSpaceOverlay;
			helper.AddComponent<GraphicRaycaster>();

			var tgo = new GameObject("Hint");
			tgo.transform.SetParent(helper.transform, false);
			helperText = tgo.AddComponent<Text>();
			helperText.alignment = TextAnchor.MiddleCenter;
			helperText.font = uiFont != null ? uiFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			// Цвет по стороне: HOST -> чёрный, CLIENT -> синий
			helperText.color = Globalflags.ifHost ? Color.black : Color.blue;
			// Базовый крупный размер
			helperText.fontSize = 70;
			// Включаем авто-уменьшение, чтобы всегда влезало
			helperText.resizeTextForBestFit = true;
			helperText.resizeTextMinSize = 16;
			helperText.resizeTextMaxSize = 70;
			helperText.horizontalOverflow = HorizontalWrapMode.Overflow;
			helperText.verticalOverflow = VerticalWrapMode.Truncate;
			var rt = (RectTransform)tgo.transform;
			rt.anchorMin = new Vector2(0.15f, 0.02f);
			rt.anchorMax = new Vector2(0.85f, 0.12f);
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;
		}
		UpdateHelperText();
	}

	private void UpdateHelperText()
	{
		if (!helperText) return;
		if (!isPlacing || placeIndex < 0 || placeIndex >= myUnits.Count)
		{
			helperText.text = "";
			return;
		}
		var u = myUnits[placeIndex];
		string name = string.IsNullOrEmpty(u.unitType) ? "Unit" : u.unitType;
		// Показываем только имя юнита, без индекса/ключа и без префикса
		helperText.text = name;
	}

	private void Update()
	{
		if (!isPlacing) return;
		if (placeIndex < 0 || placeIndex >= myUnits.Count)
		{
			FinishPlacement();
			return;
		}

		Vector3 world;
		if (TryGetClickWorld(out world))
		{
			world = ClampToMyHalf(world);
			TryPlaceCurrentAt(world);
		}
	}

	private Vector3 ScreenToWorld(Vector3 screen)
	{
		if (!cam) cam = Camera.main;
		if (!cam) return Vector3.zero;
		float z = Mathf.Abs(cam.transform.position.z);
		var w = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, z));
		w.z = 0f;
		return w;
	}

	private bool TryGetClickWorld(out Vector3 world)
	{
#if ENABLE_INPUT_SYSTEM
		// Mouse
		if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
		{
			var pos = Mouse.current.position.ReadValue();
			world = ScreenToWorld(new Vector3(pos.x, pos.y, 0f));
			return true;
		}
		// Touch
		if (Touchscreen.current != null)
		{
			var touch = Touchscreen.current.primaryTouch;
			if (touch != null && touch.press.wasPressedThisFrame)
			{
				var pos = touch.position.ReadValue();
				world = ScreenToWorld(new Vector3(pos.x, pos.y, 0f));
				return true;
			}
		}
#else
		if (Input.GetMouseButtonDown(0))
		{
			world = ScreenToWorld(Input.mousePosition);
			return true;
		}
		if (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)
		{
			world = ScreenToWorld(Input.GetTouch(0).position);
			return true;
		}
#endif
		world = Vector3.zero;
		return false;
	}

	private Vector3 ClampToMyHalf(Vector3 pos)
	{
		bool iAmHost = Globalflags.ifHost;
		// Половина: хост — x <= 0; клиент — x >= 0
		if (iAmHost) pos.x = Mathf.Min(pos.x, 0f);
		else         pos.x = Mathf.Max(pos.x, 0f);
		// По краям кадра
		pos.x = Mathf.Clamp(pos.x, -halfW + 0.2f, halfW - 0.2f);
		pos.y = Mathf.Clamp(pos.y, -halfH + 0.5f, halfH - 0.5f);
		return pos;
	}

	// Быстрый «тост» на 1 секунду
	private GameObject toastGo;
	private Text toastText;
	private Coroutine toastCo;
	private void ShowToast(string msg, float seconds = 1f)
	{
		EnsureHelperOverlay(true);
		if (!toastGo)
		{
			toastGo = new GameObject("Toast");
			toastGo.transform.SetParent(helper.transform, false);
			toastText = toastGo.AddComponent<Text>();
			toastText.alignment = TextAnchor.MiddleCenter;
			toastText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			toastText.fontSize = 28;
			toastText.color = Color.red;
			var rt = (RectTransform)toastGo.transform;
			rt.anchorMin = new Vector2(0.2f, 0.80f);
			rt.anchorMax = new Vector2(0.8f, 0.90f);
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;
		}
		toastText.text = msg;
		if (toastCo != null) StopCoroutine(toastCo);
		toastCo = StartCoroutine(HideToastAfter(seconds));
	}
	private IEnumerator HideToastAfter(float s)
	{
		yield return new WaitForSeconds(Mathf.Max(0.1f, s));
		if (toastText) toastText.text = "";
	}

	private bool IsPlacementBlockedFor(Unit placing, Vector3 world, out string reason)
	{
		// Проверяем:
		// 1) Не кладём в ту же точку, что и другой юнит
		// 2) Не кладём поверх пассивных препятствий (стен и т.п.)
		const float minSpacing = 0.6f;   // минимальная дистанция до других юнитов
		const float obstaclePad = 0.5f;  // отступ от пассивных препятствий
		reason = null;
#if UNITY_2022_2_OR_NEWER
		var all = UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
		var all = UnityEngine.Object.FindObjectsOfType<Unit>(true);
#endif
		for (int i = 0; i < all.Length; i++)
		{
			var u = all[i];
			if (!u) continue;
			if (u == placing) continue;
			Vector3 pos;
			try { pos = u.transform.position; } catch { continue; }
			float dist = Vector2.Distance(pos, world);
			bool isPassive = false;
			try { isPassive = u.isPassive; } catch { isPassive = false; }
			if (isPassive)
			{
				// Блокируем размещение на стенах/препятствиях с небольшим полем безопасности
				if (dist < obstaclePad)
				{
					reason = "Cannot place on obstacles";
					return true;
				}
				continue;
			}
			if (dist < minSpacing)
			{
				reason = "Cannot place units at the same spot";
				return true;
			}
		}

		// Дополнительная страховка: если в точке клика есть коллайдер пассивного объекта, тоже запрещаем
		var hits = Physics2D.OverlapPointAll(new Vector2(world.x, world.y));
		if (hits != null && hits.Length > 0)
		{
			for (int i = 0; i < hits.Length; i++)
			{
				try
				{
					var go = hits[i].gameObject;
					if (!go) continue;
					var u = go.GetComponentInParent<Unit>();
					bool isPassive = false;
					try { isPassive = (u != null && u.isPassive); } catch { isPassive = false; }
					if (isPassive)
					{
						reason = "Cannot place on obstacles";
						return true;
					}
				}
				catch { /* ignore */ }
			}
		}
		return false;
	}

	private async void TryPlaceCurrentAt(Vector3 world)
	{
		var u = myUnits[placeIndex];
		if (!u) { placeIndex++; UpdateHelperText(); return; }

		// Валидируем позицию — запретить стаканье/перекрытия
		if (IsPlacementBlockedFor(u, world, out var whyBlocked))
		{
			ShowToast(whyBlocked ?? "Invalid placement");
			return;
		}

		Debug.Log($"[Placement] Place {u.unitKey} at {world} (index {placeIndex+1}/{myUnits.Count})");

		// Локально ставим
		u.transform.position = world;
		// Повернём лицом к стороне противника:
		// hostArmy (слева) смотрит вправо, clientArmy (справа) — влево
		float lookTargetX = u.transform.position.x + (u.host ? +1f : -1f);
		u.FaceTowardsX(lookTargetX);

		// Пишем в RTDB координаты (moving=false), чтобы другая сторона увидела ту же позицию
		try
		{
			if (!string.IsNullOrEmpty(u.sessionId) && !string.IsNullOrEmpty(u.unitKey))
			{
				string branch = u.host ? "hostArmy" : "clientArmy";
				var stateRef = FirebaseDatabase.DefaultInstance.RootReference
					.Child("sessions").Child(u.sessionId)
					.Child(branch).Child(u.unitKey).Child("state");

				var payload = new Dictionary<string, object>
				{
					["x"] = (double)world.x,
					["y"] = (double)world.y,
					["moving"] = false,
					["facing"] = u.host ? 1 : -1,
					["updatedAt"] = ServerValue.Timestamp
				};
				await stateRef.UpdateChildrenAsync(payload);
				await stateRef.Parent.Child("updatedAt").SetValueAsync(ServerValue.Timestamp);
			}
		}
		catch (Exception ex)
		{
			Debug.LogWarning($"[Placement] RTDB write failed for {u.unitKey}: {ex.Message}");
		}

		placeIndex++;
		UpdateHelperText();
		if (placeIndex >= myUnits.Count) FinishPlacement();
	}

	private void FinishPlacement()
	{
		isPlacing = false;
		BattlePlacementState.EndPlacement();
		// Закончили расстановку — считаемся готовыми
		Debug.Log("[Placement] FinishPlacement → ready now");
		BattleReadyManager.SignalLocalReady();
		EnsureHelperOverlay(false);
	}
}


