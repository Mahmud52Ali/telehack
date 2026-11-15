using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.App.Theming;
using Telecom.Core.Configuration;
using Telecom.Core.Logging;

namespace Telecom.App.Forms.Settings
{
	// Страница «Настройки»: вкладки сканирования, базы данных, файлов и оформления.
	// Изменения сканирования и базы — черновик до нажатия «Сохранить»; оформление сохраняется сразу.
	internal sealed class SettingsPanel : UserControl
	{
		private readonly string configurationPath;
		private readonly StatusReporter status;
		private readonly TabControl tabs = new AppearanceTabControl { Dock = DockStyle.Fill };
		private readonly ScanSettingsTab scanPage = new ScanSettingsTab();
		private readonly DatabaseSettingsTab databasePage;
		private readonly FilesTab filesPage;
		private readonly AppearanceTab personalizationPage;
		private readonly Button saveButton = new Button { Text = "Сохранить", AutoSize = true, Enabled = false };
		private readonly Button discardButton = new Button { Text = "Отменить изменения", AutoSize = true, Enabled = false };
		private readonly Label settingsStatusLabel = new Label
		{
			Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
		};
		private bool updating;

		// Есть ли несохранённые изменения сканирования или базы данных.
		internal bool HasUnsavedChanges { get; private set; }

		// Текущие тема и масштаб шрифта.
		internal AppearanceSettings Appearance { get { return personalizationPage.Appearance; } }

		// Рабочий XML сохранён, загружен или восстановлен.
		internal event EventHandler ConfigurationChanged;

		// Тема или масштаб изменились.
		internal event EventHandler AppearanceChanged;

		// Создаёт вкладки; appearance.xml лежит рядом с рабочим XML.
		internal SettingsPanel(string configurationPath, string templatePath, ILogger logger)
		{
			this.configurationPath = configurationPath;
			Dock = DockStyle.Fill;
			status = new StatusReporter(logger);
			databasePage = new DatabaseSettingsTab(status);
			filesPage = new FilesTab(configurationPath, templatePath, status, settingsStatusLabel,
				() => HasUnsavedChanges, () => Appearance.FontPercent);
			status.FailureLogged += (sender, args) => filesPage.RefreshLinks();
			personalizationPage = new AppearanceTab(Path.Combine(Path.GetDirectoryName(configurationPath), "appearance.xml"), status);

			scanPage.Changed += (sender, args) => MarkChanged();
			databasePage.Changed += (sender, args) => MarkChanged();
			filesPage.ConfigurationReplaced += (configuration, message) =>
			{
				ApplyConfiguration(configuration);
				OnConfigurationSaved(message);
			};
			personalizationPage.AppearanceChanged += (sender, args) =>
			{
				EventHandler changed = AppearanceChanged;
				if (changed != null)
				{
					changed(this, EventArgs.Empty);
				}
			};

			tabs.TabPages.AddRange(new TabPage[] { scanPage, databasePage, filesPage, personalizationPage });
			tabs.Selecting += (sender, args) => args.Cancel = args.TabPage != null && !args.TabPage.Enabled;

			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.Controls.Add(tabs, 0, 0);
			layout.SetColumnSpan(tabs, 2);
			layout.Controls.Add(settingsStatusLabel, 0, 1);
			FlowLayoutPanel configurationActions = StatusReporter.CreateActions(discardButton, saveButton);
			layout.Controls.Add(configurationActions, 1, 1);
			tabs.SelectedIndexChanged += (sender, args) =>
			{
				// Кнопки сохранения относятся к XML, а оформление сохраняется само.
				bool configurationPage = tabs.SelectedTab != personalizationPage;
				settingsStatusLabel.Visible = configurationPage;
				configurationActions.Visible = configurationPage;
				filesPage.RefreshLinks();
			};
			saveButton.Click += OnSave;
			discardButton.Click += (sender, args) => LoadConfiguration();
			Controls.Add(layout);
		}

		// Перечитывает файлы при входе в настройки, сохраняя редактируемый черновик.
		internal void RefreshFromDisk()
		{
			filesPage.RefreshLinks();
			if (!HasUnsavedChanges)
			{
				LoadConfiguration();
			}
		}

		// Скрывает строку подключения при выходе из настроек.
		internal void HideConnectionString()
		{
			databasePage.HideConnectionString();
		}

		// Освобождает подсказки строк состояния.
		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				status.Dispose();
			}

			base.Dispose(disposing);
		}

		// Проверяет редактируемые пути и сохраняет их вместе с параметрами MSSQL и таймаутом.
		private void OnSave(object sender, EventArgs args)
		{
			try
			{
				scanPage.EndEdit();
				string[] directories = scanPage.ReadDirectories();
				string[] cryptoPaths = scanPage.ReadCryptoProExecutables();
				int timeout;
				try
				{
					timeout = scanPage.ReadTimeout();
				}
				catch (InvalidDataException)
				{
					tabs.SelectedTab = scanPage;
					throw;
				}

				var configuration = new ScanConfiguration(directories, cryptoPaths, timeout, databasePage.ConnectionString);
				ScanConfigurationFile.Save(configuration, configurationPath);
				ApplyConfiguration(configuration);
				OnConfigurationSaved("Настройки сохранены.");
			}
			catch (Exception exception)
			{
				status.ReportFailure(this, settingsStatusLabel, "Не удалось сохранить настройки.", exception);
			}
		}

		// Перечитывает сохранённое, оставляя импорт и восстановление доступными при повреждении файла.
		private void LoadConfiguration()
		{
			try
			{
				ApplyConfiguration(ScanConfigurationFile.Load(configurationPath));
				status.Show(settingsStatusLabel, string.Empty, false);
			}
			catch (Exception exception)
			{
				scanPage.Enabled = false;
				databasePage.Enabled = false;
				if (tabs.SelectedTab != personalizationPage)
				{
					tabs.SelectedTab = filesPage;
				}
				SetUnsavedChanges(false);
				status.ReportFailure(this, settingsStatusLabel,
					"Не удалось прочитать рабочий XML. Загрузите или восстановите конфигурацию.", exception);
			}
		}

		// Подставляет проверенные значения и сбрасывает состояние черновика.
		private void ApplyConfiguration(ScanConfiguration configuration)
		{
			updating = true;
			try
			{
				scanPage.Enabled = true;
				databasePage.Enabled = true;
				scanPage.ShowConfiguration(configuration);
				databasePage.ShowConnectionString(configuration.ConnectionString);
				SetUnsavedChanges(false);
			}
			finally
			{
				updating = false;
			}
		}

		// Оповещает основное окно о сохранении и перечитывает доступность файлов.
		private void OnConfigurationSaved(string message)
		{
			filesPage.RefreshLinks();
			status.Show(settingsStatusLabel, message, false);
			EventHandler changed = ConfigurationChanged;
			if (changed != null)
			{
				changed(this, EventArgs.Empty);
			}
		}

		// Включает действия черновика после изменения любого поля.
		private void MarkChanged()
		{
			if (!updating)
			{
				SetUnsavedChanges(true);
				status.Show(settingsStatusLabel, "Есть несохранённые изменения.", false);
			}
		}

		// Согласует доступность сохранения и отмены с состоянием формы.
		private void SetUnsavedChanges(bool changed)
		{
			HasUnsavedChanges = changed;
			saveButton.Enabled = changed;
			discardButton.Enabled = changed;
		}
	}
}
