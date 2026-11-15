using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;
using Telecom.Core;

namespace Telecom.App
{
	internal static class WindowAppearance
	{
		private const int DwmUseImmersiveDarkMode = 20;
		private const int DwmCaptionColor = 35;
		private const int DwmTextColor = 36;
		private const int DwmDefaultColor = -1;
		private const string WindowsPersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

		internal static readonly Color DarkSurface = Color.FromArgb(31, 35, 42);
		internal static readonly Color DarkField = Color.FromArgb(24, 28, 34);
		internal static readonly Color DarkButton = Color.FromArgb(43, 49, 59);
		internal static readonly Color DarkBorder = Color.FromArgb(75, 84, 98);
		internal static readonly Color DarkText = Color.FromArgb(232, 237, 244);
		internal static readonly Color DarkMutedText = Color.FromArgb(159, 171, 188);
		private static readonly Color DarkHighlight = Color.FromArgb(49, 78, 110);
		private static readonly Color DarkError = Color.FromArgb(255, 162, 157);

		private static readonly Font[] Fonts = Enumerable.Range(0,
			(AppearanceSettings.MaxFontPercent - AppearanceSettings.MinFontPercent) / AppearanceSettings.FontPercentStep + 1)
			.Select(index => new Font("Segoe UI", 9F * (AppearanceSettings.MinFontPercent + index * AppearanceSettings.FontPercentStep) / 100F))
			.ToArray();

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
			return window != null && window.BackColor == DarkSurface;
		}

		// Выбирает читаемый цвет ошибки для текущей палитры.
		internal static Color ErrorColor(Control control)
		{
			return IsDark(control) ? DarkError : Color.Firebrick;
		}

		// Применяет палитру ко всем готовым элементам окна и штатному заголовку.
		internal static void ApplyTheme(Form window, bool dark)
		{
			if (IsDark(window) != dark)
			{
				window.BackColor = dark ? DarkSurface : Color.Empty;
				window.ForeColor = dark ? DarkText : Color.Empty;
				ApplyControlColors(window, dark);
				window.Invalidate(true);
			}

			if (window.IsHandleCreated)
			{
				ApplyCaption(window);
			}
		}

		// Перекрашивает поля, таблицы и кнопки, сохраняя настройки самих элементов.
		private static void ApplyControlColors(Control parent, bool dark)
		{
			foreach (Control control in parent.Controls)
			{
				var grid = control as DataGridView;
				var button = control as ButtonBase;
				var tabs = control as AppearanceTabControl;
				var page = control as TabPage;
				var combo = control as ComboBox;
				var group = control as GroupBox;
				if (grid != null)
				{
					ApplyGridColors(grid, dark);
				}
				else if (button != null)
				{
					button.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
					button.BackColor = dark ? DarkButton : Color.Empty;
					button.UseVisualStyleBackColor = !dark;
					button.FlatAppearance.BorderColor = dark ? DarkBorder : Color.Empty;
					button.FlatAppearance.MouseOverBackColor = dark ? DarkBorder : Color.Empty;
					button.FlatAppearance.MouseDownBackColor = dark ? DarkHighlight : Color.Empty;
					button.FlatAppearance.CheckedBackColor = dark ? DarkHighlight : Color.Empty;
					button.Paint -= PaintDisabledButton;
					var radio = button as RadioButton;
					if (dark && (button is Button || (radio != null && radio.Appearance == Appearance.Button)))
					{
						button.Paint += PaintDisabledButton;
					}
				}
				else if (tabs != null)
				{
					tabs.UseDarkColors(dark);
				}
				else if (page != null)
				{
					page.BackColor = dark ? DarkSurface : Color.Empty;
					page.UseVisualStyleBackColor = !dark;
				}
				else if (combo != null)
				{
					combo.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
					combo.BackColor = dark ? DarkField : Color.Empty;
					combo.ForeColor = dark ? DarkText : Color.Empty;
					combo.DrawMode = dark ? DrawMode.OwnerDrawFixed : DrawMode.Normal;
					combo.DrawItem -= DrawComboItem;
					combo.FontChanged -= OnComboFontChanged;
					if (dark)
					{
						combo.ItemHeight = combo.Font.Height + 2;
						combo.FontChanged += OnComboFontChanged;
						combo.DrawItem += DrawComboItem;
					}
				}
				else if (control is TextBoxBase || control is UpDownBase)
				{
					control.BackColor = dark ? DarkField : Color.Empty;
					control.ForeColor = dark ? DarkText : Color.Empty;
				}
				else if (group != null)
				{
					group.ForeColor = dark ? DarkText : Color.Empty;
					group.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
				}
				else if (control is Label && (control.ForeColor == DarkError || control.ForeColor == Color.Firebrick))
				{
					control.ForeColor = dark ? DarkError : Color.Firebrick;
				}

				if (grid == null)
				{
					ApplyControlColors(control, dark);
				}
			}
		}

		// Рисует текст недоступной кнопки читаемым серым на тёмном фоне.
		private static void PaintDisabledButton(object sender, PaintEventArgs args)
		{
			var button = (ButtonBase)sender;
			if (button.Enabled)
			{
				return;
			}

			Rectangle bounds = button.ClientRectangle;
			using (var fill = new SolidBrush(DarkButton))
			using (var border = new Pen(DarkBorder))
			{
				args.Graphics.FillRectangle(fill, bounds);
				args.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
			}

			TextRenderer.DrawText(args.Graphics, button.Text, button.Font, bounds, DarkMutedText,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
		}

		// Подгоняет строки раскрытого списка под текущий шрифт.
		private static void OnComboFontChanged(object sender, EventArgs args)
		{
			var combo = (ComboBox)sender;
			combo.ItemHeight = combo.Font.Height + 2;
		}

		// Окрашивает выбранное значение и раскрытый список вместе с остальными полями.
		private static void DrawComboItem(object sender, DrawItemEventArgs args)
		{
			var combo = (ComboBox)sender;
			bool selected = (args.State & DrawItemState.Selected) != 0;
			using (var background = new SolidBrush(selected ? DarkHighlight : DarkField))
			{
				args.Graphics.FillRectangle(background, args.Bounds);
			}

			if (args.Index >= 0)
			{
				Rectangle bounds = args.Bounds;
				bounds.Inflate(-2, 0);
				TextRenderer.DrawText(args.Graphics, combo.GetItemText(combo.Items[args.Index]),
					combo.Font, bounds, DarkText, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
			}

			args.DrawFocusRectangle();
		}

		// Применяет цвета таблицы, не сбрасывая форматирование и привязки столбцов.
		private static void ApplyGridColors(DataGridView grid, bool dark)
		{
			grid.BackgroundColor = dark ? DarkField : SystemColors.Window;
			grid.GridColor = dark ? DarkBorder : SystemColors.ControlDark;
			grid.EnableHeadersVisualStyles = !dark;
			grid.ColumnHeadersBorderStyle = dark ? DataGridViewHeaderBorderStyle.Single : DataGridViewHeaderBorderStyle.Raised;
			DataGridViewCellStyle rows = grid.DefaultCellStyle;
			rows.BackColor = dark ? DarkField : SystemColors.Window;
			rows.ForeColor = dark ? DarkText : SystemColors.ControlText;
			rows.SelectionBackColor = dark ? DarkHighlight : SystemColors.Highlight;
			rows.SelectionForeColor = dark ? DarkText : SystemColors.HighlightText;
			DataGridViewCellStyle headers = grid.ColumnHeadersDefaultCellStyle;
			headers.BackColor = dark ? DarkButton : SystemColors.Control;
			headers.ForeColor = rows.ForeColor;
			headers.SelectionBackColor = rows.SelectionBackColor;
			headers.SelectionForeColor = rows.SelectionForeColor;
			foreach (DataGridViewButtonColumn column in grid.Columns.OfType<DataGridViewButtonColumn>())
			{
				column.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
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
				: ColorTranslator.ToWin32(dark ? DarkSurface : Color.FromArgb(204, 228, 247));
			if (DwmSetWindowAttribute(window.Handle, DwmCaptionColor, ref background, sizeof(int)) == 0)
			{
				int text = highContrast ? DwmDefaultColor
					: ColorTranslator.ToWin32(dark ? DarkText : Color.FromArgb(43, 34, 28));
				DwmSetWindowAttribute(window.Handle, DwmTextColor, ref text, sizeof(int));
			}
		}
	}
}
