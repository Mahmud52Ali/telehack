using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Certificates;
using Telecom.Core.Computers;
using Telecom.Core.Configuration;
using Telecom.Core.Database;
using Telecom.Core.Logging;

namespace Telecom.Core.Scanning
{
	// Основной сценарий приложения: подключить выбранные ПК → просканировать доступные → записать результаты в БД.
	// Сбой одного ПК не останавливает остальные. Работает синхронно, интерфейс вызывает его в фоновой задаче.
	public sealed class ScanCoordinator
	{
		private readonly IComputerConnector connector;
		private readonly ComputerScanner scanner;
		private readonly IMachineCertificateStore machineStore;
		private readonly Func<string, ICertificateWriter> createWriter;
		private readonly ILogger logger;

		// createWriter получает строку подключения из конфигурации и возвращает объект записи в БД.
		public ScanCoordinator(IComputerAccess access, IComputerConnector connector, IMachineCertificateStore machineStore,
			Func<string, ICertificateWriter> createWriter, ILogger logger)
		{
			this.connector = connector;
			this.machineStore = machineStore;
			this.createWriter = createWriter;
			this.logger = logger;
			scanner = new ComputerScanner(access, new CertificateParser(), logger);
		}

		// Подключает удалённые ПК из списка; при scan = true затем сканирует подключённые и записывает находки.
		// При отмене бросает OperationCanceledException после сообщения о текущем этапе.
		public ScanRunSummary Run(IList<ComputerProfile> profiles, bool scan, ScanConfiguration configuration,
			IProgress<ComputerProgress> progress, CancellationToken cancellationToken)
		{
			var summary = new ScanRunSummary();
			List<ComputerProfile> connected = ConnectAll(profiles, configuration, progress, cancellationToken);
			summary.Connected = connected.Count;
			if (!scan)
			{
				return summary;
			}

			foreach (ComputerProfile profile in connected)
			{
				cancellationToken.ThrowIfCancellationRequested();
				ComputerScanResult result = ScanComputer(profile, configuration, progress, cancellationToken);
				summary.Completed++;
				if (!SaveResults(profile, result, configuration, progress, cancellationToken))
				{
					summary.DatabaseFailures++;
				}

				cancellationToken.ThrowIfCancellationRequested();
			}

			return summary;
		}

		// Подключается к каждому удалённому ПК по очереди и возвращает те, что доступны; локальный ПК подключать не нужно.
		private List<ComputerProfile> ConnectAll(IList<ComputerProfile> profiles, ScanConfiguration configuration,
			IProgress<ComputerProgress> progress, CancellationToken cancellationToken)
		{
			var connected = new List<ComputerProfile>();
			foreach (ComputerProfile profile in profiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (profile.IsLocal)
				{
					connected.Add(profile);
					continue;
				}

				progress.Report(new ComputerProgress(profile, ComputerStage.Connecting));
				try
				{
					logger.Info("Подключение SMB к " + profile.Description);
					connector.Connect(profile, configuration.ConnectionTimeoutSeconds, cancellationToken);
					connected.Add(profile);
					logger.Info("Соединение SMB установлено: " + profile.Description);
					progress.Report(new ComputerProgress(profile, ComputerStage.Connected));
				}
				catch (OperationCanceledException)
				{
					progress.Report(new ComputerProgress(profile, ComputerStage.ConnectionCancelled));
					throw;
				}
				catch (Exception exception)
				{
					// InvalidOperationException здесь означает неполный профиль или несовпадение сохранённой учётки.
					if (!ErrorClassifier.IsRecoverable(exception) && !(exception is InvalidOperationException))
					{
						throw;
					}

					logger.Error("Ошибка подключения к " + profile.Description, exception);
					progress.Report(new ComputerProgress(profile, ComputerStage.ConnectionFailed, error: exception));
				}
			}

			return connected;
		}

		// Сканирует один ПК с общими и его дополнительными путями.
		private ComputerScanResult ScanComputer(ComputerProfile profile, ScanConfiguration configuration,
			IProgress<ComputerProgress> progress, CancellationToken cancellationToken)
		{
			progress.Report(new ComputerProgress(profile, ComputerStage.Scanning));
			ScanConfiguration computerConfiguration = configuration.WithAdditionalDirectories(profile.AdditionalDirectories);
			ComputerScanResult result = scanner.Scan(profile, computerConfiguration, machineStore, cancellationToken);
			progress.Report(new ComputerProgress(profile, ComputerStage.Scanned, result));
			if (result.Status == ComputerScanStatus.Cancelled)
			{
				throw new OperationCanceledException(cancellationToken);
			}

			return result;
		}

		// Записывает находки одного ПК в БД; возвращает false только при ошибке записи.
		private bool SaveResults(ComputerProfile profile, ComputerScanResult result, ScanConfiguration configuration,
			IProgress<ComputerProgress> progress, CancellationToken cancellationToken)
		{
			if (result.Certificates.Count == 0)
			{
				progress.Report(new ComputerProgress(profile, ComputerStage.SaveSkippedNoCertificates));
				return true;
			}

			if (configuration.ConnectionString.Length == 0)
			{
				progress.Report(new ComputerProgress(profile, ComputerStage.SaveSkippedNoDatabase));
				return true;
			}

			cancellationToken.ThrowIfCancellationRequested();
			progress.Report(new ComputerProgress(profile, ComputerStage.Saving));
			try
			{
				ICertificateWriter writer = createWriter(configuration.ConnectionString);
				CertificateSaveResult saved = writer.Save(result.Certificates, result.CryptoProVersion, result.ComputerName);
				progress.Report(new ComputerProgress(profile, ComputerStage.Saved, saveResult: saved));
				return true;
			}
			catch (Exception exception)
			{
				if (!(exception is DbException) && !(exception is InvalidOperationException) && !(exception is ArgumentException))
				{
					throw;
				}

				logger.Error("Ошибка записи в базу данных для " + profile.Description, exception);
				progress.Report(new ComputerProgress(profile, ComputerStage.SaveFailed, error: exception));
				return false;
			}
		}
	}
}
