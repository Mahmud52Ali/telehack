using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using Telecom.App;
using Telecom.App.Windows;
using Telecom.Core.Common;
using Telecom.Core.Computers;
using Telecom.Core.Configuration;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private const string PrimaryCryptoPath = @"C:\Program Files\Crypto Pro\CSP\csptest.exe";
		private const string SecondaryCryptoPath = @"C:\Program Files (x86)\Crypto Pro\CSP\csptest.exe";
		private const string ConfigurationTemplateName = "scan.config.template.xml";

		// Регистрирует проверки реального XML, валидации путей и построения адресов административных папок.
		private static void AddConfigurationTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("Конфигурация и журнал используют корень решения или отдельной поставки", CheckDataDirectory));
			tests.Add(new KeyValuePair<string, Action>("Конфигурация: первый запуск создаёт configs/scan.config.xml из шаблона", CheckShippedConfiguration));
			tests.Add(new KeyValuePair<string, Action>("Конфигурация: повторный запуск сохраняет рабочий XML", CheckExistingConfiguration));
			tests.Add(new KeyValuePair<string, Action>("Конфигурация: сохранение путей, таймаута и строки MSSQL", CheckConfigurationSave));
			tests.Add(new KeyValuePair<string, Action>("Конфигурация: проверка импорта, точное восстановление шаблона и повторное создание", CheckConfigurationReplacement));
			tests.Add(new KeyValuePair<string, Action>("Конфигурация: точная копия рабочего XML и восстановление после удаления", CheckConfigurationCopy));
			AddWindowsOnly(tests, "Настройки: прокрутка списков, сохранение и отмена из окна", CheckSettingsEditor);
			AddWindowsOnly(tests, "Персонализация: тема и масштаб применяются сразу и сохраняются отдельно", CheckPersonalization);
			tests.Add(new KeyValuePair<string, Action>("Пустые, ошибочные и повторяющиеся разделы отвергаются", CheckInvalidConfiguration));
			tests.Add(new KeyValuePair<string, Action>("DTD и внешние XML-сущности запрещены", CheckExternalEntities));
			tests.Add(new KeyValuePair<string, Action>("Конфигурация: нормализация, повторы и таймаут", CheckConfigurationValidation));
			tests.Add(new KeyValuePair<string, Action>("Преобразование C:, D: и корня диска в UNC", CheckRemotePaths));
			tests.Add(new KeyValuePair<string, Action>("Пути с обходом каталогов и некорректные имена ПК отвергаются", CheckInvalidRemotePaths));
		}

		// Сверяет расположение данных в сборке из репозитория и в отдельной папке с программой.
		private static void CheckDataDirectory()
		{
			using (var workspace = new TestWorkspace())
			{
				string solutionDirectory = Path.Combine(workspace.DirectoryPath, "repository");
				string buildDirectory = Path.Combine(solutionDirectory, "src", "Telecom.App", "bin", "Release", "net45");
				Directory.CreateDirectory(buildDirectory);
				File.WriteAllText(Path.Combine(solutionDirectory, "Telecom.sln"), string.Empty);
				string releaseDirectory = Path.Combine(workspace.DirectoryPath, "release");
				Directory.CreateDirectory(releaseDirectory);
				Type application = typeof(MainForm).Assembly.GetType("Telecom.App.AppPaths", true);
				MethodInfo find = application.GetMethod("FindDataDirectory", BindingFlags.Static | BindingFlags.NonPublic);
				Check.Equal(solutionDirectory, (string)find.Invoke(null, new object[] { buildDirectory }));
				Check.Equal(releaseDirectory, (string)find.Invoke(null, new object[] { releaseDirectory }));
			}
		}

		// Создаёт рабочий XML из шаблона даже при наличии одноимённого конфига рядом с exe.
		private static void CheckShippedConfiguration()
		{
			using (var workspace = new TestWorkspace())
			{
				string applicationDirectory = Path.Combine(workspace.DirectoryPath, "app");
				Directory.CreateDirectory(applicationDirectory);
				string templatePath = Path.Combine(applicationDirectory, ConfigurationTemplateName);
				File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigurationTemplateName), templatePath);
				File.WriteAllText(Path.Combine(applicationDirectory, "scan.config.xml"), ConfigurationXml());
				File.SetAttributes(templatePath, FileAttributes.ReadOnly);
				string configurationPath = Path.Combine(applicationDirectory, "configs", "scan.config.xml");
				try
				{
					Check.Equal(true, ScanConfigurationFile.CreateFromTemplate(configurationPath, templatePath));
					ScanConfiguration configuration = ScanConfigurationFile.Load(configurationPath);
					Check.Equal(1, configuration.Directories.Count);
					Check.Equal(@"C:\Users\*\AppData\Roaming\Microsoft\SystemCertificates\My\Certificates", configuration.Directories[0]);
					Check.Equal(2, configuration.CryptoProExecutables.Count);
					Check.Equal(3, configuration.ConnectionTimeoutSeconds);
					Check.Equal(string.Empty, configuration.ConnectionString);
					Check.Equal(false, (File.GetAttributes(configurationPath) & FileAttributes.ReadOnly) != 0);
				}
				finally
				{
					File.SetAttributes(templatePath, FileAttributes.Normal);
				}
			}
		}

		// Проверяет, что запуск сохраняет настроенные пути и строку подключения в configs.
		private static void CheckExistingConfiguration()
		{
			using (var workspace = new TestWorkspace())
			{
				string applicationDirectory = workspace.DirectoryPath;
				string settingsDirectory = Path.Combine(applicationDirectory, "configs");
				string configurationPath = Path.Combine(settingsDirectory, "scan.config.xml");
				string editedXml = ConfigurationXml().Replace(@"C:\Certs", @"D:\Edited")
					.Replace("</scanSettings>", "<connectionString>Server=EXAMPLE;Database=Certificates</connectionString></scanSettings>");
				Directory.CreateDirectory(settingsDirectory);
				File.WriteAllText(configurationPath, editedXml);
				string templatePath = Path.Combine(applicationDirectory, ConfigurationTemplateName);
				File.WriteAllText(templatePath, ConfigurationXml());
				Check.Equal(false, ScanConfigurationFile.CreateFromTemplate(configurationPath, templatePath));
				Check.Equal(editedXml, File.ReadAllText(configurationPath));
				Check.Equal(1, Directory.GetFiles(settingsDirectory).Length);
			}
		}

		// Проверяет чтение сохранённых путей и SQL-строки с XML-символами.
		private static void CheckConfigurationSave()
		{
			using (var workspace = new TestWorkspace())
			{
				string path = Path.Combine(workspace.DirectoryPath, "configs", "scan.config.xml");
				const string connection = "Server=EXAMPLE;Database=Certificates;User Id=sample;Password=\"dummy<&;>\"";
				var configuration = new ScanConfiguration(new[] { @"c:/Team & Co/*/Certs", @"D:\Отдел" },
					new[] { SecondaryCryptoPath, PrimaryCryptoPath }, 17, connection);
				ScanConfigurationFile.Save(configuration, path);

				ScanConfiguration reloaded = ScanConfigurationFile.Load(path);
				Check.Equal(2, reloaded.Directories.Count);
				Check.Equal(@"C:\Team & Co\*\Certs", reloaded.Directories[0]);
				Check.Equal(@"D:\Отдел", reloaded.Directories[1]);
				Check.Equal(SecondaryCryptoPath, reloaded.CryptoProExecutables[0]);
				Check.Equal(17, reloaded.ConnectionTimeoutSeconds);
				Check.Equal(connection, reloaded.ConnectionString);

				var invalidDatabase = new ScanConfiguration(reloaded.Directories, reloaded.CryptoProExecutables, 18, "Server=EXAMPLE");
				Check.Throws<ArgumentException>(() => ScanConfigurationFile.Save(invalidDatabase, path));
				Check.Equal(17, ScanConfigurationFile.Load(path).ConnectionTimeoutSeconds);
				Check.Equal(1, Directory.GetFiles(Path.GetDirectoryName(path)).Length);
			}
		}

		// Проверяет, что импорт сохраняет исходный XML, а ошибка оставляет прежний снимок на диске.
		private static void CheckConfigurationReplacement()
		{
			using (var workspace = new TestWorkspace())
			{
				string path = Path.Combine(workspace.DirectoryPath, "configs", "scan.config.xml");
				string template = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigurationTemplateName);
				string source = Path.Combine(workspace.DirectoryPath, "office.xml");
				ScanConfigurationFile.CreateFromTemplate(path, template);
				string importedXml = "<!-- Конфигурация подразделения -->\r\n"
					+ ConfigurationXml().Replace(@"C:\Certs", @"D:\Imported")
						.Replace("</scanSettings>", "<connectionString>Server=EXAMPLE;Database=Certificates</connectionString></scanSettings>");
				File.WriteAllText(source, importedXml, System.Text.Encoding.Unicode);
				ScanConfiguration imported = ScanConfigurationFile.Import(source, path);
				Check.Equal(@"D:\Imported", imported.Directories[0]);
				Check.Equal(File.ReadAllText(source), File.ReadAllText(path));
				Check.Equal(Convert.ToBase64String(File.ReadAllBytes(source)), Convert.ToBase64String(File.ReadAllBytes(path)));

				File.WriteAllText(source, "<scanSettings>");
				Check.Throws<XmlException>(() => ScanConfigurationFile.Import(source, path));
				File.WriteAllText(source, importedXml.Replace("Database=Certificates", "UnknownSetting=Certificates"));
				Check.Throws<ArgumentException>(() => ScanConfigurationFile.Import(source, path));
				Check.Equal(importedXml, File.ReadAllText(path));
				Check.Equal(1, Directory.GetFiles(Path.GetDirectoryName(path)).Length);

				ScanConfiguration restored = ScanConfigurationFile.Import(template, path);
				Check.Equal(string.Empty, restored.ConnectionString);
				Check.Equal(File.ReadAllText(template), File.ReadAllText(path));
				File.Delete(path);
				Check.Equal(true, ScanConfigurationFile.CreateFromTemplate(path, template));
				Check.Equal(File.ReadAllText(template), File.ReadAllText(path));
			}
		}

		// Проверяет, что копия сохраняет исходные байты и пригодна для восстановления.
		private static void CheckConfigurationCopy()
		{
			using (var workspace = new TestWorkspace())
			{
				string path = Path.Combine(workspace.DirectoryPath, "scan.config.xml");
				string copy = Path.Combine(workspace.DirectoryPath, "scan.config.backup.xml");
				string xml = "<!-- Настройки отдела -->\r\n" + ConfigurationXml().Replace("</scanSettings>",
					"<connectionString>Server=EXAMPLE;Database=Certificates</connectionString></scanSettings>");
				File.WriteAllText(path, xml, System.Text.Encoding.Unicode);
				File.WriteAllText(copy, "Предыдущая копия");
				ScanConfigurationFile.SaveCopy(path, copy);
				Check.Equal(true, File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(copy)));
				Check.Throws<IOException>(() => ScanConfigurationFile.SaveCopy(path, path));
				File.WriteAllText(path, "<scanSettings>");
				Check.Throws<XmlException>(() => ScanConfigurationFile.SaveCopy(path, copy));
				Check.Equal(xml, File.ReadAllText(copy));
				File.Delete(path);
				Check.Equal("Server=EXAMPLE;Database=Certificates", ScanConfigurationFile.Import(copy, path).ConnectionString);
				Check.Equal(true, File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(copy)));
				Check.Equal(2, Directory.GetFiles(workspace.DirectoryPath).Length);
			}
		}

		// Редактирует настройки через элементы окна, не затрагивая файлы оператора.
		private static void CheckSettingsEditor()
		{
			using (var workspace = new TestWorkspace())
			using (var form = new MainForm(new MemoryLogger(),
				new ComputerListFile(Path.Combine(workspace.DirectoryPath, "computers.xml")), new WindowsFileSystem(), null))
			{
				form.ShowInTaskbar = false;
				form.StartPosition = FormStartPosition.Manual;
				form.Location = new Point(-20000, -20000);
				form.Size = form.MinimumSize;
				form.Show();
				WaitForFormIdle(form);

				RadioButton settings = FormField<RadioButton>(form, "settingsButton");
				RadioButton computers = FormField<RadioButton>(form, "computersButton");
				Control panel = FormField<Control>(form, "settingsPanel");
				settings.Checked = true;
				Check.Equal(true, panel.Visible);
				Check.Equal(false, computers.Checked);
				Check.Equal(false, FormField<Button>(form, "scanButton").Visible);

				var directories = FormField<DataGridView>(panel, "directoriesGrid");
				var cryptoPaths = FormField<DataGridView>(panel, "cryptoPathsGrid");
				Button save = FormField<Button>(panel, "saveButton");
				Check.Equal(false, save.Enabled);
				int windowHeight = form.Height;
				Button addDirectory = directories.Parent.Controls.OfType<Button>().Single();
				Button addCryptoPath = cryptoPaths.Parent.Controls.OfType<Button>().Single();
				CheckPathListScrolling(directories, addDirectory, @"D:\Folder", string.Empty);
				CheckPathListScrolling(cryptoPaths, addCryptoPath, @"D:\CSP", @"\csptest.exe");
				Check.Equal(windowHeight, form.Height);
				FormField<NumericUpDown>(panel, "timeoutInput").Value = 9;

				TabControl tabs = FormField<TabControl>(panel, "tabs");
				tabs.SelectedTab = FormField<TabPage>(panel, "databasePage");
				const string connection = "Server=EXAMPLE;Database=Certificates;Integrated Security=True";
				TextBox database = FormField<TextBox>(panel, "connectionInput");
				database.Text = "Server=EXAMPLE";
				save.PerformClick();
				Check.Equal(true, save.Enabled);
				string path = Path.Combine(workspace.DirectoryPath, "scan.config.xml");
				Check.Equal(3, ScanConfigurationFile.Load(path).ConnectionTimeoutSeconds);

				database.Text = connection;
				save.PerformClick();
				Check.Equal(false, save.Enabled);
				ScanConfiguration saved = ScanConfigurationFile.Load(path);
				Check.Equal(25, saved.Directories.Count);
				Check.Equal(26, saved.CryptoProExecutables.Count);
				Check.Equal(9, saved.ConnectionTimeoutSeconds);
				Check.Equal(connection, saved.ConnectionString);
				Check.Equal(true, database.UseSystemPasswordChar);

				database.Text = string.Empty;
				computers.Checked = true;
				Check.Equal(false, panel.Visible);
				settings.Checked = true;
				Check.Equal(string.Empty, database.Text);
				FormField<Button>(panel, "discardButton").PerformClick();
				Check.Equal(connection, database.Text);
				Check.Equal(false, save.Enabled);
				form.Close();
			}
		}

		// Переключает темы и масштаб через окно, затем проверяет повторный запуск и ошибочный файл.
		private static void CheckPersonalization()
		{
			using (var workspace = new TestWorkspace())
			{
				var store = new ComputerListFile(Path.Combine(workspace.DirectoryPath, "computers.xml"));
				string appearancePath = Path.Combine(workspace.DirectoryPath, "appearance.xml");
				string configurationPath = Path.Combine(workspace.DirectoryPath, "scan.config.xml");
				using (var form = new MainForm(new MemoryLogger(), store, new WindowsFileSystem(), null))
				{
					form.ShowInTaskbar = false;
					form.StartPosition = FormStartPosition.Manual;
					form.Location = new Point(-20000, -20000);
					form.Show();
					WaitForFormIdle(form);

					string originalConfiguration = File.ReadAllText(configurationPath);
					FormField<RadioButton>(form, "settingsButton").Checked = true;
					Control panel = FormField<Control>(form, "settingsPanel");
					TabControl tabs = FormField<TabControl>(panel, "tabs");
					NumericUpDown timeout = FormField<NumericUpDown>(panel, "timeoutInput");
					timeout.Value = 8;
					tabs.SelectedTab = FormField<TabPage>(panel, "personalizationPage");
					var theme = FormField<ComboBox>(panel, "themeInput");
					var fontScale = FormField<ComboBox>(panel, "fontScaleInput");
					var grid = FormField<DataGridView>(form, "computersGrid");
					Color light = form.BackColor;
					Check.Equal("Светлая", theme.Text);
					Check.Equal("100%", fontScale.Text);
					Check.Equal(false, File.Exists(appearancePath));
					Check.Equal(false, FormField<Button>(panel, "saveButton").Visible);

					theme.SelectedIndex = 1;
					Color dark = form.BackColor;
					Check.Equal(SystemInformation.HighContrast, dark == light);
					Check.Equal(!SystemInformation.HighContrast, grid.BackgroundColor != SystemColors.Window);
					Check.Equal(!SystemInformation.HighContrast, theme.ForeColor != SystemColors.WindowText);
					Size windowSize = form.Size;
					fontScale.SelectedIndex = 7;
					Check.Equal(13.5F, form.Font.SizeInPoints);
					Check.Equal(13.5F, grid.Font.SizeInPoints);
					Check.Equal(windowSize, form.Size);
					Check.Equal(true, tabs.GetTabRect(tabs.SelectedIndex).Height >= tabs.Font.Height);

					theme.SelectedIndex = 0;
					Check.Equal(light, form.BackColor);
					Check.Equal(SystemColors.Window, grid.BackgroundColor);
					theme.SelectedIndex = 2;
					var systemLight = Microsoft.Win32.Registry.GetValue(
						@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
					Check.Equal(!SystemInformation.HighContrast && Equals(systemLight, 0) ? dark : light, form.BackColor);
					theme.SelectedIndex = 1;

					tabs.SelectedTab = FormField<TabPage>(panel, "scanPage");
					Check.Equal(8M, timeout.Value);
					Check.Equal(true, FormField<Button>(panel, "saveButton").Enabled);
					FormField<Button>(panel, "discardButton").PerformClick();
					Check.Equal(originalConfiguration, File.ReadAllText(configurationPath));
					form.Close();
				}

				using (var reopened = new MainForm(new MemoryLogger(), store, new WindowsFileSystem(), null))
				{
					Control panel = FormField<Control>(reopened, "settingsPanel");
					Check.Equal("Тёмная", FormField<ComboBox>(panel, "themeInput").Text);
					Check.Equal("150%", FormField<ComboBox>(panel, "fontScaleInput").Text);
					Check.Equal(13.5F, reopened.Font.SizeInPoints);
				}

				File.WriteAllText(appearancePath, "<appearance theme=\"Dark\" fontPercent=\"500\" />");
				var logger = new MemoryLogger();
				using (var reopened = new MainForm(logger, store, new WindowsFileSystem(), null))
				{
					Control panel = FormField<Control>(reopened, "settingsPanel");
					Check.Equal("Светлая", FormField<ComboBox>(panel, "themeInput").Text);
					Check.Equal(9F, reopened.Font.SizeInPoints);
					Check.Equal(1, logger.Errors.Count);
				}
			}
		}

		// Проверяет, что добавление оставляет таблицу прежней высоты и показывает новую строку.
		private static void CheckPathListScrolling(DataGridView grid, Button add, string prefix, string suffix)
		{
			int height = grid.Height;
			int originalCount = grid.Rows.Count;
			for (int index = 0; index < 24; index++)
			{
				add.PerformClick();
				var editor = (TextBox)grid.EditingControl;
				editor.Text = prefix + index + suffix;
				grid.EndEdit();
			}

			Check.Equal(originalCount + 24, grid.Rows.Count);
			Check.Equal(height, grid.Height);
			Check.Equal(true, grid.FirstDisplayedScrollingRowIndex > 0);
			Check.Equal(true, grid.Rows[grid.Rows.Count - 1].Displayed);
		}

		// Проверяет сообщения об отсутствующей конфигурации, синтаксической ошибке и неверной структуре.
		private static void CheckInvalidConfiguration()
		{
			using (var workspace = new TestWorkspace())
			{
				string path = Path.Combine(workspace.DirectoryPath, "settings.xml");
				Check.Throws<FileNotFoundException>(() => ScanConfigurationFile.Load(path));
				File.WriteAllText(path, "<scanSettings>");
				Check.Throws<XmlException>(() => ScanConfigurationFile.Load(path));
				File.WriteAllText(path, "<wrongRoot />");
				Check.Throws<InvalidDataException>(() => ScanConfigurationFile.Load(path));
				File.WriteAllText(path, "<scanSettings><directories /></scanSettings>");
				Check.Throws<InvalidDataException>(() => ScanConfigurationFile.Load(path));
				File.WriteAllText(path, ConfigurationXml().Replace("</scanSettings>", "<directories><directory>C:\\Extra</directory></directories></scanSettings>"));
				Check.Throws<InvalidDataException>(() => ScanConfigurationFile.Load(path));
				File.WriteAllText(path, ConfigurationXml().Replace("<directory>", "<direcotry>").Replace("</directory>", "</direcotry>"));
				Check.Throws<InvalidDataException>(() => ScanConfigurationFile.Load(path));
				File.WriteAllText(path, ConfigurationXml().Replace("connectionTimeoutSeconds", "connectionTimoutSeconds"));
				Check.Throws<InvalidDataException>(() => ScanConfigurationFile.Load(path));
				File.WriteAllText(path, ConfigurationXml().Replace("<directory>", "<directory recurse=\"false\">"));
				Check.Throws<InvalidDataException>(() => ScanConfigurationFile.Load(path));
				File.WriteAllText(path, ConfigurationXml().Replace("</scanSettings>", "<connectionString>first</connectionString><connectionString>second</connectionString></scanSettings>"));
				Check.Throws<InvalidDataException>(() => ScanConfigurationFile.Load(path));
			}
		}

		// Проверяет, что конфигурация не может подгрузить содержимое другого файла как XML-сущность.
		private static void CheckExternalEntities()
		{
			using (var workspace = new TestWorkspace())
			{
				string path = Path.Combine(workspace.DirectoryPath, "external.xml");
				File.WriteAllText(path, "<!DOCTYPE scanSettings [<!ENTITY external SYSTEM 'file:///unreadable'>]>" +
					ConfigurationXml().Replace("</scanSettings>", "<connectionString>&external;</connectionString></scanSettings>"));
				Check.Throws<XmlException>(() => ScanConfigurationFile.Load(path));
			}
		}

		// Проверяет, что сканер не получит неоднозначные пути и бесконечное ожидание подключения.
		private static void CheckConfigurationValidation()
		{
			var configuration = new ScanConfiguration(new[] { @"c:/Users/*/Certs", @"C:\Users\*\Certs" },
				new[] { PrimaryCryptoPath }, 5, string.Empty);
			Check.Equal(1, configuration.Directories.Count);
			Check.Equal(@"C:\Users\*\Certs", configuration.Directories[0]);
			Check.Equal(5, configuration.ConnectionTimeoutSeconds);
			Check.Throws<InvalidDataException>(() => new ScanConfiguration(new[] { @"C:\Certs" }, new[] { PrimaryCryptoPath }, 0, string.Empty));
			Check.Throws<InvalidDataException>(() => new ScanConfiguration(new[] { @"C:\Certs" }, new[] { PrimaryCryptoPath }, 61, string.Empty));
			Check.Throws<InvalidDataException>(() => new ScanConfiguration(new[] { @"C:\Certs" }, new[] { @"C:\Tools\other.exe" }, 3, string.Empty));
			Check.Throws<ArgumentException>(() => new ScanConfiguration(new[] { @"C:\Users\I*\Certs" }, new[] { PrimaryCryptoPath }, 3, string.Empty));

			var common = new ScanConfiguration(new[] { @"C:\CommonCerts" }, new[] { PrimaryCryptoPath, SecondaryCryptoPath }, 7,
				"Server=YOUR_SQL_SERVER;Database=Certificates;Integrated Security=True");
			ScanConfiguration first = common.WithAdditionalDirectories(new[] { @" c:/commoncerts/ ", @"D:\Certificates", @"d:/certificates/" });
			ScanConfiguration second = common.WithAdditionalDirectories(new string[0]);
			Check.Equal(2, first.Directories.Count);
			Check.Equal(@"C:\CommonCerts", first.Directories[0]);
			Check.Equal(@"D:\Certificates", first.Directories[1]);
			Check.Equal(1, common.Directories.Count);
			Check.Equal(1, second.Directories.Count);
			Check.Equal(@"C:\CommonCerts", second.Directories[0]);
			Check.Equal(2, first.CryptoProExecutables.Count);
			Check.Equal(PrimaryCryptoPath, first.CryptoProExecutables[0]);
			Check.Equal(SecondaryCryptoPath, first.CryptoProExecutables[1]);
			Check.Equal(common.ConnectionTimeoutSeconds, first.ConnectionTimeoutSeconds);
			Check.Equal(common.ConnectionString, first.ConnectionString);
		}

		// Проверяет конкретные ожидаемые адреса, включая диски, отличные от C:.
		private static void CheckRemotePaths()
		{
			Check.Equal(@"\\LAB-PC\C$\Users\*\Certs", RemotePath.ToUnc(" LAB-PC ", @"C:\Users\*\Certs"));
			Check.Equal(@"\\192.168.56.101\D$\Certs", RemotePath.ToUnc("192.168.56.101", @"d:\Certs\"));
			Check.Equal(@"\\LAB-PC\C$\", RemotePath.ToUnc("LAB-PC", @"C:\"));
		}

		// Проверяет, что имя ПК не позволяет подменить административную папку или задать URL.
		private static void CheckInvalidRemotePaths()
		{
			foreach (string host in new[] { "", @"PC\C$", "http://PC", "PC:445", "::1", "PC name" })
			{
				Check.Throws<ArgumentException>(() => RemotePath.NormalizeComputerName(host));
			}

			foreach (string path in new[] { @"relative\Certs", @"\\other\C$\Certs", @"C:\Users\..\Windows", @"C:\Certs?.cer", @"C:\Certs:stream" })
			{
				Check.Throws<ArgumentException>(() => RemotePath.ToUnc("LAB-PC", path));
			}
		}

		// Возвращает небольшой XML для проверок повреждений структуры.
		private static string ConfigurationXml()
		{
			return "<scanSettings connectionTimeoutSeconds=\"3\"><directories><directory>C:\\Certs</directory></directories>" +
				"<cryptoProExecutables><path>C:\\Program Files\\Crypto Pro\\CSP\\csptest.exe</path></cryptoProExecutables></scanSettings>";
		}
	}
}
