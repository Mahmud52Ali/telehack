using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Certificates;
using Telecom.Core.Common;
using Telecom.Core.Computers;
using Telecom.Core.Logging;
using Telecom.Core.Scanning;

namespace Telecom.Core.Locations
{
	public sealed class CertificateLocationChecker
	{
		private readonly ICertificateFileSystem fileSystem;
		private readonly IMachineCertificateStore machineStore;
		private readonly IComputerConnector connector;
		private readonly int connectionTimeoutSeconds;
		private readonly ILogger logger;
		private readonly CertificateParser parser = new CertificateParser();

		// connector устанавливает сеанс с удалённым ПК перед чтением файлов; для локального ПК не вызывается.
		public CertificateLocationChecker(ICertificateFileSystem fileSystem, IMachineCertificateStore machineStore,
			IComputerConnector connector, int connectionTimeoutSeconds, ILogger logger)
		{
			this.fileSystem = fileSystem;
			this.machineStore = machineStore;
			this.connector = connector;
			this.connectionTimeoutSeconds = connectionTimeoutSeconds;
			this.logger = logger;
		}

		// Проверяет сохранённые места одного ПК, читая каждый путь один раз.
		public void CheckComputer(string computerAddress, ComputerProfile profile, IList<CertificateLocationCheck> locations, CancellationToken token)
		{
			token.ThrowIfCancellationRequested();
			foreach (CertificateLocationCheck location in locations)
			{
				location.Status = CertificateLocationStatus.Unverified;
				location.CheckedAtUtc = default(DateTime);
			}
			if (profile == null)
			{
				RecordFailure(computerAddress, locations, new InvalidOperationException("Для этого компьютера нет сохранённого профиля."));
				return;
			}

			string storePrefix = ComputerScanner.GetMachineStoreSource(computerAddress) + @"\";
			var files = new List<IGrouping<string, CertificateLocationCheck>>();
			var entries = new Dictionary<string, IGrouping<string, CertificateLocationCheck>>(StringComparer.OrdinalIgnoreCase);
			foreach (IGrouping<string, CertificateLocationCheck> path in locations.GroupBy(
				location => location.SourcePath, StringComparer.OrdinalIgnoreCase))
			{
				if (path.Key.StartsWith(storePrefix, StringComparison.OrdinalIgnoreCase))
				{
					entries.Add(path.Key.Substring(storePrefix.Length), path);
				}
				else
				{
					files.Add(path);
				}
			}

			if (files.Count > 0)
			{
				CheckFiles(profile, files, token);
			}
			if (entries.Count > 0)
			{
				CheckStore(profile, entries, token);
			}
		}

		// Авторизуется один раз для всех файлов ПК и открывает только сохранённые пути.
		private void CheckFiles(ComputerProfile profile, IList<IGrouping<string, CertificateLocationCheck>> files, CancellationToken token)
		{
			if (!profile.IsLocal)
			{
				try
				{
					connector.Connect(profile, connectionTimeoutSeconds, token);
				}
				catch (Exception exception)
				{
					if (!ErrorClassifier.IsRecoverable(exception) && !(exception is InvalidOperationException))
					{
						throw;
					}

					RecordFailure(profile.IpAddress, files.SelectMany(file => file), exception);
					return;
				}
			}

			foreach (IGrouping<string, CertificateLocationCheck> file in files)
			{
				token.ThrowIfCancellationRequested();
				byte[] contents;
				try
				{
					string path = ResolveFilePath(profile, file.Key);
					if (fileSystem.IsReparsePoint(path))
					{
						throw new IOException("Путь указывает на ссылку.");
					}
					contents = fileSystem.ReadAllBytes(path);
				}
				catch (Exception exception)
				{
					if (!ErrorClassifier.IsRecoverable(exception) && !(exception is ArgumentException))
					{
						throw;
					}

					if (ErrorClassifier.Classify(exception) == ScanErrorKind.NotFound)
					{
						logger.Info("При проверке наличия путь отсутствует: " + file.Key);
						RecordResult(file, CertificateLocationStatus.Missing);
					}
					else
					{
						RecordFailure(file.Key, file, exception);
					}
					continue;
				}

				CompareCertificate(file.Key, file, contents);
			}
		}

		// Ограничивает удалённый файловый путь дисками ПК из сохранённой привязки.
		private static string ResolveFilePath(ComputerProfile profile, string sourcePath)
		{
			if (profile.IsLocal)
			{
				return RemotePath.NormalizeLocalPath(sourcePath, false);
			}

			if (sourcePath.StartsWith(@"\\", StringComparison.Ordinal))
			{
				string prefix = @"\\" + profile.IpAddress + @"\";
				if (!sourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				{
					throw new ArgumentException("UNC-путь относится к другому компьютеру.");
				}

				string relativePath = sourcePath.Substring(prefix.Length);
				if (relativePath.Length < 4 || relativePath[1] != '$' || relativePath[2] != '\\')
				{
					throw new ArgumentException("Путь должен указывать на файл на удалённом диске.");
				}

				sourcePath = relativePath[0] + ":" + relativePath.Substring(2);
			}

			return RemotePath.ToUnc(profile.IpAddress, RemotePath.NormalizeLocalPath(sourcePath, false));
		}

		// Открывает хранилище один раз и читает только ключи найденных ранее сертификатов.
		private void CheckStore(ComputerProfile profile, IDictionary<string, IGrouping<string, CertificateLocationCheck>> entries,
			CancellationToken token)
		{
			try
			{
				if (machineStore == null)
				{
					throw new InvalidOperationException("Чтение хранилища сертификатов недоступно.");
				}

				machineStore.ReadEntries(profile, entries.Keys, token,
					(entry, contents) => CompareCertificate(entries[entry].Key, entries[entry], contents),
					(entry, exception) => RecordFailure(entries[entry].Key, entries[entry], exception));
			}
			catch (Exception exception)
			{
				if (!ErrorClassifier.IsRecoverable(exception) && !(exception is InvalidOperationException))
				{
					throw;
				}

				RecordFailure("хранилище «Личное» " + (profile.IsLocal ? Environment.MachineName : profile.IpAddress),
					entries.Values.SelectMany(entry => entry).Where(location => location.CheckedAtUtc == default(DateTime)), exception);
			}
		}

		// Сравнивает серийный номер прочитанного сертификата со всеми находками по этому пути.
		private void CompareCertificate(string sourcePath, IEnumerable<CertificateLocationCheck> locations, byte[] contents)
		{
			if (contents == null)
			{
				RecordResult(locations, CertificateLocationStatus.Missing);
				return;
			}

			CertificateInfo certificate;
			try
			{
				certificate = parser.Parse(contents, sourcePath);
			}
			catch (Exception exception)
			{
				if (!ErrorClassifier.IsRecoverable(exception))
				{
					throw;
				}

				RecordFailure(sourcePath, locations, exception);
				return;
			}

			DateTime checkedAt = DateTime.UtcNow;
			foreach (CertificateLocationCheck location in locations)
			{
				location.Status = string.Equals(location.SerialNumber, certificate.SerialNumber, StringComparison.OrdinalIgnoreCase)
					? CertificateLocationStatus.Found : CertificateLocationStatus.DifferentCertificate;
				location.CheckedAtUtc = checkedAt;
			}
		}

		// Пишет ошибку в журнал и помечает места как «не удалось проверить».
		private void RecordFailure(string source, IEnumerable<CertificateLocationCheck> locations, Exception exception)
		{
			logger.Error("Не удалось проверить наличие сертификата. Источник: " + source, exception);
			RecordResult(locations, CertificateLocationStatus.Unverified);
		}

		// Проставляет итог и время проверки всем указанным местам.
		private static void RecordResult(IEnumerable<CertificateLocationCheck> locations, CertificateLocationStatus status)
		{
			DateTime checkedAt = DateTime.UtcNow;
			foreach (CertificateLocationCheck location in locations)
			{
				location.Status = status;
				location.CheckedAtUtc = checkedAt;
			}
		}
	}
}
