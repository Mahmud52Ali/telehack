using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Telecom.Core.Computers;
using Telecom.Core.Locations;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private const string LocationComputerIp = "192.0.2.40";
		private const string LocationFile = @"\\192.0.2.40\C$\Certs\active.cer";
		private const string LocationStore = @"Реестр 192.0.2.40\LocalMachine\My\";

		// Проверяет адресное чтение и ошибки без зависимости от MSSQL и доступности удалённых ПК.
		private static void AddCertificateLocationTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("Наличие: сравнение номеров, отсутствие, отказ и повреждение без обхода папок", CheckLocationFiles));
			tests.Add(new KeyValuePair<string, Action>("Наличие: локальный файл проверяется без SMB и учётных данных", CheckLocalLocationFile));
			AddWindowsOnly(tests, "Наличие: хранилище читает только сохранённые ключи один раз", CheckLocationStoreEntries);
			AddWindowsOnly(tests, "Наличие: сбой SMB не мешает хранилищу, неизвестный ПК остаётся непроверенным", CheckLocationConnectionFailures);
			AddWindowsOnly(tests, "Наличие: повторный отказ хранилища заменяет прежний успешный итог", CheckRepeatedLocationStoreFailure);
			tests.Add(new KeyValuePair<string, Action>("Наличие: отмена сохраняет итог прочитанного пути и не читает следующий", CheckLocationCancellation));
			tests.Add(new KeyValuePair<string, Action>("Наличие: чужой UNC, маска и ссылка не дают ложного отсутствия", CheckLocationPathRestrictions));
		}

		// Один физический источник сопоставляется со всеми ожидаемыми номерами без чтения соседних файлов.
		private static void CheckLocationFiles()
		{
			var files = new AddressedCertificateFiles();
			files.Contents.Add(LocationFile, File.ReadAllBytes(Fixture(Ivanov, ".cer")));
			string corrupt = LocationFile + ".corrupt";
			string denied = LocationFile + ".denied";
			files.Contents.Add(corrupt, new byte[] { 1, 2, 3 });
			files.Failures.Add(denied, new UnauthorizedAccessException("Тестовый отказ чтения."));
			var locations = new[]
			{
				LocationCheck("03a14016", LocationFile),
				LocationCheck("378F0E85", LocationFile.ToLowerInvariant()),
				LocationCheck("03A14016", LocationFile + ".missing"),
				LocationCheck("03A14016", corrupt),
				LocationCheck("03A14016", denied)
			};
			var logger = new MemoryLogger();
			int connections = 0;
			var checker = new CertificateLocationChecker(files, null, (profile, token) => connections++, logger);
			DateTime started = DateTime.UtcNow;
			checker.CheckComputer(LocationComputerIp, LocationProfile(), locations, CancellationToken.None);
			Check.Equal(1, connections);
			Check.Equal(2, files.ReadPaths.Count);
			CheckLocationResults(locations, CertificateLocationStatus.Found, CertificateLocationStatus.DifferentCertificate,
				CertificateLocationStatus.Missing, CertificateLocationStatus.Unverified, CertificateLocationStatus.Unverified);
			Check.Equal(locations[0].CheckedAtUtc, locations[1].CheckedAtUtc);
			Check.Equal(true, locations.All(location => location.CheckedAtUtc >= started && location.CheckedAtUtc <= DateTime.UtcNow));
			Check.Equal(true, locations.All(location => location.CheckedAtUtc.Kind == DateTimeKind.Utc));
			Check.Equal(2, logger.Errors.Count);
		}

		// Локальные привязки не требуют ни сетевого подключения, ни выбранной галочки сканирования.
		private static void CheckLocalLocationFile()
		{
			const string path = @"D:\Certs\local.cer";
			var files = new AddressedCertificateFiles();
			files.Contents.Add(path, File.ReadAllBytes(Fixture(Petrov, ".cer")));
			var local = ComputerProfile.CreateLocal(false);
			var location = new CertificateLocationCheck("378F0E85", Environment.MachineName, path, DateTime.Today, DateTime.Today);
			var checker = new CertificateLocationChecker(files, null,
				(profile, token) => { throw new InvalidOperationException("Локальный файл не должен требовать SMB."); }, new MemoryLogger());
			checker.CheckComputer(Environment.MachineName, local, new[] { location }, CancellationToken.None);
			CheckLocationResults(new[] { location }, CertificateLocationStatus.Found);
			Check.Equal(path, files.ReadPaths.Single());
		}

		// Отсутствующий ключ отличается от повреждённого Blob и запрета чтения отдельной записи.
		private static void CheckLocationStoreEntries()
		{
			var locations = new[]
			{
				LocationCheck("03A14016", LocationStore + "active"),
				LocationCheck("378F0E85", LocationStore + "ACTIVE"),
				LocationCheck("03A14016", LocationStore + "missing"),
				LocationCheck("03A14016", LocationStore + "corrupt"),
				LocationCheck("03A14016", LocationStore + "denied")
			};
			var logger = new MemoryLogger();
			var store = new LabMachineCertificateStore(
				(profile, token, read, error) => { throw new InvalidOperationException("Полный обход хранилища запрещён."); },
				(profile, entries, token, read, error) =>
				{
					Check.Equal(true, new HashSet<string>(entries).SetEquals(new[] { "active", "missing", "corrupt", "denied" }));
					read("active", SerializeCertificateFixture(Ivanov));
					read("missing", null);
					read("corrupt", new byte[] { 1, 2, 3 });
					error("denied", new UnauthorizedAccessException("Нет доступа к ключу."));
				});
			var checker = new CertificateLocationChecker(new AddressedCertificateFiles(), store,
				(profile, token) => { throw new InvalidOperationException("Проверка реестра не должна подключать SMB."); }, logger);
			checker.CheckComputer(LocationComputerIp, LocationProfile(), locations, CancellationToken.None);
			CheckLocationResults(locations, CertificateLocationStatus.Found, CertificateLocationStatus.DifferentCertificate,
				CertificateLocationStatus.Missing, CertificateLocationStatus.Unverified, CertificateLocationStatus.Unverified);
			Check.Equal(1, store.Reads);
			Check.Equal(2, logger.Errors.Count);
		}

		// Ошибка одного способа доступа не отменяет независимую проверку другого источника.
		private static void CheckLocationConnectionFailures()
		{
			var locations = new[] { LocationCheck("03A14016", LocationFile), LocationCheck("03A14016", LocationStore + "active") };
			var files = new AddressedCertificateFiles();
			var store = new LabMachineCertificateStore(null,
				(profile, entries, token, read, error) => read("active", SerializeCertificateFixture(Ivanov)));
			var logger = new MemoryLogger();
			var checker = new CertificateLocationChecker(files, store,
				(profile, token) => { throw new TimeoutException("ПК не ответил на SMB-подключение."); }, logger);
			checker.CheckComputer(LocationComputerIp, LocationProfile(), locations, CancellationToken.None);
			CheckLocationResults(locations, CertificateLocationStatus.Unverified, CertificateLocationStatus.Found);
			Check.Equal(0, files.ReadPaths.Count);
			checker.CheckComputer(LocationComputerIp, null, locations, CancellationToken.None);
			CheckLocationResults(locations, CertificateLocationStatus.Unverified, CertificateLocationStatus.Unverified);
			Check.Equal(1, store.Reads);
			Check.Equal(2, logger.Errors.Count);
		}

		// Повторная проверка не должна сохранять «Найден», если хранилище стало недоступно.
		private static void CheckRepeatedLocationStoreFailure()
		{
			bool denied = false;
			var store = new LabMachineCertificateStore(null, (profile, entries, token, read, error) =>
			{
				if (denied)
				{
					throw new UnauthorizedAccessException("Хранилище стало недоступно.");
				}
				read("active", SerializeCertificateFixture(Ivanov));
			});
			var locations = new[] { LocationCheck("03A14016", LocationStore + "active") };
			var checker = new CertificateLocationChecker(new AddressedCertificateFiles(), store, null, new MemoryLogger());
			checker.CheckComputer(LocationComputerIp, LocationProfile(), locations, CancellationToken.None);
			CheckLocationResults(locations, CertificateLocationStatus.Found);
			denied = true;
			checker.CheckComputer(LocationComputerIp, LocationProfile(), locations, CancellationToken.None);
			CheckLocationResults(locations, CertificateLocationStatus.Unverified);
		}

		// Отмена не превращает непрочитанные источники в отсутствующие сертификаты.
		private static void CheckLocationCancellation()
		{
			using (var cancellation = new CancellationTokenSource())
			{
				var files = new AddressedCertificateFiles();
				files.Contents.Add(LocationFile, File.ReadAllBytes(Fixture(Ivanov, ".cer")));
				files.Contents.Add(LocationFile + ".next", File.ReadAllBytes(Fixture(Petrov, ".cer")));
				files.OnRead = cancellation.Cancel;
				var locations = new[] { LocationCheck("03A14016", LocationFile), LocationCheck("378F0E85", LocationFile + ".next") };
				var checker = new CertificateLocationChecker(files, null, (profile, token) => { }, new MemoryLogger());
				Check.Throws<OperationCanceledException>(() => checker.CheckComputer(LocationComputerIp, LocationProfile(), locations, cancellation.Token));
				Check.Equal(CertificateLocationStatus.Found, locations[0].Status);
				Check.Equal(default(DateTime), locations[1].CheckedAtUtc);
				Check.Equal(1, files.ReadPaths.Count);
				Check.Throws<OperationCanceledException>(() => checker.CheckComputer(LocationComputerIp, LocationProfile(), locations, cancellation.Token));
				Check.Equal(1, files.ReadPaths.Count);
			}
		}

		// Некорректная привязка не должна увести проверку к другому ПК или запустить обход маски.
		private static void CheckLocationPathRestrictions()
		{
			var files = new AddressedCertificateFiles();
			files.Contents.Add(LocationFile, File.ReadAllBytes(Fixture(Ivanov, ".cer")));
			files.LinkedPaths.Add(LocationFile);
			var locations = new[]
			{
				LocationCheck("03A14016", @"\\192.0.2.41\C$\Certs\a.cer"),
				LocationCheck("03A14016", @"C:\Users\*\a.cer"),
				LocationCheck("03A14016", LocationFile)
			};
			var logger = new MemoryLogger();
			var checker = new CertificateLocationChecker(files, null, (profile, token) => { }, logger);
			checker.CheckComputer(LocationComputerIp, LocationProfile(), locations, CancellationToken.None);
			CheckLocationResults(locations, CertificateLocationStatus.Unverified, CertificateLocationStatus.Unverified, CertificateLocationStatus.Unverified);
			Check.Equal(0, files.ReadPaths.Count);
			Check.Equal(3, logger.Errors.Count);
		}

		// Профиль удалённого ПК для проверок наличия.
		private static ComputerProfile LocationProfile()
		{
			return new ComputerProfile(Guid.NewGuid(), LocationComputerIp, "Тестовый ПК", "reader", false);
		}

		// Создаёт задание проверки с фиксированной версией строки.
		private static CertificateLocationCheck LocationCheck(string serialNumber, string sourcePath)
		{
			return new CertificateLocationCheck(serialNumber, LocationComputerIp, sourcePath, DateTime.Today, DateTime.Today);
		}

		// Сверяет итоги проверки с ожидаемыми статусами по порядку.
		private static void CheckLocationResults(IList<CertificateLocationCheck> locations, params CertificateLocationStatus[] statuses)
		{
			Check.Equal(statuses.Length, locations.Count);
			for (int index = 0; index < statuses.Length; index++)
			{
				string source = locations[index].SourcePath + ": ";
				Check.Equal(source + statuses[index], source + locations[index].Status);
				Check.Equal(true, locations[index].CheckedAtUtc != default(DateTime));
			}
		}
	}
}
