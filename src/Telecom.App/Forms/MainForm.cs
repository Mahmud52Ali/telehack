using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Telecom.App.Forms.Database;
using Telecom.App.Forms.Scan;
using Telecom.App.Forms.Settings;
using Telecom.App.Theming;
using Telecom.App.Windows;
using Telecom.Core.Access;
using Telecom.Core.Computers;
using Telecom.Core.Logging;

namespace Telecom.App.Forms
{
	// Главное окно: навигация между страницами «Компьютеры», «База данных» и «Настройки», тема и закрытие.
	// Работа самих страниц описана в их классах.
	public sealed class MainForm : Form
	{
		private const int WmSystemColorChange = 0x0015;
		private const int WmSettingChange = 0x001A;
		private readonly ILogger logger;
		private readonly OperationGate operations = new OperationGate();
		private readonly RadioButton computersButton = new RadioButton { Text = "Компьютеры", Appearance = Appearance.Button, AutoSize = true, Checked = true };
		private readonly RadioButton databaseButton = new RadioButton { Text = "База данных", Appearance = Appearance.Button, AutoSize = true };
		private readonly RadioButton settingsButton = new RadioButton { Text = "Настройки", Appearance = Appearance.Button, AutoSize = true };
		private readonly ScanPage scanWorkspace;
		private readonly DatabasePage databaseWorkspace;
		private readonly SettingsPanel settingsPanel;

		// Создаёт окно с настоящими адаптерами Windows; подключение сохранённых ПК начинается после появления окна.
		public MainForm(ILogger logger)
			: this(logger, new ComputerListFile(AppPaths.ComputerListPath),
				new WindowsFileSystem(), null, new WindowsMachineCertificateStore())
		{
		}

		// Принимает хранилище и сетевые адаптеры, чтобы проверять полный сценарий без настоящих ПК и паролей.
		// Рабочий XML и appearance.xml лежат в той же папке, что и список ПК.
		public MainForm(ILogger logger, ComputerListFile computerListFile, IComputerAccess computerAccess, WindowsSmbConnection connection,
			IMachineCertificateStore machineCertificateStore = null)
		{
			this.logger = logger;
			string configurationFilePath = Path.Combine(Path.GetDirectoryName(computerListFile.FilePath), "scan.config.xml");
			string configurationTemplatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scan.config.template.xml");
			var credentialStore = new WindowsCredentialStore();
			IComputerConnector connector = connection ?? new WindowsSmbConnection(credentialStore);
			Text = "СКЗИ — сканирование сертификатов";
			WindowAppearance.Apply(this, AutoScaleMode.Dpi);
			Size = new Size(1200, 850);
			MinimumSize = new Size(1000, 700);
			StartPosition = FormStartPosition.CenterScreen;

			settingsPanel = new SettingsPanel(configurationFilePath, configurationTemplatePath, logger);
			Func<int> fontPercent = () => settingsPanel.Appearance.FontPercent;
			scanWorkspace = new ScanPage(logger, computerListFile, configurationFilePath, configurationTemplatePath,
				computerAccess, connector, machineCertificateStore, credentialStore, operations, fontPercent);
			databaseWorkspace = new DatabasePage(logger, configurationFilePath, computerAccess, connector, machineCertificateStore,
				operations, () => scanWorkspace.LoadedProfiles, fontPercent);
			settingsPanel.ConfigurationChanged += (sender, args) => scanWorkspace.ShowStatus("Настройки сканирования обновлены.");
			settingsPanel.AppearanceChanged += (sender, args) => ApplyAppearance();
			operations.StateChanged += (sender, args) => UpdateState();

			BuildLayout();
			ApplyAppearance();
			Shown += async (sender, args) => await scanWorkspace.InitializeAsync();
			FormClosing += OnFormClosing;
		}

		// Показывает навигацию над тремя страницами; видна только выбранная.
		private void BuildLayout()
		{
			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 2, ColumnCount = 1 };
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty, ColumnCount = 3, RowCount = 1 };
			navigation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			navigation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			navigation.Controls.Add(computersButton, 0, 0);
			navigation.Controls.Add(databaseButton, 1, 0);
			navigation.Controls.Add(settingsButton, 2, 0);
			layout.Controls.Add(navigation, 0, 0);

			var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
			body.Controls.Add(scanWorkspace);
			body.Controls.Add(databaseWorkspace);
			body.Controls.Add(settingsPanel);
			databaseWorkspace.Visible = false;
			settingsPanel.Visible = false;
			layout.Controls.Add(body, 0, 1);
			Controls.Add(layout);

			scanWorkspace.IsActive = true;
			databaseWorkspace.AlignRefreshButton(settingsButton);
			AcceptButton = scanWorkspace.ScanButton;
			computersButton.CheckedChanged += OnWorkspaceChanged;
			databaseButton.CheckedChanged += OnWorkspaceChanged;
			settingsButton.CheckedChanged += OnWorkspaceChanged;
		}

		// Открывает выбранную страницу и перечитывает данные при переходе в настройки или базу.
		private async void OnWorkspaceChanged(object sender, EventArgs args)
		{
			if (!((RadioButton)sender).Checked)
			{
				return;
			}

			scanWorkspace.Visible = computersButton.Checked;
			databaseWorkspace.Visible = databaseButton.Checked;
			settingsPanel.Visible = settingsButton.Checked;
			scanWorkspace.IsActive = computersButton.Checked;
			databaseWorkspace.IsActive = databaseButton.Checked;
			UpdateState();
			if (settingsButton.Checked)
			{
				settingsPanel.RefreshFromDisk();
			}
			else
			{
				settingsPanel.HideConnectionString();
			}

			if (databaseButton.Checked)
			{
				await databaseWorkspace.RefreshDatabase();
			}
		}

		// Пересчитывает доступность кнопок всех страниц; во время операции настройки недоступны.
		// Если окно ждёт завершения операций для закрытия, закрывает его.
		private void UpdateState()
		{
			scanWorkspace.UpdateState();
			databaseWorkspace.UpdateState();
			settingsButton.Enabled = !operations.IsBusy;
			AcceptButton = scanWorkspace.ScanButton.Enabled ? scanWorkspace.ScanButton : null;
			if (operations.ClosePending && !operations.IsBusy && !databaseWorkspace.IsDeleting)
			{
				operations.ClosePending = false;
				Close();
			}
		}

		// Применяет сохранённый масштаб и разрешает системную тему в текущую палитру.
		private void ApplyAppearance()
		{
			WindowAppearance.ApplyFont(this, settingsPanel.Appearance.FontPercent);
			UpdateTheme();
		}

		// Обновляет палитру также у открытых диалогов приложения.
		private void UpdateTheme()
		{
			bool dark = WindowAppearance.UseDarkTheme(settingsPanel.Appearance.Theme, logger);
			WindowAppearance.ApplyTheme(this, dark);
			foreach (Form dialog in OwnedForms)
			{
				WindowAppearance.ApplyTheme(dialog, dark);
			}
		}

		// Подхватывает смену системной палитры из уведомлений Windows.
		protected override void WndProc(ref Message message)
		{
			base.WndProc(ref message);
			if (settingsPanel != null && IsHandleCreated && !Disposing && !IsDisposed
				&& (message.Msg == WmSettingChange || message.Msg == WmSystemColorChange))
			{
				UpdateTheme();
			}
		}

		// Не обрывает текущее обращение к Windows или к базе: просит операцию остановиться и закрывает окно после неё.
		private void OnFormClosing(object sender, FormClosingEventArgs args)
		{
			if (operations.IsBusy)
			{
				operations.ClosePending = true;
				args.Cancel = true;
				operations.Cancel();
				const string message = "Закрытие… Ожидание текущего обращения к Windows.";
				if (databaseWorkspace.IsCheckingLocations)
				{
					databaseWorkspace.ShowStatus(message);
				}
				else
				{
					scanWorkspace.ShowStatus(message);
				}
			}
			else if (databaseWorkspace.IsDeleting)
			{
				operations.ClosePending = true;
				args.Cancel = true;
				databaseWorkspace.ShowStatus("Закрытие… Ожидание ответа базы данных.");
			}
			else if (args.CloseReason == CloseReason.UserClosing && settingsPanel.HasUnsavedChanges)
			{
				args.Cancel = MessageBox.Show(this, "Закрыть приложение без сохранения изменений настроек?", "Настройки",
					MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes;
			}
		}
	}
}
