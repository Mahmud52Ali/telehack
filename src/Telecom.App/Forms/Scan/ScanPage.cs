using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.App.Windows;
using Telecom.Core.Access;
using Telecom.Core.Certificates;
using Telecom.Core.Computers;
using Telecom.Core.Configuration;
using Telecom.Core.Database;
using Telecom.Core.Logging;
using Telecom.Core.Scanning;

namespace Telecom.App.Forms.Scan
{
	// Страница «Компьютеры»: список ПК, запуск подключения и сканирования, ход работы и результаты.
	// Сам сценарий выполняет ScanCoordinator, страница только показывает его ход.
	internal sealed class ScanPage : UserControl
	{
		private readonly ILogger logger;
		private readonly string configurationFilePath;
		private readonly string configurationTemplatePath;
		private readonly IComputerAccess computerAccess;
		private readonly IComputerConnector connection;
		private readonly IMachineCertificateStore machineCertificateStore;
		private readonly OperationGate operations;
		private readonly ComputerListPanel computersPanel;
		private readonly ScanResultsPanel resultsPanel = new ScanResultsPanel();
		private readonly Button scanButton = new Button { Text = "Сканировать", AutoSize = true };
		private readonly Button cancelButton = new Button { Text = "Отменить", AutoSize = true, Enabled = false };
		private readonly Label statusLabel = new Label();
		private bool running;

		// Открыта ли эта страница; сканирование запускается только с открытой страницы.
		internal bool IsActive { get; set; }

		// Выполняется ли сейчас подключение или сканирование, запущенное с этой страницы.
		internal bool IsRunning { get { return running; } }

		// Кнопка «Сканировать», которую окно назначает кнопкой по Enter.
		internal Button ScanButton { get { return scanButton; } }

		// Сохранённые профили ПК или null, пока список не загружен.
		internal IList<ComputerProfile> LoadedProfiles
		{
			get { return computersPanel.ProfilesLoaded ? computersPanel.Rows.Select(row => row.Profile).ToList() : null; }
		}

		// Раскладывает список ПК сверху, кнопки и строку состояния под ним, результаты внизу.
		internal ScanPage(ILogger logger, ComputerListFile computerListFile, string configurationFilePath, string configurationTemplatePath,
			IComputerAccess computerAccess, IComputerConnector connection, IMachineCertificateStore machineCertificateStore,
			WindowsCredentialStore credentialStore, OperationGate operations, Func<int> fontPercent)
		{
			this.logger = logger;
			this.configurationFilePath = configurationFilePath;
			this.configurationTemplatePath = configurationTemplatePath;
			this.computerAccess = computerAccess;
			this.connection = connection;
			this.machineCertificateStore = machineCertificateStore;
			this.operations = operations;
			computersPanel = new ComputerListPanel(computerListFile, credentialStore, operations, ShowStatus, fontPercent);
			computersPanel.ConnectRequested += async (sender, args) => await RunComputers(false);
			Dock = DockStyle.Fill;
			Margin = Padding.Empty;

			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 4 };
			layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			layout.Controls.Add(computersPanel, 0, 0);

			var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
			scanButton.Click += async (sender, args) => await RunComputers(true);
			cancelButton.Click += OnCancel;
			actions.Controls.Add(scanButton);
			actions.Controls.Add(cancelButton);
			layout.Controls.Add(actions, 0, 1);

			statusLabel.Text = "Отметьте этот компьютер или добавьте ПК сети для сканирования.";
			statusLabel.AutoSize = true;
			statusLabel.Dock = DockStyle.Fill;
			statusLabel.Margin = new Padding(0, 0, 0, 10);
			layout.Controls.Add(statusLabel, 0, 2);
			layout.Controls.Add(resultsPanel, 0, 3);
			Controls.Add(layout);
		}

		// Готовит конфигурацию и список ПК после появления окна, затем подключает отмеченные удалённые ПК.
		internal async Task InitializeAsync()
		{
			bool configurationReady = PrepareConfiguration();
			computersPanel.LoadProfiles();
			operations.NotifyChanged();
			if (configurationReady && computersPanel.ProfilesLoaded
				&& computersPanel.Rows.Any(item => item.Selected && !item.Profile.IsLocal))
			{
				await RunComputers(false);
			}
		}

		// Показывает сообщение в строке состояния страницы.
		internal void ShowStatus(string message)
		{
			statusLabel.Text = message;
		}

		// Согласует кнопки страницы с идущей операцией.
		internal void UpdateState()
		{
			computersPanel.UpdateState();
			scanButton.Enabled = !operations.IsBusy && computersPanel.ProfilesLoaded && IsActive;
			cancelButton.Enabled = operations.IsBusy && running && !operations.IsCancellationRequested;
		}

		// Создаёт рабочий XML из шаблона при первом запуске и направляет к настройкам при ошибке.
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
				ErrorReporter.Show("Не удалось подготовить конфигурацию сканирования: " + configurationFilePath, exception);
				statusLabel.Text = "Откройте «Настройки → Файлы и журнал», чтобы загрузить или восстановить конфигурацию.";
				return false;
			}
		}

		// Подключает отмеченные ПК, а при scan = true ещё и сканирует их; сбой одной учётки не останавливает остальные.
		private async Task RunComputers(bool scan)
		{
			if (operations.IsBusy || !computersPanel.ProfilesLoaded || !IsActive)
			{
				return;
			}

			computersPanel.EndEdit();
			List<ComputerRow> selected = computersPanel.Rows.Where(item => item.Selected && (scan || !item.Profile.IsLocal)).ToList();
			if (selected.Count == 0)
			{
				statusLabel.Text = scan ? "Отметьте хотя бы один компьютер галочкой." : "Отметьте удалённые компьютеры для подключения.";
				return;
			}

			running = true;
			CancellationToken token = operations.Start();
			var errors = new BindingList<ScanError>();
			var certificates = new BindingList<CertificateInfo>();
			resultsPanel.ShowErrors(errors);
			foreach (ComputerRow item in selected)
			{
				item.ConnectionStatus = item.Profile.IsLocal ? "Локально" : "Ожидание подключения";
				if (scan)
				{
					item.ScanStatus = "Ожидание сканирования";
					item.CryptoProVersion = string.Empty;
					item.DatabaseStatus = "Не записано";
				}
			}

			computersPanel.RefreshRows();
			if (scan)
			{
				resultsPanel.ShowCertificates(certificates);
			}

			Dictionary<ComputerProfile, ComputerRow> rows = selected.ToDictionary(item => item.Profile);
			var progress = new ControlProgress<ComputerProgress>(this,
				update => ShowProgress(rows[update.Profile], update, scan, errors, certificates));
			try
			{
				ScanConfiguration configuration = await Task.Run(() => ScanConfigurationFile.Load(configurationFilePath), token);
				var coordinator = new ScanCoordinator(computerAccess, connection, machineCertificateStore,
					connectionString => new CertificateWriter(new DatabaseConnection(connectionString, logger), logger), logger);
				List<ComputerProfile> profiles = selected.Select(item => item.Profile).ToList();
				ScanRunSummary summary = await Task.Run(() => coordinator.Run(profiles, scan, configuration, progress, token), token);
				string processed = scan
					? string.Format("Обработано ПК: {0} из {1}; сертификатов: {2}. ", summary.Completed, selected.Count, certificates.Count)
					: string.Format("Подключено удалённых ПК: {0} из {1}. ", summary.Connected, selected.Count);
				statusLabel.Text = processed + string.Format("Ошибок подключения и чтения: {0}.{1}", errors.Count,
					summary.DatabaseFailures == 0 ? string.Empty : " Ошибок записи в БД: " + summary.DatabaseFailures + ".");
			}
			catch (OperationCanceledException)
			{
				foreach (ComputerRow item in selected.Where(value => value.ConnectionStatus == "Ожидание подключения"))
				{
					item.ConnectionStatus = "Подключение отменено";
				}

				foreach (ComputerRow item in selected.Where(
					value => value.ScanStatus == "Ожидание сканирования" || value.ScanStatus == "Сканирование…"))
				{
					item.ScanStatus = "Отменено";
				}

				logger.Info("Подключение или сканирование выбранных ПК отменено.");
				statusLabel.Text = "Операция отменена. Уже полученные результаты сохранены в окне.";
				computersPanel.RefreshRows();
			}
			catch (Exception exception)
			{
				ErrorReporter.Show("Не удалось обработать выбранные компьютеры.", exception);
				statusLabel.Text = "Ошибка. Подробности в " + AppPaths.ErrorLogFilePath + ".";
			}
			finally
			{
				running = false;
				operations.Finish();
			}
		}

		// Переводит событие хода работы в статусы строки ПК и пополняет таблицы сертификатов и ошибок.
		private void ShowProgress(ComputerRow item, ComputerProgress update, bool scan,
			IList<ScanError> errors, IList<CertificateInfo> certificates)
		{
			switch (update.Stage)
			{
				case ComputerStage.Connecting:
					item.ConnectionStatus = "Подключение…";
					statusLabel.Text = "Подключение к " + item.Description + "…";
					break;
				case ComputerStage.Connected:
					item.ConnectionStatus = "SMB подключено";
					break;
				case ComputerStage.ConnectionCancelled:
					item.ConnectionStatus = "Подключение отменено";
					break;
				case ComputerStage.ConnectionFailed:
					item.ConnectionStatus = update.Error.Message;
					item.ScanStatus = scan ? "Пропущено: нет подключения" : item.ScanStatus;
					errors.Add(new ScanError(item.Description, update.Error.Message, ErrorClassifier.Classify(update.Error)));
					break;
				case ComputerStage.Scanning:
					item.ScanStatus = "Сканирование…";
					statusLabel.Text = "Сканирование " + item.Description + "…";
					break;
				case ComputerStage.Scanned:
					foreach (CertificateInfo certificate in update.ScanResult.Certificates)
					{
						certificates.Add(certificate);
					}

					foreach (ScanError error in update.ScanResult.Errors)
					{
						errors.Add(error);
					}

					item.ScanStatus = DisplayNames.ScanStatus(update.ScanResult.Status) + "; сертификатов: " + update.ScanResult.Certificates.Count;
					item.CryptoProVersion = DisplayNames.CryptoPro(update.ScanResult);
					break;
				case ComputerStage.SaveSkippedNoCertificates:
					item.DatabaseStatus = "Нет сертификатов";
					break;
				case ComputerStage.SaveSkippedNoDatabase:
					item.DatabaseStatus = "Строка подключения не задана";
					break;
				case ComputerStage.Saving:
					item.DatabaseStatus = "Запись…";
					statusLabel.Text = "Запись в БД для " + item.Description + "…";
					break;
				case ComputerStage.Saved:
					item.DatabaseStatus = string.Format("Добавлено: {0}, обновлено: {1}", update.SaveResult.Inserted, update.SaveResult.Updated);
					break;
				case ComputerStage.SaveFailed:
					item.DatabaseStatus = "Ошибка записи. " + update.Error.Message;
					break;
			}

			computersPanel.RefreshRows();
		}

		// Просит завершить обход; уже выполняющийся системный вызов SMB прервётся только после ответа Windows.
		private void OnCancel(object sender, EventArgs args)
		{
			if (operations.IsBusy)
			{
				operations.Cancel();
				cancelButton.Enabled = false;
				statusLabel.Text = "Отмена… Ожидание завершения текущего чтения.";
			}
		}
	}
}
