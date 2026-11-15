using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Telecom.Core
{
	public sealed class ScanConfiguration
	{
		public IList<string> Directories { get; private set; }
		public IList<string> CryptoProExecutables { get; private set; }
		public int ConnectionTimeoutSeconds { get; private set; }
		public string ConnectionString { get; private set; }

		// Проверяет настройки независимо от источника: XML, тест или другой интерфейс.
		public ScanConfiguration(IEnumerable<string> directories, IEnumerable<string> cryptoProExecutables,
			int connectionTimeoutSeconds, string connectionString)
		{
			Directories = NormalizePaths(directories, true);
			CryptoProExecutables = NormalizePaths(cryptoProExecutables, false);
			foreach (string executable in CryptoProExecutables)
			{
				if (!executable.EndsWith("\\csptest.exe", StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("Для определения версии КриптоПро укажите путь к csptest.exe.");
				}
			}

			if (connectionTimeoutSeconds < 1 || connectionTimeoutSeconds > 60)
			{
				throw new InvalidDataException("Таймаут подключения должен быть от 1 до 60 секунд.");
			}

			ConnectionTimeoutSeconds = connectionTimeoutSeconds;
			ConnectionString = connectionString ?? string.Empty;
		}

		// Создаёт настройки для одного ПК, добавляя его пути к общему списку.
		public ScanConfiguration WithAdditionalDirectories(IEnumerable<string> additionalDirectories)
		{
			return new ScanConfiguration(Directories.Concat(additionalDirectories), CryptoProExecutables,
				ConnectionTimeoutSeconds, ConnectionString);
		}

		// Записывает настройки в том же формате, который читает сканер.
		public void Save(string filePath)
		{
			var root = new XElement("scanSettings",
				new XAttribute("connectionTimeoutSeconds", ConnectionTimeoutSeconds),
				new XElement("directories", Directories.Select(path => new XElement("directory", path))),
				new XElement("cryptoProExecutables", CryptoProExecutables.Select(path => new XElement("path", path))),
				new XElement("connectionString", ConnectionString));
			WriteFile(filePath, temporaryPath =>
			{
				new XDocument(root).Save(temporaryPath);
				return Load(temporaryPath);
			}, true);
		}

		// Проверяет копию выбранного XML и переносит её в рабочий файл вместе с исходным оформлением.
		public static ScanConfiguration Import(string sourcePath, string filePath)
		{
			return CopyConfiguration(sourcePath, filePath, true);
		}

		// Сохраняет точную копию рабочего XML, которую можно загрузить обратно.
		public static void SaveCopy(string sourcePath, string copyPath)
		{
			if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(copyPath), StringComparison.OrdinalIgnoreCase))
			{
				throw new IOException("Выберите другое место или имя для копии конфигурации.");
			}

			CopyConfiguration(sourcePath, copyPath, true);
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
				CopyConfiguration(templatePath, filePath, false);
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
		private static ScanConfiguration CopyConfiguration(string sourcePath, string filePath, bool replaceExisting)
		{
			return WriteFile(filePath, temporaryPath =>
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

		// Заменяет рабочий файл атомарно, после проверки XML и строки подключения.
		private static ScanConfiguration WriteFile(string filePath, Func<string, ScanConfiguration> stage, bool replaceExisting)
		{
			filePath = Path.GetFullPath(filePath);
			Directory.CreateDirectory(Path.GetDirectoryName(filePath));
			string temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				ScanConfiguration configuration = stage(temporaryPath);
				if (configuration.ConnectionString.Length > 0)
				{
					CertificateRepository.ParseConnectionString(configuration.ConnectionString);
				}

				if (replaceExisting && File.Exists(filePath))
				{
					File.Replace(temporaryPath, filePath, null);
				}
				else
				{
					File.Move(temporaryPath, filePath);
				}

				return configuration;
			}
			finally
			{
				if (File.Exists(temporaryPath))
				{
					File.Delete(temporaryPath);
				}
			}
		}

		// Открывает локальный или выбранный пользователем сетевой XML как файл.
		public static ScanConfiguration Load(string filePath)
		{
			using (FileStream stream = File.OpenRead(filePath))
			{
				return Read(stream);
			}
		}

		// Читает конфигурацию без разрешения DTD и внешних XML-сущностей.
		private static ScanConfiguration Read(Stream stream)
		{
			var settings = new XmlReaderSettings
			{
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null,
				MaxCharactersInDocument = 1024 * 1024
			};
			using (XmlReader reader = XmlReader.Create(stream, settings))
			{
				XElement root = XDocument.Load(reader).Root;
				if (root == null || root.Name != "scanSettings")
				{
					throw new InvalidDataException("Корневой элемент конфигурации должен называться scanSettings.");
				}

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

				int connections = 0;
				foreach (XElement connectionElement in root.Elements("connectionString"))
				{
					connections++;
					if (connections > 1 || connectionElement.HasElements || connectionElement.HasAttributes)
					{
						throw new InvalidDataException("Допускается один текстовый элемент connectionString.");
					}
				}

				int timeout = 3;
				XAttribute timeoutAttribute = root.Attribute("connectionTimeoutSeconds");
				if (timeoutAttribute != null && !int.TryParse(timeoutAttribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout))
				{
					throw new InvalidDataException("Таймаут подключения должен быть целым числом.");
				}

				XElement connection = root.Element("connectionString");
				return new ScanConfiguration(ReadSection(root, "directories", "directory"),
					ReadSection(root, "cryptoProExecutables", "path"), timeout, connection == null ? string.Empty : connection.Value.Trim());
			}
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

		// Требует хотя бы один нормализованный путь в каждом списке общей конфигурации.
		private static IList<string> NormalizePaths(IEnumerable<string> paths, bool allowWildcard)
		{
			IList<string> normalized = RemotePath.NormalizeLocalPaths(paths ?? Enumerable.Empty<string>(), allowWildcard);
			if (normalized.Count == 0)
			{
				throw new InvalidDataException("Список путей конфигурации не должен быть пустым.");
			}

			return normalized;
		}
	}
}
