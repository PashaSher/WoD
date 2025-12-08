using UnityEngine;

public static class SPBattleConfig
{
	// В SP баттле: true = игрок слева, false = игрок справа
	public static bool PlayerOnLeft = true;
	// Цвет игрока: true = blue, false = black
	public static bool PlayerIsBlue = true;

	// Нежёсткая тонировка спрайтов по цвету стороны (для лучшей читаемости)
	public static Color GetTint(bool isPlayerSide)
	{
		bool blueSide = isPlayerSide ? PlayerIsBlue : !PlayerIsBlue;
		// Синему дадим лёгкую голубую подсветку, чёрному — лёгкое затемнение
		return blueSide
			? new Color(0.75f, 0.85f, 1.00f, 1f) // мягкий голубой
			: new Color(0.30f, 0.30f, 0.30f, 1f); // мягкий тёмный
	}
}


