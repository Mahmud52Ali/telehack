using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Telecom.Core.Common;

namespace Telecom.Core.Computers
{
	public sealed class ComputerListFile
	{
		private static readonly string[] RemoteAttributes = { "id", "ipAddress", "name", "userName", "selected" };

		public string FilePath { get; private set; }

		// Запоминает полный путь к файлу списка ПК.
		public ComputerListFile(string filePath)
		{
			FilePath = Path.GetFullPath(filePath);
		}

		// Читает только метаданные ПК; повреждённый файл не заменяется пустым списком.
		public List<ComputerProfile> Load()
		{
			XDocument document;
			try
			{
				using (FileStream stream = File.OpenRead(FilePath))
				{
					document = SafeXml.Load(stream, 1024 * 1024);
				}
			}
			catch (FileNotFoundException)
			{
				return new List<ComputerProfile>();
			}
			catch (DirectoryNotFoundException)
			{
				return new List<ComputerProfile>();
			}

			XElement root = document.Root;
			if (root == null || root.Name != "computers" || (string)root.Attribute("version") != "4"
				|| root.Attributes().Any(attribute => attribute.Name != "version"))
			{
				throw new InvalidDataException("Ожидался список компьютеров версии 4.");
			}

			var profiles = new List<ComputerProfile>();
			foreach (XElement element in root.Elements())
			{
				profiles.Add(element.Name == "localComputer" ? ReadLocalComputer(element) : ReadRemoteComputer(element));
			}

			Validate(profiles);
			return profiles;
		}

		// Читает строку «Этот компьютер»: только галочка и дополнительные пути.
		private static ComputerProfile ReadLocalComputer(XElement element)
		{
			bool selected;
			if (element.Attributes().Any(attribute => attribute.Name != "selected") || !HasOnlyDirectories(element)
				|| !bool.TryParse((string)element.Attribute("selected"), out selected))
			{
				throw new InvalidDataException("Некорректные настройки локального компьютера.");
			}

			return ComputerProfile.CreateLocal(selected, ReadDirectories(element));
		}

		// Читает удалённый ПК: идентификатор, IPv4, подпись, логин, галочку и дополнительные пути.
		private static ComputerProfile ReadRemoteComputer(XElement element)
		{
			Guid id;
			bool selected;
			if (element.Name != "computer" || !HasOnlyDirectories(element)
				|| element.Attributes().Any(attribute => !RemoteAttributes.Contains(attribute.Name.LocalName))
				|| !Guid.TryParse((string)element.Attribute("id"), out id)
				|| !bool.TryParse((string)element.Attribute("selected"), out selected))
			{
				throw new InvalidDataException("Некорректная запись в списке компьютеров.");
			}

			return new ComputerProfile(id, (string)element.Attribute("ipAddress"), (string)element.Attribute("name"),
				(string)element.Attribute("userName"), selected, ReadDirectories(element));
		}

		// Проверяет, что внутри записи нет текста и других элементов, кроме additionalDirectory без атрибутов.
		private static bool HasOnlyDirectories(XElement element)
		{
			return !element.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value))
				&& !element.Elements().Any(child => child.Name != "additionalDirectory" || child.HasAttributes || child.HasElements);
		}

		// Возвращает дополнительные пути записи в порядке следования.
		private static IEnumerable<string> ReadDirectories(XElement element)
		{
			return element.Elements("additionalDirectory").Select(directory => directory.Value);
		}

		// Проверяет уникальность до записи и атомарно заменяет существующий файл настроек.
		public void Save(IEnumerable<ComputerProfile> profiles)
		{
			var items = new List<ComputerProfile>(profiles);
			Validate(items);
			var root = new XElement("computers", new XAttribute("version", "4"));
			foreach (ComputerProfile profile in items.OrderBy(item => !item.IsLocal))
			{
				if (profile.IsLocal)
				{
					root.Add(new XElement("localComputer", new XAttribute("selected", profile.Selected),
						profile.AdditionalDirectories.Select(path => new XElement("additionalDirectory", path))));
					continue;
				}

				root.Add(new XElement("computer", new XAttribute("id", profile.Id), new XAttribute("ipAddress", profile.IpAddress),
					new XAttribute("name", profile.DisplayName), new XAttribute("userName", profile.UserName),
					new XAttribute("selected", profile.Selected),
					profile.AdditionalDirectories.Select(path => new XElement("additionalDirectory", path))));
			}

			AtomicFile.Write(FilePath, temporaryPath => new XDocument(root).Save(temporaryPath));
		}

		// IPv4 и идентификаторы уникальны; подписи могут совпадать.
		public static void Validate(IEnumerable<ComputerProfile> profiles)
		{
			var computers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var identifiers = new HashSet<Guid>();
			foreach (ComputerProfile profile in profiles)
			{
				if (profile == null || (!profile.IsLocal && !computers.Add(profile.IpAddress)) || !identifiers.Add(profile.Id))
				{
					throw new InvalidDataException("Список содержит повторяющийся компьютер или идентификатор.");
				}
			}
		}
	}
}
