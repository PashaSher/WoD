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
}
