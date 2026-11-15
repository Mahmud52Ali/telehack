using System;
using System.Globalization;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.App.Theming;

namespace Telecom.App.Forms.Settings
{
	// Вкладка «Персонализация»: тема и масштаб шрифта. Сохраняется сразу в appearance.xml, отдельно от настроек сканирования.
	internal sealed class AppearanceTab : TabPage
	{
		private readonly string appearancePath;
		private readonly StatusReporter status;
		private readonly ComboBox themeInput = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
		private readonly ComboBox fontScaleInput = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
		private readonly Label appearanceStatus = new Label { AutoSize = true };
		private bool updatingAppearance;

		// Текущие сохранённые тема и масштаб.
		internal AppearanceSettings Appearance { get; private set; }

		// Пользователь выбрал другую тему или масштаб, и выбор сохранён.
		internal event EventHandler AppearanceChanged;

		// Даёт выбрать тему и размер шрифта; повреждённый файл оформления не мешает открыть окно.
		internal AppearanceTab(string appearancePath, StatusReporter status) : base("Персонализация")
		{
			this.appearancePath = appearancePath;
			this.status = status;
			var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 3, Padding = new Padding(8) };
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			themeInput.AccessibleName = "Тема";
			themeInput.Items.AddRange(new object[] { "Светлая", "Тёмная", "Системная" });
			fontScaleInput.AccessibleName = "Масштаб шрифта";
			for (int percent = AppearanceSettings.MinFontPercent; percent <= AppearanceSettings.MaxFontPercent; percent += AppearanceSettings.FontPercentStep)
			{
				fontScaleInput.Items.Add(percent.ToString(CultureInfo.InvariantCulture) + "%");
			}

			layout.Controls.Add(new Label { Text = "Тема", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
			layout.Controls.Add(themeInput, 1, 0);
			layout.Controls.Add(new Label { Text = "Масштаб шрифта", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
			layout.Controls.Add(fontScaleInput, 1, 1);
			layout.Controls.Add(appearanceStatus, 0, 2);
			layout.SetColumnSpan(appearanceStatus, 2);
			Controls.Add(layout);

			LoadAppearance();
			themeInput.SelectedIndexChanged += OnAppearanceChanged;
			fontScaleInput.SelectedIndexChanged += OnAppearanceChanged;
		}

		// Читает сохранённое оформление; при ошибке остаются светлая тема и масштаб 100%.
		private void LoadAppearance()
		{
			Appearance = AppearanceSettings.Default;
			try
			{
				Appearance = AppearanceSettings.Load(appearancePath);
			}
			catch (Exception exception)
			{
				status.ReportFailure(this, appearanceStatus, "Не удалось прочитать оформление. Применены стандартные настройки.", exception);
			}

			ShowAppearanceChoices();
		}

		// Показывает сохранённые значения без повторной записи при программном выборе.
		private void ShowAppearanceChoices()
		{
			updatingAppearance = true;
			try
			{
				themeInput.SelectedIndex = (int)Appearance.Theme;
				fontScaleInput.SelectedIndex = (Appearance.FontPercent - AppearanceSettings.MinFontPercent) / AppearanceSettings.FontPercentStep;
			}
			finally
			{
				updatingAppearance = false;
			}
		}

		// Сохраняет выбор сразу, а при ошибке возвращает предыдущие значения в выпадающие списки.
		private void OnAppearanceChanged(object sender, EventArgs args)
		{
			if (updatingAppearance || themeInput.SelectedIndex < 0 || fontScaleInput.SelectedIndex < 0)
			{
				return;
			}

			int fontPercent = AppearanceSettings.MinFontPercent + fontScaleInput.SelectedIndex * AppearanceSettings.FontPercentStep;
			var selection = new AppearanceSettings((AppearanceTheme)themeInput.SelectedIndex, fontPercent);
			if (selection.Theme == Appearance.Theme && selection.FontPercent == Appearance.FontPercent)
			{
				return;
			}

			try
			{
				selection.Save(appearancePath);
			}
			catch (Exception exception)
			{
				ShowAppearanceChoices();
				status.ReportFailure(this, appearanceStatus, "Не удалось сохранить оформление.", exception);
				return;
			}

			Appearance = selection;
			status.Show(appearanceStatus, string.Empty, false);
			EventHandler changed = AppearanceChanged;
			if (changed != null)
			{
				changed(this, EventArgs.Empty);
			}
		}
	}
}
