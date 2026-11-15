using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Telecom.Core.Certificates;
using Telecom.Core.Common;
using Telecom.Core.Computers;
using Telecom.Core.Configuration;
using Telecom.Core.Scanning;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		// Регистрирует сценарии полного, частичного и неудачного сбора с моделируемого ПК.
		private static void AddComputerTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("ПК: два профиля по маске и версия КриптоПро", CheckComputerCollection));
			tests.Add(new KeyValuePair<string, Action>("ПК: служебные ссылки распознаются по назначению независимо от языка", CheckProfileCompatibilityLinks));
			tests.Add(new KeyValuePair<string, Action>("ПК: ссылки на профили и недоступные назначения остаются ошибками", CheckUnverifiedProfileLinks));
			tests.Add(new KeyValuePair<string, Action>("ПК: вложенные маски и диск D:", CheckNestedPatterns));
			tests.Add(new KeyValuePair<string, Action>("ПК: перекрывающиеся папки не дублируют источники", CheckOverlappingSources));
			tests.Add(new KeyValuePair<string, Action>("ПК: отказ доступа к профилю даёт частичный результат", CheckPartialComputer));
			tests.Add(new KeyValuePair<string, Action>("ПК: закрытая административная папка отличается от отсутствующего файла", CheckDeniedComputer));
			tests.Add(new KeyValuePair<string, Action>("ПК: сетевой отказ не превращается в пустой успех", CheckUnavailableComputer));
			tests.Add(new KeyValuePair<string, Action>("ПК: пустые личные хранилища отличаются от отказа доступа", CheckEmptyUserCertificateStores));
			tests.Add(new KeyValuePair<string, Action>("ПК: отсутствующая папка и произвольная маска без совпадений видны как ошибки", CheckMissingComputerDirectories));
			tests.Add(new KeyValuePair<string, Action>("КриптоПро: отсутствие, запасной путь и отказ доступа", CheckCryptoProOutcomes));
			tests.Add(new KeyValuePair<string, Action>("КриптоПро: ProductVersion нормализуется без потери ненулевой ревизии", CheckProductVersionNormalization));
			tests.Add(new KeyValuePair<string, Action>("ПК: отмена до подключения и после первого сертификата", CheckComputerCancellation));
			tests.Add(new KeyValuePair<string, Action>("Коды Windows классифицируются независимо от языка сообщения", CheckNetworkErrorClassification));
			tests.Add(new KeyValuePair<string, Action>("Неожиданная ошибка реализации не маскируется под сетевой отказ", CheckUnexpectedComputerFailure));
		}

		// Проверяет сбор из реальных профилей рядом с пустым профилем и штатными ссылками Windows.
		private static void CheckComputerCollection()
		{
			using (var workspace = new TestWorkspace())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "C$", "Users", "Public"));
				var access = CreateLabAccess(workspace.DirectoryPath);
				foreach (string name in new[] { "Default User", "All Users" })
				{
					Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "C$", "Users", name));
					access.LinkedPaths.Add(@"\\LAB-PC\C$\Users\" + name);
					access.LinkTargets.Add(@"\\LAB-PC\C$\Users\" + name,
						name == "Default User" ? @"C:\Users\Default" : @"C:\ProgramData");
				}
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

		// Проверяет локализованные имена и оба формата назначения, возвращаемые при удалённом чтении.
		private static void CheckProfileCompatibilityLinks()
		{
			using (var workspace = new TestWorkspace())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				var access = CreateLabAccess(workspace.DirectoryPath);
				foreach (string name in new[] { "All Users", "Все пользователи", "Общие данные", "Default User", "Пользователь по умолчанию" })
				{
					Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "C$", "Users", name));
					string target = @"c:\programdata\";
					if (name == "Default User" || name == "Пользователь по умолчанию")
					{
						target = @"C:\Users\Default";
					}
					else if (name == "Общие данные")
					{
						target = @"\\LAB-PC\C$\ProgramData\";
					}
					access.LinkTargets.Add(@"\\LAB-PC\C$\Users\" + name, target);
				}
				var logger = new MemoryLogger();
				ComputerScanResult result = new ComputerScanner(access, new CertificateParser(), logger)
					.Scan("LAB-PC", LabConfiguration(@"C:\Users\*\Certs"), CancellationToken.None);
				Check.Equal(ComputerScanStatus.Complete, result.Status);
				Check.Equal(2, result.Certificates.Count);
				Check.Equal(0, result.Errors.Count);
				Check.Equal(0, logger.Errors.Count);
				Check.Equal(5, logger.Messages.FindAll(message => message.StartsWith("Пропущена системная ссылка")).Count);
			}
		}

		// Не скрывает перенаправления профилей даже под служебным именем и сохраняет ошибки чтения назначения.
		private static void CheckUnverifiedProfileLinks()
		{
			foreach (string target in new[] { @"C:\Users\Ivanov", @"D:\ProgramData", @"\\OTHER-PC\C$\ProgramData", @"C:\ProgramData\Personal", null })
			{
				using (var workspace = new TestWorkspace())
				{
					CreateLabProfiles(workspace.DirectoryPath);
					var access = CreateLabAccess(workspace.DirectoryPath);
					Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "C$", "Users", "All Users"));
					access.LinkTargets.Add(@"\\LAB-PC\C$\Users\All Users", target);
					ComputerScanResult result = ScanLab(access, LabConfiguration(@"C:\Users\*\Certs"));
					Check.Equal(ComputerScanStatus.Partial, result.Status);
					Check.Equal(2, result.Certificates.Count);
					Check.Equal(1, result.Errors.Count);
					Check.Equal(@"\\LAB-PC\C$\Users\All Users\Certs", result.Errors[0].Path);
				}
			}

			using (var workspace = new TestWorkspace())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				var access = CreateLabAccess(workspace.DirectoryPath);
				string link = @"\\LAB-PC\C$\Users\Все пользователи";
				Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "C$", "Users", "Все пользователи"));
				access.LinkedPaths.Add(link);
				access.LinkTargetErrors.Add(link, new UnauthorizedAccessException("Метаданные ссылки недоступны."));
				ComputerScanResult result = ScanLab(access, LabConfiguration(@"C:\Users\*\Certs"));
				Check.Equal(ComputerScanStatus.Partial, result.Status);
				Check.Equal(1, result.Errors.Count);
				Check.Equal(ScanErrorKind.AccessDenied, result.Errors[0].Kind);
			}

			using (var workspace = new TestWorkspace())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				var access = CreateLabAccess(workspace.DirectoryPath);
				Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "C$", "Profiles", "All Users"));
				access.LinkTargets.Add(@"\\LAB-PC\C$\Profiles\All Users", @"C:\ProgramData");
				ComputerScanResult result = ScanLab(access, LabConfiguration(@"C:\Users\*\Certs", @"C:\Profiles\*\Certs"));
				Check.Equal(ComputerScanStatus.Partial, result.Status);
				Check.Equal(2, result.Certificates.Count);
				Check.Equal(1, result.Errors.Count);
				Check.Equal(@"\\LAB-PC\C$\Profiles\All Users\Certs", result.Errors[0].Path);
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

		// Проверяет, что недоступный профиль и перенаправление видны в результате частичного сбора.
		private static void CheckPartialComputer()
		{
			using (var workspace = new TestWorkspace())
			{
				CreateLabProfiles(workspace.DirectoryPath);
				var access = CreateLabAccess(workspace.DirectoryPath);
				access.DeniedPaths.Add(@"\\LAB-PC\C$\Users\Petrov");
				Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "C$", "Users", "Redirected"));
				access.LinkedPaths.Add(@"\\LAB-PC\C$\Users\Redirected");
				ComputerScanResult result = ScanLab(access, LabConfiguration(@"C:\Users\*\Certs"));
				Check.Equal(ComputerScanStatus.Partial, result.Status);
				Check.Equal(1, result.Certificates.Count);
				Check.Equal("03A14016", result.Certificates[0].SerialNumber);
				Check.Equal(2, result.Errors.Count);
				Check.Equal(ScanErrorKind.AccessDenied, result.Errors[0].Kind);
				Check.Equal(@"\\LAB-PC\C$\Users\Redirected\Certs", result.Errors[1].Path);
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

		// Проверяет штатную маску на доступном ПК без сертификатов и с закрытым профилем.
		private static void CheckEmptyUserCertificateStores()
		{
			using (var workspace = new TestWorkspace())
			{
				const string computer = "192.0.2.10";
				Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "C$", "Users", "Ivanov", "AppData", "Roaming"));
				var profile = new ComputerProfile(Guid.NewGuid(), computer, string.Empty, "reader", true);
				var access = new LabComputerAccess(workspace.DirectoryPath, computer);
				var scanner = new ComputerScanner(access, new CertificateParser(), new MemoryLogger());
				var store = new LabMachineCertificateStore((target, token, readCertificate, reportError) => Check.Equal(computer, target.IpAddress));
				ScanConfiguration configuration = ScanConfigurationFile.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scan.config.template.xml"));
				ComputerScanResult empty = scanner.ScanProfile(profile, configuration, store, CancellationToken.None);
				Check.Equal(ComputerScanStatus.Complete, empty.Status);
				Check.Equal(0, empty.Certificates.Count);
				Check.Equal(0, empty.Errors.Count);
				Check.Equal(1, store.Reads);

				var unavailableStore = new LabMachineCertificateStore((target, token, readCertificate, reportError) =>
				{
					throw new TimeoutException("Тестовый отказ удалённого реестра.");
				});
				ComputerScanResult unavailable = scanner.ScanProfile(profile, configuration, unavailableStore, CancellationToken.None);
				Check.Equal(ComputerScanStatus.Partial, unavailable.Status);
				Check.Equal(1, unavailable.Errors.Count);
				Check.Equal(ScanErrorKind.Unavailable, unavailable.Errors[0].Kind);

				access.DeniedPaths.Add(@"\\192.0.2.10\C$\Users\Ivanov");
				ComputerScanResult denied = scanner.ScanProfile(profile, configuration, store, CancellationToken.None);
				Check.Equal(ComputerScanStatus.Partial, denied.Status);
				Check.Equal(1, denied.Errors.Count);
				Check.Equal(ScanErrorKind.AccessDenied, denied.Errors[0].Kind);

				ComputerScanResult missingRoot = scanner.ScanProfile(profile, LabConfiguration(@"D:\Profiles\*\AppData\Roaming\Microsoft\SystemCertificates\My\Certificates"),
					store, CancellationToken.None);
				Check.Equal(ComputerScanStatus.Partial, missingRoot.Status);
				Check.Equal(1, missingRoot.Errors.Count);
				Check.Equal(ScanErrorKind.NotFound, missingRoot.Errors[0].Kind);
			}
		}

		// Проверяет произвольную маску без совпадений и продолжение после отсутствующего буквального пути.
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
			Check.Equal(ScanErrorKind.AccessDenied, ErrorClassifier.Classify(new IOException("anything", unchecked((int)0x8007052E))));
			Check.Equal(ScanErrorKind.AccessDenied, ErrorClassifier.Classify(new IOException("anything", unchecked((int)0x800704C3))));
			Check.Equal(ScanErrorKind.Unavailable, ErrorClassifier.Classify(new IOException("anything", unchecked((int)0x80070043))));
			Check.Equal(ScanErrorKind.NotFound, ErrorClassifier.Classify(new DirectoryNotFoundException("anything")));
			Check.Equal(ScanErrorKind.InvalidData, ErrorClassifier.Classify(new InvalidDataException("anything")));
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
