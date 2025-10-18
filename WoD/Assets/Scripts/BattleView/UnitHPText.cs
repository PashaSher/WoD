using UnityEngine;
using TMPro;

[ExecuteAlways]
[DisallowMultipleComponent]
public class UnitHPText : MonoBehaviour
{
	[SerializeField] private Unit unit;
	[SerializeField] private bool showMaxHp = false;
	[SerializeField] private TMP_FontAsset font;
	[SerializeField] private HpColorMode colorMode = HpColorMode.AutoByOwner;
	[SerializeField] private Color customColor = Color.white;
	[SerializeField] private float fontSize = 3.5f;
	[SerializeField] private Vector3 localOffset = Vector3.zero;
	[SerializeField] private bool useIntOnly = true;

	private TextMeshPro _tmp;
	private int _lastHp = int.MinValue;
	private int _lastMax = int.MinValue;

	public enum HpColorMode { AutoByOwner, Black, Blue, Custom }

	private void Awake()
	{
		EnsureComponents();
	}

	private void Reset()
	{
		unit = GetComponentInParent<Unit>();
		EnsureComponents();
		ApplyStaticAppearance();
		UpdateText(true);
	}

	private void OnEnable()
	{
		if (unit == null) unit = GetComponentInParent<Unit>();
		EnsureComponents();
		ApplyStaticAppearance();
		UpdateText(true);
	}

	private void OnValidate()
	{
		EnsureComponents();
		ApplyStaticAppearance();
		UpdateText(true);
	}

	private void Update()
	{
		if (unit == null) unit = GetComponentInParent<Unit>();
		if (_tmp == null) return;

		if (localOffset != Vector3.zero && transform.localPosition != localOffset)
			transform.localPosition = localOffset;

		// keep color updated if it's auto by owner
		_tmp.color = ResolveColor();

		UpdateText(false);
	}

	private void EnsureComponents()
	{
		if (_tmp == null)
		{
			_tmp = GetComponent<TextMeshPro>();
			if (_tmp == null) _tmp = gameObject.AddComponent<TextMeshPro>();
			_tmp.alignment = TextAlignmentOptions.Center;
			_tmp.raycastTarget = false;
			_tmp.enableWordWrapping = false;
			_tmp.sortingOrder = 50;
		}
	}

	private void ApplyStaticAppearance()
	{
		if (_tmp == null) return;
		if (font != null) _tmp.font = font;
		_tmp.fontSize = fontSize;
		_tmp.color = ResolveColor();
	}

	private Color ResolveColor()
	{
		switch (colorMode)
		{
			case HpColorMode.AutoByOwner:
				// Host -> black, Client -> blue (cyan for visibility)
				if (unit != null)
					return unit.host ? Color.black : Color.blue;
				return Color.black;
			case HpColorMode.Blue: return Color.cyan;
			case HpColorMode.Custom: return customColor;
			default: return Color.black;
		}
	}

	private void UpdateText(bool force)
	{
		if (unit == null)
		{
			if (_tmp != null) _tmp.text = "";
			return;
		}

		int hp = unit.health;
		int max = unit.maxHP;

		if (!force && hp == _lastHp && max == _lastMax) return;

		_lastHp = hp;
		_lastMax = max;

		if (showMaxHp)
		{
			_tmp.text = useIntOnly ? ($"{hp}/{max}") : string.Format("{0}/{1}", hp, max);
		}
		else
		{
			_tmp.text = useIntOnly ? hp.ToString() : string.Format("{0}", hp);
		}
	}
}


