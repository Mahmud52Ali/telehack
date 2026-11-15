using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;
using Telecom.App.Forms;
using Telecom.App.Windows;
using Telecom.Core.Access;
using Telecom.Core.Certificates;
using Telecom.Core.Computers;
using Telecom.Core.Configuration;
using Telecom.Core.Scanning;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		// Проверяет локальную строку, её настройки и сбор данных без SMB и сохранённых паролей.
		private static void AddLocalComputerTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("Этот компьютер: настройки сохраняются отдельно от удалённых ПК", CheckLocalComputerSettings));
			AddWindowsOnly(tests, "Этот компьютер: редактор меняет только дополнительные пути", CheckLocalComputerDialog);
			AddWindowsOnly(tests, "Этот компьютер: файлы, маски, версия, ошибки и отмена без сети", CheckLocalComputerScan);
			AddWindowsOnly(tests, "Этот компьютер: чтение настоящего локального хранилища без учётки", CheckLocalMachineStore);
			AddWindowsOnly(tests, "Этот компьютер: первая строка, запрет удаления, выбор и сканирование из окна", CheckLocalComputerFlow);
		}

		// Сохраняет локальные настройки отдельно от адресов и учётных данных удалённых ПК.
		private static void CheckLocalComputerSettings()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerListFile(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				var remote = new ComputerProfile(Guid.NewGuid(), ProfileComputerIp, "Удалённый ПК", @"LAB-PC\reader", false,
					new[] { @"D:\RemoteCerts" });
				var local = ComputerProfile.CreateLocal(true, new[] { @" d:/LocalCerts/ ", @"D:\localcerts", @"C:\Users\*\Certs" });
				store.Save(new[] { remote, local });
				List<ComputerProfile> loaded = store.Load();
				Check.Equal(2, loaded.Count);
				Check.Equal(true, loaded[0].IsLocal);
				Check.Equal("Этот компьютер", loaded[0].DisplayName);
				Check.Equal(string.Empty, loaded[0].IpAddress);
				Check.Equal(false, loaded[0].HasUserName);
				Check.Equal(true, loaded[0].Selected);
				Check.Equal(2, loaded[0].AdditionalDirectories.Count);
				Check.Equal(@"D:\LocalCerts", loaded[0].AdditionalDirectories[0]);
				Check.Equal(remote.Id, loaded[1].Id);
				Check.Equal(remote.UserName, loaded[1].UserName);
				Check.Equal(false, loaded[1].IsLocal);
				loaded[0].Selected = false;
				store.Save(loaded);
				Check.Equal(false, store.Load()[0].Selected);
				string xml = File.ReadAllText(store.FilePath);
				Check.Throws<InvalidDataException>(() => store.Save(new[] { local, ComputerProfile.CreateLocal(false) }));
				Check.Equal(xml, File.ReadAllText(store.FilePath));
				File.WriteAllText(store.FilePath, "<computers version=\"4\"><localComputer selected=\"true\" userName=\"changed\" /></computers>");
				Check.Throws<InvalidDataException>(() => store.Load());
				File.WriteAllText(store.FilePath, "<computers version=\"4\"><localComputer selected=\"true\" /><localComputer selected=\"false\" /></computers>");
				Check.Throws<InvalidDataException>(() => store.Load());
			}
		}

		// Проверяет отсутствие полей подключения и сохранение неизменяемой локальной идентичности.
		private static void CheckLocalComputerDialog()
		{
			var local = ComputerProfile.CreateLocal(false, new[] { @"D:\Certificates" });
			Type dialogType = typeof(MainForm).Assembly.GetType("Telecom.App.Forms.Scan.ComputerProfileDialog");
			ConstructorInfo constructor = dialogType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];
			MethodInfo save = dialogType.GetMethod("OnSave", BindingFlags.NonPublic | BindingFlags.Instance);
			using (var dialog = (Form)constructor.Invoke(new object[] { local }))
			{
				Check.Equal(true, FormField<TextBox>(dialog, "nameInput").ReadOnly);
				Check.Equal("Этот компьютер", FormField<TextBox>(dialog, "nameInput").Text);
				Check.Equal(null, FormField<TextBox>(dialog, "ipAddressInput").Parent);
				Check.Equal(null, FormField<TextBox>(dialog, "userInput").Parent);
				Check.Equal(null, FormField<TextBox>(dialog, "passwordInput").Parent);
				TextBox paths = FormField<TextBox>(dialog, "additionalDirectoriesInput");
				Check.Equal(@"D:\Certificates", paths.Text);
				paths.Text = @"C:\Users\Ivan*\Certs";
				save.Invoke(dialog, new object[] { dialog, EventArgs.Empty });
				Check.Equal(DialogResult.None, dialog.DialogResult);
				Check.Equal(true, FormField<Label>(dialog, "errorLabel").Text.Contains("Ivan*"));
				FormField<TextBox>(dialog, "nameInput").Text = "Попытка сменить название";
				paths.Lines = new[] { @" d:/Extra/ ", @"D:\extra", @"D:\Profiles\*\Certs" };
				save.Invoke(dialog, new object[] { dialog, EventArgs.Empty });
				Check.Equal(DialogResult.OK, dialog.DialogResult);
				var saved = (ComputerProfile)dialogType.GetProperty("Profile", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog, null);
				Check.Equal(true, saved.IsLocal);
				Check.Equal(local.Id, saved.Id);
				Check.Equal("Этот компьютер", saved.DisplayName);
				Check.Equal(string.Empty, saved.IpAddress);
				Check.Equal(false, saved.HasUserName);
				Check.Equal(false, saved.Selected);
				Check.Equal(2, saved.AdditionalDirectories.Count);
				Check.Equal(@"D:\Extra", saved.AdditionalDirectories[0]);
			}
		}

		// Использует настоящие временные папки и запрещает любые сетевые проверки в локальной ветке.
		private static void CheckLocalComputerScan()
		{
			using (var workspace = new TestWorkspace())
			using (var cancellation = new CancellationTokenSource())
			{
				string common = Path.Combine(workspace.DirectoryPath, "common");
				string extra = Path.Combine(workspace.DirectoryPath, "profiles", "Ivanov", "Certs");
				string denied = Path.Combine(workspace.DirectoryPath, "denied");
				Directory.CreateDirectory(common);
				Directory.CreateDirectory(extra);
				File.Copy(Fixture(Ivanov, ".cer"), Path.Combine(common, "ivanov.cer"));
				File.Copy(Fixture(Petrov, ".cer"), Path.Combine(extra, "petrov.cer"));
				File.WriteAllBytes(Path.Combine(common, "invalid.cer"), new byte[] { 0x30, 0x82, 0x03 });
				string cryptoPath = Path.Combine(workspace.DirectoryPath, "CSP", "csptest.exe");
				var access = new LocalComputerAccess { DeniedDirectory = denied, CryptoPath = cryptoPath };
				var logger = new MemoryLogger();
				var scanner = new ComputerScanner(access, new CertificateParser(), logger);
				var local = ComputerProfile.CreateLocal(true, new[] { Path.Combine(workspace.DirectoryPath, "profiles", "*", "Certs"), common, denied });
				var configuration = new ScanConfiguration(new[] { common }, new[] { cryptoPath }, 3, string.Empty)
					.WithAdditionalDirectories(local.AdditionalDirectories);
				var machineStore = new LabMachineCertificateStore((profile, token, readCertificate, reportError) =>
				{
					Check.Equal(true, profile.IsLocal);
					Check.Equal(false, profile.HasUserName);
					readCertificate("machine-certificate", SerializeCertificateFixture(Ivanov));
				});
				ComputerScanResult result = scanner.Scan(local, configuration, machineStore, CancellationToken.None);
				Check.Equal(Environment.MachineName, result.ComputerName);
				Check.Equal(ComputerScanStatus.Partial, result.Status);
				Check.Equal(3, result.Certificates.Count);
				Check.Equal(3, result.Certificates.Select(certificate => certificate.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
				Check.Equal(true, result.Certificates.Any(certificate => certificate.SourcePath == Path.Combine(extra, "petrov.cer")));
				Check.Equal(true, result.Certificates.All(certificate => !certificate.SourcePath.StartsWith(@"\\", StringComparison.Ordinal)));
				Check.Equal(CryptoProStatus.Found, result.CryptoProStatus);
				Check.Equal("5.0.13003", result.CryptoProVersion);
				Check.Equal(cryptoPath, result.CryptoProSource);
				Check.Equal(2, result.Errors.Count);
				Check.Equal(true, result.Errors.Any(error => error.Path == denied && error.Kind == ScanErrorKind.AccessDenied));
				Check.Equal(2, logger.Errors.Count);
				cancellation.Cancel();
				Check.Equal(ComputerScanStatus.Cancelled, scanner.Scan(local, configuration, machineStore, cancellation.Token).Status);
				Check.Equal(1, machineStore.Reads);
			}
		}

		// Сверяет локальное чтение на Windows с системным API, не устанавливая и не удаляя сертификаты.
		private static void CheckLocalMachineStore()
		{
			Type storeType = typeof(MainForm).Assembly.GetType("Telecom.App.Windows.WindowsMachineCertificateStore");
			var reader = (IMachineCertificateStore)Activator.CreateInstance(storeType, true);
			var profile = ComputerProfile.CreateLocal(true);
			var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			reader.Read(profile, CancellationToken.None,
				(entry, contents) => actual.Add(entry, new CertificateParser().Parse(contents, entry).SerialNumber),
				(entry, exception) => { throw new InvalidOperationException("Ошибка чтения локального хранилища.", exception); });
			var expected = new X509Store(StoreName.My, StoreLocation.LocalMachine);
			try
			{
				expected.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
				Check.Equal(true, new HashSet<string>(actual.Values, StringComparer.OrdinalIgnoreCase)
					.SetEquals(expected.Certificates.Cast<X509Certificate2>().Select(certificate => certificate.SerialNumber)));
			}
			finally
			{
				expected.Close();
			}

			string absent = Guid.NewGuid().ToString("N");
			var entries = actual.Keys.Take(1).Concat(new[] { absent }).ToArray();
			var checkedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			reader.ReadEntries(profile, entries, CancellationToken.None,
				(entry, contents) =>
				{
					Check.Equal(true, checkedEntries.Add(entry));
					if (entry == absent)
					{
						Check.Equal(null, contents);
					}
					else
					{
						Check.Equal(actual[entry], new CertificateParser().Parse(contents, entry).SerialNumber);
					}
				},
				(entry, exception) => { throw new InvalidOperationException("Ошибка адресной проверки локального хранилища.", exception); });
			Check.Equal(true, checkedEntries.SetEquals(entries));
		}

		// Проверяет первый запуск, сохранение путей из редактора, выбор и повторное открытие окна.
		private static void CheckLocalComputerFlow()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerListFile(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				string common = Path.Combine(workspace.DirectoryPath, "common");
				string extra = Path.Combine(workspace.DirectoryPath, "extra");
				string cryptoPath = Path.Combine(workspace.DirectoryPath, "CSP", "csptest.exe");
				Directory.CreateDirectory(common);
				Directory.CreateDirectory(extra);
				File.Copy(Fixture(Ivanov, ".cer"), Path.Combine(extra, "ivanov.cer"));
				File.WriteAllText(Path.Combine(workspace.DirectoryPath, "scan.config.xml"),
					"<scanSettings><directories><directory>" + SecurityElement.Escape(common) + "</directory></directories>"
					+ "<cryptoProExecutables><path>" + SecurityElement.Escape(cryptoPath) + "</path></cryptoProExecutables></scanSettings>");
				var access = new LocalComputerAccess { CryptoPath = cryptoPath };
				var machineStore = new LabMachineCertificateStore((profile, token, readCertificate, reportError) => Check.Equal(true, profile.IsLocal));
				var connection = new WindowsSmbConnection(new WindowsCredentialStore(), access,
					(path, user, password) => { throw new InvalidOperationException("Локальный ПК не должен использовать SMB."); });
				using (var form = new MainForm(new MemoryLogger(), store, access, connection, machineStore))
				{
					form.ShowInTaskbar = false;
					form.StartPosition = FormStartPosition.Manual;
					form.Location = new Point(-20000, -20000);
					form.Show();
					WaitForFormIdle(form);
					DataGridView grid = FormField<DataGridView>(form, "computersGrid");
					Check.Equal(1, grid.Rows.Count);
					Check.Equal("Этот компьютер", grid.Rows[0].Cells[1].Value);
					Check.Equal(false, FormField<Button>(form, "removeComputerButton").Enabled);
					Check.Equal(false, FormField<Button>(form, "connectButton").Enabled);
					InvokeMethod(form, "OnRemoveComputer", form, EventArgs.Empty);
					Check.Equal(1, grid.Rows.Count);
					using (var timer = new System.Windows.Forms.Timer { Interval = 10 })
					{
						timer.Tick += (sender, args) =>
						{
							Form dialog = Application.OpenForms.Cast<Form>().SingleOrDefault(value => value.GetType().Name == "ComputerProfileDialog");
							if (dialog == null)
							{
								return;
							}

							timer.Stop();
							FormField<TextBox>(dialog, "additionalDirectoriesInput").Text = extra;
							((Button)dialog.AcceptButton).PerformClick();
						};
						timer.Start();
						FormField<Button>(form, "editComputerButton").PerformClick();
					}

					Check.Equal(extra, store.Load()[0].AdditionalDirectories[0]);
					grid.Rows[0].Cells[0].Value = true;
					grid.EndEdit();
					Check.Equal(true, store.Load()[0].Selected);
					FormField<Button>(form, "scanButton").PerformClick();
					WaitForFormIdle(form);
					Check.Equal(1, machineStore.Reads);
					Check.Equal(1, FormField<DataGridView>(form, "certificatesGrid").Rows.Count);
					Check.Equal(0, FormField<DataGridView>(form, "errorsGrid").Rows.Count);
					Check.Equal("Локально", grid.Rows[0].Cells[4].Value);
					Check.Equal("5.0.13003", grid.Rows[0].Cells[6].Value);
					Check.Equal("Строка подключения не задана", grid.Rows[0].Cells[7].Value);
					grid.Rows[0].Cells[0].Value = false;
					grid.EndEdit();
					FormField<Button>(form, "scanButton").PerformClick();
					WaitForFormIdle(form);
					Check.Equal(1, machineStore.Reads);
					Check.Equal(false, store.Load()[0].Selected);
					form.Close();
				}

				using (var form = new MainForm(new MemoryLogger(), store, access, connection, machineStore))
				{
					form.ShowInTaskbar = false;
					form.StartPosition = FormStartPosition.Manual;
					form.Location = new Point(-20000, -20000);
					form.Show();
					WaitForFormIdle(form);
					DataGridView grid = FormField<DataGridView>(form, "computersGrid");
					Check.Equal(1, grid.Rows.Count);
					Check.Equal(false, grid.Rows[0].Cells[0].Value);
					Check.Equal(extra, store.Load()[0].AdditionalDirectories[0]);
					form.Close();
				}
			}
		}
	}
}
