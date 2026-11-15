using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;
using Telecom.Core.Logging;

namespace Telecom.App.Theming
{
	internal static class WindowAppearance
	{
		private const int DwmUseImmersiveDarkMode = 20;
		private const int DwmCaptionColor = 35;
		private const int DwmTextColor = 36;
		private const int DwmDefaultColor = -1;
		private const string WindowsPersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

		private static readonly Font[] Fonts = Enumerable.Range(0,
			(AppearanceSettings.MaxFontPercent - AppearanceSettings.MinFontPercent) / AppearanceSettings.FontPercentStep + 1)
			.Select(index => new Font("Segoe UI", 9F * (AppearanceSettings.MinFontPercent + index * AppearanceSettings.FontPercentStep) / 100F))
			.ToArray();

		// Задаёт атрибут оформления окна (тёмный заголовок, цвет заголовка) через Desktop Window Manager.
		[DllImport("dwmapi.dll", ExactSpelling = true)]
		private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

		// Применяет общие иконку, шрифт и оформление заголовка ко всем окнам приложения.
		internal static void Apply(Form window, AutoScaleMode scaleMode = AutoScaleMode.Font)
		{
			window.Icon = new Icon(typeof(WindowAppearance), "Telecom.ico");
			ApplyFont(window, AppearanceSettings.Default.FontPercent);
			window.AutoScaleMode = scaleMode;
			window.AutoScaleDimensions = window.CurrentAutoScaleDimensions;
			window.HandleCreated += OnHandleCreated;
		}

		// Наследуемый шрифт меняется от базовых 9 пт, независимо от предыдущего множителя.
		internal static void ApplyFont(Form window, int fontPercent)
		{
			Font font = Fonts[(fontPercent - AppearanceSettings.MinFontPercent) / AppearanceSettings.FontPercentStep];
			if (!window.Font.Equals(font))
			{
				window.Font = font;
			}
		}

		// Оформляет диалог в цветах владельца и вписывает его в рабочую область экрана.
		internal static void ApplyToDialog(Form dialog, Form owner, int fontPercent)
		{
			ApplyFont(dialog, fontPercent);
			dialog.MaximumSize = Screen.FromControl(owner).WorkingArea.Size;
			ApplyTheme(dialog, IsDark(owner));
		}

		// Читает системный режим приложений Windows. При ошибке остаётся светлая палитра.
		internal static bool UseDarkTheme(AppearanceTheme theme, ILogger logger)
		{
			if (SystemInformation.HighContrast || theme == AppearanceTheme.Light)
			{
				return false;
			}

			if (theme == AppearanceTheme.Dark)
			{
				return true;
			}

			try
			{
				return Equals(Registry.GetValue(WindowsPersonalizeKey, "AppsUseLightTheme", 1), 0);
			}
			catch (Exception exception)
			{
				if (!(exception is IOException) && !(exception is SecurityException) && !(exception is UnauthorizedAccessException))
				{
					throw;
				}

				logger.Error("Не удалось прочитать системную тему Windows.", exception);
				return false;
			}
		}

		// Определяет палитру окна без повторного обращения к реестру при отрисовке.
		internal static bool IsDark(Control control)
		{
			Form window = control.FindForm();
			return window != null && window.BackColor == ThemePalette.DarkSurface;
		}

		// Выбирает читаемый цвет ошибки для текущей палитры.
		internal static Color ErrorColor(Control control)
		{
			return IsDark(control) ? ThemePalette.DarkError : Color.Firebrick;
		}

		// Применяет палитру ко всем готовым элементам окна и штатному заголовку.
		internal static void ApplyTheme(Form window, bool dark)
		{
			if (IsDark(window) != dark)
			{
				window.BackColor = dark ? ThemePalette.DarkSurface : Color.Empty;
				window.ForeColor = dark ? ThemePalette.DarkText : Color.Empty;
				ControlTheme.Apply(window, dark);
				window.Invalidate(true);
			}

			if (window.IsHandleCreated)
			{
				ApplyCaption(window);
			}
		}

		// Восстанавливает цвета, если Windows пересоздаст дескриптор окна.
		private static void OnHandleCreated(object sender, EventArgs args)
		{
			ApplyCaption((Form)sender);
		}

		// Неподдерживаемые атрибуты DWM оставляют штатный заголовок Windows.
		private static void ApplyCaption(Form window)
		{
			bool highContrast = SystemInformation.HighContrast;
			bool dark = IsDark(window) && !highContrast;
			int immersiveDark = dark ? 1 : 0;
			DwmSetWindowAttribute(window.Handle, DwmUseImmersiveDarkMode, ref immersiveDark, sizeof(int));

			int background = highContrast ? DwmDefaultColor
				: ColorTranslator.ToWin32(dark ? ThemePalette.DarkSurface : Color.FromArgb(204, 228, 247));
			if (DwmSetWindowAttribute(window.Handle, DwmCaptionColor, ref background, sizeof(int)) == 0)
			{
				int text = highContrast ? DwmDefaultColor
					: ColorTranslator.ToWin32(dark ? ThemePalette.DarkText : Color.FromArgb(43, 34, 28));
				DwmSetWindowAttribute(window.Handle, DwmTextColor, ref text, sizeof(int));
			}
		}
	}
}
