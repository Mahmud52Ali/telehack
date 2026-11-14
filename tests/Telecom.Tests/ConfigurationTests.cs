using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;
using Telecom.App;
using Telecom.Core;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private const string PrimaryCryptoPath = @"C:\Program Files\Crypto Pro\CSP\csptest.exe";
		private const string SecondaryCryptoPath = @"C:\Program Files (x86)\Crypto Pro\CSP\csptest.exe";

		// Регистрирует проверки реального XML, валидации путей и построения адресов административных папок.
		private static void AddConfigurationTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("Конфигурация: первый запуск создаёт рабочий XML из шаблона", CheckShippedConfiguration));
			tests.Add(new KeyValuePair<string, Action>("Конфигурация: перенос из старой сборки и сохранение пользовательской копии", CheckConfigurationMigration));
			tests.Add(new KeyValuePair<string, Action>("Пустые, ошибочные и повторяющиеся разделы отвергаются", CheckInvalidConfiguration));
			tests.Add(new KeyValuePair<string, Action>("DTD и внешние XML-сущности запрещены", CheckExternalEntities));
			tests.Add(new KeyValuePair<string, Action>("Конфигурация: нормализация, повторы и таймаут", CheckConfigurationValidation));
			tests.Add(new KeyValuePair<string, Action>("Преобразование C:, D: и корня диска в UNC", CheckRemotePaths));
			tests.Add(new KeyValuePair<string, Action>("Пути с обходом каталогов и некорректные имена ПК отвергаются", CheckInvalidRemotePaths));
		}

		// Создаёт рабочий XML из шаблона, скопированного при сборке.
		private static void CheckShippedConfiguration()
		{
			using (var workspace = new TestWorkspace())
			{
				string applicationDirectory = Path.Combine(workspace.DirectoryPath, "app");
				Directory.CreateDirectory(applicationDirectory);
				const string templateName = "scan.config.template.xml";
				string templatePath = Path.Combine(applicationDirectory, templateName);
				File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, templateName), templatePath);
				File.SetAttributes(templatePath, FileAttributes.ReadOnly);
				string configurationPath = Path.Combine(workspace.DirectoryPath, "Telecom", "scan.config.xml");
				try
				{
					InitializeUserConfiguration(configurationPath, applicationDirectory);
					ScanConfiguration configuration = ScanConfiguration.Load(configurationPath);
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

		// Сверяет перенос путей и строки подключения и проверяет приоритет уже существующего файла.
		private static void CheckConfigurationMigration()
		{
			using (var workspace = new TestWorkspace())
			{
				string applicationDirectory = workspace.DirectoryPath;
				string previousPath = Path.Combine(applicationDirectory, "scan.config.xml");
				string settingsDirectory = Path.Combine(workspace.DirectoryPath, "Telecom");
				string configurationPath = Path.Combine(settingsDirectory, "scan.config.xml");
				Directory.CreateDirectory(settingsDirectory);
				var store = new ComputerProfileStore(Path.Combine(settingsDirectory, "computers.xml"));
				store.Save(new[] { new ComputerProfile(Guid.NewGuid(), "192.0.2.20", "Тестовый ПК", "reader", false) });
				string profilesXml = File.ReadAllText(store.FilePath);
				string previousXml = ConfigurationXml().Replace(@"C:\Certs", @"D:\Migrated")
					.Replace("</scanSettings>", "<connectionString>Server=EXAMPLE;Database=Certificates</connectionString></scanSettings>");
				File.WriteAllText(previousPath, previousXml);
				File.WriteAllText(Path.Combine(applicationDirectory, "scan.config.template.xml"), ConfigurationXml());
				InitializeUserConfiguration(configurationPath, applicationDirectory);
				Check.Equal(previousXml, File.ReadAllText(configurationPath));
				ScanConfiguration configuration = ScanConfiguration.Load(configurationPath);
				Check.Equal(@"D:\Migrated", configuration.Directories[0]);
				Check.Equal("Server=EXAMPLE;Database=Certificates", configuration.ConnectionString);

				string editedXml = previousXml.Replace(@"D:\Migrated", @"D:\Edited");
				File.WriteAllText(configurationPath, editedXml);
				InitializeUserConfiguration(configurationPath, applicationDirectory);
				Check.Equal(editedXml, File.ReadAllText(configurationPath));
				Check.Equal(previousXml, File.ReadAllText(previousPath));
				Check.Equal(profilesXml, File.ReadAllText(store.FilePath));
				Check.Equal(2, Directory.GetFiles(settingsDirectory).Length);
			}
		}

		// Вызывает код первого запуска с временными путями, чтобы не менять конфигурацию оператора.
		private static void InitializeUserConfiguration(string path, string applicationDirectory)
		{
			MethodInfo initialize = typeof(MainForm).GetMethod("InitializeUserConfiguration", BindingFlags.Static | BindingFlags.NonPublic);
			initialize.Invoke(null, new object[] { path, applicationDirectory });
		}

		// Проверяет сообщения об отсутствующей конфигурации, синтаксической ошибке и неверной структуре.
		private static void CheckInvalidConfiguration()
		{
			using (var workspace = new TestWorkspace())
			{
				string path = Path.Combine(workspace.DirectoryPath, "settings.xml");
				Check.Throws<FileNotFoundException>(() => ScanConfiguration.Load(path));
				File.WriteAllText(path, "<scanSettings>");
				Check.Throws<XmlException>(() => ScanConfiguration.Load(path));
				File.WriteAllText(path, "<wrongRoot />");
				Check.Throws<InvalidDataException>(() => ScanConfiguration.Load(path));
				File.WriteAllText(path, "<scanSettings><directories /></scanSettings>");
				Check.Throws<InvalidDataException>(() => ScanConfiguration.Load(path));
				File.WriteAllText(path, ConfigurationXml().Replace("</scanSettings>", "<directories><directory>C:\\Extra</directory></directories></scanSettings>"));
				Check.Throws<InvalidDataException>(() => ScanConfiguration.Load(path));
				File.WriteAllText(path, ConfigurationXml().Replace("<directory>", "<direcotry>").Replace("</directory>", "</direcotry>"));
				Check.Throws<InvalidDataException>(() => ScanConfiguration.Load(path));
				File.WriteAllText(path, ConfigurationXml().Replace("connectionTimeoutSeconds", "connectionTimoutSeconds"));
				Check.Throws<InvalidDataException>(() => ScanConfiguration.Load(path));
				File.WriteAllText(path, ConfigurationXml().Replace("<directory>", "<directory recurse=\"false\">"));
				Check.Throws<InvalidDataException>(() => ScanConfiguration.Load(path));
				File.WriteAllText(path, ConfigurationXml().Replace("</scanSettings>", "<connectionString>first</connectionString><connectionString>second</connectionString></scanSettings>"));
				Check.Throws<InvalidDataException>(() => ScanConfiguration.Load(path));
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
				Check.Throws<XmlException>(() => ScanConfiguration.Load(path));
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
