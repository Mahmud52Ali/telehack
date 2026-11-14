using System;
using System.Data.Common;
using System.Drawing;
using System.IO;
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
		private readonly TextBox computerInput = new TextBox();
		private readonly TextBox configurationInput = new TextBox();
		private readonly TabControl modeTabs = new TabControl();
		private readonly Button browseButton = new Button();
		private readonly Button scanButton = new Button();
		private readonly Button cancelButton = new Button();
		private readonly Label statusLabel = new Label();
		private readonly DataGridView certificatesGrid = new DataGridView();
		private readonly DataGridView errorsGrid = new DataGridView();
		private CancellationTokenSource scanCancellation;

		// Создаёт панель проверки одного компьютера и сохраняет ручной режим проверки папки.
		public MainForm(ILogger logger)
		{
			this.logger = logger;
			Text = "СКЗИ — сканирование сертификатов";
			Font = new Font("Segoe UI", 9F);
			AutoScaleMode = AutoScaleMode.Font;
			Size = new Size(1150, 720);
			MinimumSize = new Size(950, 550);
			StartPosition = FormStartPosition.CenterScreen;
			BuildLayout();
			FormClosing += OnFormClosing;
		}

		// Раскладывает выбор режима, кнопки, статус и две таблицы без файлов дизайнера.
		private void BuildLayout()
		{
			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 4, ColumnCount = 1 };
			layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 155));
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

			statusLabel.Text = "Укажите компьютер и конфигурацию. Используются права текущей учётной записи Windows.";
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

		// Разделяет проверку ПК по конфигурации и прежний ручной выбор одной папки.
		private void BuildModes()
		{
			modeTabs.Dock = DockStyle.Fill;
			var computerTab = new TabPage("Компьютер");
			var computerPanel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(6), ColumnCount = 3, RowCount = 3 };
			computerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
			computerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			computerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			computerPanel.Controls.Add(new Label { Text = "Имя ПК или IPv4", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
			computerInput.Dock = DockStyle.Fill;
			computerInput.AccessibleName = "Имя компьютера или IPv4";
			computerPanel.Controls.Add(computerInput, 1, 0);
			computerPanel.Controls.Add(new Label { Text = "Конфигурация", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
			configurationInput.Dock = DockStyle.Fill;
			configurationInput.AccessibleName = "Файл конфигурации";
			configurationInput.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scan.config.xml");
			computerPanel.Controls.Add(configurationInput, 1, 1);
			var configButton = new Button { Text = "Обзор…", AutoSize = true };
			configButton.Click += OnBrowseConfiguration;
			computerPanel.Controls.Add(configButton, 2, 1);
			var hint = new Label { Text = "Папки и пути к КриптоПро задаются в XML-конфигурации рядом с программой.", AutoSize = true };
			computerPanel.Controls.Add(hint, 0, 2);
			computerPanel.SetColumnSpan(hint, 3);
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
			bool computerMode = modeTabs.SelectedIndex == 0;
			string source = computerMode ? computerInput.Text.Trim() : directoryInput.Text.Trim();
			if (source.Length == 0)
			{
				statusLabel.Text = computerMode ? "Укажите имя компьютера или IPv4." : "Укажите папку для проверки.";
				(computerMode ? computerInput : directoryInput).Focus();
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
				if (computerMode)
				{
					string configurationPath = configurationInput.Text.Trim();
					var scanner = new ComputerScanner(new WindowsFileSystem(), new CertificateParser(), logger);
					string databaseStatus = null;
					ComputerScanResult result = await Task.Run(() =>
					{
						ScanConfiguration configuration = ScanConfiguration.Load(configurationPath);
						ComputerScanResult scanResult = scanner.Scan(source, configuration, token);
						databaseStatus = SaveToDatabase(configuration, scanResult);
						return scanResult;
					});
					if (!IsDisposed)
					{
						certificatesGrid.DataSource = result.Certificates;
						errorsGrid.DataSource = result.Errors;
						statusLabel.Text = string.Format("ПК: {0}. {1}. КриптоПро: {2}.{3}Сертификатов: {4}; папок проверено: {5}; ошибок: {6}.{3}{7}",
							result.ComputerName, DescribeStatus(result.Status), DescribeCryptoPro(result), Environment.NewLine,
							result.Certificates.Count, result.ScannedDirectories, result.Errors.Count, databaseStatus);
					}
				}
				else
				{
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
				scanCancellation.Dispose();
				scanCancellation = null;
				if (!IsDisposed)
				{
					SetScanning(false);
				}
			}
		}

		// Записывает найденные сертификаты в MSSQL; сбой базы не отменяет показ результатов сканирования.
		private string SaveToDatabase(ScanConfiguration configuration, ComputerScanResult result)
		{
			if (result.Status == ComputerScanStatus.Cancelled)
			{
				return "База данных: запись не выполнялась, проверка отменена.";
			}

			if (result.Certificates.Count == 0)
			{
				return "База данных: нет сертификатов для записи.";
			}

			if (configuration.ConnectionString.Length == 0)
			{
				return "База данных: строка подключения не задана, запись не выполнялась.";
			}

			try
			{
				var repository = new CertificateRepository(configuration.ConnectionString, logger);
				CertificateSaveResult saved = repository.Save(result.Certificates, result.CryptoProVersion);
				return string.Format("База данных: добавлено сертификатов: {0}; обновлено: {1}.", saved.Inserted, saved.Updated);
			}
			catch (Exception exception)
			{
				// Ошибки сервера, сети и неверной строки подключения ожидаемы; остальные считаются ошибками программы.
				if (!(exception is DbException) && !(exception is InvalidOperationException) && !(exception is ArgumentException))
				{
					throw;
				}

				logger.Error("Ошибка записи в базу данных.", exception);
				return "База данных: ошибка записи. " + exception.Message;
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
			scanButton.Enabled = !scanning;
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

		// Останавливает дальнейший обход при закрытии окна.
		private void OnFormClosing(object sender, FormClosingEventArgs args)
		{
			if (scanCancellation != null)
			{
				scanCancellation.Cancel();
			}
		}
	}
}
