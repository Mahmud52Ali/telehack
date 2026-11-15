using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Certificates;
using Telecom.Core.Common;
using Telecom.Core.Computers;
using Telecom.Core.Configuration;
using Telecom.Core.Logging;

namespace Telecom.Core.Scanning
{
	public sealed class ComputerScanner
	{
		private readonly IComputerAccess access;
		private readonly CertificateParser parser;
		private readonly ILogger logger;

		// Объединяет доступ к ПК, существующий парсер и журнал без привязки Core к SMB или WinForms.
		public ComputerScanner(IComputerAccess access, CertificateParser parser, ILogger logger)
		{
			this.access = access;
			this.parser = parser;
			this.logger = logger;
		}

		// Проверяет настроенные папки и версию КриптоПро на одном ПК.
		public ComputerScanResult Scan(string computerName, ScanConfiguration configuration, CancellationToken cancellationToken)
		{
			return Scan(computerName, configuration, cancellationToken, null, null);
		}

		// Проверяет папки и хранилище «Личное»; локальный профиль читается без сетевого подключения.
		public ComputerScanResult ScanProfile(ComputerProfile profile, ScanConfiguration configuration,
			IMachineCertificateStore machineStore, CancellationToken cancellationToken)
		{
			return Scan(profile.IsLocal ? Environment.MachineName : profile.IpAddress, configuration, cancellationToken, profile, machineStore);
		}

		// Выполняет сбор с одного ПК и сохраняет доступные результаты при ошибках отдельных источников.
		private ComputerScanResult Scan(string computerName, ScanConfiguration configuration, CancellationToken cancellationToken,
			ComputerProfile profile, IMachineCertificateStore machineStore)
		{
			bool isLocal = profile != null && profile.IsLocal;
			string computer = isLocal ? computerName : RemotePath.NormalizeComputerName(computerName);
			if (configuration == null)
			{
				throw new ArgumentNullException("configuration");
			}

			var result = new ComputerScanResult(computer);
			bool storeChecked = false;
			logger.Info("Начало проверки компьютера: " + computer);
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!isLocal)
				{
					access.CheckConnection(computer, configuration.ConnectionTimeoutSeconds, cancellationToken);
				}
				var scanner = new DirectoryScanner(access, parser, logger);
				var expander = new DirectoryPatternExpander(access, logger);
				var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				var certificatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (string localPath in configuration.Directories)
				{
					cancellationToken.ThrowIfCancellationRequested();
					string pattern = isLocal ? localPath : RemotePath.ToUnc(computer, localPath);
					int errorsBefore = result.Errors.Count;
					IList<string> paths = expander.Expand(pattern, result.Errors, cancellationToken);
					// У доступных профилей хранилище «Личное» может быть пустым.
					storeChecked |= paths.Count == 0 && result.Errors.Count == errorsBefore;
					foreach (string path in paths)
					{
						if (!scannedPaths.Add(path))
						{
							continue;
						}

						DirectoryScanResult directory = scanner.Scan(path, cancellationToken);
						foreach (CertificateInfo certificate in directory.Certificates)
						{
							if (certificatePaths.Add(certificate.SourcePath))
							{
								result.Certificates.Add(certificate);
							}
						}

						foreach (ScanError error in directory.Errors)
						{
							result.Errors.Add(error);
						}

						result.ScannedDirectories += directory.ScannedDirectories;
						result.SkippedFiles += directory.SkippedFiles;
						if (directory.Cancelled)
						{
							throw new OperationCanceledException(cancellationToken);
						}
					}
				}

				if (machineStore != null)
				{
					cancellationToken.ThrowIfCancellationRequested();
					storeChecked |= ReadMachineStore(profile, machineStore, result, cancellationToken);
				}

				new CryptoProVersionReader(access, logger).Read(configuration, result, isLocal, cancellationToken);
				result.Status = GetStatus(result, storeChecked);
			}
			catch (OperationCanceledException)
			{
				result.Status = ComputerScanStatus.Cancelled;
				logger.Info("Проверка компьютера отменена: " + computer);
			}
			catch (Exception exception)
			{
				if (!ErrorClassifier.IsRecoverable(exception))
				{
					throw;
				}

				RecordError(computer, exception, result);
				result.Status = GetStatus(result, storeChecked);
			}

			logger.Info(string.Format("Конец проверки ПК {0}: {1}; сертификатов: {2}; ошибок: {3}.",
				computer, result.Status, result.Certificates.Count, result.Errors.Count));
			return result;
		}

		// Добавляет сертификаты из реестра к файловым находкам и отдельно отмечает сбой хранилища.
		private bool ReadMachineStore(ComputerProfile profile, IMachineCertificateStore machineStore,
			ComputerScanResult result, CancellationToken cancellationToken)
		{
			string source = GetMachineStoreSource(result.ComputerName);
			try
			{
				machineStore.Read(profile, cancellationToken,
					(entry, contents) => ParseStoreCertificate(contents, source + @"\" + entry, result),
					(entry, exception) => RecordError(source + @"\" + entry, exception, result));
				logger.Info("Завершено чтение хранилища «Личное» компьютера (" + source + ").");
				return true;
			}
			catch (Exception exception)
			{
				if (!ErrorClassifier.IsRecoverable(exception))
				{
					throw;
				}

				const string message = "Не удалось прочитать хранилище «Личное» компьютера.";
				result.Errors.Add(new ScanError(source, message + " " + exception.Message, ErrorClassifier.Classify(exception)));
				logger.Error(message + " " + source, exception);
				return false;
			}
		}

		// Формирует тот же префикс источника для поиска записи при проверке наличия.
		internal static string GetMachineStoreSource(string computerName)
		{
			return "Реестр " + computerName + @"\LocalMachine\My";
		}

		// Записывает ошибку отдельного сертификата, чтобы следующие записи хранилища тоже обработались.
		private void ParseStoreCertificate(byte[] contents, string source, ComputerScanResult result)
		{
			try
			{
				result.Certificates.Add(parser.Parse(contents, source));
			}
			catch (Exception exception)
			{
				if (!ErrorClassifier.IsRecoverable(exception))
				{
					throw;
				}

				RecordError(source, exception, result);
			}
		}

		// Не объявляет полный успех при непроверенных ресурсах, даже если часть сертификатов найдена.
		private static ComputerScanStatus GetStatus(ComputerScanResult result, bool storeChecked)
		{
			if (result.Errors.Count == 0)
			{
				return ComputerScanStatus.Complete;
			}

			if (storeChecked || result.ScannedDirectories > 0 || result.Certificates.Count > 0)
			{
				return ComputerScanStatus.Partial;
			}

			foreach (ScanError error in result.Errors)
			{
				if (error.Kind == ScanErrorKind.Unavailable)
				{
					return ComputerScanStatus.Unavailable;
				}
				if (error.Kind == ScanErrorKind.AccessDenied)
				{
					return ComputerScanStatus.AccessDenied;
				}
			}

			return ComputerScanStatus.Failed;
		}

		// Записывает ошибку с контекстом ПК или ресурса в результат и журнал.
		private void RecordError(string path, Exception exception, ComputerScanResult result)
		{
			result.Errors.Add(new ScanError(path, exception.Message, ErrorClassifier.Classify(exception)));
			logger.Error("Ошибка проверки компьютера или ресурса: " + path, exception);
		}
	}
}
