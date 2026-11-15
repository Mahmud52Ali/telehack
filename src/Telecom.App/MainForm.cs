using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Telecom.Core.Access;
using Telecom.Core.Certificates;
using Telecom.Core.Computers;
using Telecom.Core.Configuration;
using Telecom.Core.Database;
using Telecom.Core.Locations;
using Telecom.Core.Logging;
using Telecom.Core.Scanning;

namespace Telecom.App
{
	public sealed class MainForm : Form
	{
		private const string CertificateExpiryField = "ExpiresAtUtc";
		private const int WmSystemColorChange = 0x0015;
		private const int WmSettingChange = 0x001A;
		private readonly ILogger logger;
		private readonly DataGridView computersGrid = new DataGridView();
		private readonly BindingList<ComputerListItem> computers = new BindingList<ComputerListItem>();
		private readonly ComputerListFile computerListFile;
		private readonly string configurationFilePath;
		private readonly string configurationTemplatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scan.config.template.xml");
		private readonly WindowsCredentialStore credentialStore = new WindowsCredentialStore();
		private readonly IComputerAccess computerAccess;
		private readonly WindowsSmbConnection connection;
		private readonly IMachineCertificateStore machineCertificateStore;
		private readonly Button addComputerButton = new Button();
		private readonly Button editComputerButton = new Button();
		private readonly Button removeComputerButton = new Button();
		private readonly Button connectButton = new Button();
		private readonly Panel computersPanel = new Panel { Dock = DockStyle.Fill };
		private readonly RadioButton computersButton = new RadioButton { Text = "Компьютеры", Appearance = Appearance.Button, AutoSize = true, Checked = true };
		private readonly RadioButton databaseButton = new RadioButton { Text = "База данных", Appearance = Appearance.Button, AutoSize = true };
		private readonly RadioButton settingsButton = new RadioButton { Text = "Настройки", Appearance = Appearance.Button, AutoSize = true };
		private readonly TableLayoutPanel scanWorkspace = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 4 };
		private readonly TableLayoutPanel databaseWorkspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
		private readonly SettingsPanel settingsPanel;
		private readonly Button scanButton = new Button();
		private readonly Button cancelButton = new Button();
		private readonly Label statusLabel = new Label();
		private readonly DataGridView certificatesGrid = new DataGridView();
		private readonly DataGridView errorsGrid = new DataGridView();
		private readonly TabControl databaseTables = new AppearanceTabControl { Dock = DockStyle.Fill };
		private readonly TabPage certificateLocationsPage = new TabPage("Сертификаты по компьютерам");
		private readonly DataGridView databaseGrid = new DataGridView();
		private readonly DataGridView certificateLocationsGrid = new DataGridView();
		private readonly Button refreshDatabaseButton = new Button { Text = "Обновить" };
		private readonly Button checkLocationsButton = new Button { Text = "Проверить наличие", AutoSize = true, Visible = false };
		private readonly ToolStripMenuItem checkLocationMenuItem = new ToolStripMenuItem("Проверить наличие");
		private readonly Label databaseStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
		private CancellationTokenSource scanCancellation;
		private CertificateRepository displayedCertificateRepository;
		private bool databaseRefreshPending;
		private bool deletingCertificate;
		private bool checkingLocations;
		private bool changingSelection;
		private bool profilesLoaded;
		private bool closeWhenIdle;

		// Создаёт список ПК; подключение сохранённых компьютеров начинается после появления окна.
		public MainForm(ILogger logger)
			: this(logger, new ComputerListFile(Path.Combine(Program.DataDirectory, "configs", "computers.xml")),
				new WindowsFileSystem(), null, new WindowsMachineCertificateStore())
		{
		}

		// Принимает хранилище и сетевые адаптеры для проверки полного сценария без настоящих ПК и паролей.
		public MainForm(ILogger logger, ComputerListFile computerListFile, IComputerAccess computerAccess, WindowsSmbConnection connection,
			IMachineCertificateStore machineCertificateStore = null)
		{
			this.logger = logger;
			this.computerListFile = computerListFile;
			configurationFilePath = Path.Combine(Path.GetDirectoryName(computerListFile.FilePath), "scan.config.xml");
			this.computerAccess = computerAccess;
			this.connection = connection ?? new WindowsSmbConnection(credentialStore);
			this.machineCertificateStore = machineCertificateStore;
			computers.Add(new ComputerListItem(ComputerProfile.CreateLocal(false)));
			Text = "СКЗИ — сканирование сертификатов";
			WindowAppearance.Apply(this, AutoScaleMode.Dpi);
			Size = new Size(1200, 850);
			MinimumSize = new Size(1000, 700);
			StartPosition = FormStartPosition.CenterScreen;
			settingsPanel = new SettingsPanel(configurationFilePath, configurationTemplatePath, logger);
			settingsPanel.ConfigurationChanged += (sender, args) => statusLabel.Text = "Настройки сканирования обновлены.";
			settingsPanel.AppearanceChanged += (sender, args) => ApplyAppearance();
			BuildLayout();
			ApplyAppearance();
			Shown += OnShown;
			FormClosing += OnFormClosing;
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

		// Показывает навигацию над рабочими областями компьютеров, базы данных и настроек.
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

			scanWorkspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
			scanWorkspace.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			scanWorkspace.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			scanWorkspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			BuildComputersPanel();
			scanWorkspace.Controls.Add(computersPanel, 0, 0);

			var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
			scanButton.Text = "Сканировать";
			scanButton.AutoSize = true;
			scanButton.Click += OnScan;
			cancelButton.Text = "Отменить";
			cancelButton.AutoSize = true;
			cancelButton.Enabled = false;
			cancelButton.Click += OnCancel;
			actions.Controls.Add(scanButton);
			actions.Controls.Add(cancelButton);
			scanWorkspace.Controls.Add(actions, 0, 1);
			AcceptButton = scanButton;

			statusLabel.Text = "Отметьте этот компьютер или добавьте ПК сети для сканирования.";
			statusLabel.AutoSize = true;
			statusLabel.Dock = DockStyle.Fill;
			statusLabel.Margin = new Padding(0, 0, 0, 10);
			scanWorkspace.Controls.Add(statusLabel, 0, 2);

			ConfigureGrid(certificatesGrid);
			AddColumn(certificatesGrid, "OwnerName", "Владелец", 110);
			AddColumn(certificatesGrid, "CertificationAuthority", "Удостоверяющий центр", 110);
			AddColumn(certificatesGrid, "SerialNumber", "Серийный номер", 65);
			certificatesGrid.Columns.Add(new DataGridViewCheckBoxColumn
			{
				DataPropertyName = "IsCryptoProLicenseEmbedded", HeaderText = "Лицензия в сертификате", FillWeight = 55
			});
			AddColumn(certificatesGrid, "SourcePath", "Источник", 160);
			ConfigureGrid(errorsGrid);
			AddColumn(errorsGrid, "Kind", "Категория", 55);
			AddColumn(errorsGrid, "Path", "Путь", 120);
			AddColumn(errorsGrid, "Message", "Причина", 140);
			errorsGrid.CellFormatting += OnErrorFormatting;

			var tabs = new AppearanceTabControl { Dock = DockStyle.Fill };
			var certificateTab = new TabPage("Сертификаты");
			certificateTab.Controls.Add(certificatesGrid);
			var errorTab = new TabPage("Ошибки");
			errorTab.Controls.Add(errorsGrid);
			tabs.TabPages.Add(certificateTab);
			tabs.TabPages.Add(errorTab);
			scanWorkspace.Controls.Add(tabs, 0, 3);

			var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
			body.Controls.Add(scanWorkspace);
			BuildDatabaseWorkspace();
			body.Controls.Add(databaseWorkspace);
			body.Controls.Add(settingsPanel);
			databaseWorkspace.Visible = false;
			settingsPanel.Visible = false;
			layout.Controls.Add(body, 0, 1);
			Controls.Add(layout);
			computersButton.CheckedChanged += OnWorkspaceChanged;
			databaseButton.CheckedChanged += OnWorkspaceChanged;
			settingsButton.CheckedChanged += OnWorkspaceChanged;
		}

		// Открывает выбранную область и перечитывает данные при переходе на вкладку базы.
		private async void OnWorkspaceChanged(object sender, EventArgs args)
		{
			if (!((RadioButton)sender).Checked)
			{
				return;
			}

			scanWorkspace.Visible = computersButton.Checked;
			databaseWorkspace.Visible = databaseButton.Checked;
			settingsPanel.Visible = settingsButton.Checked;
			SetScanning(scanCancellation != null);
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
				await RefreshDatabase();
			}
		}

		// Даёт переключаться между реестром и местами обнаружения сертификатов.
		private void BuildDatabaseWorkspace()
		{
			ConfigureGrid(databaseGrid);
			databaseGrid.AccessibleName = "Сохранённые сертификаты";
			AddColumn(databaseGrid, "SerialNumber", "Серийный номер", 145);
			const string timestampFormat = "dd.MM.yyyy HH:mm:ss.fff";
			AddColumn(databaseGrid, "CreatedAt", "Внесён", 155).DefaultCellStyle.Format = timestampFormat;
			AddColumn(databaseGrid, "UpdatedAt", "Обновлён", 155).DefaultCellStyle.Format = timestampFormat;
			AddColumn(databaseGrid, "OwnerName", "Владелец", 165);
			AddColumn(databaseGrid, "CertificationAuthority", "Удостоверяющий центр", 165);
			AddColumn(databaseGrid, "CryptoProVersion", "Версия КриптоПро", 95).DefaultCellStyle.NullValue = "—";
			databaseGrid.Columns.Add(new DataGridViewCheckBoxColumn
			{
				DataPropertyName = "IsCryptoProLicenseEmbedded", HeaderText = "Лицензия в сертификате", FillWeight = 95
			});
			AddDatabaseExpiryAndDeleteColumns(databaseGrid);

			ConfigureGrid(certificateLocationsGrid);
			certificateLocationsGrid.AccessibleName = "Сертификаты по компьютерам";
			AddColumn(certificateLocationsGrid, "SerialNumber", "Серийный номер", 120);
			AddColumn(certificateLocationsGrid, "OwnerName", "Владелец", 150);
			AddColumn(certificateLocationsGrid, "CertificationAuthority", "Удостоверяющий центр", 150);
			AddColumn(certificateLocationsGrid, "ComputerAddress", "IP компьютера", 105).ToolTipText = "Для локального сканирования здесь указано имя компьютера";
			AddColumn(certificateLocationsGrid, "SourcePath", "Путь к сертификату", 265);
			AddColumn(certificateLocationsGrid, "CheckStatus", "Наличие", 240).MinimumWidth = 200;
			AddColumn(certificateLocationsGrid, "CheckedAtUtc", "Проверено", 150).MinimumWidth = 125;
			certificateLocationsGrid.CellFormatting += OnLocationCheckFormatting;
			var locationMenu = new ContextMenuStrip();
			locationMenu.Items.Add(checkLocationMenuItem);
			locationMenu.Opening += (sender, args) =>
			{
				locationMenu.Font = certificateLocationsGrid.Font;
				UpdateLocationCheckButton();
				args.Cancel = !checkLocationMenuItem.Enabled;
			};
			checkLocationMenuItem.Click += OnCheckLocation;
			certificateLocationsGrid.ContextMenuStrip = locationMenu;
			certificateLocationsGrid.MouseDown += (sender, args) =>
			{
				if (args.Button == MouseButtons.Right)
				{
					DataGridView.HitTestInfo hit = certificateLocationsGrid.HitTest(args.X, args.Y);
					certificateLocationsGrid.ClearSelection();
					certificateLocationsGrid.CurrentCell = hit.RowIndex < 0 ? null
						: certificateLocationsGrid.Rows[hit.RowIndex].Cells[Math.Max(0, hit.ColumnIndex)];
				}
			};
			certificateLocationsGrid.Disposed += (sender, args) => locationMenu.Dispose();
			AddDatabaseExpiryAndDeleteColumns(certificateLocationsGrid);

			var registryPage = new TabPage("Реестр сертификатов");
			registryPage.Controls.Add(databaseGrid);
			certificateLocationsPage.Controls.Add(certificateLocationsGrid);
			databaseTables.TabPages.Add(registryPage);
			databaseTables.TabPages.Add(certificateLocationsPage);
			databaseTables.SelectedIndexChanged += async (sender, args) =>
			{
				UpdateLocationCheckButton();
				await RefreshDatabase();
			};

			refreshDatabaseButton.Size = settingsButton.Size;
			settingsButton.SizeChanged += (sender, args) => refreshDatabaseButton.Size = settingsButton.Size;
			refreshDatabaseButton.Click += async (sender, args) => await RefreshDatabase();
			checkLocationsButton.Click += OnCheckLocations;
			var databaseActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
			databaseActions.Controls.Add(checkLocationsButton);
			databaseActions.Controls.Add(refreshDatabaseButton);

			databaseWorkspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			databaseWorkspace.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			databaseWorkspace.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			databaseWorkspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			databaseWorkspace.Controls.Add(databaseStatusLabel, 0, 0);
			databaseWorkspace.Controls.Add(databaseActions, 1, 0);
			databaseWorkspace.Controls.Add(databaseTables, 0, 1);
			databaseWorkspace.SetColumnSpan(databaseTables, 2);
		}

		// Добавляет дату окончания и действие удаления с подсказкой для текущей таблицы.
		private void AddDatabaseExpiryAndDeleteColumns(DataGridView grid)
		{
			DataGridViewColumn expiration = AddColumn(grid, CertificateExpiryField, "Дата истечения", 155);
			expiration.MinimumWidth = 125;
			expiration.DefaultCellStyle.NullValue = "—";
			grid.CellFormatting += OnCertificateExpiryFormatting;
			grid.Columns.Add(new DataGridViewButtonColumn
			{
				HeaderText = "Удалить", Text = "×", UseColumnTextForButtonValue = true, Width = 75,
				AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
				ToolTipText = grid == certificateLocationsGrid ? "Удалить выбранную запись о компьютере"
					: "Удалить сертификат и записи о привязанных к нему компьютерах"
			});
			grid.CellContentClick += OnDeleteCertificate;
		}

		// Показывает срок в местном времени и окрашивает только ячейку с датой.
		private void OnCertificateExpiryFormatting(object sender, DataGridViewCellFormattingEventArgs args)
		{
			var grid = (DataGridView)sender;
			if (!(args.Value is DateTime) || grid.Columns[args.ColumnIndex].DataPropertyName != CertificateExpiryField)
			{
				return;
			}

			DateTime expiresAtUtc = DateTime.SpecifyKind((DateTime)args.Value, DateTimeKind.Utc);
			args.Value = expiresAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
			args.FormattingApplied = true;

			TimeSpan remaining = expiresAtUtc - DateTime.UtcNow;
			if (remaining > TimeSpan.FromDays(30))
			{
				return;
			}

			bool expired = remaining <= TimeSpan.Zero;
			if (SystemInformation.HighContrast)
			{
				return;
			}

			bool dark = WindowAppearance.IsDark(grid);
			args.CellStyle.BackColor = dark
				? (expired ? Color.FromArgb(75, 41, 47) : Color.FromArgb(62, 48, 31))
				: (expired ? Color.MistyRose : Color.PeachPuff);
			args.CellStyle.ForeColor = dark
				? (expired ? Color.FromArgb(255, 174, 174) : Color.FromArgb(255, 218, 153))
				: (expired ? Color.DarkRed : Color.SaddleBrown);
			args.CellStyle.SelectionBackColor = args.CellStyle.BackColor;
			args.CellStyle.SelectionForeColor = args.CellStyle.ForeColor;
		}

		// Выполняет один запрос за раз и повторяет его, если вкладку открыли снова во время загрузки.
		private async Task RefreshDatabase()
		{
			if (!refreshDatabaseButton.Enabled)
			{
				databaseRefreshPending = true;
				return;
			}

			SetDatabaseBusy(true);
			try
			{
				await LoadDatabaseRows(null);
			}
			finally
			{
				if (!IsDisposed)
				{
					SetDatabaseBusy(false);
				}
			}
		}

		// Показывает итог отдельной проверки и её время в часовом поясе администратора.
		private void OnLocationCheckFormatting(object sender, DataGridViewCellFormattingEventArgs args)
		{
			string field = certificateLocationsGrid.Columns[args.ColumnIndex].DataPropertyName;
			if (field == "CheckStatus")
			{
				switch (args.Value is byte ? (CertificateLocationStatus)(byte)args.Value : 0)
				{
					case CertificateLocationStatus.Found: args.Value = "Найден"; break;
					case CertificateLocationStatus.Missing: args.Value = "Не найден по этому пути"; break;
					case CertificateLocationStatus.DifferentCertificate: args.Value = "Заменён другим сертификатом"; break;
					case CertificateLocationStatus.Unverified: args.Value = "Не удалось проверить"; break;
					default: args.Value = "Не проверялся"; break;
				}
				args.FormattingApplied = true;
			}
			else if (field == "CheckedAtUtc")
			{
				args.Value = args.Value is DateTime
					? DateTime.SpecifyKind((DateTime)args.Value, DateTimeKind.Utc).ToLocalTime()
						.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)
					: "—";
				args.FormattingApplied = true;
			}
		}

		private void SetDatabaseBusy(bool busy)
		{
			refreshDatabaseButton.Enabled = !busy;
			UpdateLocationCheckButton();
		}

		// Включает проверку при открытом списке сохранённых путей, а во время работы даёт её отменить.
		private void UpdateLocationCheckButton()
		{
			checkLocationsButton.Visible = databaseTables.SelectedTab == certificateLocationsPage;
			checkLocationsButton.Text = checkingLocations ? "Отменить проверку" : "Проверить наличие";
			var locations = certificateLocationsGrid.DataSource as DataTable;
			checkLocationsButton.Enabled = checkingLocations ? !scanCancellation.IsCancellationRequested
				: profilesLoaded && scanCancellation == null && refreshDatabaseButton.Enabled
					&& displayedCertificateRepository != null && locations != null && locations.Rows.Count > 0;
			checkLocationMenuItem.Enabled = !checkingLocations && checkLocationsButton.Enabled
				&& certificateLocationsGrid.CurrentRow != null;
		}

		// Сопоставляет сохранённые места со списком ПК и проверяет их независимо от галочек сканирования.
		private async void OnCheckLocations(object sender, EventArgs args)
		{
			if (checkingLocations)
			{
				scanCancellation.Cancel();
				databaseStatusLabel.Text = "Отмена… Ожидание завершения текущего чтения.";
				UpdateLocationCheckButton();
				return;
			}

			await CheckLocations(null);
		}

		// Проверяет единственную находку, выбранную правой кнопкой мыши.
		private async void OnCheckLocation(object sender, EventArgs args)
		{
			DataGridViewRow selected = certificateLocationsGrid.CurrentRow;
			if (!checkingLocations && selected != null)
			{
				await CheckLocations(CreateLocationCheck(((DataRowView)selected.DataBoundItem).Row));
			}
		}

		// Запускает отдельную проверку с общими правилами блокировки, отмены и обновления таблицы.
		private async Task CheckLocations(CertificateLocationCheck selectedLocation)
		{
			var rows = certificateLocationsGrid.DataSource as DataTable;
			CertificateRepository repository = displayedCertificateRepository;
			if (repository == null || rows == null || rows.Rows.Count == 0 || !refreshDatabaseButton.Enabled || scanCancellation != null)
			{
				return;
			}

			var profiles = computers.Select(item => item.Profile)
				.ToDictionary(profile => profile.IsLocal ? Environment.MachineName : profile.IpAddress, StringComparer.OrdinalIgnoreCase);
			scanCancellation = new CancellationTokenSource();
			CancellationToken token = scanCancellation.Token;
			checkingLocations = true;
			SetScanning(true);
			SetDatabaseBusy(true);
			try
			{
				logger.Info(selectedLocation == null ? "Начата проверка наличия всех сохранённых сертификатов."
					: "Начата проверка наличия сертификата. Компьютер: " + selectedLocation.ComputerAddress
						+ "; источник: " + selectedLocation.SourcePath + "; серийный номер: " + selectedLocation.SerialNumber);
				string summary = await CheckLocationGroups(repository, profiles, selectedLocation, token);
				if (!IsDisposed && !closeWhenIdle && databaseButton.Checked)
				{
					await LoadDatabaseRows(summary);
				}
			}
			finally
			{
				checkingLocations = false;
				SetDatabaseBusy(false);
				FinishOperation();
			}
		}

		// Сохраняет ключ и версию строки для защиты от её изменения во время проверки.
		private static CertificateLocationCheck CreateLocationCheck(DataRow row)
		{
			return new CertificateLocationCheck((string)row["SerialNumber"], (string)row["ComputerAddress"],
				(string)row["SourcePath"], (DateTime)row["CreatedAt"], (DateTime)row["UpdatedAt"]);
		}

		// Сохраняет завершённые проверки по одному ПК и показывает ход работы.
		private async Task<string> CheckLocationGroups(CertificateRepository repository, IDictionary<string, ComputerProfile> profiles,
			CertificateLocationCheck selectedLocation, CancellationToken token)
		{
			int checkedLocations = 0;
			int savedLocations = 0;
			try
			{
				ScanConfiguration configuration = await Task.Run(() => ScanConfigurationFile.Load(configurationFilePath), token);
				var groups = await Task.Run(() => (selectedLocation == null
						? repository.ReadLocations().Rows.Cast<DataRow>().Select(CreateLocationCheck)
						: new[] { selectedLocation })
					.GroupBy(location => location.ComputerAddress, StringComparer.OrdinalIgnoreCase).ToList(), token);
				var checker = new CertificateLocationChecker(computerAccess, machineCertificateStore,
					(profile, cancellation) => connection.Connect(profile, configuration.ConnectionTimeoutSeconds, cancellation), logger);
				for (int index = 0; index < groups.Count; index++)
				{
					token.ThrowIfCancellationRequested();
					IGrouping<string, CertificateLocationCheck> group = groups[index];
					List<CertificateLocationCheck> locations = group.ToList();
					ComputerProfile profile;
					profiles.TryGetValue(group.Key, out profile);
					databaseStatusLabel.Text = string.Format("Проверка компьютера {0} ({1} из {2})…", group.Key, index + 1, groups.Count);
					savedLocations += await Task.Run(() =>
					{
						try
						{
							checker.CheckComputer(group.Key, profile, locations, token);
						}
						catch (OperationCanceledException)
						{
							if (!token.IsCancellationRequested)
							{
								throw;
							}
						}

						return repository.SaveLocationChecks(locations.Where(location => location.CheckedAtUtc != default(DateTime)));
					}, token);
					checkedLocations += locations.Count(location => location.CheckedAtUtc != default(DateTime));
					token.ThrowIfCancellationRequested();
				}

				string summary = string.Format("Проверка завершена. Сохранено результатов: {0} из {1}.", savedLocations, checkedLocations);
				logger.Info(summary);
				return summary;
			}
			catch (OperationCanceledException)
			{
				string summary = "Проверка отменена. Сохранено результатов: " + savedLocations + ".";
				logger.Info(summary);
				return summary;
			}
			catch (Exception exception)
			{
				logger.Error("Ошибка проверки наличия сертификатов.", exception);
				return "Не удалось завершить проверку. " + exception.Message;
			}
		}

		// Загружает таблицу для текущих настроек и связывает результат с его исходной базой.
		private async Task LoadDatabaseRows(string actionStatus)
		{
			displayedCertificateRepository = null;
			databaseGrid.DataSource = null;
			certificateLocationsGrid.DataSource = null;
			databaseStatusLabel.Text = "Загрузка записей…";
			do
			{
				databaseRefreshPending = false;
				bool showingLocations = databaseTables.SelectedTab == certificateLocationsPage;
				DataGridView targetGrid = showingLocations ? certificateLocationsGrid : databaseGrid;
				CertificateRepository repository = null;
				DataTable certificates = null;
				string status;
				try
				{
					certificates = await Task.Run(() =>
					{
						ScanConfiguration configuration = ScanConfigurationFile.Load(configurationFilePath);
						if (configuration.ConnectionString.Length == 0)
						{
							return null;
						}

						repository = new CertificateRepository(configuration.ConnectionString, logger);
						return showingLocations ? repository.ReadLocations() : repository.ReadAll();
					});
					status = certificates == null
						? "Укажите и сохраните строку подключения в разделе «Настройки → База данных»."
						: "Записей: " + certificates.Rows.Count;
				}
				catch (Exception exception)
				{
					status = "Не удалось загрузить записи. " + exception.Message;
					logger.Error("Не удалось прочитать сертификаты из базы данных.", exception);
				}

				if (!IsDisposed && databaseButton.Checked && !databaseRefreshPending)
				{
					displayedCertificateRepository = certificates == null ? null : repository;
					targetGrid.DataSource = certificates;
					databaseStatusLabel.Text = actionStatus == null ? status : actionStatus + " " + status;
				}
			} while (!IsDisposed && databaseButton.Checked && !closeWhenIdle && databaseRefreshPending);
		}

		// Подтверждает конкретную строку и удаляет её из той базы, из которой она была загружена.
		private async void OnDeleteCertificate(object sender, DataGridViewCellEventArgs args)
		{
			var grid = (DataGridView)sender;
			CertificateRepository repository = displayedCertificateRepository;
			if (args.RowIndex < 0 || args.ColumnIndex < 0 || !(grid.Columns[args.ColumnIndex] is DataGridViewButtonColumn)
				|| repository == null || !refreshDatabaseButton.Enabled)
			{
				return;
			}

			DataRow row = ((DataRowView)grid.Rows[args.RowIndex].DataBoundItem).Row;
			string serialNumber = (string)row["SerialNumber"];
			DateTime createdAt = (DateTime)row["CreatedAt"];
			DateTime updatedAt = (DateTime)row["UpdatedAt"];
			string computerAddress = null;
			string sourcePath = null;
			int remainingLocations = 0;
			string deletionDetails = "Сертификат и записи обо всех привязанных к нему компьютерах будут удалены из базы данных.";
			if (grid == certificateLocationsGrid)
			{
				computerAddress = (string)row["ComputerAddress"];
				sourcePath = (string)row["SourcePath"];
				string[] remainingComputers = row.Table.Rows.Cast<DataRow>()
					.Where(location => location != row && (string)location["SerialNumber"] == serialNumber)
					.Select(location => (string)location["ComputerAddress"]).ToArray();
				remainingLocations = remainingComputers.Length;
				deletionDetails = remainingLocations == 0
					? "Это последний компьютер, привязанный к сертификату под серийным номером " + serialNumber
						+ ". При удалении исчезнет запись как о компьютере, так и о сертификате в обеих таблицах."
						+ "\r\n\r\nКомпьютер " + computerAddress
					: "Будет удалена запись о компьютере для выбранного пути. Сертификат останется в реестре."
						+ "\r\nКомпьютер " + computerAddress + ", привязанный к сертификату с серийным номером " + serialNumber + ".";
				deletionDetails += "\r\n\r\n" + sourcePath;
				if (remainingLocations > 0)
				{
					deletionDetails += "\r\n\r\nОставшиеся компьютеры\r\n" + string.Join(", ", remainingComputers.Distinct(StringComparer.OrdinalIgnoreCase));
				}
			}

			using (var dialog = new DeleteCertificateDialog((string)row["OwnerName"], serialNumber, deletionDetails))
			{
				WindowAppearance.ApplyToDialog(dialog, this, settingsPanel.Appearance.FontPercent);
				if (dialog.ShowDialog(this) != DialogResult.OK)
				{
					return;
				}
			}

			deletingCertificate = true;
			SetDatabaseBusy(true);
			databaseStatusLabel.Text = "Удаление записи…";
			try
			{
				string status = await Task.Run(() => DeleteCertificate(repository, serialNumber, createdAt, updatedAt,
					computerAddress, sourcePath, remainingLocations));
				if (!IsDisposed && !closeWhenIdle && databaseButton.Checked)
				{
					await LoadDatabaseRows(status);
				}
			}
			finally
			{
				deletingCertificate = false;
				if (!IsDisposed)
				{
					SetDatabaseBusy(false);
					CloseIfIdle();
				}
			}
		}

		// Выполняет запрос и записывает исход удаления вместе с учётной записью оператора.
		private string DeleteCertificate(CertificateRepository repository, string serialNumber, DateTime createdAt, DateTime updatedAt,
			string computerAddress, string sourcePath, int remainingLocations)
		{
			string auditDetails = string.Format("Серийный номер: {0}. Оператор: {1}\\{2}.",
				serialNumber, Environment.UserDomainName, Environment.UserName);
			string status;
			Exception deletionError = null;
			try
			{
				if (computerAddress == null)
				{
					status = repository.TryDelete(serialNumber, createdAt, updatedAt)
						? "Запись удалена." : "Запись уже удалена или обновлена с момента загрузки.";
				}
				else
				{
					auditDetails += " Компьютер: " + computerAddress + ". Путь: " + sourcePath + ".";
					bool deleted = repository.TryDeleteLocation(serialNumber, createdAt, updatedAt, computerAddress, sourcePath, remainingLocations);
					status = !deleted ? "Данные изменились. Проверьте список и подтвердите удаление снова."
						: remainingLocations == 0 ? "Записи о компьютере и сертификате удалены из базы данных."
						: "Запись о компьютере для выбранного пути удалена. Сертификат остаётся в реестре.";
				}
			}
			catch (Exception exception)
			{
				status = "Не удалось удалить запись. Подробности в журнале.";
				deletionError = exception;
			}

			try
			{
				if (deletionError == null)
				{
					logger.Info(status + " " + auditDetails);
				}
				else
				{
					logger.Error("Ошибка удаления записи из базы данных. " + auditDetails, deletionError);
				}
			}
			catch (Exception loggingError)
			{
				Console.Error.WriteLine(deletionError);
				Console.Error.WriteLine(loggingError);
				status = (deletionError == null ? status : "Не удалось удалить запись. " + deletionError.Message)
					+ " Журнал недоступен.";
			}

			return status;
		}

		// Показывает локальный ПК первым в общем списке с удалёнными компьютерами.
		private void BuildComputersPanel()
		{
			var computerPanel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 6), ColumnCount = 1, RowCount = 2 };
			computerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			computerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			computerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			ConfigureGrid(computersGrid);
			computersGrid.Margin = new Padding(0, 3, 0, 3);
			computersGrid.ReadOnly = false;
			computersGrid.MultiSelect = false;
			computersGrid.AccessibleName = "Компьютеры для подключения и сканирования";
			computersGrid.Columns.Add(new DataGridViewCheckBoxColumn
			{
				Name = "Selected", DataPropertyName = "Selected", HeaderText = "Выбор", FillWeight = 30
			});
			AddColumn(computersGrid, "DisplayName", "Название", 90);
			AddColumn(computersGrid, "IpAddress", "IPv4", 80);
			AddColumn(computersGrid, "Account", "Учётная запись", 110);
			AddColumn(computersGrid, "ConnectionStatus", "Подключение", 140);
			AddColumn(computersGrid, "ScanStatus", "Сканирование", 140);
			AddColumn(computersGrid, "CryptoProVersion", "КриптоПро", 85);
			AddColumn(computersGrid, "DatabaseStatus", "База данных", 110);
			foreach (DataGridViewColumn column in computersGrid.Columns)
			{
				column.ReadOnly = column.Name != "Selected";
			}

			computersGrid.DataSource = computers;
			computersGrid.CurrentCellDirtyStateChanged += OnComputerSelectionDirty;
			computersGrid.CellValueChanged += OnComputerSelectionChanged;
			computersGrid.SelectionChanged += OnComputerRowChanged;
			computerPanel.Controls.Add(computersGrid, 0, 0);
			var computerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
			addComputerButton.Margin = new Padding(0, 3, 3, 3);
			AddComputerAction(computerActions, addComputerButton, "Добавить", OnAddComputer);
			AddComputerAction(computerActions, editComputerButton, "Изменить", OnEditComputer);
			AddComputerAction(computerActions, removeComputerButton, "Удалить", OnRemoveComputer);
			AddComputerAction(computerActions, connectButton, "Подключить выбранные", OnConnect);
			computerPanel.Controls.Add(computerActions, 0, 1);
			computersPanel.Controls.Add(computerPanel);
		}

		// Локальную строку можно редактировать, но удаление доступно только для удалённых ПК.
		private void OnComputerRowChanged(object sender, EventArgs args)
		{
			UpdateComputerActions();
		}

		// Добавляет действие над списком ПК без отдельной панели настроек.
		private static void AddComputerAction(FlowLayoutPanel panel, Button button, string text, EventHandler handler)
		{
			button.Text = text;
			button.AutoSize = true;
			button.Click += handler;
			panel.Controls.Add(button);
		}

		// Загружает настройки до первого подключения; ошибки не приводят к перезаписи файла.
		private async void OnShown(object sender, EventArgs args)
		{
			bool configurationReady = PrepareConfiguration();
			try
			{
				changingSelection = true;
				foreach (ComputerProfile profile in computerListFile.Load())
				{
					if (profile.IsLocal)
					{
						computers[0] = new ComputerListItem(profile);
					}
					else
					{
						computers.Add(new ComputerListItem(profile));
					}
				}

				profilesLoaded = true;
			}
			catch (Exception exception)
			{
				Program.ShowError("Не удалось загрузить список ПК: " + computerListFile.FilePath, exception);
				statusLabel.Text = "Исправьте файл списка ПК: " + computerListFile.FilePath;
			}
			finally
			{
				changingSelection = false;
				SetScanning(false);
			}

			if (configurationReady && profilesLoaded && computers.Any(item => item.Selected && !item.Profile.IsLocal))
			{
				await RunComputers(false);
			}
		}

		// Готовит рабочий XML перед автоподключением и направляет к настройкам при ошибке.
		private bool PrepareConfiguration()
		{
			try
			{
				bool created = ScanConfigurationFile.CreateFromTemplate(configurationFilePath, configurationTemplatePath);
				ScanConfigurationFile.Load(configurationFilePath);
				if (created)
				{
					logger.Info("Создан рабочий файл конфигурации из шаблона: " + configurationFilePath);
					statusLabel.Text = "Созданы настройки первого запуска. Строку подключения можно указать в «Настройках».";
				}

				return true;
			}
			catch (Exception exception)
			{
				Program.ShowError("Не удалось подготовить конфигурацию сканирования: " + configurationFilePath, exception);
				statusLabel.Text = "Откройте «Настройки → Файлы и журнал», чтобы загрузить или восстановить конфигурацию.";
				return false;
			}
		}

		// Сразу фиксирует галочку, чтобы нажатие кнопки учитывало последний выбор.
		private void OnComputerSelectionDirty(object sender, EventArgs args)
		{
			if (computersGrid.IsCurrentCellDirty)
			{
				computersGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		}

		// Сохраняет выбранный набор между запусками; при ошибке восстанавливает прежнее значение.
		private void OnComputerSelectionChanged(object sender, DataGridViewCellEventArgs args)
		{
			if (changingSelection || !profilesLoaded || args.RowIndex < 0 || args.ColumnIndex != 0)
			{
				return;
			}

			var item = (ComputerListItem)computersGrid.Rows[args.RowIndex].DataBoundItem;
			item.Selected = Convert.ToBoolean(computersGrid.Rows[args.RowIndex].Cells[0].Value);
			try
			{
				computerListFile.Save(computers.Select(value => value.Profile));
				item.SavedSelection = item.Selected;
			}
			catch (Exception exception)
			{
				changingSelection = true;
				item.Selected = item.SavedSelection;
				computers.ResetBindings();
				changingSelection = false;
				Program.ShowError("Не удалось сохранить выбор компьютеров.", exception);
			}

			UpdateComputerActions();
		}

		// Сохраняет новый ПК и сразу подключает отмеченные компьютеры.
		private async void OnAddComputer(object sender, EventArgs args)
		{
			if (EditComputer(null))
			{
				await RunComputers(false);
			}
		}

		// Меняет настройки одной строки и повторяет подключение с новой учёткой.
		private async void OnEditComputer(object sender, EventArgs args)
		{
			ComputerListItem item = CurrentComputer();
			if (item != null && EditComputer(item) && !item.Profile.IsLocal)
			{
				await RunComputers(false);
			}
		}

		// Сначала сохраняет новый список, затем удаляет только собственный секрет исключённого ПК.
		private void OnRemoveComputer(object sender, EventArgs args)
		{
			ComputerListItem item = CurrentComputer();
			if (item == null || item.Profile.IsLocal)
			{
				return;
			}

			try
			{
				computerListFile.Save(computers.Where(value => value != item).Select(value => value.Profile));
				changingSelection = true;
				computers.Remove(item);
				changingSelection = false;
				RemoveCredential(item.Profile);
				statusLabel.Text = "Компьютер удалён из списка. Действующие соединения Windows сохраняются.";
			}
			catch (Exception exception)
			{
				changingSelection = false;
				Program.ShowError("Не удалось удалить компьютер или его сохранённую учётку.", exception);
			}
		}

		// Возвращает строку под курсором; галочки задают состав запуска, а не объект редактирования.
		private ComputerListItem CurrentComputer()
		{
			return computersGrid.CurrentRow == null ? null : computersGrid.CurrentRow.DataBoundItem as ComputerListItem;
		}

		// Новая запись секрета сохраняется до смены XML, поэтому неудачная запись списка не портит прежнюю учётку.
		private bool EditComputer(ComputerListItem previous)
		{
			using (var dialog = new ComputerProfileDialog(previous == null ? null : previous.Profile))
			{
				WindowAppearance.ApplyToDialog(dialog, this, settingsPanel.Appearance.FontPercent);
				if (dialog.ShowDialog(this) != DialogResult.OK)
				{
					return false;
				}

				ComputerProfile profile = dialog.Profile;
				bool secretSaved = false;
				bool profileSaved = false;
				try
				{
					var profiles = computers.Select(item => item == previous ? profile : item.Profile).ToList();
					if (previous == null)
					{
						profiles.Add(profile);
					}
					ComputerListFile.Validate(profiles);
					if (!profile.IsLocal)
					{
						NetworkCredential retained = dialog.KeepPreviousPassword ? credentialStore.Read(previous.Profile.Id) : null;
						if (retained != null && !string.Equals(retained.UserName, profile.UserName, StringComparison.OrdinalIgnoreCase))
						{
							throw new InvalidOperationException("Сохранённая учётная запись изменилась. Введите пароль заново.");
						}

						credentialStore.Save(profile.Id, profile.UserName, retained == null ? dialog.Password : retained.Password);
						secretSaved = true;
					}

					computerListFile.Save(profiles);
					profileSaved = true;
					changingSelection = true;
					if (previous == null)
					{
						computers.Add(new ComputerListItem(profile));
					}
					else
					{
						computers[computers.IndexOf(previous)] = new ComputerListItem(profile);
						RemoveCredential(previous.Profile);
					}

					return true;
				}
				catch (Exception exception)
				{
					if (secretSaved && !profileSaved)
					{
						RemoveCredential(profile);
					}

					Program.ShowError("Не удалось сохранить компьютер.", exception);
					return false;
				}
				finally
				{
					changingSelection = false;
					UpdateComputerActions();
				}
			}
		}

		// Ошибка очистки собственного секрета фиксируется отдельно от уже сохранённого списка.
		private void RemoveCredential(ComputerProfile profile)
		{
			if (profile.HasUserName)
			{
				try
				{
					credentialStore.Delete(profile.Id);
				}
				catch (Exception exception)
				{
					Program.ShowError("Список ПК сохранён, но прежнюю запись Telecom/Computer/" + profile.Id.ToString("N")
						+ " для " + profile.DisplayName + " (" + profile.IpAddress + ") нужно удалить из диспетчера учётных данных вручную.", exception);
				}
			}
		}

		// Повторяет подключение после изменения сети или пароля, не запуская сканирование.
		private async void OnConnect(object sender, EventArgs args)
		{
			await RunComputers(false);
		}

		// Настраивает таблицу результатов только для просмотра.
		private static void ConfigureGrid(DataGridView grid)
		{
			grid.Dock = DockStyle.Fill;
			grid.AutoGenerateColumns = false;
			grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
			grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
			grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
			grid.ReadOnly = true;
			grid.AllowUserToAddRows = false;
			grid.AllowUserToDeleteRows = false;
			grid.RowHeadersVisible = false;
			grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
			grid.BackgroundColor = SystemColors.Window;
			grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
		}

		// Добавляет столбец с привязкой к открытому свойству модели.
		private static DataGridViewTextBoxColumn AddColumn(DataGridView grid, string property, string title, float weight)
		{
			var column = new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, FillWeight = weight };
			grid.Columns.Add(column);
			return column;
		}

		// Сканирует выбранные локальный и удалённые ПК в фоне.
		private async void OnScan(object sender, EventArgs args)
		{
			await RunComputers(true);
		}

		// Сначала подключает выбранные ПК, затем сканирует доступные; сбой одной учётки не останавливает остальные.
		private async Task RunComputers(bool scan)
		{
			if (scanCancellation != null || !profilesLoaded || !computersButton.Checked)
			{
				return;
			}

			computersGrid.EndEdit();
			List<ComputerListItem> selected = computers.Where(item => item.Selected && (scan || !item.Profile.IsLocal)).ToList();
			if (selected.Count == 0)
			{
				statusLabel.Text = scan ? "Отметьте хотя бы один компьютер галочкой." : "Отметьте удалённые компьютеры для подключения.";
				return;
			}

			scanCancellation = new CancellationTokenSource();
			CancellationToken token = scanCancellation.Token;
			SetScanning(true);
			var errors = new BindingList<ScanError>();
			var certificates = new BindingList<CertificateInfo>();
			errorsGrid.DataSource = errors;
			foreach (ComputerListItem item in selected)
			{
				item.ConnectionStatus = item.Profile.IsLocal ? "Локально" : "Ожидание подключения";
				if (scan)
				{
					item.ScanStatus = "Ожидание сканирования";
					item.CryptoProVersion = string.Empty;
					item.DatabaseStatus = "Не записано";
				}
			}

			RefreshComputers();
			if (scan)
			{
				certificatesGrid.DataSource = certificates;
			}

			try
			{
				ScanConfiguration configuration = await Task.Run(() => ScanConfigurationFile.Load(configurationFilePath), token);
				var connected = new List<ComputerListItem>();
				foreach (ComputerListItem item in selected)
				{
					token.ThrowIfCancellationRequested();
					if (item.Profile.IsLocal)
					{
						connected.Add(item);
						continue;
					}

					item.ConnectionStatus = "Подключение…";
					RefreshComputers();
					statusLabel.Text = "Подключение к " + item.Description + "…";
					try
					{
						logger.Info("Подключение SMB к " + item.Description);
						await Task.Run(() => connection.Connect(item.Profile, configuration.ConnectionTimeoutSeconds, token), token);
						item.ConnectionStatus = "SMB подключено";
						connected.Add(item);
						logger.Info("Соединение SMB установлено: " + item.Description);
					}
					catch (OperationCanceledException)
					{
						item.ConnectionStatus = "Подключение отменено";
						throw;
					}
					catch (Exception exception)
					{
						if (!ErrorClassifier.IsRecoverable(exception) && !(exception is InvalidOperationException))
						{
							throw;
						}

						item.ConnectionStatus = exception.Message;
						item.ScanStatus = scan ? "Пропущено: нет подключения" : item.ScanStatus;
						errors.Add(new ScanError(item.Description, exception.Message, ErrorClassifier.Classify(exception)));
						logger.Error("Ошибка подключения к " + item.Description, exception);
					}

					RefreshComputers();
				}

				int completed = 0;
				int databaseFailures = 0;
				if (scan)
				{
					var scanner = new ComputerScanner(computerAccess, new CertificateParser(), logger);
					foreach (ComputerListItem item in connected)
					{
						token.ThrowIfCancellationRequested();
						item.ScanStatus = "Сканирование…";
						RefreshComputers();
						statusLabel.Text = "Сканирование " + item.Description + "…";
						ScanConfiguration computerConfiguration = configuration.WithAdditionalDirectories(item.Profile.AdditionalDirectories);
						ComputerScanResult result = await Task.Run(() => scanner.ScanProfile(item.Profile, computerConfiguration,
							machineCertificateStore, token), token);
						foreach (CertificateInfo certificate in result.Certificates)
						{
							certificates.Add(certificate);
						}

						foreach (ScanError error in result.Errors)
						{
							errors.Add(error);
						}

						item.ScanStatus = DescribeStatus(result.Status) + "; сертификатов: " + result.Certificates.Count;
						item.CryptoProVersion = DescribeCryptoPro(result);
						RefreshComputers();
						if (result.Status == ComputerScanStatus.Cancelled)
						{
							throw new OperationCanceledException(token);
						}

						completed++;
						if (!await SaveToDatabase(item, configuration, result, token))
						{
							databaseFailures++;
						}

						RefreshComputers();
						token.ThrowIfCancellationRequested();
					}
				}

				string summary = scan
					? string.Format("Обработано ПК: {0} из {1}; сертификатов: {2}. ", completed, selected.Count, certificates.Count)
					: string.Format("Подключено удалённых ПК: {0} из {1}. ", connected.Count, selected.Count);
				statusLabel.Text = summary + string.Format("Ошибок подключения и чтения: {0}.{1}", errors.Count,
					databaseFailures == 0 ? string.Empty : " Ошибок записи в БД: " + databaseFailures + ".");
			}
			catch (OperationCanceledException)
			{
				foreach (ComputerListItem item in selected.Where(value => value.ConnectionStatus == "Ожидание подключения"))
				{
					item.ConnectionStatus = "Подключение отменено";
				}

				foreach (ComputerListItem item in selected.Where(value => value.ScanStatus == "Ожидание сканирования" || value.ScanStatus == "Сканирование…"))
				{
					item.ScanStatus = "Отменено";
				}

				logger.Info("Подключение или сканирование выбранных ПК отменено.");
				statusLabel.Text = "Операция отменена. Уже полученные результаты сохранены в окне.";
				RefreshComputers();
			}
			catch (Exception exception)
			{
				Program.ShowError("Не удалось обработать выбранные компьютеры.", exception);
				statusLabel.Text = "Ошибка. Подробности в " + Program.ErrorLogFilePath + ".";
			}
			finally
			{
				FinishOperation();
			}
		}

		// Сохраняет находки одного ПК в фоне и выводит исход записи в его строке.
		private async Task<bool> SaveToDatabase(ComputerListItem item, ScanConfiguration configuration, ComputerScanResult result,
			CancellationToken token)
		{
			if (result.Certificates.Count == 0)
			{
				item.DatabaseStatus = "Нет сертификатов";
				return true;
			}

			if (configuration.ConnectionString.Length == 0)
			{
				item.DatabaseStatus = "Строка подключения не задана";
				return true;
			}

			token.ThrowIfCancellationRequested();
			item.DatabaseStatus = "Запись…";
			RefreshComputers();
			statusLabel.Text = "Запись в БД для " + item.Description + "…";
			try
			{
				CertificateSaveResult saved = await Task.Run(() =>
				{
					var repository = new CertificateRepository(configuration.ConnectionString, logger);
					return repository.Save(result.Certificates, result.CryptoProVersion, result.ComputerName);
				}, token);
				item.DatabaseStatus = string.Format("Добавлено: {0}, обновлено: {1}", saved.Inserted, saved.Updated);
				return true;
			}
			catch (OperationCanceledException)
			{
				item.DatabaseStatus = "Запись отменена";
				throw;
			}
			catch (Exception exception)
			{
				if (!(exception is DbException) && !(exception is InvalidOperationException) && !(exception is ArgumentException))
				{
					throw;
				}

				item.DatabaseStatus = "Ошибка записи. " + exception.Message;
				logger.Error("Ошибка записи в базу данных для " + item.Description, exception);
				return false;
			}
		}

		// Обновляет статусы без повторного сохранения галочек через события привязки.
		private void RefreshComputers()
		{
			changingSelection = true;
			computers.ResetBindings();
			changingSelection = false;
		}

		// Возвращает управление после системного вызова и завершает отложенное закрытие окна.
		private void FinishOperation()
		{
			scanCancellation.Dispose();
			scanCancellation = null;
			SetScanning(false);
			CloseIfIdle();
		}

		// Закрывает окно после завершения сканирования и подтверждённого удаления.
		private void CloseIfIdle()
		{
			if (closeWhenIdle && scanCancellation == null && !deletingCertificate)
			{
				closeWhenIdle = false;
				Close();
			}
		}

		// Просит завершить обход; уже выполняющийся системный вызов SMB прервётся только после ответа Windows.
		private void OnCancel(object sender, EventArgs args)
		{
			if (scanCancellation != null)
			{
				scanCancellation.Cancel();
				cancelButton.Enabled = false;
				statusLabel.Text = "Отмена… Ожидание завершения текущего чтения.";
			}
		}

		// Блокирует новый обход и редактирование настроек до конца текущей проверки.
		private void SetScanning(bool scanning)
		{
			computersPanel.Enabled = !scanning;
			settingsButton.Enabled = !scanning;
			scanButton.Enabled = !scanning && profilesLoaded && computersButton.Checked;
			AcceptButton = scanButton.Enabled ? scanButton : null;
			addComputerButton.Enabled = !scanning && profilesLoaded;
			UpdateComputerActions();
			cancelButton.Enabled = scanning && !checkingLocations && !scanCancellation.IsCancellationRequested;
			UpdateLocationCheckButton();
		}

		// Согласует действия с текущей строкой и исключает локальный ПК из сетевого подключения.
		private void UpdateComputerActions()
		{
			bool enabled = scanCancellation == null && profilesLoaded;
			ComputerListItem current = CurrentComputer();
			editComputerButton.Enabled = enabled && current != null;
			removeComputerButton.Enabled = enabled && current != null && !current.Profile.IsLocal;
			connectButton.Enabled = enabled && computers.Any(item => item.Selected && !item.Profile.IsLocal);
		}

		// Переводит технический итог проверки ПК в понятную подпись.
		private static string DescribeStatus(ComputerScanStatus status)
		{
			switch (status)
			{
				case ComputerScanStatus.Complete: return "Сканирование завершено";
				case ComputerScanStatus.Partial: return "Проверено частично";
				case ComputerScanStatus.AccessDenied: return "Нет прав доступа";
				case ComputerScanStatus.Unavailable: return "Сетевой доступ недоступен";
				case ComputerScanStatus.Cancelled: return "Проверка отменена";
				default: return "Не удалось проверить настроенные папки";
			}
		}

		// Не выдаёт отсутствие файлов в заданных местах за доказательство отсутствия установленного продукта.
		private static string DescribeCryptoPro(ComputerScanResult result)
		{
			switch (result.CryptoProStatus)
			{
				case CryptoProStatus.Found: return result.CryptoProVersion;
				case CryptoProStatus.NotFound: return "не найден в настроенных путях";
				case CryptoProStatus.Error: return "версию определить не удалось";
				default: return "не проверялось";
			}
		}

		// Показывает локализованную категорию ошибки, сохраняя исходное сообщение Windows в соседнем столбце.
		private void OnErrorFormatting(object sender, DataGridViewCellFormattingEventArgs args)
		{
			if (args.Value is ScanErrorKind)
			{
				switch ((ScanErrorKind)args.Value)
				{
					case ScanErrorKind.AccessDenied: args.Value = "Нет доступа"; break;
					case ScanErrorKind.Unavailable: args.Value = "Ресурс недоступен"; break;
					case ScanErrorKind.NotFound: args.Value = "Не найдено"; break;
					case ScanErrorKind.InvalidData: args.Value = "Некорректные данные"; break;
					default: args.Value = "Ошибка чтения"; break;
				}

				args.FormattingApplied = true;
			}
		}

		// Дожидается текущего вызова Windows, не обрывая запись настроек или работу SMB.
		private void OnFormClosing(object sender, FormClosingEventArgs args)
		{
			if (scanCancellation != null)
			{
				scanCancellation.Cancel();
				closeWhenIdle = true;
				args.Cancel = true;
				Label activeStatus = checkingLocations ? databaseStatusLabel : statusLabel;
				activeStatus.Text = "Закрытие… Ожидание текущего обращения к Windows.";
			}
			else if (deletingCertificate)
			{
				closeWhenIdle = true;
				args.Cancel = true;
				databaseStatusLabel.Text = "Закрытие… Ожидание ответа базы данных.";
			}
			else if (args.CloseReason == CloseReason.UserClosing && settingsPanel.HasUnsavedChanges)
			{
				args.Cancel = MessageBox.Show(this, "Закрыть приложение без сохранения изменений настроек?", "Настройки",
					MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes;
			}
		}

		private sealed class ComputerListItem
		{
			public ComputerProfile Profile { get; private set; }
			public bool Selected { get { return Profile.Selected; } set { Profile.Selected = value; } }
			public bool SavedSelection { get; set; }
			public string IpAddress { get { return Profile.IsLocal ? "—" : Profile.IpAddress; } }
			public string DisplayName { get { return Profile.DisplayName; } }
			public string Description
			{
				get
				{
					if (Profile.IsLocal)
					{
						return DisplayName + " (" + Environment.MachineName + ")";
					}

					return DisplayName.Length == 0 ? IpAddress : DisplayName + " (" + IpAddress + ")";
				}
			}
			public string Account { get { return Profile.IsLocal ? "Текущий пользователь" : Profile.HasUserName ? Profile.UserName : "Не задана"; } }
			public string ConnectionStatus { get; set; }
			public string ScanStatus { get; set; }
			public string CryptoProVersion { get; set; }
			public string DatabaseStatus { get; set; }

			// Отделяет отображаемые статусы от метаданных, которые сохраняются на диск.
			public ComputerListItem(ComputerProfile profile)
			{
				Profile = profile;
				SavedSelection = profile.Selected;
				ConnectionStatus = profile.IsLocal ? "Локально" : "Не подключено";
			}
		}
	}
}
