using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Certificates;
using Telecom.Core.Computers;
using Telecom.Core.Configuration;
using Telecom.Core.Database;
using Telecom.Core.Scanning;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private const string CoordinatorOnlineIp = "192.0.2.20";
		private const string CoordinatorOfflineIp = "192.0.2.21";

		// Проверяет сценарий «подключить → сканировать → записать» без сети, SMB и MSSQL.
		private static void AddScanCoordinatorTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("Координатор: недоступный ПК не мешает сканированию и записи остальных", CheckCoordinatorFullRun));
			tests.Add(new KeyValuePair<string, Action>("Координатор: ошибка записи в БД учитывается и не прерывает запуск", CheckCoordinatorSaveFailure));
			tests.Add(new KeyValuePair<string, Action>("Координатор: без строки подключения запись пропускается", CheckCoordinatorWithoutDatabase));
			tests.Add(new KeyValuePair<string, Action>("Координатор: режим подключения не сканирует", CheckCoordinatorConnectOnly));
			tests.Add(new KeyValuePair<string, Action>("Координатор: отмена во время подключения", CheckCoordinatorCancellation));
		}

		// Один ПК доступен, второй нет: доступный сканируется и записывается, недоступный получает ошибку подключения.
		private static void CheckCoordinatorFullRun()
		{
			var connector = new FakeConnector();
			connector.Failures[CoordinatorOfflineIp] = new IOException("Тестовый отказ сети.");
			var writer = new FakeCertificateWriter();
			var progress = new RecordingProgress();
			ComputerProfile online = CoordinatorProfile(CoordinatorOnlineIp);
			ComputerProfile offline = CoordinatorProfile(CoordinatorOfflineIp);
			ScanCoordinator coordinator = CreateCoordinator(connector, writer);

			ScanRunSummary summary = coordinator.Run(new[] { offline, online }, true, CoordinatorConfiguration("Server=.;Database=Tests"),
				progress, CancellationToken.None);

			Check.Equal(1, summary.Connected);
			Check.Equal(1, summary.Completed);
			Check.Equal(0, summary.DatabaseFailures);
			Check.Equal("Connecting ConnectionFailed", progress.StagesOf(offline));
			Check.Equal("Connecting Connected Scanning Scanned Saving Saved", progress.StagesOf(online));
			Check.Equal(1, writer.Calls.Count);
			Check.Equal(CoordinatorOnlineIp, writer.Calls[0].ComputerAddress);
			Check.Equal("5.0.13003", writer.Calls[0].CryptoProVersion);
			Check.Equal(1, writer.Calls[0].Certificates.Count);
		}

		// Ошибка записи даёт этап SaveFailed и счётчик, а не исключение.
		private static void CheckCoordinatorSaveFailure()
		{
			var writer = new FakeCertificateWriter { Failure = new InvalidOperationException("Тестовый отказ БД.") };
			var progress = new RecordingProgress();
			ComputerProfile online = CoordinatorProfile(CoordinatorOnlineIp);

			ScanRunSummary summary = CreateCoordinator(new FakeConnector(), writer).Run(new[] { online }, true,
				CoordinatorConfiguration("Server=.;Database=Tests"), progress, CancellationToken.None);

			Check.Equal(1, summary.DatabaseFailures);
			Check.Equal("Connecting Connected Scanning Scanned Saving SaveFailed", progress.StagesOf(online));
		}

		// Пустая строка подключения: запись не выполняется и объект записи не создаётся.
		private static void CheckCoordinatorWithoutDatabase()
		{
			var progress = new RecordingProgress();
			ComputerProfile online = CoordinatorProfile(CoordinatorOnlineIp);
			var coordinator = new ScanCoordinator(new CertificateFolderAccess(), new FakeConnector(), null,
				connectionString => { throw new InvalidOperationException("Объект записи не должен создаваться."); }, new MemoryLogger());

			ScanRunSummary summary = coordinator.Run(new[] { online }, true, CoordinatorConfiguration(string.Empty), progress, CancellationToken.None);

			Check.Equal(0, summary.DatabaseFailures);
			Check.Equal("Connecting Connected Scanning Scanned SaveSkippedNoDatabase", progress.StagesOf(online));
		}

		// При scan = false выполняется только подключение.
		private static void CheckCoordinatorConnectOnly()
		{
			var writer = new FakeCertificateWriter();
			var progress = new RecordingProgress();
			ComputerProfile online = CoordinatorProfile(CoordinatorOnlineIp);

			ScanRunSummary summary = CreateCoordinator(new FakeConnector(), writer).Run(new[] { online }, false,
				CoordinatorConfiguration("Server=.;Database=Tests"), progress, CancellationToken.None);

			Check.Equal(1, summary.Connected);
			Check.Equal(0, summary.Completed);
			Check.Equal("Connecting Connected", progress.StagesOf(online));
			Check.Equal(0, writer.Calls.Count);
		}

		// Отмена во время подключения сообщает о ней и прерывает запуск исключением.
		private static void CheckCoordinatorCancellation()
		{
			using (var cancellation = new CancellationTokenSource())
			{
				var connector = new FakeConnector { OnConnect = cancellation.Cancel };
				var progress = new RecordingProgress();
				ComputerProfile online = CoordinatorProfile(CoordinatorOnlineIp);

				Check.Throws<OperationCanceledException>(() => CreateCoordinator(connector, new FakeCertificateWriter()).Run(new[] { online }, true,
					CoordinatorConfiguration("Server=.;Database=Tests"), progress, cancellation.Token));
				Check.Equal("Connecting ConnectionCancelled", progress.StagesOf(online));
			}
		}

		// Создаёт координатор с файловым доступом, который отдаёт один сертификат Иванова.
		private static ScanCoordinator CreateCoordinator(FakeConnector connector, FakeCertificateWriter writer)
		{
			return new ScanCoordinator(new CertificateFolderAccess(), connector, null, connectionString => writer, new MemoryLogger());
		}

		// Профиль удалённого ПК с логином.
		private static ComputerProfile CoordinatorProfile(string ipAddress)
		{
			return new ComputerProfile(Guid.NewGuid(), ipAddress, string.Empty, @"LAB\reader", true);
		}

		// Конфигурация с одной папкой C:\Certs и одним путём к csptest.exe.
		private static ScanConfiguration CoordinatorConfiguration(string connectionString)
		{
			return new ScanConfiguration(new[] { @"C:\Certs" }, new[] { @"C:\CSP\csptest.exe" }, 3, connectionString);
		}

		// Доступ к ПК в памяти: в \\IP\C$\Certs лежит сертификат Иванова, версия КриптоПро 5.0.13003.0.
		private sealed class CertificateFolderAccess : IComputerAccess
		{
			// Сеть не проверяется: подключение моделирует FakeConnector.
			public void CheckConnection(string computerName, int timeoutSeconds, CancellationToken cancellationToken)
			{
			}

			// Возвращает версию для любого пути к csptest.exe.
			public string GetProductVersion(string executablePath)
			{
				return "5.0.13003.0";
			}

			// В папке Certs любого ПК один файл сертификата.
			public IEnumerable<string> GetFiles(string directoryPath)
			{
				if (!directoryPath.EndsWith(@"\C$\Certs", StringComparison.OrdinalIgnoreCase))
				{
					throw new DirectoryNotFoundException(directoryPath);
				}

				return new[] { directoryPath + @"\ivanov.cer" };
			}

			// Подпапок нет.
			public IEnumerable<string> GetDirectories(string directoryPath)
			{
				return Enumerable.Empty<string>();
			}

			// Ссылок нет.
			public bool IsReparsePoint(string path)
			{
				return false;
			}

			// Ссылок нет, назначение не требуется.
			public string GetReparsePointTarget(string path)
			{
				return null;
			}

			// Отдаёт открытый сертификат организаторов.
			public byte[] ReadAllBytes(string filePath)
			{
				return File.ReadAllBytes(Fixture(Ivanov, ".cer"));
			}
		}

		// Подключение без сети: заданные IP падают с указанным исключением.
		private sealed class FakeConnector : IComputerConnector
		{
			internal readonly Dictionary<string, Exception> Failures = new Dictionary<string, Exception>();
			internal Action OnConnect;

			// Вызывает OnConnect, затем бросает исключение, если оно задано для IP, и проверяет отмену.
			public void Connect(ComputerProfile profile, int timeoutSeconds, CancellationToken cancellationToken)
			{
				if (OnConnect != null)
				{
					OnConnect();
				}

				cancellationToken.ThrowIfCancellationRequested();
				Exception failure;
				if (Failures.TryGetValue(profile.IpAddress, out failure))
				{
					throw failure;
				}
			}
		}

		// Запись в БД без MSSQL: запоминает вызовы или бросает заданное исключение.
		private sealed class FakeCertificateWriter : ICertificateWriter
		{
			internal readonly List<SaveCall> Calls = new List<SaveCall>();
			internal Exception Failure;

			// Запоминает переданные данные и возвращает «всё добавлено».
			public CertificateSaveResult Save(IEnumerable<CertificateInfo> certificates, string cryptoProVersion, string computerAddress)
			{
				if (Failure != null)
				{
					throw Failure;
				}

				Calls.Add(new SaveCall { Certificates = certificates.ToList(), CryptoProVersion = cryptoProVersion, ComputerAddress = computerAddress });
				return new CertificateSaveResult();
			}
		}

		// Параметры одного вызова записи.
		private sealed class SaveCall
		{
			internal List<CertificateInfo> Certificates;
			internal string CryptoProVersion;
			internal string ComputerAddress;
		}

		// Синхронно собирает события хода работы, чтобы проверить их порядок.
		private sealed class RecordingProgress : IProgress<ComputerProgress>
		{
			private readonly List<ComputerProgress> updates = new List<ComputerProgress>();

			// Запоминает событие.
			public void Report(ComputerProgress value)
			{
				updates.Add(value);
			}

			// Возвращает этапы одного ПК через пробел, например «Connecting Connected».
			internal string StagesOf(ComputerProfile profile)
			{
				return string.Join(" ", updates.Where(update => update.Profile == profile).Select(update => update.Stage.ToString()));
			}
		}
	}
}
