using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Telecom.Core.Common;
using Telecom.Core.Database;

namespace Telecom.Core.Configuration
{
	// Чтение и запись scan.config.xml. Формат:
	// <scanSettings connectionTimeoutSeconds="3">
	//   <directories><directory>C:\...</directory></directories>
	//   <cryptoProExecutables><path>C:\...\csptest.exe</path></cryptoProExecutables>
	//   <connectionString>...</connectionString>
	// </scanSettings>
	public static class ScanConfigurationFile
	{
		private const int MaxFileCharacters = 1024 * 1024;
		private const int DefaultTimeoutSeconds = 3;

		// Открывает локальный или выбранный пользователем сетевой XML как файл.
		public static ScanConfiguration Load(string filePath)
		{
			using (FileStream stream = File.OpenRead(filePath))
			{
				return Read(stream);
			}
		}

		// Записывает настройки в том же формате, который читает сканер.
		public static void Save(ScanConfiguration configuration, string filePath)
		{
			var root = new XElement("scanSettings",
				new XAttribute("connectionTimeoutSeconds", configuration.ConnectionTimeoutSeconds),
				new XElement("directories", configuration.Directories.Select(path => new XElement("directory", path))),
				new XElement("cryptoProExecutables", configuration.CryptoProExecutables.Select(path => new XElement("path", path))),
				new XElement("connectionString", configuration.ConnectionString));
			WriteValidated(filePath, temporaryPath =>
			{
				new XDocument(root).Save(temporaryPath);
				return Load(temporaryPath);
			}, true);
		}

		// Проверяет копию выбранного XML и переносит её в рабочий файл вместе с исходным оформлением.
		public static ScanConfiguration Import(string sourcePath, string filePath)
		{
			return CopyValidated(sourcePath, filePath, true);
		}

		// Сохраняет точную копию рабочего XML, которую можно загрузить обратно.
		public static void SaveCopy(string sourcePath, string copyPath)
		{
			if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(copyPath), StringComparison.OrdinalIgnoreCase))
			{
				throw new IOException("Выберите другое место или имя для копии конфигурации.");
			}

			CopyValidated(sourcePath, copyPath, true);
		}

		// Создаёт отсутствующий рабочий файл из шаблона. Другой экземпляр мог успеть создать его первым.
		public static bool CreateFromTemplate(string filePath, string templatePath)
		{
			if (File.Exists(filePath))
			{
				return false;
			}

			try
			{
				CopyValidated(templatePath, filePath, false);
				return true;
			}
			catch (IOException)
			{
				if (!File.Exists(filePath))
				{
					throw;
				}

				return false;
			}
		}

		// Читает и копирует один снимок XML, сохраняя кодировку и комментарии.
		private static ScanConfiguration CopyValidated(string sourcePath, string filePath, bool replaceExisting)
		{
			return WriteValidated(filePath, temporaryPath =>
			{
				using (FileStream source = File.OpenRead(sourcePath))
				{
					ScanConfiguration configuration = Read(source);
					source.Position = 0;
					using (FileStream target = File.Create(temporaryPath))
					{
						source.CopyTo(target);
					}

					return configuration;
				}
			}, replaceExisting);
		}

		// Заменяет файл только после того, как временная копия прочитана и строка подключения проверена.
		private static ScanConfiguration WriteValidated(string filePath, Func<string, ScanConfiguration> writeTemporary, bool replaceExisting)
		{
			ScanConfiguration configuration = null;
			AtomicFile.Write(filePath, temporaryPath =>
			{
				configuration = writeTemporary(temporaryPath);
				if (configuration.ConnectionString.Length > 0)
				{
					CertificateRepository.ParseConnectionString(configuration.ConnectionString);
				}
			}, replaceExisting);
			return configuration;
		}

		// Разбирает XML и не допускает неизвестных разделов, чтобы опечатка не проходила незамеченной.
		private static ScanConfiguration Read(Stream stream)
		{
			XElement root = SafeXml.Load(stream, MaxFileCharacters).Root;
			if (root == null || root.Name != "scanSettings")
			{
				throw new InvalidDataException("Корневой элемент конфигурации должен называться scanSettings.");
			}

			CheckKnownNames(root);
			string connectionString = ReadConnectionString(root);
			int timeout = ReadTimeout(root);
			return new ScanConfiguration(ReadSection(root, "directories", "directory"),
				ReadSection(root, "cryptoProExecutables", "path"), timeout, connectionString);
		}

		// Проверяет, что в корне есть только известные разделы и атрибуты.
		private static void CheckKnownNames(XElement root)
		{
			foreach (XElement element in root.Elements())
			{
				if (element.Name != "directories" && element.Name != "cryptoProExecutables" && element.Name != "connectionString")
				{
					throw new InvalidDataException("В конфигурации найден неизвестный раздел.");
				}
			}

			foreach (XAttribute attribute in root.Attributes())
			{
				if (attribute.Name != "connectionTimeoutSeconds")
				{
					throw new InvalidDataException("В конфигурации найден неизвестный параметр.");
				}
			}
		}

		// Читает таймаут подключения; без атрибута используется значение по умолчанию.
		private static int ReadTimeout(XElement root)
		{
			int timeout = DefaultTimeoutSeconds;
			XAttribute timeoutAttribute = root.Attribute("connectionTimeoutSeconds");
			if (timeoutAttribute != null
				&& !int.TryParse(timeoutAttribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout))
			{
				throw new InvalidDataException("Таймаут подключения должен быть целым числом.");
			}

			return timeout;
		}

		// Читает необязательную строку подключения; допускается не больше одного текстового элемента.
		private static string ReadConnectionString(XElement root)
		{
			List<XElement> elements = root.Elements("connectionString").ToList();
			if (elements.Count > 1 || elements.Any(element => element.HasElements || element.HasAttributes))
			{
				throw new InvalidDataException("Допускается один текстовый элемент connectionString.");
			}

			return elements.Count == 0 ? string.Empty : elements[0].Value.Trim();
		}

		// Не допускает пустых списков, повторяющихся разделов и незаметно пропущенных опечаток.
		private static IEnumerable<string> ReadSection(XElement root, string sectionName, string itemName)
		{
			var values = new List<string>();
			int sections = 0;
			foreach (XElement section in root.Elements(sectionName))
			{
				sections++;
				if (section.HasAttributes)
				{
					throw new InvalidDataException("В разделе " + sectionName + " не должно быть атрибутов.");
				}

				foreach (XElement item in section.Elements())
				{
					if (item.Name != itemName || item.HasElements || item.HasAttributes)
					{
						throw new InvalidDataException("Неверная структура раздела " + sectionName + ".");
					}

					values.Add(item.Value);
				}
			}

			if (sections != 1 || values.Count == 0)
			{
				throw new InvalidDataException("Нужен один непустой раздел " + sectionName + ".");
			}

			return values;
		}
	}
}
