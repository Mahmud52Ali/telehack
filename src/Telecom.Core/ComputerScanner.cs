using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Telecom.Core
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

		// Проверяет один ПК по конфигурации, сохраняя частичные результаты при ошибках отдельных ресурсов.
		public ComputerScanResult Scan(string computerName, ScanConfiguration configuration, CancellationToken cancellationToken)
		{
			string computer = RemotePath.NormalizeComputerName(computerName);
			if (configuration == null)
			{
				throw new ArgumentNullException("configuration");
			}

			var result = new ComputerScanResult(computer);
			logger.Info("Начало проверки компьютера: " + computer);
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				access.CheckConnection(computer, configuration.ConnectionTimeoutSeconds, cancellationToken);
				var scanner = new DirectoryScanner(access, parser, logger);
				var expander = new DirectoryPatternExpander(access, logger);
				var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				var certificatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (string localPath in configuration.Directories)
				{
					cancellationToken.ThrowIfCancellationRequested();
					string pattern = RemotePath.ToUnc(computer, localPath);
					foreach (string path in expander.Expand(pattern, result.Errors, cancellationToken))
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

				FindCryptoPro(configuration, result, cancellationToken);
				result.Status = GetStatus(result);
			}
			catch (OperationCanceledException)
			{
				result.Status = ComputerScanStatus.Cancelled;
				logger.Info("Проверка компьютера отменена: " + computer);
			}
			catch (Exception exception)
			{
				if (!ScanFailure.IsRecoverable(exception))
				{
					throw;
				}

				RecordError(computer, exception, result);
				result.Status = GetStatus(result);
			}

			logger.Info(string.Format("Конец проверки ПК {0}: {1}; сертификатов: {2}; ошибок: {3}.",
				computer, result.Status, result.Certificates.Count, result.Errors.Count));
			return result;
		}

		// Читает ProductVersion csptest.exe из настроенных путей; отсутствие файла не равно отказу доступа.
		private void FindCryptoPro(ScanConfiguration configuration, ComputerScanResult result, CancellationToken cancellationToken)
		{
			int errorsBefore = result.Errors.Count;
			foreach (string localPath in configuration.CryptoProExecutables)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string remotePath = RemotePath.ToUnc(result.ComputerName, localPath);
				try
				{
					string version = NormalizeProductVersion(access.GetProductVersion(remotePath));
					result.CryptoProVersion = version;
					result.CryptoProSource = remotePath;
					result.CryptoProStatus = CryptoProStatus.Found;
					logger.Info("КриптоПро на " + result.ComputerName + ": " + version + "; источник: " + remotePath);
					return;
				}
				catch (Exception exception)
				{
					if (!ScanFailure.IsRecoverable(exception))
					{
						throw;
					}

					if (ScanFailure.Classify(exception) == ScanErrorKind.NotFound)
					{
						logger.Info("Не найден файл проверки КриптоПро: " + remotePath + Environment.NewLine + exception);
					}
					else
					{
						RecordError(remotePath, exception, result);
					}
				}
			}

			result.CryptoProStatus = result.Errors.Count == errorsBefore ? CryptoProStatus.NotFound : CryptoProStatus.Error;
		}

		// Нормализует ProductVersion, сохраняя ненулевую четвёртую часть версии.
		public static string NormalizeProductVersion(string value)
		{
			Match match = Regex.Match(value ?? string.Empty, @"^\s*(\d+\.\d+\.\d+(?:\.\d+)?)(?:\s.*)?$");
			Version version;
			if (!match.Success || !Version.TryParse(match.Groups[1].Value, out version))
			{
				throw new InvalidDataException("В файле csptest.exe отсутствует корректная версия продукта.");
			}

			return version.Revision <= 0 ? version.ToString(3) : version.ToString(4);
		}

		// Не объявляет полный успех при непроверенных ресурсах, даже если часть сертификатов найдена.
		private static ComputerScanStatus GetStatus(ComputerScanResult result)
		{
			if (result.Errors.Count == 0)
			{
				return ComputerScanStatus.Complete;
			}

			if (result.ScannedDirectories > 0 || result.Certificates.Count > 0)
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
			result.Errors.Add(new ScanError(path, exception.Message, ScanFailure.Classify(exception)));
			logger.Error("Ошибка проверки компьютера или ресурса: " + path, exception);
		}
	}
}
