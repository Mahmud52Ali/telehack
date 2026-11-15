using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Common;
using Telecom.Core.Configuration;
using Telecom.Core.Logging;

namespace Telecom.Core.Scanning
{
	// Определяет версию КриптоПро CSP по ProductVersion файла csptest.exe, не запуская его.
	public sealed class CryptoProVersionReader
	{
		private readonly IComputerAccess access;
		private readonly ILogger logger;

		// Использует тот же доступ к файлам ПК, что и сканер сертификатов.
		public CryptoProVersionReader(IComputerAccess access, ILogger logger)
		{
			this.access = access;
			this.logger = logger;
		}

		// Проверяет пути из конфигурации по порядку и записывает в результат первую найденную версию.
		// Отсутствие файла не считается ошибкой, а отказ доступа или битая версия — считаются.
		public void Read(ScanConfiguration configuration, ComputerScanResult result, bool isLocal, CancellationToken cancellationToken)
		{
			int errorsBefore = result.Errors.Count;
			foreach (string localPath in configuration.CryptoProExecutables)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string path = isLocal ? localPath : RemotePath.ToUnc(result.ComputerName, localPath);
				try
				{
					string version = NormalizeProductVersion(access.GetProductVersion(path));
					result.CryptoProVersion = version;
					result.CryptoProSource = path;
					result.CryptoProStatus = CryptoProStatus.Found;
					logger.Info("КриптоПро на " + result.ComputerName + ": " + version + "; источник: " + path);
					return;
				}
				catch (Exception exception)
				{
					if (!ErrorClassifier.IsRecoverable(exception))
					{
						throw;
					}

					if (ErrorClassifier.Classify(exception) == ScanErrorKind.NotFound)
					{
						logger.Info("Не найден файл проверки КриптоПро: " + path);
					}
					else
					{
						result.Errors.Add(new ScanError(path, exception.Message, ErrorClassifier.Classify(exception)));
						logger.Error("Ошибка проверки компьютера или ресурса: " + path, exception);
					}
				}
			}

			result.CryptoProStatus = result.Errors.Count == errorsBefore ? CryptoProStatus.NotFound : CryptoProStatus.Error;
		}

		// Приводит ProductVersion к виду 5.0.13003; ненулевая четвёртая часть сохраняется.
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
	}
}
