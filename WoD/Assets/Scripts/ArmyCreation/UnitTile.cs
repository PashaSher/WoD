using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UnitTile : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private TextMeshProUGUI priceText;
    [SerializeField] private TextMeshProUGUI countText;
    [SerializeField] private Button plusBtn;
    [SerializeField] private Button minusBtn;
    [Header("Preview (optional)")]
    [SerializeField] private Image previewImage; // картинка юнита в магазине (необязательно)

    private UnitType _type;
    private ArmyShopController _shop;
    private bool _initialized; // защита от двойной инициализации

    public void Init(ArmyShopController shop, UnitType type)
    {
        if (_initialized) return;
        _initialized = true;

        _shop = shop;
        _type = type;

        if (titleText) titleText.text = type.ToString();
        if (priceText) priceText.text = UnitPrices.Cost[type].ToString();

        // Сбрасываем превью, чтобы спрайт по умолчанию из префаба (например, Rifleman)
        // не «просачивался» на все остальные плитки.
        if (previewImage)
        {
            previewImage.sprite = null;
            previewImage.enabled = false;
        }

        // ПОЛНЫЙ СБРОС подписчиков (включая те, что в Инспекторе)
        if (plusBtn != null)
        {
            plusBtn.onClick = new Button.ButtonClickedEvent();
            plusBtn.onClick.AddListener(() => _shop.OnPlus(_type));
        }
        if (minusBtn != null)
        {
            minusBtn.onClick = new Button.ButtonClickedEvent();
            minusBtn.onClick.AddListener(() => _shop.OnMinus(_type));
        }
    }

    public void SetCount(int value)
    {
        if (countText) countText.text = value.ToString();
    }

    public void SetPreview(Sprite sprite)
    {
        if (!previewImage) return;
        previewImage.sprite = sprite;
        previewImage.enabled = sprite != null;
    }

    public void SetPlusInteractable(bool enabled)
    {
        if (plusBtn) plusBtn.interactable = enabled;
    }
}
