using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Telecom.App;
using Telecom.Core;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private const string ProfileComputerIp = "192.0.2.10";
		private const string ProfileOfflineIp = "192.0.2.11";
		private const string ProfileIgnoredIp = "192.0.2.12";
		private const string ProfileCommonOnlyIp = "192.0.2.13";
		private const string ProfileWithoutLoginIp = "192.0.2.14";

		// Проверяет постоянные настройки и границу авторизации без обращения к настоящим SMB-серверам.
		private static void AddComputerProfileTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("Список ПК: сохранение дополнительных путей, выбора и учёток", CheckComputerProfileRoundTrip));
			tests.Add(new KeyValuePair<string, Action>("Список ПК: дубли не повреждают прежний файл", CheckDuplicateComputerProfiles));
			tests.Add(new KeyValuePair<string, Action>("Список ПК: повреждённый XML и DTD не принимаются", CheckInvalidComputerProfiles));
			tests.Add(new KeyValuePair<string, Action>("Список ПК: только полный IPv4 без имён и неоднозначных сокращений", CheckProfileIpv4));
			tests.Add(new KeyValuePair<string, Action>("Список ПК: прежние записи сохраняют учётки и требуют IPv4 вместо имени", CheckLegacyComputerProfiles));
			tests.Add(new KeyValuePair<string, Action>("Список ПК: перенос версии 2 сохраняет поля и ссылки на пароли", CheckVersionTwoComputerProfiles));
			tests.Add(new KeyValuePair<string, Action>("Редактор ПК: проверка IPv4 и путей с сохранением учётки", CheckComputerProfileDialog));
			tests.Add(new KeyValuePair<string, Action>("Учётки Windows: запись, обновление и удаление собственного секрета", CheckWindowsCredentialStore));
			tests.Add(new KeyValuePair<string, Action>("SMB: ПК без логина требует настройки перед подключением", CheckSmbWithoutCredentials));
			tests.Add(new KeyValuePair<string, Action>("SMB: после проверки TCP используется сохранённая учётка ПК", CheckExplicitSmbConnection));
			tests.Add(new KeyValuePair<string, Action>("SMB: конфликт учёток и отмена не отключают существующие сеансы", CheckSmbConnectionErrors));
			tests.Add(new KeyValuePair<string, Action>("Интерфейс: сканирование общих и дополнительных путей выбранных ПК после входа", CheckComputerSelectionFlow));
		}

		// Проверяет восстановление выбранного набора без паролей и разовых файлов рядом с настройками.
		private static void CheckComputerProfileRoundTrip()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerProfileStore(Path.Combine(workspace.DirectoryPath, "settings", "computers.xml"));
				Check.Equal(0, store.Load().Count);
				var first = new ComputerProfile(Guid.NewGuid(), ProfileComputerIp, "Бухгалтерия — Иванов & партнёры", @"LAB-PC\reader", true,
					new[] { @" d:/Сертификаты & архив/ ", " ", @"D:\сертификаты & архив", @"C:\Users\*\Certs" });
				var second = new ComputerProfile(Guid.NewGuid(), "192.168.56.101", null, null, false);
				store.Save(new[] { first, second });
				List<ComputerProfile> loaded = store.Load();
				Check.Equal(2, loaded.Count);
				Check.Equal(first.Id, loaded[0].Id);
				Check.Equal(ProfileComputerIp, loaded[0].IpAddress);
				Check.Equal(first.DisplayName, loaded[0].DisplayName);
				Check.Equal(string.Empty, loaded[1].DisplayName);
				Check.Equal(first.UserName, loaded[0].UserName);
				Check.Equal(2, loaded[0].AdditionalDirectories.Count);
				Check.Equal(@"D:\Сертификаты & архив", loaded[0].AdditionalDirectories[0]);
				Check.Equal(@"C:\Users\*\Certs", loaded[0].AdditionalDirectories[1]);
				Check.Equal(0, loaded[1].AdditionalDirectories.Count);
				Check.Equal(true, loaded[0].Selected);
				Check.Equal(false, loaded[1].Selected);
				Check.Equal(false, loaded[1].HasUserName);
				string xml = File.ReadAllText(store.FilePath);
				Check.Equal(true, xml.Contains("<computers version=\"3\">"));
				Check.Equal(false, xml.Contains("password"));
				loaded[0].Selected = false;
				store.Save(loaded);
				ComputerProfile updated = store.Load()[0];
				Check.Equal(false, updated.Selected);
				Check.Equal(@"D:\Сертификаты & архив", updated.AdditionalDirectories[0]);
				Check.Equal(first.Id, updated.Id);
				Check.Equal(1, Directory.GetFiles(Path.GetDirectoryName(store.FilePath)).Length);
			}
		}

		// IPv4 уникальны независимо от подписи; одинаковые подписи у разных адресов допустимы.
		private static void CheckDuplicateComputerProfiles()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerProfileStore(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				var first = new ComputerProfile(Guid.NewGuid(), ProfileComputerIp, "Бухгалтерия", null, true);
				store.Save(new[] { first });
				string previous = File.ReadAllText(store.FilePath);
				var duplicateAddress = new ComputerProfile(Guid.NewGuid(), ProfileComputerIp, "Другой ПК", @"LAB-PC\other", false);
				Check.Throws<InvalidDataException>(() => store.Save(new[] { first, duplicateAddress }));
				var duplicateId = new ComputerProfile(first.Id, ProfileIgnoredIp, "Другой ПК", null, true);
				Check.Throws<InvalidDataException>(() => store.Save(new[] { first, duplicateId }));
				Check.Equal(previous, File.ReadAllText(store.FilePath));
				var sameName = new ComputerProfile(Guid.NewGuid(), ProfileIgnoredIp, first.DisplayName, null, true);
				store.Save(new[] { first, sameName });
				Check.Equal(2, store.Load().Count);
			}
		}

		// Список с ошибкой должен требовать исправления, сохраняя исходный файл для диагностики.
		private static void CheckInvalidComputerProfiles()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerProfileStore(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				File.WriteAllText(store.FilePath, "<computers version=\"99\" />", Encoding.UTF8);
				Check.Throws<InvalidDataException>(() => store.Load());
				string xml = "<!DOCTYPE computers [<!ENTITY external SYSTEM 'file:///unavailable'>]><computers version=\"1\">&external;</computers>";
				File.WriteAllText(store.FilePath, xml, Encoding.UTF8);
				Check.Throws<XmlException>(() => store.Load());
				Check.Equal(xml, File.ReadAllText(store.FilePath));
				File.WriteAllText(store.FilePath, "<computers", Encoding.UTF8);
				Check.Throws<XmlException>(() => store.Load());
				string computer = "<computer id=\"" + Guid.NewGuid() + "\" ipAddress=\"" + ProfileComputerIp + "\" selected=\"true\">";
				File.WriteAllText(store.FilePath, "<computers version=\"3\">" + computer
					+ "<additionalDirectory>C:\\Users\\Ivan*\\Certs</additionalDirectory></computer></computers>");
				Check.Throws<ArgumentException>(() => store.Load());
				File.WriteAllText(store.FilePath, "<computers version=\"3\">" + computer
					+ "<additionalDirectory other=\"true\">C:\\Certs</additionalDirectory></computer></computers>");
				Check.Throws<InvalidDataException>(() => store.Load());
			}
		}

		// Отклоняет адреса, которые могли бы незаметно превратиться в другой ПК или потребовать DNS.
		private static void CheckProfileIpv4()
		{
			foreach (string invalid in new[]
			{
				null, "", "LAB-PC", "localhost", "192.168.56.256", "192.168.56", "127.1", "2130706433",
				"0xC0A83865", "192.168.056.101", "192.168.56.101:445", @"\\192.168.56.101\C$",
				"::1", "::ffff:192.168.56.101", "192.168.56.101.extra"
			})
			{
				Check.Throws<ArgumentException>(() => new ComputerProfile(Guid.NewGuid(), invalid, "Подпись", null, true));
			}

			Check.Equal("192.168.56.101", new ComputerProfile(Guid.NewGuid(), " 192.168.56.101 ", null, null, true).IpAddress);
			Check.Equal("0.0.0.0", ComputerProfile.NormalizeIpAddress("0.0.0.0"));
			Check.Equal("255.255.255.255", ComputerProfile.NormalizeIpAddress("255.255.255.255"));
		}

		// Переносит прежний список без потери идентификаторов паролей и без попытки разрешить имя в сети.
		private static void CheckLegacyComputerProfiles()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerProfileStore(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				Guid addressId = Guid.NewGuid();
				Guid nameId = Guid.NewGuid();
				File.WriteAllText(store.FilePath, "<computers version=\"1\"><computer id=\"" + addressId
					+ "\" address=\"" + ProfileComputerIp + "\" userName=\"LAB-PC\\reader\" selected=\"true\" />"
					+ "<computer id=\"" + nameId + "\" address=\"LAB-PC\" userName=\"LAB-PC\\reader\" selected=\"true\" /></computers>");
				List<ComputerProfile> profiles = store.Load();
				Check.Equal(addressId, profiles[0].Id);
				Check.Equal(ProfileComputerIp, profiles[0].IpAddress);
				Check.Equal(string.Empty, profiles[0].DisplayName);
				Check.Equal(@"LAB-PC\reader", profiles[0].UserName);
				Check.Equal(true, profiles[0].Selected);
				Check.Equal(0, profiles[0].AdditionalDirectories.Count);
				Check.Equal(nameId, profiles[1].Id);
				Check.Equal(string.Empty, profiles[1].IpAddress);
				Check.Equal("LAB-PC", profiles[1].DisplayName);
				Check.Equal(@"LAB-PC\reader", profiles[1].UserName);
				Check.Equal(true, profiles[1].Selected);
				Check.Equal(0, profiles[1].AdditionalDirectories.Count);
				var access = new LabComputerAccess(workspace.DirectoryPath, ProfileComputerIp);
				var connection = new WindowsSmbConnection(new WindowsCredentialStore(), access,
					(path, user, password) => { throw new InvalidOperationException("Попытка входа без IPv4."); });
				Check.Throws<InvalidOperationException>(() => connection.Connect(profiles[1], 3, CancellationToken.None));
				Check.Equal(0, access.ConnectionChecks);
				store.Save(profiles);
				List<ComputerProfile> restored = store.Load();
				Check.Equal(addressId, restored[0].Id);
				Check.Equal(nameId, restored[1].Id);
				Check.Equal("LAB-PC", restored[1].DisplayName);
				Check.Equal(false, restored[1].HasIpAddress);
				Check.Equal(@"LAB-PC\reader", restored[1].UserName);
				Check.Equal(true, restored[1].Selected);
				Check.Equal(0, restored[1].AdditionalDirectories.Count);
			}
		}

		// Проверяет чтение списка версии 2 и повторную запись с прежними атрибутами профиля.
		private static void CheckVersionTwoComputerProfiles()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerProfileStore(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				Guid id = Guid.NewGuid();
				string xml = "<computers version=\"2\"><computer id=\"" + id + "\" ipAddress=\"" + ProfileComputerIp
					+ "\" name=\"Подпись\" userName=\"LAB-PC\\reader\" selected=\"false\" /></computers>";
				File.WriteAllText(store.FilePath, xml.Replace(ProfileComputerIp, "LAB-PC"));
				Check.Throws<ArgumentException>(() => store.Load());
				File.WriteAllText(store.FilePath, xml);
				List<ComputerProfile> profiles = store.Load();
				Check.Equal(0, profiles[0].AdditionalDirectories.Count);
				store.Save(profiles);
				ComputerProfile restored = store.Load()[0];
				Check.Equal(id, restored.Id);
				Check.Equal(ProfileComputerIp, restored.IpAddress);
				Check.Equal("Подпись", restored.DisplayName);
				Check.Equal(@"LAB-PC\reader", restored.UserName);
				Check.Equal(false, restored.Selected);
				Check.Equal(0, restored.AdditionalDirectories.Count);
			}
		}

		// Проверяет, что редактор показывает независимые поля, отклоняет имя и сохраняет пароль при переименовании.
		private static void CheckComputerProfileDialog()
		{
			var previous = new ComputerProfile(Guid.NewGuid(), ProfileComputerIp, "Прежняя подпись", @"LAB-PC\reader", true,
				new[] { @"C:\Certs", @"D:\Profiles\*\Certs" });
			Type dialogType = typeof(MainForm).Assembly.GetType("Telecom.App.ComputerProfileDialog");
			ConstructorInfo constructor = dialogType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];
			MethodInfo save = dialogType.GetMethod("OnSave", BindingFlags.NonPublic | BindingFlags.Instance);
			using (var dialog = (Form)constructor.Invoke(new object[] { null }))
			{
				FormField<TextBox>(dialog, "ipAddressInput").Text = ProfileComputerIp;
				TextBox userInput = FormField<TextBox>(dialog, "userInput");
				TextBox passwordInput = FormField<TextBox>(dialog, "passwordInput");
				Check.Equal(true, userInput.Enabled);
				Check.Equal(true, passwordInput.Enabled);
				save.Invoke(dialog, new object[] { dialog, EventArgs.Empty });
				Check.Equal(DialogResult.None, dialog.DialogResult);
				Check.Equal("Введите учётную запись и пароль.", FormField<Label>(dialog, "errorLabel").Text);
				userInput.Text = @"LAB-PC\reader";
				save.Invoke(dialog, new object[] { dialog, EventArgs.Empty });
				Check.Equal(DialogResult.None, dialog.DialogResult);
				Check.Equal("Введите учётную запись и пароль.", FormField<Label>(dialog, "errorLabel").Text);
				passwordInput.Text = "dummy-password";
				save.Invoke(dialog, new object[] { dialog, EventArgs.Empty });
				Check.Equal(DialogResult.OK, dialog.DialogResult);
			}

			using (var dialog = (Form)constructor.Invoke(new object[] { previous }))
			{
				Check.Equal(ProfileComputerIp, FormField<TextBox>(dialog, "ipAddressInput").Text);
				Check.Equal(previous.DisplayName, FormField<TextBox>(dialog, "nameInput").Text);
				TextBox pathsInput = FormField<TextBox>(dialog, "additionalDirectoriesInput");
				Check.Equal(true, pathsInput.Multiline);
				Check.Equal(string.Join(Environment.NewLine, previous.AdditionalDirectories), pathsInput.Text);
				FormField<TextBox>(dialog, "nameInput").Text = "Бухгалтерия — Иванов";
				FormField<TextBox>(dialog, "ipAddressInput").Text = "OFFICE-PC";
				save.Invoke(dialog, new object[] { dialog, EventArgs.Empty });
				Check.Equal(DialogResult.None, dialog.DialogResult);
				Check.Equal(true, FormField<Label>(dialog, "errorLabel").Text.Length > 0);
				FormField<TextBox>(dialog, "ipAddressInput").Text = ProfileComputerIp;
				pathsInput.Text = @"C:\Users\Ivan*\Certs";
				save.Invoke(dialog, new object[] { dialog, EventArgs.Empty });
				Check.Equal(DialogResult.None, dialog.DialogResult);
				Check.Equal(true, FormField<Label>(dialog, "errorLabel").Text.Contains("Ivan*"));
				pathsInput.Lines = new[] { @" d:/Extra/ ", " ", @"D:\extra", @"D:\Profiles\*\Certs" };
				save.Invoke(dialog, new object[] { dialog, EventArgs.Empty });
				Check.Equal(DialogResult.OK, dialog.DialogResult);
				var saved = (ComputerProfile)dialogType.GetProperty("Profile", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog, null);
				Check.Equal(ProfileComputerIp, saved.IpAddress);
				Check.Equal("Бухгалтерия — Иванов", saved.DisplayName);
				Check.Equal(previous.UserName, saved.UserName);
				Check.Equal(2, saved.AdditionalDirectories.Count);
				Check.Equal(@"D:\Extra", saved.AdditionalDirectories[0]);
				Check.Equal(@"D:\Profiles\*\Certs", saved.AdditionalDirectories[1]);
				Check.Equal(true, (bool)dialogType.GetProperty("KeepPreviousPassword", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog, null));
			}
		}

		// Использует только новый случайный идентификатор и заведомо вымышленные данные; запись удаляется в finally.
		private static void CheckWindowsCredentialStore()
		{
			var store = new WindowsCredentialStore();
			Guid id = Guid.NewGuid();
			try
			{
				store.Save(id, @"LAB-PC\dummy", "тестовый-пароль-" + id.ToString("N"));
				Check.Equal(@"LAB-PC\dummy", store.Read(id).UserName);
				Check.Equal(true, store.Read(id).Password == "тестовый-пароль-" + id.ToString("N"));
				store.Save(id, @"LAB-PC\other", "другой-тестовый-пароль");
				Check.Equal(@"LAB-PC\other", store.Read(id).UserName);
				Check.Equal(true, store.Read(id).Password == "другой-тестовый-пароль");
				store.Delete(id);
				Check.Throws<Win32Exception>(() => store.Read(id));
				store.Delete(id);
			}
			finally
			{
				store.Delete(id);
			}
		}

		// Загружает ранее сохранённый профиль без логина и проверяет отказ до любых сетевых вызовов.
		private static void CheckSmbWithoutCredentials()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerProfileStore(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				var profile = new ComputerProfile(Guid.NewGuid(), ProfileComputerIp, "Прежний ПК", null, true);
				store.Save(new[] { profile });
				var access = new LabComputerAccess(workspace.DirectoryPath, ProfileComputerIp);
				int calls = 0;
				var connection = new WindowsSmbConnection(new WindowsCredentialStore(), access,
					(path, user, password) => { calls++; return 0; });
				Check.Throws<InvalidOperationException>(() => connection.Connect(store.Load()[0], 7, CancellationToken.None));
				Check.Equal(0, access.ConnectionChecks);
				Check.Equal(0, calls);
			}
		}

		// Проверяет порядок подключения, передачу учётки ПК и обработку ошибок сохранённых данных.
		private static void CheckExplicitSmbConnection()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new WindowsCredentialStore();
				Guid id = Guid.NewGuid();
				try
				{
					var profile = new ComputerProfile(id, ProfileComputerIp, "ПК", @"LAB-PC\dummy", true);
					store.Save(id, profile.UserName, "dummy-password");
					var access = new LabComputerAccess(workspace.DirectoryPath, ProfileComputerIp);
					int calls = 0;
					var connection = new WindowsSmbConnection(store, access, (path, user, password) =>
					{
						Check.Equal(1, access.ConnectionChecks);
						Check.Equal(@"\\" + ProfileComputerIp + @"\IPC$", path);
						Check.Equal(@"LAB-PC\dummy", user);
						Check.Equal(true, password == "dummy-password");
						calls++;
						return 0;
					});
					connection.Connect(profile, 7, CancellationToken.None);
					Check.Equal(7, access.RequestedTimeout);
					Check.Equal(1, calls);
					access.ConnectionFailure = new TimeoutException("Нет ответа TCP.");
					Check.Throws<TimeoutException>(() => connection.Connect(profile, 7, CancellationToken.None));
					access.ConnectionFailure = null;
					Check.Throws<InvalidOperationException>(() => connection.Connect(
						new ComputerProfile(id, ProfileComputerIp, "ПК", @"LAB-PC\other", true), 3, CancellationToken.None));
					store.Delete(id);
					Check.Throws<Win32Exception>(() => connection.Connect(profile, 3, CancellationToken.None));
					Check.Equal(1, calls);
				}
				finally
				{
					store.Delete(id);
				}
			}
		}

		// Конфликт 1219 сохраняется как отказ доступа; отменённый запуск не вызывает API авторизации.
		private static void CheckSmbConnectionErrors()
		{
			using (var workspace = new TestWorkspace())
			using (var cancellation = new CancellationTokenSource())
			{
				var credentials = new WindowsCredentialStore();
				var profile = new ComputerProfile(Guid.NewGuid(), ProfileComputerIp, "ПК", @"LAB-PC\dummy", true);
				try
				{
					credentials.Save(profile.Id, profile.UserName, "dummy-password");
					int calls = 0;
					var connection = new WindowsSmbConnection(credentials, new LabComputerAccess(workspace.DirectoryPath, ProfileComputerIp),
						(path, user, password) => { calls++; return 1219; });
					try
					{
						connection.Connect(profile, 3, CancellationToken.None);
						throw new InvalidOperationException("Конфликт SMB не был показан.");
					}
					catch (Win32Exception exception)
					{
						Check.Equal(1219, exception.NativeErrorCode);
						Check.Equal(ScanErrorKind.AccessDenied, ScanFailure.Classify(exception));
					}

					cancellation.Cancel();
					Check.Throws<OperationCanceledException>(() => connection.Connect(profile, 3, cancellation.Token));
					Check.Equal(1, calls);
				}
				finally
				{
					credentials.Delete(profile.Id);
				}
			}
		}

		// Запускает настоящее окно вне видимого экрана с временными настройками и полностью подставной сетью.
		private static void CheckComputerSelectionFlow()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerProfileStore(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				var profiles = new[]
				{
					new ComputerProfile(Guid.NewGuid(), ProfileOfflineIp, "Выключенный ПК", @"OFFLINE-PC\reader", true),
					new ComputerProfile(Guid.NewGuid(), ProfileComputerIp, "Бухгалтерия — Иванов", @"LAB-PC\reader", true,
						new[] { @"c:/commoncerts/", @"D:\Denied", @"D:\Profiles\*\Certs", @"C:\CommonCerts\Nested" }),
					new ComputerProfile(Guid.NewGuid(), ProfileCommonOnlyIp, "Только общие пути", @"SECOND-PC\reader", true),
					new ComputerProfile(Guid.NewGuid(), ProfileIgnoredIp, "Не выбран", null, false),
					new ComputerProfile(Guid.NewGuid(), ProfileWithoutLoginIp, "Без учётной записи", null, true)
				};
				store.Save(profiles);
				string firstRoot = Path.Combine(workspace.DirectoryPath, "first");
				string secondRoot = Path.Combine(workspace.DirectoryPath, "second");
				string firstCommon = Path.Combine(firstRoot, "C$", "CommonCerts");
				string nested = Path.Combine(firstCommon, "Nested");
				string extra = Path.Combine(firstRoot, "D$", "Profiles", "Ivanov", "Certs");
				string secondCommon = Path.Combine(secondRoot, "C$", "CommonCerts");
				Directory.CreateDirectory(nested);
				Directory.CreateDirectory(extra);
				Directory.CreateDirectory(secondCommon);
				File.Copy(Fixture(Ivanov, ".cer"), Path.Combine(firstCommon, "ivanov.cer"));
				File.Copy(Fixture(Petrov, ".cer"), Path.Combine(nested, "petrov.cer"));
				File.Copy(Fixture(Ivanov, ".cer"), Path.Combine(extra, "extra.cer"));
				File.Copy(Fixture(Petrov, ".cer"), Path.Combine(secondCommon, "petrov.cer"));
				string configurationPath = Path.Combine(workspace.DirectoryPath, "scan.xml");
				File.WriteAllText(configurationPath, @"<scanSettings connectionTimeoutSeconds=""3""><directories><directory>C:\CommonCerts</directory></directories>"
					+ @"<cryptoProExecutables><path>C:\Program Files\Crypto Pro\CSP\csptest.exe</path></cryptoProExecutables></scanSettings>");
				var firstAccess = new LabComputerAccess(firstRoot, ProfileComputerIp);
				var secondAccess = new LabComputerAccess(secondRoot, ProfileCommonOnlyIp);
				string deniedDirectory = @"\\" + ProfileComputerIp + @"\D$\Denied";
				firstAccess.DeniedPaths.Add(deniedDirectory);
				var access = new SessionCheckedAccess(new Dictionary<string, LabComputerAccess>
				{
					{ ProfileComputerIp, firstAccess },
					{ ProfileCommonOnlyIp, secondAccess }
				});
				int authorizations = 0;
				var credentials = new WindowsCredentialStore();
				ComputerProfile[] configuredProfiles = profiles.Where(profile => profile.HasUserName).ToArray();
				var connection = new WindowsSmbConnection(credentials, access, (path, user, password) =>
				{
					string computer = path.Split('\\')[2];
					Check.Equal(@"\\" + computer + @"\IPC$", path);
					ComputerProfile profile = configuredProfiles.Single(value => value.IpAddress == computer);
					Check.Equal(profile.UserName, user);
					Check.Equal(true, password == "dummy-password-" + profile.Id.ToString("N"));
					Check.Equal(true, access.AuthenticatedComputers.Add(computer));
					authorizations++;
					return 0;
				});
				var logger = new MemoryLogger();
				try
				{
					foreach (ComputerProfile profile in configuredProfiles)
					{
						credentials.Save(profile.Id, profile.UserName, "dummy-password-" + profile.Id.ToString("N"));
					}

					using (var form = new MainForm(logger, store, access, connection))
					{
						form.ShowInTaskbar = false;
						form.StartPosition = FormStartPosition.Manual;
						form.Location = new Point(-20000, -20000);
						FormField<TextBox>(form, "configurationInput").Text = configurationPath;
						form.Show();
						WaitForFormIdle(form);
						Check.Equal(2, authorizations);
						Check.Equal(0, firstAccess.ReadPaths.Count);
						Check.Equal(0, secondAccess.ReadPaths.Count);

						DataGridView grid = FormField<DataGridView>(form, "computersGrid");
						Check.Equal("Бухгалтерия — Иванов", grid.Rows[1].Cells[1].Value);
						Check.Equal(ProfileComputerIp, grid.Rows[1].Cells[2].Value);
						Check.Equal("Не задана", grid.Rows[4].Cells[3].Value);
						grid.Rows[1].Cells[0].Value = false;
						grid.EndEdit();
						Check.Equal(false, store.Load()[1].Selected);
						grid.Rows[1].Cells[0].Value = true;
						grid.EndEdit();
						Check.Equal(true, store.Load()[1].Selected);

						access.AuthenticatedComputers.Clear();
						logger.Errors.Clear();
						FormField<Button>(form, "scanButton").PerformClick();
						WaitForFormIdle(form);
						Check.Equal(4, authorizations);
						Check.Equal(false, access.AttemptedComputers.Contains(ProfileIgnoredIp));
						Check.Equal(false, access.AttemptedComputers.Contains(ProfileWithoutLoginIp));
						var certificates = (IList<CertificateInfo>)FormField<DataGridView>(form, "certificatesGrid").DataSource;
						Check.Equal(4, certificates.Count);
						var sources = new HashSet<string>(certificates.Select(certificate => certificate.SourcePath), StringComparer.OrdinalIgnoreCase);
						Check.Equal(true, sources.SetEquals(new[]
						{
							@"\\" + ProfileComputerIp + @"\C$\CommonCerts\ivanov.cer",
							@"\\" + ProfileComputerIp + @"\C$\CommonCerts\Nested\petrov.cer",
							@"\\" + ProfileComputerIp + @"\D$\Profiles\Ivanov\Certs\extra.cer",
							@"\\" + ProfileCommonOnlyIp + @"\C$\CommonCerts\petrov.cer"
						}));
						var errors = (IList<ScanError>)FormField<DataGridView>(form, "errorsGrid").DataSource;
						Check.Equal(3, errors.Count);
						Check.Equal(true, errors[1].Path.Contains(ProfileWithoutLoginIp));
						Check.Equal(true, errors[1].Message.Contains("«Изменить»"));
						Check.Equal(ScanErrorKind.AccessDenied, errors[2].Kind);
						Check.Equal(deniedDirectory, errors[2].Path);
						Check.Equal(3, logger.Errors.Count);
						Check.Equal(true, logger.Errors[2] is UnauthorizedAccessException);
						form.Close();
					}
				}
				finally
				{
					foreach (ComputerProfile profile in configuredProfiles)
					{
						credentials.Delete(profile.Id);
					}
				}
			}
		}

		// Проверяет публичное поведение окна, не добавляя в продукт тестовых команд и режимов.
		private static T FormField<T>(Form form, string name)
		{
			return (T)form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
		}

		// Обрабатывает WinForms-продолжения асинхронных операций с ограниченным ожиданием.
		private static void WaitForFormIdle(MainForm form)
		{
			var timer = Stopwatch.StartNew();
			do
			{
				Application.DoEvents();
				if (FormField<CancellationTokenSource>(form, "scanCancellation") == null)
				{
					return;
				}

				Thread.Sleep(1);
			}
			while (timer.Elapsed < TimeSpan.FromSeconds(10));

			throw new TimeoutException("Окно не завершило операцию с тестовыми адаптерами.");
		}

		private sealed class SessionCheckedAccess : IComputerAccess
		{
			private readonly Dictionary<string, LabComputerAccess> computers;
			internal readonly List<string> AttemptedComputers = new List<string>();
			internal readonly HashSet<string> AuthenticatedComputers = new HashSet<string>();

			// Сохраняет файловую часть существующего стенда и добавляет проверку предварительного входа.
			internal SessionCheckedAccess(Dictionary<string, LabComputerAccess> computers)
			{
				this.computers = computers;
			}

			// Моделирует рабочий и недоступный ПК без обращения к DNS и сети.
			public void CheckConnection(string computerName, int timeoutSeconds, CancellationToken cancellationToken)
			{
				cancellationToken.ThrowIfCancellationRequested();
				AttemptedComputers.Add(computerName);
				if (computerName == ProfileOfflineIp)
				{
					throw new TimeoutException("Тестовый компьютер недоступен.");
				}

				computers[computerName].CheckConnection(computerName, timeoutSeconds, cancellationToken);
			}

			// Проверяет отдельный сеанс для каждого ПК перед обращением к его каталогу.
			private LabComputerAccess AuthorizedAccess(string path)
			{
				string computer = path.Split('\\')[2];
				Check.Equal(true, AuthenticatedComputers.Contains(computer));
				return computers[computer];
			}

			// Перечисляет файлы тестового диска после авторизации.
			public IEnumerable<string> GetFiles(string directoryPath)
			{
				return AuthorizedAccess(directoryPath).GetFiles(directoryPath);
			}

			// Делегирует обход локального тестового диска.
			public IEnumerable<string> GetDirectories(string directoryPath)
			{
				return AuthorizedAccess(directoryPath).GetDirectories(directoryPath);
			}

			// Проверяет признак ссылки через исходный адаптер.
			public bool IsReparsePoint(string path)
			{
				return AuthorizedAccess(path).IsReparsePoint(path);
			}

			// Учитывает только действительно прочитанные открытые сертификаты.
			public byte[] ReadAllBytes(string filePath)
			{
				return AuthorizedAccess(filePath).ReadAllBytes(filePath);
			}

			// Моделирует отсутствие КриптоПро после успешной авторизации.
			public string GetProductVersion(string executablePath)
			{
				return AuthorizedAccess(executablePath).GetProductVersion(executablePath);
			}
		}
	}
}
