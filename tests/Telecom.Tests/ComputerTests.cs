using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Telecom.Core;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		// Регистрирует сценарии полного, частичного и неудачного сбора с моделируемого ПК.
		private static void AddComputerTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("ПК: два профиля по маске и версия КриптоПро", CheckComputerCollection));
			tests.Add(new KeyValuePair<string, Action>("ПК: вложенные маски и диск D:", CheckNestedPatterns));
			tests.Add(new KeyValuePair<string, Action>("ПК: перекрывающиеся папки не дублируют источники", CheckOverlappingSources));
			tests.Add(new KeyValuePair<string, Action>("ПК: отказ доступа к профилю даёт частичный результат", CheckPartialComputer));
			tests.Add(new KeyValuePair<string, Action>("ПК: закрытая административная папка отличается от отсутствующего файла", CheckDeniedComputer));
			tests.Add(new KeyValuePair<string, Action>("ПК: сетевой отказ не превращается в пустой успех", CheckUnavailableComputer));
			tests.Add(new KeyValuePair<string, Action>("ПК: отсутствующая папка и пустая маска видны как ошибки", CheckMissingComputerDirectories));
			tests.Add(new KeyValuePair<string, Action>("КриптоПро: отсутствие, запасной путь и отказ доступа", CheckCryptoProOutcomes));
			tests.Add(new KeyValuePair<string, Action>("КриптоПро: ProductVersion нормализуется без потери ненулевой ревизии", CheckProductVersionNormalization));
			tests.Add(new KeyValuePair<string, Action>("ПК: отмена до подключения и после первого сертификата", CheckComputerCancellation));
			tests.Add(new KeyValuePair<string, Action>("Коды Windows классифицируются независимо от языка сообщения", CheckNetworkErrorClassification));
			tests.Add(new KeyValuePair<string, Action>("Неожиданная ошибка реализации не маскируется под сетевой отказ", CheckUnexpectedComputerFailure));
		}

		// Проверяет весь путь от настроек до моделей, включая отсутствие папки у пустого профиля.
		private static void CheckComputerCollection()
		{
			using (var workspace = new TestWorkspace())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "C$", "Users", "Public"));
				var access = CreateLabAccess(workspace.DirectoryPath);
				ComputerScanResult result = ScanLab(access, LabConfiguration(@"C:\Users\*\Certs"));
				Check.Equal(ComputerScanStatus.Complete, result.Status);
				Check.Equal(2, result.Certificates.Count);
				Check.Equal(0, result.Errors.Count);
				Check.Equal(2, result.ScannedDirectories);
				Check.Equal("5.0.13003", result.CryptoProVersion);
				Check.Equal(CryptoProStatus.Found, result.CryptoProStatus);
				Check.Equal(RemotePath.ToUnc("LAB-PC", PrimaryCryptoPath), result.CryptoProSource);
				Check.Equal(1, access.ConnectionChecks);
				Check.Equal(3, access.RequestedTimeout);
				foreach (CertificateInfo certificate in result.Certificates)
				{
					Check.Equal(true, certificate.SourcePath.StartsWith(@"\\LAB-PC\C$\Users\", StringComparison.Ordinal));
				}
			}
		}

		// Проверяет маски с несколькими сегментами и поддержку административных папок других дисков.
		private static void CheckNestedPatterns()
		{
			using (var workspace = new TestWorkspace())
			{
				string directory = Path.Combine(workspace.DirectoryPath, "D$", "Profiles", "Ivanov", "Stores", "Personal", "Certs");
				Directory.CreateDirectory(directory);
				Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "D$", "Profiles", "Public"));
				File.Copy(Fixture(Ivanov, ".cer"), Path.Combine(directory, "ivanov.cer"));
				ComputerScanResult result = ScanLab(CreateLabAccess(workspace.DirectoryPath), LabConfiguration(@"D:\Profiles\*\Stores\*\Certs"));
				Check.Equal(ComputerScanStatus.Complete, result.Status);
				Check.Equal(1, result.Certificates.Count);
				Check.Equal("03A14016", result.Certificates[0].SerialNumber);
				Check.Equal(true, result.Certificates[0].SourcePath.StartsWith(@"\\LAB-PC\D$\", StringComparison.Ordinal));
			}
		}

		// Проверяет, что один и тот же файл не появляется несколько раз при перекрытии настроенных путей.
		private static void CheckOverlappingSources()
		{
			using (var workspace = new TestWorkspace())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				ComputerScanResult result = ScanLab(CreateLabAccess(workspace.DirectoryPath),
					LabConfiguration(@"C:\Users\*\Certs", @"C:\Users\Ivanov\Certs", @"C:\Users\Ivanov"));
				Check.Equal(2, result.Certificates.Count);
				Check.Equal(ComputerScanStatus.Complete, result.Status);
			}
		}

		// Проверяет, что недоступный профиль не мешает собрать данные доступного пользователя.
		private static void CheckPartialComputer()
		{
			using (var workspace = new TestWorkspace())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				var access = CreateLabAccess(workspace.DirectoryPath);
				access.DeniedPaths.Add(@"\\LAB-PC\C$\Users\Petrov");
				ComputerScanResult result = ScanLab(access, LabConfiguration(@"C:\Users\*\Certs"));
				Check.Equal(ComputerScanStatus.Partial, result.Status);
				Check.Equal(1, result.Certificates.Count);
				Check.Equal("03A14016", result.Certificates[0].SerialNumber);
				Check.Equal(1, result.Errors.Count);
				Check.Equal(ScanErrorKind.AccessDenied, result.Errors[0].Kind);
			}
		}

		// Проверяет отказ доступа даже при доступном TCP-порте, без ложного заключения об отсутствии КриптоПро.
		private static void CheckDeniedComputer()
		{
			using (var workspace = new TestWorkspace())
			{
				var access = CreateLabAccess(workspace.DirectoryPath);
				access.DeniedPaths.Add(@"\\LAB-PC\C$");
				ComputerScanResult result = ScanLab(access, LabConfiguration(@"C:\Users\*\Certs"));
				Check.Equal(ComputerScanStatus.AccessDenied, result.Status);
				Check.Equal(CryptoProStatus.Error, result.CryptoProStatus);
				Check.Equal(0, result.Certificates.Count);
				Check.Equal(true, result.Errors.Count > 0);
			}
		}

		// Проверяет таймаут и недоступность сети до любых обращений к файлам.
		private static void CheckUnavailableComputer()
		{
			using (var workspace = new TestWorkspace())
			{
				foreach (Exception failure in new Exception[] { new TimeoutException("timeout"), new SocketException(10061), new IOException("network", unchecked((int)0x80070035)) })
				{
					var access = CreateLabAccess(workspace.DirectoryPath);
					access.ConnectionFailure = failure;
					ComputerScanResult result = ScanLab(access, LabConfiguration(@"C:\Certs"));
					Check.Equal(ComputerScanStatus.Unavailable, result.Status);
					Check.Equal(CryptoProStatus.NotChecked, result.CryptoProStatus);
					Check.Equal(0, access.ReadPaths.Count);
					Check.Equal(1, result.Errors.Count);
				}
			}
		}

		// Проверяет отсутствие совпадений и продолжение после отсутствующего буквального пути.
		private static void CheckMissingComputerDirectories()
		{
			using (var workspace = new TestWorkspace())
			{
				Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "C$", "Users", "Public"));
				ComputerScanResult empty = ScanLab(CreateLabAccess(workspace.DirectoryPath), LabConfiguration(@"C:\Users\*\Certs"));
				Check.Equal(ComputerScanStatus.Failed, empty.Status);
				Check.Equal(1, empty.Errors.Count);
				Check.Equal(ScanErrorKind.NotFound, empty.Errors[0].Kind);
				CreateLabProfiles(workspace.DirectoryPath);
				ComputerScanResult partial = ScanLab(CreateLabAccess(workspace.DirectoryPath), LabConfiguration(@"C:\Missing", @"C:\Users\*\Certs"));
				Check.Equal(ComputerScanStatus.Partial, partial.Status);
				Check.Equal(2, partial.Certificates.Count);
				Check.Equal(1, partial.Errors.Count);
			}
		}

		// Различает отсутствие продукта в заданных местах, запасной путь, испорченную версию и отказ доступа.
		private static void CheckCryptoProOutcomes()
		{
			using (var workspace = new TestWorkspace())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				var access = CreateLabAccess(workspace.DirectoryPath);
				access.ProductVersions.Clear();
				ComputerScanResult absent = ScanLab(access, LabConfiguration(@"C:\Users\*\Certs"));
				Check.Equal(CryptoProStatus.NotFound, absent.CryptoProStatus);
				Check.Equal(ComputerScanStatus.Complete, absent.Status);
				Check.Equal(null, absent.CryptoProVersion);
				string secondary = RemotePath.ToUnc("LAB-PC", SecondaryCryptoPath);
				access.ProductVersions.Add(secondary, "5.0.13003.0");
				Check.Equal("5.0.13003", ScanLab(access, LabConfiguration(@"C:\Users\*\Certs")).CryptoProVersion);
				access.VersionErrors.Add(secondary, new UnauthorizedAccessException("Access denied"));
				ComputerScanResult denied = ScanLab(access, LabConfiguration(@"C:\Users\*\Certs"));
				Check.Equal(CryptoProStatus.Error, denied.CryptoProStatus);
				Check.Equal(ComputerScanStatus.Partial, denied.Status);
				access.VersionErrors.Clear();
				access.ProductVersions[secondary] = string.Empty;
				Check.Equal(CryptoProStatus.Error, ScanLab(access, LabConfiguration(@"C:\Users\*\Certs")).CryptoProStatus);
			}
		}

		// Проверяет версии с тремя и четырьмя компонентами, суффиксом и некорректными значениями.
		private static void CheckProductVersionNormalization()
		{
			Check.Equal("5.0.13003", ComputerScanner.NormalizeProductVersion("5.0.13003.0"));
			Check.Equal("5.0.13003", ComputerScanner.NormalizeProductVersion("5.0.13003"));
			Check.Equal("5.0.13003.12", ComputerScanner.NormalizeProductVersion("5.0.13003.12"));
			Check.Equal("5.0.13003", ComputerScanner.NormalizeProductVersion("5.0.13003.0 (KC1)"));
			Check.Throws<InvalidDataException>(() => ComputerScanner.NormalizeProductVersion(null));
			Check.Throws<InvalidDataException>(() => ComputerScanner.NormalizeProductVersion("not a version"));
		}

		// Проверяет сохранение уже собранных данных и отсутствие сетевых вызовов при предварительной отмене.
		private static void CheckComputerCancellation()
		{
			using (var workspace = new TestWorkspace())
			using (var cancellation = new CancellationTokenSource())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				var access = CreateLabAccess(workspace.DirectoryPath);
				cancellation.Cancel();
				ComputerScanResult result = new ComputerScanner(access, new CertificateParser(), new MemoryLogger()).Scan("LAB-PC", LabConfiguration(@"C:\Users\*\Certs"), cancellation.Token);
				Check.Equal(ComputerScanStatus.Cancelled, result.Status);
				Check.Equal(0, access.ConnectionChecks);
			}

			using (var workspace = new TestWorkspace())
			using (var cancellation = new CancellationTokenSource())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				var access = CreateLabAccess(workspace.DirectoryPath);
				access.OnFileRead = () => cancellation.Cancel();
				ComputerScanResult result = new ComputerScanner(access, new CertificateParser(), new MemoryLogger()).Scan("LAB-PC", LabConfiguration(@"C:\Users\*\Certs"), cancellation.Token);
				Check.Equal(ComputerScanStatus.Cancelled, result.Status);
				Check.Equal(1, result.Certificates.Count);
			}
		}

		// Проверяет реальные числовые коды вместо хрупкого поиска слов в русских или английских сообщениях.
		private static void CheckNetworkErrorClassification()
		{
			Check.Equal(ScanErrorKind.AccessDenied, ScanFailure.Classify(new IOException("anything", unchecked((int)0x8007052E))));
			Check.Equal(ScanErrorKind.AccessDenied, ScanFailure.Classify(new IOException("anything", unchecked((int)0x800704C3))));
			Check.Equal(ScanErrorKind.Unavailable, ScanFailure.Classify(new IOException("anything", unchecked((int)0x80070043))));
			Check.Equal(ScanErrorKind.NotFound, ScanFailure.Classify(new DirectoryNotFoundException("anything")));
			Check.Equal(ScanErrorKind.InvalidData, ScanFailure.Classify(new InvalidDataException("anything")));
		}

		// Проверяет, что ошибка кода поднимается вызывающему обработчику и не объявляется обычным отказом сети.
		private static void CheckUnexpectedComputerFailure()
		{
			using (var workspace = new TestWorkspace())
			{
				var access = CreateLabAccess(workspace.DirectoryPath);
				access.ConnectionFailure = new InvalidOperationException("implementation error");
				Check.Throws<InvalidOperationException>(() => ScanLab(access, LabConfiguration(@"C:\Certs")));
			}
		}

		// Создаёт два тестовых профиля с неизменёнными публичными сертификатами организаторов.
		private static void CreateLabProfiles(string root)
		{
			string ivanov = Path.Combine(root, "C$", "Users", "Ivanov", "Certs");
			string petrov = Path.Combine(root, "C$", "Users", "Petrov", "Certs");
			Directory.CreateDirectory(ivanov);
			Directory.CreateDirectory(petrov);
			File.Copy(Fixture(Ivanov, ".cer"), Path.Combine(ivanov, "ivanov.cer"));
			File.Copy(Fixture(Petrov, ".cer"), Path.Combine(petrov, "petrov.cer"));
		}

		// Подготавливает ожидаемую версию основного исполняемого файла КриптоПро.
		private static LabComputerAccess CreateLabAccess(string root)
		{
			var access = new LabComputerAccess(root);
			access.ProductVersions.Add(RemotePath.ToUnc("LAB-PC", PrimaryCryptoPath), "5.0.13003.0");
			return access;
		}

		// Возвращает настройки с двумя вариантами установки КриптоПро и заданными папками.
		private static ScanConfiguration LabConfiguration(params string[] directories)
		{
			return new ScanConfiguration(directories, new[] { PrimaryCryptoPath, SecondaryCryptoPath }, 3, string.Empty);
		}

		// Запускает настоящий координатор сбора с тестовым адаптером доступа.
		private static ComputerScanResult ScanLab(LabComputerAccess access, ScanConfiguration configuration)
		{
			return new ComputerScanner(access, new CertificateParser(), new MemoryLogger()).Scan("LAB-PC", configuration, CancellationToken.None);
		}
	}
}
