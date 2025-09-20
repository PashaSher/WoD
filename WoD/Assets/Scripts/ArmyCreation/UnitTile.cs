using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UnitTile : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI titleText;   // "Rifleman"
    [SerializeField] private TextMeshProUGUI priceText;   // "30"
    [SerializeField] private TextMeshProUGUI countText;   // между кнопками
    [SerializeField] private Button plusBtn;
    [SerializeField] private Button minusBtn;

    private UnitType _type;
    private ArmyShopController _shop;

    public void Init(ArmyShopController shop, UnitType type)
    {
        _shop = shop;
        _type = type;

        if (titleText) titleText.text = type.ToString();
        if (priceText) priceText.text = UnitPrices.Cost[type].ToString();

        plusBtn.onClick.RemoveAllListeners();
        minusBtn.onClick.RemoveAllListeners();
        plusBtn.onClick.AddListener(() => _shop.OnPlus(_type));
        minusBtn.onClick.AddListener(() => _shop.OnMinus(_type));
    }

    public void SetCount(int value)
    {
        if (countText) countText.text = value.ToString();
    }
}
