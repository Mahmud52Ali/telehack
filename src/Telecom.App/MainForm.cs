using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Common;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Telecom.Core;

namespace Telecom.App
{
	public sealed class MainForm : Form
	{
		private readonly ILogger logger;
		private readonly TextBox directoryInput = new TextBox();
		private readonly DataGridView computersGrid = new DataGridView();
		private readonly BindingList<ComputerListItem> computers = new BindingList<ComputerListItem>();
		private readonly ComputerProfileStore profileStore;
		private readonly WindowsCredentialStore credentialStore = new WindowsCredentialStore();
		private readonly IComputerAccess computerAccess;
		private readonly WindowsSmbConnection connection;
		private readonly Button addComputerButton = new Button();
		private readonly Button editComputerButton = new Button();
		private readonly Button removeComputerButton = new Button();
		private readonly Button connectButton = new Button();
		private readonly TextBox configurationInput = new TextBox();
		private readonly TabControl modeTabs = new TabControl();
		private readonly Button browseButton = new Button();
		private readonly Button scanButton = new Button();
		private readonly Button cancelButton = new Button();
		private readonly Label statusLabel = new Label();
		private readonly DataGridView certificatesGrid = new DataGridView();
		private readonly DataGridView errorsGrid = new DataGridView();
		private CancellationTokenSource scanCancellation;
		private bool changingSelection;
		private bool profilesLoaded;
		private bool closeWhenIdle;

		// Создаёт список ПК; подключение сохранённых компьютеров начинается после появления окна.
		public MainForm(ILogger logger)
			: this(logger, new ComputerProfileStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Telecom", "computers.xml")), new WindowsFileSystem(), null)
		{
		}

		// Принимает хранилище и сетевые адаптеры для проверки полного сценария без настоящих ПК и паролей.
		public MainForm(ILogger logger, ComputerProfileStore profileStore, IComputerAccess computerAccess, WindowsSmbConnection connection)
		{
			this.logger = logger;
			this.profileStore = profileStore;
			this.computerAccess = computerAccess;
			this.connection = connection ?? new WindowsSmbConnection(credentialStore);
			Text = "СКЗИ — сканирование сертификатов";
			Font = new Font("Segoe UI", 9F);
			AutoScaleMode = AutoScaleMode.Font;
			Size = new Size(1200, 850);
			MinimumSize = new Size(1000, 700);
			StartPosition = FormStartPosition.CenterScreen;
			BuildLayout();
			Shown += OnShown;
			FormClosing += OnFormClosing;
		}

		// Раскладывает выбор режима, кнопки, статус и две таблицы без файлов дизайнера.
		private void BuildLayout()
		{
			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 4, ColumnCount = 1 };
			layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 285));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			BuildModes();
			layout.Controls.Add(modeTabs, 0, 0);

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
			layout.Controls.Add(actions, 0, 1);
			AcceptButton = scanButton;

			statusLabel.Text = "Добавьте компьютеры и отметьте их для подключения и сканирования.";
			statusLabel.AutoSize = true;
			statusLabel.Dock = DockStyle.Fill;
			statusLabel.Margin = new Padding(0, 0, 0, 10);
			layout.Controls.Add(statusLabel, 0, 2);

			ConfigureGrid(certificatesGrid);
			AddColumn(certificatesGrid, "OwnerName", "Владелец", 110);
			AddColumn(certificatesGrid, "CertificationAuthority", "Удостоверяющий центр", 110);
			AddColumn(certificatesGrid, "SerialNumber", "Серийный номер", 65);
			certificatesGrid.Columns.Add(new DataGridViewCheckBoxColumn
			{
				DataPropertyName = "IsCryptoProLicenseEmbedded", HeaderText = "Лицензия в сертификате", FillWeight = 55
			});
			AddColumn(certificatesGrid, "SourcePath", "Файл", 160);
			ConfigureGrid(errorsGrid);
			AddColumn(errorsGrid, "Kind", "Категория", 55);
			AddColumn(errorsGrid, "Path", "Путь", 120);
			AddColumn(errorsGrid, "Message", "Причина", 140);
			errorsGrid.CellFormatting += OnErrorFormatting;

			var tabs = new TabControl { Dock = DockStyle.Fill };
			var certificateTab = new TabPage("Сертификаты");
			certificateTab.Controls.Add(certificatesGrid);
			var errorTab = new TabPage("Ошибки");
			errorTab.Controls.Add(errorsGrid);
			tabs.TabPages.Add(certificateTab);
			tabs.TabPages.Add(errorTab);
			layout.Controls.Add(tabs, 0, 3);
			Controls.Add(layout);
		}

		// Даёт выбрать сохранённые ПК и учётки, сохраняя ручную проверку одной папки.
		private void BuildModes()
		{
			modeTabs.Dock = DockStyle.Fill;
			var computerTab = new TabPage("Компьютеры");
			var computerPanel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(6), ColumnCount = 3, RowCount = 3 };
			computerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
			computerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			computerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			computerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			computerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			computerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			computerPanel.Controls.Add(new Label { Text = "Конфигурация", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
			configurationInput.Dock = DockStyle.Fill;
			configurationInput.AccessibleName = "Файл конфигурации";
			configurationInput.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scan.config.xml");
			computerPanel.Controls.Add(configurationInput, 1, 0);
			var configButton = new Button { Text = "Обзор…", AutoSize = true };
			configButton.Click += OnBrowseConfiguration;
			computerPanel.Controls.Add(configButton, 2, 0);
			ConfigureGrid(computersGrid);
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
			computerPanel.Controls.Add(computersGrid, 0, 1);
			computerPanel.SetColumnSpan(computersGrid, 3);
			var computerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
			AddComputerAction(computerActions, addComputerButton, "Добавить", OnAddComputer);
			AddComputerAction(computerActions, editComputerButton, "Изменить", OnEditComputer);
			AddComputerAction(computerActions, removeComputerButton, "Удалить", OnRemoveComputer);
			AddComputerAction(computerActions, connectButton, "Подключить выбранные", OnConnect);
			computerPanel.Controls.Add(computerActions, 0, 2);
			computerPanel.SetColumnSpan(computerActions, 3);
			computerTab.Controls.Add(computerPanel);

			var directoryTab = new TabPage("Папка");
			var pathPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), ColumnCount = 2, RowCount = 2 };
			pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			var label = new Label { Text = "Папка с сертификатами (включая вложенные папки)", AutoSize = true };
			pathPanel.Controls.Add(label, 0, 0);
			pathPanel.SetColumnSpan(label, 2);
			directoryInput.Dock = DockStyle.Fill;
			directoryInput.AccessibleName = "Папка с сертификатами";
			browseButton.Text = "Обзор…";
			browseButton.AutoSize = true;
			browseButton.Click += OnBrowse;
			pathPanel.Controls.Add(directoryInput, 0, 1);
			pathPanel.Controls.Add(browseButton, 1, 1);
			directoryTab.Controls.Add(pathPanel);
			modeTabs.TabPages.Add(computerTab);
			modeTabs.TabPages.Add(directoryTab);
			modeTabs.SelectedIndexChanged += OnModeChanged;
		}

		// Ручное сканирование папки остаётся доступно и при повреждённом списке ПК.
		private void OnModeChanged(object sender, EventArgs args)
		{
			if (scanCancellation == null)
			{
				SetScanning(false);
			}
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
			try
			{
				changingSelection = true;
				foreach (ComputerProfile profile in profileStore.Load())
				{
					computers.Add(new ComputerListItem(profile));
				}

				profilesLoaded = true;
			}
			catch (Exception exception)
			{
				Program.ShowError("Не удалось загрузить список ПК: " + profileStore.FilePath, exception);
				statusLabel.Text = "Исправьте файл списка ПК: " + profileStore.FilePath;
			}
			finally
			{
				changingSelection = false;
				SetScanning(false);
			}

			if (profilesLoaded && computers.Any(item => item.Selected))
			{
				await RunComputers(false);
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
				profileStore.Save(computers.Select(value => value.Profile));
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
			if (item != null && EditComputer(item))
			{
				await RunComputers(false);
			}
		}

		// Сначала сохраняет новый список, затем удаляет только собственный секрет исключённого ПК.
		private void OnRemoveComputer(object sender, EventArgs args)
		{
			ComputerListItem item = CurrentComputer();
			if (item == null)
			{
				return;
			}

			try
			{
				profileStore.Save(computers.Where(value => value != item).Select(value => value.Profile));
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
					ComputerProfileStore.Validate(profiles);
					NetworkCredential retained = dialog.KeepPreviousPassword ? credentialStore.Read(previous.Profile.Id) : null;
					if (retained != null && !string.Equals(retained.UserName, profile.UserName, StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidOperationException("Сохранённая учётная запись изменилась. Введите пароль заново.");
					}

					credentialStore.Save(profile.Id, profile.UserName, retained == null ? dialog.Password : retained.Password);
					secretSaved = true;
					profileStore.Save(profiles);
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
					logger.Error("Не удалось удалить прежнюю запись учётных данных для " + profile.DisplayName + " (" + profile.IpAddress + ")", exception);
					Program.ShowError("Список ПК сохранён, но прежнюю запись Telecom/Computer/" + profile.Id.ToString("N")
						+ " нужно удалить из диспетчера учётных данных вручную.", exception);
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
		}

		// Добавляет столбец с привязкой к открытому свойству модели.
		private static void AddColumn(DataGridView grid, string property, string title, float weight)
		{
			grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, FillWeight = weight });
		}

		// Позволяет выбрать папку без ручного ввода пути.
		private void OnBrowse(object sender, EventArgs args)
		{
			using (var dialog = new FolderBrowserDialog { Description = "Выберите папку с сертификатами", ShowNewFolderButton = false })
			{
				if (dialog.ShowDialog(this) == DialogResult.OK)
				{
					directoryInput.Text = dialog.SelectedPath;
				}
			}
		}

		// Выбирает существующий XML-файл; содержимое будет проверено непосредственно перед запуском.
		private void OnBrowseConfiguration(object sender, EventArgs args)
		{
			using (var dialog = new OpenFileDialog { Filter = "XML-конфигурация (*.xml)|*.xml|Все файлы (*.*)|*.*", CheckFileExists = true })
			{
				if (dialog.ShowDialog(this) == DialogResult.OK)
				{
					configurationInput.Text = dialog.FileName;
				}
			}
		}

		// Выполняет чтение в фоне и возвращает результаты в поток интерфейса.
		private async void OnScan(object sender, EventArgs args)
		{
			if (modeTabs.SelectedIndex == 0)
			{
				await RunComputers(true);
				return;
			}

			string source = directoryInput.Text.Trim();
			if (source.Length == 0)
			{
				statusLabel.Text = "Укажите папку для проверки.";
				directoryInput.Focus();
				return;
			}

			scanCancellation = new CancellationTokenSource();
			SetScanning(true);
			statusLabel.Text = "Проверка… Время чтения сетевых ресурсов зависит от их доступности.";
			certificatesGrid.DataSource = null;
			errorsGrid.DataSource = null;
			try
			{
				CancellationToken token = scanCancellation.Token;
				var scanner = new DirectoryScanner(new WindowsFileSystem(), new CertificateParser(), logger);
				DirectoryScanResult result = await Task.Run(() => scanner.Scan(source, token));
				if (!IsDisposed)
				{
					certificatesGrid.DataSource = result.Certificates;
					errorsGrid.DataSource = result.Errors;
					string state = result.Cancelled ? "Отменено" : result.Errors.Count > 0 ? "Завершено с ошибками" : "Папка проверена";
					statusLabel.Text = string.Format("{0}. Сертификатов: {1}. Ошибок: {2}. Пропущено файлов других форматов: {3}.",
						state, result.Certificates.Count, result.Errors.Count, result.SkippedFiles);
				}
			}
			catch (Exception exception)
			{
				Program.ShowError("Не удалось завершить сканирование.", exception);
				if (!IsDisposed)
				{
					statusLabel.Text = "Ошибка сканирования. Подробности — в журнале telecom.log рядом с программой.";
				}
			}
			finally
			{
				FinishOperation();
			}
		}

		// Сначала подключает выбранные ПК, затем сканирует доступные; сбой одной учётки не останавливает остальные.
		private async Task RunComputers(bool scan)
		{
			if (scanCancellation != null || !profilesLoaded)
			{
				return;
			}

			computersGrid.EndEdit();
			List<ComputerListItem> selected = computers.Where(item => item.Selected).ToList();
			if (selected.Count == 0)
			{
				statusLabel.Text = "Отметьте хотя бы один компьютер галочкой.";
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
				item.ConnectionStatus = "Ожидание подключения";
				if (scan)
				{
					item.ScanStatus = "Ожидание подключения";
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
				string configurationPath = configurationInput.Text.Trim();
				ScanConfiguration configuration = await Task.Run(() => ScanConfiguration.Load(configurationPath), token);
				var connected = new List<ComputerListItem>();
				foreach (ComputerListItem item in selected)
				{
					token.ThrowIfCancellationRequested();
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
						if (!ScanFailure.IsRecoverable(exception) && !(exception is InvalidOperationException))
						{
							throw;
						}

						item.ConnectionStatus = exception.Message;
						item.ScanStatus = scan ? "Пропущено: нет подключения" : item.ScanStatus;
						errors.Add(new ScanError(item.Description, exception.Message, ScanFailure.Classify(exception)));
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
						ComputerScanResult result = await Task.Run(() => scanner.Scan(item.IpAddress, computerConfiguration, token), token);
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

				statusLabel.Text = string.Format("Подключено ПК: {0} из {1}. {2}Ошибок подключения и чтения: {3}.{4}", connected.Count, selected.Count,
					scan ? "Обработано ПК: " + completed + "; сертификатов: " + certificates.Count + ". " : string.Empty, errors.Count,
					databaseFailures == 0 ? string.Empty : " Ошибок записи в БД: " + databaseFailures + ".");
			}
			catch (OperationCanceledException)
			{
				foreach (ComputerListItem item in selected.Where(value => value.ConnectionStatus == "Ожидание подключения"))
				{
					item.ConnectionStatus = "Подключение отменено";
				}

				foreach (ComputerListItem item in selected.Where(value => value.ScanStatus == "Ожидание подключения" || value.ScanStatus == "Сканирование…"))
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
				statusLabel.Text = "Ошибка. Подробности — в журнале telecom.log рядом с программой.";
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
					return repository.Save(result.Certificates, result.CryptoProVersion);
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
			if (closeWhenIdle)
			{
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

		// Не позволяет запустить второй обход или изменить путь во время текущей проверки.
		private void SetScanning(bool scanning)
		{
			modeTabs.Enabled = !scanning;
			scanButton.Enabled = !scanning && (profilesLoaded || modeTabs.SelectedIndex != 0);
			addComputerButton.Enabled = !scanning && profilesLoaded;
			editComputerButton.Enabled = !scanning && profilesLoaded;
			removeComputerButton.Enabled = !scanning && profilesLoaded;
			connectButton.Enabled = !scanning && profilesLoaded;
			browseButton.Enabled = !scanning;
			directoryInput.Enabled = !scanning;
			cancelButton.Enabled = scanning;
		}

		// Переводит технический итог проверки ПК в понятную подпись.
		private static string DescribeStatus(ComputerScanStatus status)
		{
			switch (status)
			{
				case ComputerScanStatus.Complete: return "Настроенные папки проверены";
				case ComputerScanStatus.Partial: return "Проверено частично";
				case ComputerScanStatus.AccessDenied: return "Нет прав доступа";
				case ComputerScanStatus.Unavailable: return "Сетевой доступ SMB недоступен";
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
					case ScanErrorKind.Unavailable: args.Value = "Нет связи SMB"; break;
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
				statusLabel.Text = "Закрытие… Ожидание текущего обращения к Windows.";
			}
		}

		private sealed class ComputerListItem
		{
			public ComputerProfile Profile { get; private set; }
			public bool Selected { get { return Profile.Selected; } set { Profile.Selected = value; } }
			public bool SavedSelection { get; set; }
			public string IpAddress { get { return Profile.IpAddress; } }
			public string DisplayName { get { return Profile.DisplayName; } }
			public string Description
			{
				get
				{
					string address = Profile.HasIpAddress ? IpAddress : "IPv4 не задан";
					return DisplayName.Length == 0 ? address : DisplayName + " (" + address + ")";
				}
			}
			public string Account { get { return Profile.HasUserName ? Profile.UserName : "Не задана"; } }
			public string ConnectionStatus { get; set; }
			public string ScanStatus { get; set; }
			public string CryptoProVersion { get; set; }
			public string DatabaseStatus { get; set; }

			// Отделяет отображаемые статусы от метаданных, которые сохраняются на диск.
			public ComputerListItem(ComputerProfile profile)
			{
				Profile = profile;
				SavedSelection = profile.Selected;
				ConnectionStatus = profile.HasIpAddress ? "Не подключено" : "Укажите IPv4";
			}
		}
	}
}
