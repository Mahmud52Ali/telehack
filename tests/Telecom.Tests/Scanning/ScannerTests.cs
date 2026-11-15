using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Telecom.App.Windows;
using Telecom.Core.Certificates;
using Telecom.Core.Logging;
using Telecom.Core.Scanning;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		// Регистрирует проверки реального обхода и моделируемых ошибок доступа.
		private static void AddScannerTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			AddTest(tests, "Папка организаторов: два сертификата, P12 не читаются", CheckOrganizerDirectory);
			AddTest(tests, "Повреждённый файл не мешает чтению исправных", CheckBadFileIsolation);
			AddTest(tests, "Пустая папка отличается от отсутствующей", CheckEmptyAndMissingDirectory);
			AddTest(tests, "Отказ доступа в подпапке не останавливает соседние", CheckAccessDeniedIsolation);
			AddTest(tests, "Ссылки пропускаются с явной ошибкой охвата", CheckLinkedDirectory);
			AddTest(tests, "Отмена до запуска и после первого файла", CheckCancellation);
			AddTest(tests, "CRT и файл без расширения читаются сканером", CheckAlternativeFileNames);
			AddTest(tests, "Слишком большой файл пропускается с ошибкой", CheckOversizedFile);
		}

		// Проверяет вложенные папки организаторов и отсутствие попыток чтения контейнеров закрытых ключей.
		private static void CheckOrganizerDirectory()
		{
			var logger = new MemoryLogger();
			var source = new RestrictedFileSystem { RejectPrivateKeyRead = true };
			DirectoryScanResult result = new DirectoryScanner(source, new CertificateParser(), logger).Scan(fixturesDirectory,
				CancellationToken.None);
			Check.Equal(2, result.Certificates.Count);
			Check.Equal(0, result.Errors.Count);
			Check.Equal(3, result.SkippedFiles);
			var serials = new HashSet<string>();
			foreach (CertificateInfo certificate in result.Certificates)
			{
				serials.Add(certificate.SerialNumber);
			}

			Check.Equal(true, serials.SetEquals(new[] { "03A14016", "378F0E85" }));
		}

		// Проверяет сохранение успешных результатов и запись исключения при повреждении одного файла.
		private static void CheckBadFileIsolation()
		{
			using (var workspace = new TestWorkspace())
			{
				CopyCertificates(workspace.DirectoryPath);
				File.WriteAllText(Path.Combine(workspace.DirectoryPath, "bad.cer"), "это не сертификат");
				var logger = new MemoryLogger();
				DirectoryScanResult result = ScanDirectory(workspace.DirectoryPath, logger);
				Check.Equal(2, result.Certificates.Count);
				Check.Equal(1, result.Errors.Count);
				Check.Equal(1, logger.Errors.Count);
				Check.Equal(true, logger.Errors[0].StackTrace.Length > 0);
			}
		}

		// Проверяет, что недоступная папка не представляется как успешный пустой результат.
		private static void CheckEmptyAndMissingDirectory()
		{
			using (var workspace = new TestWorkspace())
			{
				DirectoryScanResult empty = ScanDirectory(workspace.DirectoryPath, new MemoryLogger());
				Check.Equal(0, empty.Certificates.Count);
				Check.Equal(0, empty.Errors.Count);
				var logger = new MemoryLogger();
				DirectoryScanResult missing = ScanDirectory(Path.Combine(workspace.DirectoryPath, "missing"), logger);
				Check.Equal(1, missing.Errors.Count);
				Check.Equal(1, logger.Errors.Count);
			}
		}

		// Имитирует недоступный профиль, оставляя второй профиль доступным.
		private static void CheckAccessDeniedIsolation()
		{
			using (var workspace = new TestWorkspace())
			{
				string denied = Path.Combine(workspace.DirectoryPath, "denied");
				string readable = Path.Combine(workspace.DirectoryPath, "readable");
				Directory.CreateDirectory(denied);
				Directory.CreateDirectory(readable);
				CopyCertificates(readable);
				var source = new RestrictedFileSystem { DeniedDirectory = denied };
				var logger = new MemoryLogger();
				DirectoryScanResult result = new DirectoryScanner(source, new CertificateParser(),
					logger).Scan(workspace.DirectoryPath, CancellationToken.None);
				Check.Equal(2, result.Certificates.Count);
				Check.Equal(1, result.Errors.Count);
				Check.Equal(denied, result.Errors[0].Path);
				Check.Equal(typeof(UnauthorizedAccessException), logger.Errors[0].GetType());
			}
		}

		// Проверяет, что сканер не заходит в каталог-ссылку и сообщает о непроверенной области.
		private static void CheckLinkedDirectory()
		{
			using (var workspace = new TestWorkspace())
			{
				string linked = Path.Combine(workspace.DirectoryPath, "linked");
				Directory.CreateDirectory(linked);
				CopyCertificates(linked);
				var source = new RestrictedFileSystem { LinkedDirectory = linked };
				DirectoryScanResult result = new DirectoryScanner(source, new CertificateParser(),
					new MemoryLogger()).Scan(workspace.DirectoryPath, CancellationToken.None);
				Check.Equal(0, result.Certificates.Count);
				Check.Equal(1, result.Errors.Count);
			}
		}

		// Проверяет остановку обхода с сохранением уже прочитанных сертификатов.
		private static void CheckCancellation()
		{
			using (var cancellation = new CancellationTokenSource())
			using (var workspace = new TestWorkspace())
			{
				CopyCertificates(workspace.DirectoryPath);
				cancellation.Cancel();
				var scanner = new DirectoryScanner(new WindowsFileSystem(), new CertificateParser(), new MemoryLogger());
				DirectoryScanResult result = scanner.Scan(workspace.DirectoryPath, cancellation.Token);
				Check.Equal(true, result.Cancelled);
				Check.Equal(0, result.Certificates.Count);
			}

			using (var cancellation = new CancellationTokenSource())
			using (var workspace = new TestWorkspace())
			{
				CopyCertificates(workspace.DirectoryPath);
				var source = new RestrictedFileSystem { OnFileRead = path => cancellation.Cancel() };
				var scanner = new DirectoryScanner(source, new CertificateParser(), new MemoryLogger());
				DirectoryScanResult result = scanner.Scan(workspace.DirectoryPath, cancellation.Token);
				Check.Equal(true, result.Cancelled);
				Check.Equal(1, result.Certificates.Count);
			}
		}

		// Проверяет отбор имён файлов независимо от формата содержимого.
		private static void CheckAlternativeFileNames()
		{
			using (var workspace = new TestWorkspace())
			{
				File.Copy(Fixture(Ivanov, ".cer"), Path.Combine(workspace.DirectoryPath, "certificate.CRT"));
				File.Copy(Fixture(Petrov, ".cer"), Path.Combine(workspace.DirectoryPath, "0123456789ABCDEF"));
				DirectoryScanResult result = ScanDirectory(workspace.DirectoryPath, new MemoryLogger());
				Check.Equal(2, result.Certificates.Count);
				Check.Equal(0, result.Errors.Count);
			}
		}

		// Проверяет лимит до загрузки большого содержимого в память и продолжение обхода.
		private static void CheckOversizedFile()
		{
			using (var workspace = new TestWorkspace())
			{
				using (var file = File.Create(Path.Combine(workspace.DirectoryPath, "large.cer")))
				{
					file.SetLength(4 * 1024 * 1024 + 1);
				}

				CopyCertificates(workspace.DirectoryPath);
				DirectoryScanResult result = ScanDirectory(workspace.DirectoryPath, new MemoryLogger());
				Check.Equal(2, result.Certificates.Count);
				Check.Equal(1, result.Errors.Count);
			}
		}

		// Запускает настоящий файловый адаптер для локальной проверки.
		private static DirectoryScanResult ScanDirectory(string path, ILogger logger)
		{
			return new DirectoryScanner(new WindowsFileSystem(), new CertificateParser(), logger).Scan(path, CancellationToken.None);
		}

		// Копирует публичные образцы только во временную область конкретной проверки.
		private static void CopyCertificates(string directoryPath)
		{
			File.Copy(Fixture(Ivanov, ".cer"), Path.Combine(directoryPath, "ivanov.cer"));
			File.Copy(Fixture(Petrov, ".cer"), Path.Combine(directoryPath, "petrov.cer"));
		}
	}
}
