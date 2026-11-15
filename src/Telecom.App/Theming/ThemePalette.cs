using System.Drawing;

namespace Telecom.App.Theming
{
	// Цвета тёмной темы. Светлая тема использует системные цвета Windows.
	internal static class ThemePalette
	{
		internal static readonly Color DarkSurface = Color.FromArgb(31, 35, 42);
		internal static readonly Color DarkField = Color.FromArgb(24, 28, 34);
		internal static readonly Color DarkButton = Color.FromArgb(43, 49, 59);
		internal static readonly Color DarkBorder = Color.FromArgb(75, 84, 98);
		internal static readonly Color DarkText = Color.FromArgb(232, 237, 244);
		internal static readonly Color DarkMutedText = Color.FromArgb(159, 171, 188);
		internal static readonly Color DarkHighlight = Color.FromArgb(49, 78, 110);
		internal static readonly Color DarkError = Color.FromArgb(255, 162, 157);
	}
}
