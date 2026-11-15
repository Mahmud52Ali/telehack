using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Telecom.App.Theming
{
	// Окраска отдельных элементов (таблиц, кнопок, списков, полей) в светлую или тёмную тему.
	internal static class ControlTheme
	{
		// Перекрашивает поля, таблицы и кнопки, сохраняя настройки самих элементов.
		internal static void Apply(Control parent, bool dark)
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
					button.BackColor = dark ? ThemePalette.DarkButton : Color.Empty;
					button.UseVisualStyleBackColor = !dark;
					button.FlatAppearance.BorderColor = dark ? ThemePalette.DarkBorder : Color.Empty;
					button.FlatAppearance.MouseOverBackColor = dark ? ThemePalette.DarkBorder : Color.Empty;
					button.FlatAppearance.MouseDownBackColor = dark ? ThemePalette.DarkHighlight : Color.Empty;
					button.FlatAppearance.CheckedBackColor = dark ? ThemePalette.DarkHighlight : Color.Empty;
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
					page.BackColor = dark ? ThemePalette.DarkSurface : Color.Empty;
					page.UseVisualStyleBackColor = !dark;
				}
				else if (combo != null)
				{
					combo.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
					combo.BackColor = dark ? ThemePalette.DarkField : Color.Empty;
					combo.ForeColor = dark ? ThemePalette.DarkText : Color.Empty;
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
					control.BackColor = dark ? ThemePalette.DarkField : Color.Empty;
					control.ForeColor = dark ? ThemePalette.DarkText : Color.Empty;
				}
				else if (group != null)
				{
					group.ForeColor = dark ? ThemePalette.DarkText : Color.Empty;
					group.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
				}
				else if (control is Label && (control.ForeColor == ThemePalette.DarkError || control.ForeColor == Color.Firebrick))
				{
					control.ForeColor = dark ? ThemePalette.DarkError : Color.Firebrick;
				}

				if (grid == null)
				{
					Apply(control, dark);
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
			using (var fill = new SolidBrush(ThemePalette.DarkButton))
			using (var border = new Pen(ThemePalette.DarkBorder))
			{
				args.Graphics.FillRectangle(fill, bounds);
				args.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
			}

			TextRenderer.DrawText(args.Graphics, button.Text, button.Font, bounds, ThemePalette.DarkMutedText,
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
			using (var background = new SolidBrush(selected ? ThemePalette.DarkHighlight : ThemePalette.DarkField))
			{
				args.Graphics.FillRectangle(background, args.Bounds);
			}

			if (args.Index >= 0)
			{
				Rectangle bounds = args.Bounds;
				bounds.Inflate(-2, 0);
				TextRenderer.DrawText(args.Graphics, combo.GetItemText(combo.Items[args.Index]),
					combo.Font, bounds, ThemePalette.DarkText, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
			}

			args.DrawFocusRectangle();
		}

		// Применяет цвета таблицы, не сбрасывая форматирование и привязки столбцов.
		private static void ApplyGridColors(DataGridView grid, bool dark)
		{
			grid.BackgroundColor = dark ? ThemePalette.DarkField : SystemColors.Window;
			grid.GridColor = dark ? ThemePalette.DarkBorder : SystemColors.ControlDark;
			grid.EnableHeadersVisualStyles = !dark;
			grid.ColumnHeadersBorderStyle = dark ? DataGridViewHeaderBorderStyle.Single : DataGridViewHeaderBorderStyle.Raised;
			DataGridViewCellStyle rows = grid.DefaultCellStyle;
			rows.BackColor = dark ? ThemePalette.DarkField : SystemColors.Window;
			rows.ForeColor = dark ? ThemePalette.DarkText : SystemColors.ControlText;
			rows.SelectionBackColor = dark ? ThemePalette.DarkHighlight : SystemColors.Highlight;
			rows.SelectionForeColor = dark ? ThemePalette.DarkText : SystemColors.HighlightText;
			DataGridViewCellStyle headers = grid.ColumnHeadersDefaultCellStyle;
			headers.BackColor = dark ? ThemePalette.DarkButton : SystemColors.Control;
			headers.ForeColor = rows.ForeColor;
			headers.SelectionBackColor = rows.SelectionBackColor;
			headers.SelectionForeColor = rows.SelectionForeColor;
			foreach (DataGridViewButtonColumn column in grid.Columns.OfType<DataGridViewButtonColumn>())
			{
				column.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
			}
		}
	}
}
