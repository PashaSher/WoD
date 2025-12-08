using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SPUnitTile : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private TextMeshProUGUI priceText;
    [SerializeField] private TextMeshProUGUI countText;
    [SerializeField] private Button plusBtn;
    [SerializeField] private Button minusBtn;
    [Header("Preview (optional)")]
    [SerializeField] private Image previewImage;
	[Header("Debug")]
	[SerializeField] private bool verboseLogs = true;

    private UnitType _type;
    private SPArmyShopController _shop;
    private bool _initialized;

    public void Init(SPArmyShopController shop, UnitType type)
    {
        if (_initialized) return;
        _initialized = true;

        _shop = shop;
        _type = type;

		Log($"Init start for type={type}");

		// Авто‑привязка UI, если не задано в инспекторе
		if (!titleText)
			titleText = transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
		if (!priceText)
		{
			// поддержим оба варианта написания
			priceText = transform.Find("Prise")?.GetComponent<TextMeshProUGUI>()
			            ?? transform.Find("Price")?.GetComponent<TextMeshProUGUI>();
		}
		if (!countText)
			countText = transform.Find("Counter")?.GetComponent<TextMeshProUGUI>();
		if (!previewImage)
			previewImage = transform.Find("Preview")?.GetComponent<Image>();
		if (!plusBtn)
		{
			var plusTr = transform.Find("+");
			if (plusTr) plusBtn = plusTr.GetComponent<Button>();
		}
		if (!minusBtn)
		{
			var minusTr = transform.Find("-");
			if (minusTr) minusBtn = minusTr.GetComponent<Button>();
		}

		Log($"Bind UI: title={(titleText?titleText.name:"NULL")}, price={(priceText?priceText.name:"NULL")}, count={(countText?countText.name:"NULL")}, plus={(plusBtn?plusBtn.name:"NULL")}, minus={(minusBtn?minusBtn.name:"NULL")}, preview={(previewImage?previewImage.name:"NULL")}");

        if (titleText) titleText.text = type.ToString();
        if (priceText) priceText.text = UnitPrices.Cost[type].ToString();

        if (previewImage)
        {
            previewImage.sprite = null;
            previewImage.enabled = false;
        }

        if (plusBtn != null)
        {
            plusBtn.onClick = new Button.ButtonClickedEvent();
			plusBtn.onClick.AddListener(OnPlusClicked);
			Log("Plus button listener attached");
        }
        if (minusBtn != null)
        {
            minusBtn.onClick = new Button.ButtonClickedEvent();
			minusBtn.onClick.AddListener(OnMinusClicked);
			Log("Minus button listener attached");
        }

		LogClickableState();
		Log("Init done");
    }

    public void SetCount(int value)
    {
        if (countText) countText.text = value.ToString();
		Log($"SetCount type={_type} -> {value}");
    }

    public void SetPreview(Sprite sprite)
    {
        if (!previewImage) return;
        previewImage.sprite = sprite;
        previewImage.enabled = sprite != null;
		Log($"SetPreview type={_type} sprite={(sprite?sprite.name:"NULL")}");
    }

    public void SetPlusInteractable(bool enabled)
    {
        if (plusBtn) plusBtn.interactable = enabled;
		Log($"SetPlusInteractable type={_type} -> {enabled}");
    }

	private void OnPlusClicked()
	{
		Log($"PLUS clicked for {_type}");
		_shop.OnPlus(_type);
	}

	private void OnMinusClicked()
	{
		Log($"MINUS clicked for {_type}");
		_shop.OnMinus(_type);
	}

	private void LogClickableState()
	{
		// Button flags
		if (plusBtn)
		{
			var plusImg = plusBtn.GetComponent<UnityEngine.UI.Graphic>();
			bool plusRay = plusImg ? plusImg.raycastTarget : false;
			Log($"Plus state: enabled={plusBtn.enabled}, interactable={plusBtn.interactable}, raycastTarget={plusRay}");
		}
		else Log("Plus state: BUTTON MISSING");

		if (minusBtn)
		{
			var minusImg = minusBtn.GetComponent<UnityEngine.UI.Graphic>();
			bool minusRay = minusImg ? minusImg.raycastTarget : false;
			Log($"Minus state: enabled={minusBtn.enabled}, interactable={minusBtn.interactable}, raycastTarget={minusRay}");
		}
		else Log("Minus state: BUTTON MISSING");

		// CanvasGroup chain
		var groups = GetComponentsInParent<CanvasGroup>(true);
		if (groups != null && groups.Length > 0)
		{
			for (int i = 0; i < groups.Length; i++)
			{
				var g = groups[i];
				Log($"CanvasGroup[{i}] name={g.name} interactable={g.interactable} blocksRaycasts={g.blocksRaycasts} alpha={g.alpha}");
			}
		}
		else Log("CanvasGroup: none on parents");

		// Canvas and GraphicRaycaster
		var canvas = GetComponentInParent<Canvas>(true);
		if (canvas)
		{
			var gr = canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>();
			Log($"Canvas '{canvas.name}' mode={canvas.renderMode} enabled={canvas.enabled} hasGraphicRaycaster={(gr!=null)}");
		}
		else Log("Canvas: NOT FOUND");

		// EventSystem
		Log($"EventSystem.current present={(UnityEngine.EventSystems.EventSystem.current!=null)}");
	}

	private void Log(string msg)
	{
		if (verboseLogs)
			Debug.Log($"[SPUnitTile] {name}: {msg}");
	}
}


