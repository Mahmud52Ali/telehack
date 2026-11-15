using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Certificates;
using Telecom.Core.Logging;

namespace Telecom.Core.Scanning
{
	public sealed class DirectoryScanner
	{
		private readonly ICertificateFileSystem fileSystem;
		private readonly CertificateParser parser;
		private readonly ILogger logger;

		// Отделяет обход и разбор сертификатов от конкретного способа доступа к файлам.
		public DirectoryScanner(ICertificateFileSystem fileSystem, CertificateParser parser, ILogger logger)
		{
			this.fileSystem = fileSystem;
			this.parser = parser;
			this.logger = logger;
		}

		// Рекурсивно проверяет папку, продолжая обработку доступных файлов после отдельных ошибок.
		public DirectoryScanResult Scan(string directoryPath, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(directoryPath))
			{
				throw new ArgumentException("Не указан путь к папке.", "directoryPath");
			}

			var result = new DirectoryScanResult();
			var pendingDirectories = new Stack<string>();
			var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			pendingDirectories.Push(directoryPath);
			logger.Info("Начало сканирования: " + directoryPath);
			try
			{
				while (pendingDirectories.Count > 0)
				{
					cancellationToken.ThrowIfCancellationRequested();
					string currentDirectory = pendingDirectories.Pop();
					if (!visitedDirectories.Add(currentDirectory))
					{
						continue;
					}

					try
					{
						if (fileSystem.IsReparsePoint(currentDirectory))
						{
							throw new IOException("Каталог-ссылка не проверялся: обход ссылок отключён во избежание циклов.");
						}

						foreach (string filePath in fileSystem.GetFiles(currentDirectory))
						{
							cancellationToken.ThrowIfCancellationRequested();
							ReadFile(filePath, result);
						}

						foreach (string childDirectory in fileSystem.GetDirectories(currentDirectory))
						{
							cancellationToken.ThrowIfCancellationRequested();
							pendingDirectories.Push(childDirectory);
						}
						result.ScannedDirectories++;
					}
					catch (Exception exception)
					{
						if (!ErrorClassifier.IsRecoverable(exception))
						{
							throw;
						}

						RecordError(currentDirectory, exception, result);
					}
				}
			}
			catch (OperationCanceledException)
			{
				result.Cancelled = true;
				logger.Info("Сканирование отменено: " + directoryPath);
			}

			logger.Info(string.Format("Конец сканирования: {0}; сертификатов: {1}; ошибок: {2}; пропущено файлов: {3}.",
				directoryPath, result.Certificates.Count, result.Errors.Count, result.SkippedFiles));
			return result;
		}

		// Пропускает контейнеры ключей и посторонние форматы, проверяя подходящие файлы по содержимому.
		private void ReadFile(string filePath, DirectoryScanResult result)
		{
			string extension = Path.GetExtension(filePath);
			if (extension.Length > 0 && !extension.Equals(".cer", StringComparison.OrdinalIgnoreCase)
				&& !extension.Equals(".crt", StringComparison.OrdinalIgnoreCase))
			{
				result.SkippedFiles++;
				logger.Info("Пропущен неподдерживаемый файл: " + filePath);
				return;
			}

			try
			{
				if (fileSystem.IsReparsePoint(filePath))
				{
					throw new IOException("Файл-ссылка не проверялся: обход ссылок отключён.");
				}

				result.Certificates.Add(parser.Parse(fileSystem.ReadAllBytes(filePath), filePath));
			}
			catch (Exception exception)
			{
				if (!ErrorClassifier.IsRecoverable(exception))
				{
					throw;
				}

				RecordError(filePath, exception, result);
			}
		}

		// Добавляет ошибку в результат и записывает диагностические сведения в файл журнала.
		private void RecordError(string path, Exception exception, DirectoryScanResult result)
		{
			result.Errors.Add(new ScanError(path, exception.Message, ErrorClassifier.Classify(exception)));
			logger.Error("Ошибка чтения: " + path, exception);
		}
	}
}
