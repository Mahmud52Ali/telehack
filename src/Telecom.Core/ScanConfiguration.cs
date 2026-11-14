using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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

		// Читает конфигурацию без разрешения DTD и внешних XML-сущностей.
		public static ScanConfiguration Load(string filePath)
		{
			var settings = new XmlReaderSettings
			{
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null,
				MaxCharactersInDocument = 1024 * 1024
			};
			using (XmlReader reader = XmlReader.Create(filePath, settings))
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

		// Нормализует Windows-пути и удаляет повторы без учёта регистра.
		private static IList<string> NormalizePaths(IEnumerable<string> paths, bool allowWildcard)
		{
			var normalized = new List<string>();
			var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (paths != null)
			{
				foreach (string path in paths)
				{
					string value = RemotePath.NormalizeLocalPath(path, allowWildcard);
					if (unique.Add(value))
					{
						normalized.Add(value);
					}
				}
			}

			if (normalized.Count == 0)
			{
				throw new InvalidDataException("Список путей конфигурации не должен быть пустым.");
			}

			return new ReadOnlyCollection<string>(normalized);
		}
	}
}
