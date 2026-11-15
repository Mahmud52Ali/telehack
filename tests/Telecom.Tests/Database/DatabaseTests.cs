using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Telecom.App.Forms;
using Telecom.App.Windows;
using Telecom.Core.Certificates;
using Telecom.Core.Computers;
using Telecom.Core.Configuration;
using Telecom.Core.Database;
using Telecom.Core.Locations;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private const string DatabaseVariable = "TELECOM_TEST_SQL";
		private const string CreatedDatabaseName = "TelecomCreateTests";
		private const string RestrictedLoginName = "telecom_restricted_test";
		private static string databaseConnectionString;

		// Регистрирует проверки MSSQL; без строки подключения они пропускаются, а не считаются успешными.
		private static void AddDatabaseTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			databaseConnectionString = Environment.GetEnvironmentVariable(DatabaseVariable);
			if (string.IsNullOrWhiteSpace(databaseConnectionString))
			{
				Console.WriteLine("SKIP: проверки MSSQL не выполнялись — не задана переменная " + DatabaseVariable);
				return;
			}

			// Проверки удаляют таблицу, поэтому работают только с отдельной тестовой базой.
			string database = new SqlConnectionStringBuilder(databaseConnectionString).InitialCatalog;
			if (!database.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Имя базы для проверок должно оканчиваться на Tests: " + DatabaseVariable);
			}

			tests.Add(new KeyValuePair<string, Action>("БД: отсутствующая таблица создаётся, повторный вызов безопасен", CheckTableCreation));
			tests.Add(new KeyValuePair<string, Action>("БД: первое внесение, время обновления равно времени внесения", CheckFirstSave));
			tests.Add(new KeyValuePair<string, Action>("БД: повторное внесение обновляет строки без дублей", CheckRepeatedSave));
			tests.Add(new KeyValuePair<string, Action>("БД: срок действия и повторный сбор путей без дублей", CheckRepeatedLocations));
			tests.Add(new KeyValuePair<string, Action>("БД: обе таблицы, подсветка срока и удаление находок", CheckDatabaseView));
			tests.Add(new KeyValuePair<string, Action>("БД: отдельная проверка файлов и ключей хранилища по сохранённым путям", CheckLocationVerification));
			tests.Add(new KeyValuePair<string, Action>("БД: устаревшая проверка не перезаписывает новый итог и не восстанавливает удалённую находку", CheckLocationCheckVersions));
			tests.Add(new KeyValuePair<string, Action>("БД: один серийный номер в нескольких папках — одна строка", CheckDuplicateSerialNumber));
			tests.Add(new KeyValuePair<string, Action>("БД: неизвестная версия КриптоПро и текст с кавычками", CheckMissingVersionAndQuotes));
			tests.Add(new KeyValuePair<string, Action>("БД: одновременные первые запуски не спорят за создание таблицы", CheckParallelTableCreation));
			tests.Add(new KeyValuePair<string, Action>("БД: пересекающиеся записи не дают дублей и времени обновления раньше внесения", CheckParallelSave));
			tests.Add(new KeyValuePair<string, Action>("БД: отсутствующая база создаётся вместе с таблицей", CheckDatabaseCreation));
			tests.Add(new KeyValuePair<string, Action>("БД: одновременные первые запуски не спорят за создание базы", CheckParallelDatabaseCreation));
			tests.Add(new KeyValuePair<string, Action>("БД: без прав на создание базы — понятная ошибка", CheckDatabaseCreationDenied));
			tests.Add(new KeyValuePair<string, Action>("БД: недоступный сервер даёт исключение, а не пустой успех", CheckUnavailableDatabase));
		}

		// Проверяет кнопку с настоящей БД, временными файлами и подставными сетевыми адаптерами.
		private static void CheckLocationVerification()
		{
			DropCertificatesTable();
			const string ivanovSerial = "03A14016";
			const string petrovSerial = "378F0E85";
			const string registryKey = "ivanov-key";
			string prefix = @"\\" + ProfileComputerIp + @"\C$\Certs\";
			string active = prefix + "active.cer";
			string missing = prefix + "missing.cer";
			string denied = prefix + "denied.cer";
			string registry = "Реестр " + ProfileComputerIp + @"\LocalMachine\My\" + registryKey;
			string missingRegistry = "Реестр " + ProfileComputerIp + @"\LocalMachine\My\missing-key";
			var logger = new MemoryLogger();
			var repository = new TestDatabase(databaseConnectionString, logger);
			using (var workspace = new TestWorkspace())
			{
				string files = Path.Combine(workspace.DirectoryPath, "C$", "Certs");
				Directory.CreateDirectory(files);
				string activeFile = Path.Combine(files, "active.cer");
				File.Copy(Fixture(Ivanov, ".cer"), activeFile);
				File.Copy(Fixture(Ivanov, ".cer"), Path.Combine(files, "untracked.cer"));
				repository.Save(new[]
				{
					new CertificateInfo(ivanovSerial, Ivanov, Ivanov, active, false, null),
					new CertificateInfo(petrovSerial, Petrov, Petrov, active, false, null),
					new CertificateInfo(ivanovSerial, Ivanov, Ivanov, missing, false, null),
					new CertificateInfo(ivanovSerial, Ivanov, Ivanov, denied, false, null),
					new CertificateInfo(ivanovSerial, Ivanov, Ivanov, registry, false, null),
					new CertificateInfo(ivanovSerial, Ivanov, Ivanov, missingRegistry, false, null)
				}, null, ProfileComputerIp);
				repository.Save(new[] { new CertificateInfo(ivanovSerial, Ivanov, Ivanov, @"C:\Certs\a.cer", false, null) }, null, ProfileOfflineIp);
				repository.Save(new[] { new CertificateInfo(ivanovSerial, Ivanov, Ivanov, @"C:\Certs\a.cer", false, null) }, null, "192.0.2.99");
				DateTime updatedAt = ReadCertificate(ivanovSerial).UpdatedAt;

				var available = new ComputerProfile(Guid.NewGuid(), ProfileComputerIp, "Рабочий ПК", @"LAB-PC\reader", false);
				var offline = new ComputerProfile(Guid.NewGuid(), ProfileOfflineIp, "Недоступный ПК", @"OFFLINE-PC\reader", false);
				var ignored = new ComputerProfile(Guid.NewGuid(), ProfileIgnoredIp, "ПК без находок", null, false);
				var store = new ComputerListFile(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				store.Save(new[] { available, offline, ignored });
				ScanConfigurationFile.Save(new ScanConfiguration(new[] { @"C:\Certs" }, new[] { @"C:\CSP\csptest.exe" }, 3, databaseConnectionString),
					Path.Combine(workspace.DirectoryPath, "scan.config.xml"));

				var remoteFiles = new LabComputerAccess(workspace.DirectoryPath, ProfileComputerIp);
				remoteFiles.DeniedPaths.Add(denied);
				var access = new SessionCheckedAccess(new Dictionary<string, LabComputerAccess> { { ProfileComputerIp, remoteFiles } });
				var credentials = new WindowsCredentialStore();
				var connection = new WindowsSmbConnection(credentials, access, (path, user, password) =>
				{
					Check.Equal(@"\\" + ProfileComputerIp + @"\IPC$", path);
					access.AuthenticatedComputers.Add(ProfileComputerIp);
					return 0;
				});
				var machineStore = new LabMachineCertificateStore(
					(profile, token, read, error) => { throw new InvalidOperationException("Ожидалось адресное чтение."); },
					(profile, entries, token, read, error) =>
					{
						Check.Equal(ProfileComputerIp, profile.IpAddress);
						Check.Equal(true, new HashSet<string>(entries).SetEquals(new[] { registryKey, "missing-key" }));
						read(registryKey, SerializeCertificateFixture(Ivanov));
						read("missing-key", null);
					});
				try
				{
					credentials.Save(available.Id, available.UserName, "test-password");
					using (var form = new MainForm(logger, store, access, connection, machineStore))
					{
						form.ShowInTaskbar = false;
						form.StartPosition = FormStartPosition.Manual;
						form.Location = new Point(-20000, -20000);
						form.Show();
						FormField<RadioButton>(form, "databaseButton").PerformClick();
						FormField<TabControl>(form, "databaseTables").SelectedIndex = 1;
						WaitForDatabaseView(FormField<Button>(form, "refreshDatabaseButton"));
						DataGridView grid = FormField<DataGridView>(form, "certificateLocationsGrid");
						Check.Equal(8, grid.Rows.Count);
						Check.Equal("Не проверялся", grid.Rows[0].Cells[5].FormattedValue);
						Check.Equal("—", grid.Rows[0].Cells[6].FormattedValue);
						Check.Equal(0, access.AttemptedComputers.Count);

						Button check = FormField<Button>(form, "checkLocationsButton");
						RunLocationCheck(form, check.PerformClick);
						Check.Equal(8, grid.Rows.Count);
						Check.Equal(1, remoteFiles.ReadPaths.Count);
						Check.Equal(active, remoteFiles.ReadPaths[0]);
						Check.Equal(2, access.AttemptedComputers.Count);
						Check.Equal(1, machineStore.Reads);
						DataTable results = repository.ReadLocations();
						CheckLocationStatus(results, ivanovSerial, ProfileComputerIp, active, CertificateLocationStatus.Found);
						CheckLocationStatus(results, petrovSerial, ProfileComputerIp, active, CertificateLocationStatus.DifferentCertificate);
						CheckLocationStatus(results, ivanovSerial, ProfileComputerIp, missing, CertificateLocationStatus.Missing);
						CheckLocationStatus(results, ivanovSerial, ProfileComputerIp, denied, CertificateLocationStatus.Unverified);
						CheckLocationStatus(results, ivanovSerial, ProfileComputerIp, registry, CertificateLocationStatus.Found);
						CheckLocationStatus(results, ivanovSerial, ProfileComputerIp, missingRegistry, CertificateLocationStatus.Missing);
						CheckLocationStatus(results, ivanovSerial, ProfileOfflineIp, @"C:\Certs\a.cer", CertificateLocationStatus.Unverified);
						CheckLocationStatus(results, ivanovSerial, "192.0.2.99", @"C:\Certs\a.cer", CertificateLocationStatus.Unverified);
						Check.Equal(updatedAt, ReadCertificate(ivanovSerial).UpdatedAt);
						Check.Equal(2, CountRows());

						File.Copy(Fixture(Petrov, ".cer"), activeFile, true);
						Check.Equal(true, check.Enabled);
						Check.Equal(true, check.Visible);
						RunLocationCheck(form, check.PerformClick);
						Check.Equal(2, remoteFiles.ReadPaths.Count);
						Check.Equal("Проверка завершена. Сохранено результатов: 8 из 8. Записей: 8",
							FormField<Label>(form, "databaseStatusLabel").Text);
						results = repository.ReadLocations();
						CheckLocationStatus(results, ivanovSerial, ProfileComputerIp, active, CertificateLocationStatus.DifferentCertificate);
						CheckLocationStatus(results, petrovSerial, ProfileComputerIp, active, CertificateLocationStatus.Found);

						DateTime unchangedCheck = (DateTime)results.Rows.Cast<DataRow>()
							.Single(row => (string)row["SerialNumber"] == ivanovSerial && (string)row["SourcePath"] == active)["CheckedAtUtc"];
						File.Copy(Fixture(Ivanov, ".cer"), activeFile, true);
						DataGridViewRow selected = grid.Rows.Cast<DataGridViewRow>()
							.Single(row => (string)row.Cells[0].Value == petrovSerial && (string)row.Cells[4].Value == active);
						grid.CurrentCell = selected.Cells[0];
						RunLocationCheck(form, FormField<ToolStripMenuItem>(form, "checkLocationMenuItem").PerformClick);
						Check.Equal(3, remoteFiles.ReadPaths.Count);
						Check.Equal("Проверка завершена. Сохранено результатов: 1 из 1. Записей: 8",
							FormField<Label>(form, "databaseStatusLabel").Text);
						results = repository.ReadLocations();
						CheckLocationStatus(results, petrovSerial, ProfileComputerIp, active, CertificateLocationStatus.DifferentCertificate);
						DataRow unchanged = results.Rows.Cast<DataRow>()
							.Single(row => (string)row["SerialNumber"] == ivanovSerial && (string)row["SourcePath"] == active);
						Check.Equal(unchangedCheck, unchanged["CheckedAtUtc"]);
						Check.Equal((byte)CertificateLocationStatus.DifferentCertificate, unchanged["CheckStatus"]);
						Check.Equal("Заменён другим сертификатом", grid.Rows.Cast<DataGridViewRow>()
							.Single(row => (string)row.Cells[0].Value == petrovSerial && (string)row.Cells[4].Value == active).Cells[5].FormattedValue);

						ExecuteScalar("UPDATE dbo.CertificateLocations SET CheckStatus = NULL, CheckedAtUtc = NULL; SELECT 0;");
						remoteFiles.OnFileRead = () => form.Invoke(new Action(() =>
						{
							Check.Equal("Отменить проверку", check.Text);
							check.PerformClick();
							Check.Equal(false, check.Enabled);
						}));
						RunLocationCheck(form, check.PerformClick);
						Check.Equal("Проверка отменена. Сохранено результатов: 2. Записей: 8",
							FormField<Label>(form, "databaseStatusLabel").Text);
						results = repository.ReadLocations();
						Check.Equal(2, results.Rows.Cast<DataRow>().Count(row => row["CheckedAtUtc"] != DBNull.Value));
						CheckLocationStatus(results, ivanovSerial, ProfileComputerIp, active, CertificateLocationStatus.Found);
						CheckLocationStatus(results, petrovSerial, ProfileComputerIp, active, CertificateLocationStatus.DifferentCertificate);
						Check.Equal(2, machineStore.Reads);
						Check.Equal(true, check.Enabled);
						remoteFiles.OnFileRead = null;
						RunLocationCheck(form, check.PerformClick);
						Check.Equal(8, repository.ReadLocations().Rows.Cast<DataRow>().Count(row => row["CheckedAtUtc"] != DBNull.Value));
						form.Close();
					}
				}
				finally
				{
					credentials.Delete(available.Id);
				}
			}
		}

		// Проверяет защиту результатов от конкурирующего обновления и удаления строк.
		private static void CheckLocationCheckVersions()
		{
			DropCertificatesTable();
			using (var workspace = new TestWorkspace())
			{
				string path = Path.Combine(workspace.DirectoryPath, "ivanov.cer");
				File.Copy(Fixture(Ivanov, ".cer"), path);
				var logger = new MemoryLogger();
				var repository = new TestDatabase(databaseConnectionString, logger);
				var certificate = new CertificateParser().Parse(File.ReadAllBytes(path), path);
				repository.Save(new[] { certificate }, null, Environment.MachineName);
				DataRow row = repository.ReadLocations().Rows[0];
				var location = new CertificateLocationCheck(certificate.SerialNumber, Environment.MachineName, path,
					(DateTime)row["CreatedAt"], (DateTime)row["UpdatedAt"]);
				var checker = new CertificateLocationChecker(new WindowsFileSystem(), null, null, 3, logger);
				checker.CheckComputer(Environment.MachineName, ComputerProfile.CreateLocal(false), new[] { location }, CancellationToken.None);
				Check.Equal(1, repository.SaveLocationChecks(new[] { location }));
				Check.Equal(row["CreatedAt"], repository.ReadAll().Rows[0]["CreatedAt"]);
				Check.Equal(row["UpdatedAt"], repository.ReadAll().Rows[0]["UpdatedAt"]);

				ExecuteScalar("UPDATE dbo.CertificateLocations SET CheckStatus = 2, CheckedAtUtc = DATEADD(day, 1, CheckedAtUtc); SELECT 0;");
				Check.Equal(0, repository.SaveLocationChecks(new[] { location }));
				Check.Equal((byte)CertificateLocationStatus.Missing, repository.ReadLocations().Rows[0]["CheckStatus"]);
				ExecuteScalar("UPDATE dbo.CertificateLocations SET CheckedAtUtc = NULL; UPDATE dbo.Certificates SET UpdatedAt = DATEADD(second, 1, UpdatedAt); SELECT 0;");
				Check.Equal(0, repository.SaveLocationChecks(new[] { location }));
				Check.Equal((byte)CertificateLocationStatus.Missing, repository.ReadLocations().Rows[0]["CheckStatus"]);
				DataRow updated = repository.ReadLocations().Rows[0];
				Check.Equal(true, repository.TryDeleteLocation(certificate.SerialNumber, (DateTime)updated["CreatedAt"],
					(DateTime)updated["UpdatedAt"], Environment.MachineName, path, 0));
				Check.Equal(0, repository.SaveLocationChecks(new[] { location }));
				Check.Equal(0, repository.ReadLocations().Rows.Count);
				Check.Equal(0, CountRows());
			}
		}

		// Ручная обработка сообщений вне Application.Run сбрасывает контекст; восстанавливаем его перед событием.
		private static void RunLocationCheck(MainForm form, Action start)
		{
			SynchronizationContext previous = SynchronizationContext.Current;
			SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
			try
			{
				start();
				WaitForFormIdle(form);
			}
			finally
			{
				SynchronizationContext.SetSynchronizationContext(previous);
			}
		}

		// Проверяет сохранённый итог проверки наличия для одного места обнаружения.
		private static void CheckLocationStatus(DataTable rows, string serialNumber, string computerAddress,
			string sourcePath, CertificateLocationStatus expected)
		{
			DataRow match = rows.Rows.Cast<DataRow>().Single(row => (string)row["SerialNumber"] == serialNumber
				&& (string)row["ComputerAddress"] == computerAddress && (string)row["SourcePath"] == sourcePath);
			string prefix = serialNumber + " " + computerAddress + " " + sourcePath + " ";
			Check.Equal(prefix + (byte)expected, prefix + match["CheckStatus"]);
			Check.Equal(true, match["CheckedAtUtc"] is DateTime);
		}

		// Проверяет создание таблицы на пустой базе и отсутствие ошибки при втором вызове.
		private static void CheckTableCreation()
		{
			DropCertificatesTable();
			var repository = new TestDatabase(databaseConnectionString, new MemoryLogger());
			repository.EnsureTableExists();
			Check.Equal(0, CountRows());
			repository.EnsureTableExists();
			Check.Equal(0, CountRows());
		}

		// Проверяет, что запись сама создаёт таблицу и заполняет все поля из условий кейса.
		private static void CheckFirstSave()
		{
			DropCertificatesTable();
			var repository = new TestDatabase(databaseConnectionString, new MemoryLogger());
			CertificateSaveResult result = repository.Save(CreateCertificates("Иванов Иван Иванович"), "5.0.13003");
			Check.Equal(2, result.Inserted);
			Check.Equal(0, result.Updated);
			Check.Equal(2, CountRows());

			StoredCertificate stored = ReadCertificate("03A14016");
			Check.Equal("Иванов Иван Иванович", stored.OwnerName);
			Check.Equal("Тестовый УЦ", stored.CertificationAuthority);
			Check.Equal("5.0.13003", stored.CryptoProVersion);
			Check.Equal(true, stored.IsCryptoProLicenseEmbedded);
			Check.Equal(stored.CreatedAt, stored.UpdatedAt);
			Check.Equal(false, ReadCertificate("378F0E85").IsCryptoProLicenseEmbedded);
		}

		// Проверяет главное требование кейса: повторная запись не добавляет вторую строку.
		private static void CheckRepeatedSave()
		{
			DropCertificatesTable();
			var repository = new TestDatabase(databaseConnectionString, new MemoryLogger());
			repository.Save(CreateCertificates("Иванов Иван Иванович"), "5.0.13003");
			StoredCertificate first = ReadCertificate("03A14016");

			Thread.Sleep(50);
			CertificateSaveResult result = repository.Save(CreateCertificates("Иванов И. И."), "5.0.13455");
			Check.Equal(0, result.Inserted);
			Check.Equal(2, result.Updated);
			Check.Equal(2, CountRows());

			StoredCertificate second = ReadCertificate("03A14016");
			Check.Equal(first.CreatedAt, second.CreatedAt);
			Check.Equal(true, second.UpdatedAt > first.UpdatedAt);
			Check.Equal("Иванов И. И.", second.OwnerName);
			Check.Equal("5.0.13455", second.CryptoProVersion);
		}

		// Повторный сбор тех же находок не добавляет строк, а длинный путь и срок действия сохраняются.
		private static void CheckRepeatedLocations()
		{
			DropCertificatesTable();
			var repository = new TestDatabase(databaseConnectionString, new MemoryLogger());
			Check.Equal(0, repository.ReadLocations().Rows.Count);

			const string address = "192.0.2.10";
			var expiresAt = new DateTime(2027, 10, 26, 10, 21, 31, DateTimeKind.Utc);
			string longPath = @"C:\Certs\" + new string('x', 300) + @"\" + new string('y', 260) + @"\second.cer";
			var findings = new[]
			{
				new CertificateInfo("03A14016", "Иванов", "УЦ", @"C:\Certs\a.cer", false, expiresAt),
				new CertificateInfo("03a14016", "Иванов", "УЦ", longPath, false, expiresAt)
			};
			CertificateSaveResult first = repository.Save(findings, null, address);
			Check.Equal(1, first.Inserted);
			DataRow firstRecord = repository.ReadAll().Rows[0];
			CertificateSaveResult repeated = repository.Save(findings, null, address);
			Check.Equal(1, repeated.Updated);
			Check.Equal(0, repeated.Inserted);
			Check.Equal(1, CountRows());
			DataRow currentRecord = repository.ReadAll().Rows[0];
			Check.Equal(firstRecord["CreatedAt"], currentRecord["CreatedAt"]);
			Check.Equal(expiresAt, currentRecord["ExpiresAtUtc"]);
			DataTable locations = repository.ReadLocations();
			Check.Equal(2, locations.Rows.Count);
			Check.Equal(true, locations.Rows.Cast<DataRow>().Any(row => (string)row["SourcePath"] == longPath));
			foreach (DataRow location in locations.Rows)
			{
				Check.Equal("03A14016", location["SerialNumber"]);
				Check.Equal(address, location["ComputerAddress"]);
				Check.Equal(expiresAt, location["ExpiresAtUtc"]);
			}
		}

		// Проверяет обновление, цвета дат и удаление из обеих таблиц через окно.
		private static void CheckDatabaseView()
		{
			DropCertificatesTable();
			var logger = new MemoryLogger();
			var repository = new TestDatabase(databaseConnectionString, logger);
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerListFile(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				string configurationPath = Path.Combine(workspace.DirectoryPath, "scan.config.xml");
				var directories = new[] { workspace.DirectoryPath };
				var executables = new[] { Path.Combine(workspace.DirectoryPath, "csptest.exe") };
				ScanConfigurationFile.Save(new ScanConfiguration(directories, executables, 3, string.Empty), configurationPath);
				using (var form = new MainForm(logger, store, new WindowsFileSystem(), null))
				{
					form.ShowInTaskbar = false;
					form.StartPosition = FormStartPosition.Manual;
					form.Location = new Point(-20000, -20000);
					form.Show();
					RadioButton computers = FormField<RadioButton>(form, "computersButton");
					RadioButton database = FormField<RadioButton>(form, "databaseButton");
					Button refresh = FormField<Button>(form, "refreshDatabaseButton");
					DataGridView grid = FormField<DataGridView>(form, "databaseGrid");
					TabControl tables = FormField<TabControl>(form, "databaseTables");
					DataGridView locationsGrid = FormField<DataGridView>(form, "certificateLocationsGrid");
					Label status = FormField<Label>(form, "databaseStatusLabel");
					Check.Equal(true, computers.Checked);
					Check.Equal(true, grid.ReadOnly);
					Check.Equal(false, grid.AllowUserToAddRows);
					Check.Equal(false, grid.AllowUserToDeleteRows);
					database.PerformClick();
					WaitForDatabaseView(refresh);
					Check.Equal(true, status.Text.Contains("Укажите и сохраните строку подключения"));
					Check.Equal(0, logger.Errors.Count);

					ScanConfigurationFile.Save(new ScanConfiguration(directories, executables, 3, databaseConnectionString), configurationPath);
					refresh.PerformClick();
					WaitForDatabaseView(refresh);
					Check.Equal(0, grid.Rows.Count);
					Check.Equal(1, logger.Errors.Count);
					Check.Equal(true, status.Text.Contains("dbo.Certificates"));
					Check.Equal(DBNull.Value, ExecuteScalar("SELECT OBJECT_ID(N'dbo.Certificates', N'U');"));

					computers.PerformClick();
					DateTime soon = DateTime.UtcNow.Date.AddDays(15).AddHours(12);
					DateTime expired = DateTime.UtcNow.Date.AddDays(-1);
					repository.Save(CreateCertificates("Иванов Иван Иванович", soon, expired), "5.0.13003", "192.0.2.10");
					database.PerformClick();
					WaitForDatabaseView(refresh);
					Check.Equal(2, grid.Rows.Count);
					Check.Equal(9, grid.Columns.Count);
					Check.Equal(true, grid.Columns[8] is DataGridViewButtonColumn);
					DataGridViewRow certificate = FindDatabaseRow(grid, "03A14016");
					Check.Equal(certificate.Cells[1].Value, certificate.Cells[2].Value);
					Check.Equal("Иванов Иван Иванович", certificate.Cells[3].Value);
					Check.Equal("Тестовый УЦ", certificate.Cells[4].Value);
					Check.Equal("5.0.13003", certificate.Cells[5].Value);
					Check.Equal(true, certificate.Cells[6].Value);
					Check.Equal(null, form.AcceptButton);
					CheckExpiryCell(grid, certificate.Cells[7], soon, Color.PeachPuff);
					CheckExpiryCell(grid, FindDatabaseRow(grid, "378F0E85").Cells[7], expired, Color.MistyRose);

					repository.Save(CreateCertificates("Иванов Иван Иванович", soon, expired), "5.0.13003", "192.0.2.11");
					tables.SelectedIndex = 1;
					WaitForDatabaseView(refresh);
					Check.Equal(9, locationsGrid.Columns.Count);
					Check.Equal(4, locationsGrid.Rows.Count);
					DataGridViewRow location = locationsGrid.Rows.Cast<DataGridViewRow>()
						.Single(row => (string)row.Cells[0].Value == "03A14016" && (string)row.Cells[3].Value == "192.0.2.11");
					Check.Equal("Иванов Иван Иванович", location.Cells[1].Value);
					Check.Equal("Тестовый УЦ", location.Cells[2].Value);
					Check.Equal(@"C:\Certs\ivanov.cer", location.Cells[4].Value);
					CheckExpiryCell(locationsGrid, location.Cells[7], soon, Color.PeachPuff);
					CheckExpiryCell(locationsGrid, locationsGrid.Rows.Cast<DataGridViewRow>()
						.First(row => (string)row.Cells[0].Value == "378F0E85").Cells[7], expired, Color.MistyRose);
					tables.SelectedIndex = 0;
					WaitForDatabaseView(refresh);

					repository.Save(CreateCertificates("Иванов И. И."), null);
					refresh.PerformClick();
					WaitForDatabaseView(refresh);
					Check.Equal(2, grid.Rows.Count);
					certificate = FindDatabaseRow(grid, "03A14016");
					Check.Equal("Иванов И. И.", certificate.Cells[3].Value);
					Check.Equal("—", certificate.Cells[5].FormattedValue);
					Check.Equal(soon, certificate.Cells[7].Value);
					Check.Equal(1, logger.Errors.Count);

					ClickDeleteCertificate(grid, certificate.Index, false);
					Check.Equal(2, CountRows());
					ExecuteScalar("UPDATE dbo.Certificates SET UpdatedAt = DATEADD(second, 1, UpdatedAt); SELECT 0;");
					ClickDeleteCertificate(grid, certificate.Index, true);
					WaitForDatabaseView(refresh);
					Check.Equal(2, CountRows());
					Check.Equal("Запись уже удалена или обновлена с момента загрузки. Записей: 2", status.Text);

					certificate = FindDatabaseRow(grid, "03A14016");
					ClickDeleteCertificate(grid, certificate.Index, true);
					WaitForDatabaseView(refresh);
					Check.Equal(1, CountRows());
					Check.Equal(1, grid.Rows.Count);
					Check.Equal("378F0E85", grid.Rows[0].Cells[0].Value);
					Check.Equal("Запись удалена. Записей: 1", status.Text);
					Check.Equal("Запись удалена. Серийный номер: 03A14016. Оператор: " + Environment.UserDomainName + "\\" + Environment.UserName + ".",
						logger.Messages.Last());

					tables.SelectedIndex = 1;
					WaitForDatabaseView(refresh);
					Check.Equal(2, locationsGrid.Rows.Count);
					Check.Equal(true, locationsGrid.Rows.Cast<DataGridViewRow>().All(row => (string)row.Cells[0].Value == "378F0E85"));
					var otherComputer = (DataRowView)locationsGrid.Rows[1].DataBoundItem;
					Check.Equal(false, repository.TryDeleteLocation("378F0E85", (DateTime)otherComputer["CreatedAt"],
						(DateTime)otherComputer["UpdatedAt"], "192.0.2.11", @"C:\Certs\petrov.cer", 0));
					Check.Equal(2, repository.ReadLocations().Rows.Count);
					ClickDeleteCertificate(locationsGrid, 1, true,
						"Сертификат останется в реестре.\r\nКомпьютер 192.0.2.11, привязанный к сертификату с серийным номером 378F0E85.");
					WaitForDatabaseView(refresh);
					Check.Equal(1, CountRows());
					Check.Equal(1, locationsGrid.Rows.Count);
					Check.Equal("192.0.2.10", locationsGrid.Rows[0].Cells[3].Value);
					Check.Equal(true, status.Text.Contains("Сертификат остаётся в реестре."));
					Check.Equal(true, logger.Messages.Last().Contains("192.0.2.11"));

					repository.Save(new[] { new CertificateInfo("378F0E85", "Петров Петр Петрович", "Тестовый УЦ", @"D:\Backup\petrov.cer", false, null) },
						null, "192.0.2.10");
					refresh.PerformClick();
					WaitForDatabaseView(refresh);
					Check.Equal(2, locationsGrid.Rows.Count);
					ClickDeleteCertificate(locationsGrid, 1, true, "Оставшиеся компьютеры\r\n192.0.2.10");
					WaitForDatabaseView(refresh);
					Check.Equal(1, CountRows());
					Check.Equal(1, locationsGrid.Rows.Count);
					Check.Equal(@"C:\Certs\petrov.cer", locationsGrid.Rows[0].Cells[4].Value);

					ClickDeleteCertificate(locationsGrid, 0, true,
						"Это последний компьютер, привязанный к сертификату под серийным номером 378F0E85. "
						+ "При удалении исчезнет запись как о компьютере, так и о сертификате в обеих таблицах.");
					WaitForDatabaseView(refresh);
					Check.Equal(0, locationsGrid.Rows.Count);
					Check.Equal(0, CountRows());
					Check.Equal(true, status.Text.Contains("Записи о компьютере и сертификате удалены из базы данных."));
					tables.SelectedIndex = 0;
					WaitForDatabaseView(refresh);
					Check.Equal(0, grid.Rows.Count);
					form.Close();
				}
			}
		}

		// Считывает цвет и отображаемое местное время через штатное форматирование грида.
		private static void CheckExpiryCell(DataGridView grid, DataGridViewCell cell, DateTime expiresAtUtc, Color expectedColor)
		{
			Color actualColor = Color.Empty;
			DataGridViewCellFormattingEventHandler readColor = (sender, args) =>
			{
				if (args.RowIndex == cell.RowIndex && args.ColumnIndex == cell.ColumnIndex)
				{
					actualColor = args.CellStyle.BackColor;
				}
			};
			grid.CellFormatting += readColor;
			try
			{
				Check.Equal(expiresAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
					cell.FormattedValue);
				Check.Equal(expectedColor, actualColor);
			}
			finally
			{
				grid.CellFormatting -= readColor;
			}
		}

		// Запускает действие ячейки и вводит номер в открывшемся окне подтверждения.
		private static void ClickDeleteCertificate(DataGridView grid, int rowIndex, bool confirm, string expectedDetails = null)
		{
			int columnIndex = grid.Columns.Count - 1;
			string serialNumber = (string)grid.Rows[rowIndex].Cells[0].Value;
			grid.CurrentCell = grid.Rows[rowIndex].Cells[columnIndex];
			grid.Focus();
			bool dialogShown = false;
			bool invalidNumberRejected = false;
			bool validNumberAccepted = false;
			string details = null;
			using (var timer = new System.Windows.Forms.Timer { Interval = 15 })
			{
				timer.Tick += (sender, args) =>
				{
					Form dialog = Application.OpenForms.Cast<Form>().SingleOrDefault(window => window.GetType().Name == "DeleteCertificateDialog");
					if (dialog == null)
					{
						return;
					}

					timer.Stop();
					dialogShown = true;
					details = FormField<TextBox>(dialog, "deletionDetails").Text;
					TextBox input = FormField<TextBox>(dialog, "serialNumberInput");
					input.Text = serialNumber.Substring(0, serialNumber.Length - 1);
					invalidNumberRejected = !((Button)dialog.AcceptButton).Enabled;
					input.Text = serialNumber.ToLowerInvariant();
					validNumberAccepted = ((Button)dialog.AcceptButton).Enabled;
					if (confirm && validNumberAccepted)
					{
						dialog.AcceptButton.PerformClick();
					}
					else
					{
						dialog.CancelButton.PerformClick();
					}
				};
				timer.Start();
				typeof(DataGridView).GetMethod("OnCellContentClick", BindingFlags.Instance | BindingFlags.NonPublic)
					.Invoke(grid, new object[] { new DataGridViewCellEventArgs(columnIndex, rowIndex) });
			}

			Check.Equal(true, dialogShown);
			Check.Equal(true, invalidNumberRejected);
			Check.Equal(true, validNumberAccepted);
			if (expectedDetails != null)
			{
				Check.Equal(true, details.Contains(expectedDetails));
			}
		}

		// Находит сертификат по ключу независимо от времени его обновления.
		private static DataGridViewRow FindDatabaseRow(DataGridView grid, string serialNumber)
		{
			return grid.Rows.Cast<DataGridViewRow>().Single(row => (string)row.Cells[0].Value == serialNumber);
		}

		// Обрабатывает продолжение запроса в потоке окна до повторной доступности кнопки.
		private static void WaitForDatabaseView(Button refresh)
		{
			var timeout = Stopwatch.StartNew();
			while (!refresh.Enabled && timeout.Elapsed < TimeSpan.FromSeconds(10))
			{
				Application.DoEvents();
				Thread.Sleep(1);
			}

			if (!refresh.Enabled)
			{
				throw new TimeoutException("Вкладка не завершила чтение из тестовой базы.");
			}
		}

		// Проверяет, что копии одного сертификата из разных папок не считаются разными записями.
		private static void CheckDuplicateSerialNumber()
		{
			DropCertificatesTable();
			var certificates = new List<CertificateInfo>();
			certificates.Add(new CertificateInfo("03A14016", "Иванов", "УЦ", @"C:\Certs\a.cer", false, null));
			certificates.Add(new CertificateInfo("03a14016", "Иванов", "УЦ", @"D:\Certs\a.cer", false, null));
			var repository = new TestDatabase(databaseConnectionString, new MemoryLogger());
			CertificateSaveResult result = repository.Save(certificates, null);
			Check.Equal(1, result.Inserted);
			Check.Equal(0, result.Updated);
			Check.Equal(1, CountRows());
		}

		// Проверяет запись NULL вместо версии и сохранение текста с кавычками без искажения запроса.
		private static void CheckMissingVersionAndQuotes()
		{
			DropCertificatesTable();
			string ownerName = "О'Коннор'); DROP TABLE dbo.Certificates; --";
			var certificates = new List<CertificateInfo>();
			certificates.Add(new CertificateInfo("0A", ownerName, "УЦ \"Тест\"", @"C:\Certs\a.cer", false, null));
			var repository = new TestDatabase(databaseConnectionString, new MemoryLogger());
			repository.Save(certificates, null);

			StoredCertificate stored = ReadCertificate("0A");
			Check.Equal(ownerName, stored.OwnerName);
			Check.Equal("УЦ \"Тест\"", stored.CertificationAuthority);
			Check.Equal(null, stored.CryptoProVersion);
		}

		// Проверяет гонку первого обращения: пока таблицы нет, её одновременно создают несколько запусков.
		private static void CheckParallelTableCreation()
		{
			for (int attempt = 0; attempt < 5; attempt++)
			{
				DropCertificatesTable();
				Parallel.For(0, 8, index =>
				{
					var repository = new TestDatabase(databaseConnectionString, new MemoryLogger());
					if (index % 2 == 0)
					{
						repository.EnsureTableExists();
					}
					else
					{
						repository.Save(CreateCertificates("Иванов Иван Иванович"), "5.0.13003");
					}
				});
				Check.Equal(2, CountRows());
			}
		}

		// Проверяет, что при пересекающихся записях строк не становится больше, а время обновления не раньше внесения.
		private static void CheckParallelSave()
		{
			DropCertificatesTable();
			Parallel.For(0, 40, index =>
			{
				var repository = new TestDatabase(databaseConnectionString, new MemoryLogger());
				repository.Save(CreateCertificates("Иванов " + index), "5.0.13003");
			});
			Check.Equal(2, CountRows());
			Check.Equal(0, (int)ExecuteScalar("SELECT COUNT(*) FROM dbo.Certificates WHERE UpdatedAt < CreatedAt;"));
		}

		// Проверяет запись на сервере без базы: она создаётся один раз, повторный запуск её не трогает.
		private static void CheckDatabaseCreation()
		{
			DropCreatedDatabase();
			try
			{
				var logger = new MemoryLogger();
				var repository = new TestDatabase(CreatedDatabaseConnectionString(), logger);
				CertificateSaveResult first = repository.Save(CreateCertificates("Иванов Иван Иванович"), "5.0.13003");
				Check.Equal(2, first.Inserted);
				Check.Equal(true, logger.Messages.Contains("Создана база данных: " + CreatedDatabaseName));

				logger.Messages.Clear();
				CertificateSaveResult second = repository.Save(CreateCertificates("Иванов Иван Иванович"), "5.0.13003");
				Check.Equal(2, second.Updated);
				Check.Equal(false, logger.Messages.Contains("Создана база данных: " + CreatedDatabaseName));
				Check.Equal(2, (int)ExecuteScalar(CreatedDatabaseConnectionString(), "SELECT COUNT(*) FROM dbo.Certificates;"));
			}
			finally
			{
				DropCreatedDatabase();
			}
		}

		// Проверяет гонку первого обращения к серверу, на котором ещё нет базы.
		private static void CheckParallelDatabaseCreation()
		{
			DropCreatedDatabase();
			try
			{
				Parallel.For(0, 6, index =>
				{
					var repository = new TestDatabase(CreatedDatabaseConnectionString(), new MemoryLogger());
					repository.Save(CreateCertificates("Иванов Иван Иванович"), "5.0.13003");
				});
				Check.Equal(2, (int)ExecuteScalar(CreatedDatabaseConnectionString(), "SELECT COUNT(*) FROM dbo.Certificates;"));
			}
			finally
			{
				DropCreatedDatabase();
			}
		}

		// Проверяет учётную запись без права создавать базы: ошибка должна объяснять, что делать.
		private static void CheckDatabaseCreationDenied()
		{
			var builder = new SqlConnectionStringBuilder(CreatedDatabaseConnectionString());
			if (builder.IntegratedSecurity)
			{
				Console.WriteLine("SKIP: проверка учётной записи без прав требует входа по логину SQL Server");
				return;
			}

			// Пароль одноразовый: учётная запись создаётся на время проверки и удаляется после неё.
			string password = "Tc!" + Guid.NewGuid().ToString("N");
			DropCreatedDatabase();
			DropRestrictedLogin();
			try
			{
				ExecuteScalar(MasterConnectionString(), "CREATE LOGIN " + RestrictedLoginName + " WITH PASSWORD = N'" + password
					+ "', CHECK_POLICY = OFF; SELECT 0;");
				builder.UserID = RestrictedLoginName;
				builder.Password = password;
				var repository = new TestDatabase(builder.ConnectionString, new MemoryLogger());
				Check.Throws<InvalidOperationException>(() => repository.Save(CreateCertificates("Иванов"), null));
				Check.Equal(true, ExecuteScalar(MasterConnectionString(), "SELECT DB_ID(N'" + CreatedDatabaseName + "');") is DBNull);
			}
			finally
			{
				DropRestrictedLogin();
			}
		}

		// Проверяет, что ошибка подключения доходит до вызывающего кода.
		private static void CheckUnavailableDatabase()
		{
			var repository = new TestDatabase(
				"Server=127.0.0.1,1;Database=TelecomTests;User Id=nobody;Password=nothing;Connect Timeout=2", new MemoryLogger());
			Check.Throws<SqlException>(() => repository.Save(CreateCertificates("Иванов"), null));
			Check.Throws<ArgumentException>(() => new TestDatabase(" ", new MemoryLogger()));
			Check.Throws<ArgumentException>(() => new TestDatabase("Server=127.0.0.1;Integrated Security=True", new MemoryLogger()));
		}

		// Создаёт два сертификата с серийными номерами образцов организаторов.
		private static IList<CertificateInfo> CreateCertificates(string firstOwnerName, DateTime? firstExpiry = null, DateTime? secondExpiry = null)
		{
			var certificates = new List<CertificateInfo>();
			certificates.Add(new CertificateInfo("03A14016", firstOwnerName, "Тестовый УЦ", @"C:\Certs\ivanov.cer", true, firstExpiry));
			certificates.Add(new CertificateInfo("378F0E85", "Петров Петр Петрович", "Тестовый УЦ", @"C:\Certs\petrov.cer", false, secondExpiry));
			return certificates;
		}

		// Удаляет тестовые таблицы в порядке, учитывающем внешний ключ.
		private static void DropCertificatesTable()
		{
			ExecuteScalar(@"
IF OBJECT_ID(N'dbo.CertificateLocations', N'U') IS NOT NULL DROP TABLE dbo.CertificateLocations;
IF OBJECT_ID(N'dbo.Certificates', N'U') IS NOT NULL DROP TABLE dbo.Certificates;
SELECT 0;");
		}

		// Возвращает число строк таблицы сертификатов.
		private static int CountRows()
		{
			return (int)ExecuteScalar("SELECT COUNT(*) FROM dbo.Certificates;");
		}

		// Возвращает строку подключения к тому же серверу, но к базе, которую создаёт проверяемый класс.
		private static string CreatedDatabaseConnectionString()
		{
			var builder = new SqlConnectionStringBuilder(databaseConnectionString);
			builder.InitialCatalog = CreatedDatabaseName;
			return builder.ConnectionString;
		}

		// Возвращает строку подключения к служебной базе master того же сервера.
		private static string MasterConnectionString()
		{
			var builder = new SqlConnectionStringBuilder(databaseConnectionString);
			builder.InitialCatalog = "master";
			return builder.ConnectionString;
		}

		// Удаляет только временную базу этих проверок; закрытые соединения из пула иначе мешали бы удалению.
		private static void DropCreatedDatabase()
		{
			SqlConnection.ClearAllPools();
			ExecuteScalar(MasterConnectionString(), "IF DB_ID(N'" + CreatedDatabaseName + "') IS NOT NULL BEGIN ALTER DATABASE ["
				+ CreatedDatabaseName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + CreatedDatabaseName + "]; END; SELECT 0;");
			SqlConnection.ClearAllPools();
		}

		// Удаляет временную учётную запись проверки, если она осталась от прошлого запуска.
		private static void DropRestrictedLogin()
		{
			SqlConnection.ClearAllPools();
			ExecuteScalar(MasterConnectionString(), "IF SUSER_ID(N'" + RestrictedLoginName + "') IS NOT NULL DROP LOGIN "
				+ RestrictedLoginName + "; SELECT 0;");
		}

		// Выполняет служебный запрос проверки в основной тестовой базе.
		private static object ExecuteScalar(string sql)
		{
			return ExecuteScalar(databaseConnectionString, sql);
		}

		// Выполняет служебный запрос проверки; в тексте SQL только константы самих проверок.
		private static object ExecuteScalar(string connectionString, string sql)
		{
			using (var connection = new SqlConnection(connectionString))
			using (var command = new SqlCommand(sql, connection))
			{
				connection.Open();
				return command.ExecuteScalar();
			}
		}

		// Читает одну строку таблицы независимо от проверяемого класса.
		private static StoredCertificate ReadCertificate(string serialNumber)
		{
			const string sql = "SELECT CreatedAt, UpdatedAt, OwnerName, CertificationAuthority, CryptoProVersion, "
				+ "IsCryptoProLicenseEmbedded FROM dbo.Certificates WHERE SerialNumber = @SerialNumber;";
			using (var connection = new SqlConnection(databaseConnectionString))
			using (var command = new SqlCommand(sql, connection))
			{
				command.Parameters.AddWithValue("@SerialNumber", serialNumber);
				connection.Open();
				using (SqlDataReader reader = command.ExecuteReader())
				{
					if (!reader.Read())
					{
						throw new InvalidOperationException("В таблице нет сертификата " + serialNumber);
					}

					var stored = new StoredCertificate();
					stored.CreatedAt = reader.GetDateTime(0);
					stored.UpdatedAt = reader.GetDateTime(1);
					stored.OwnerName = reader.GetString(2);
					stored.CertificationAuthority = reader.GetString(3);
					stored.CryptoProVersion = reader.IsDBNull(4) ? null : reader.GetString(4);
					stored.IsCryptoProLicenseEmbedded = reader.GetBoolean(5);
					return stored;
				}
			}
		}

		private sealed class StoredCertificate
		{
			public DateTime CreatedAt;
			public DateTime UpdatedAt;
			public string OwnerName;
			public string CertificationAuthority;
			public string CryptoProVersion;
			public bool IsCryptoProLicenseEmbedded;
		}
	}
}
