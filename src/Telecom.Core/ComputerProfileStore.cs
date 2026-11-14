using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Telecom.Core
{
	public sealed class ComputerProfileStore
	{
		public string FilePath { get; private set; }

		// Принимает путь к локальным настройкам пользователя, не связанный с каталогом программы или Git.
		public ComputerProfileStore(string filePath)
		{
			FilePath = Path.GetFullPath(filePath);
		}

		// Читает только метаданные ПК; повреждённый файл не заменяется пустым списком.
		public List<ComputerProfile> Load()
		{
			XDocument document;
			try
			{
				var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
				using (var stream = File.OpenRead(FilePath))
				using (var reader = XmlReader.Create(stream, settings))
				{
					document = XDocument.Load(reader);
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

			string version = document.Root == null ? null : (string)document.Root.Attribute("version");
			if (document.Root == null || document.Root.Name != "computers" || (version != "1" && version != "2" && version != "3")
				|| document.Root.Attributes().Any(attribute => attribute.Name != "version"))
			{
				throw new InvalidDataException("Ожидался список компьютеров версии 1, 2 или 3.");
			}

			var profiles = new List<ComputerProfile>();
			foreach (XElement element in document.Root.Elements())
			{
				Guid id;
				bool selected;
				string addressAttribute = version == "1" ? "address" : "ipAddress";
				if (element.Name != "computer" || element.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value))
					|| element.Elements().Any(directory => version != "3" || directory.Name != "additionalDirectory"
						|| directory.HasAttributes || directory.HasElements)
					|| element.Attributes().Any(attribute => attribute.Name != "id" && attribute.Name != addressAttribute
						&& (version == "1" || attribute.Name != "name") && attribute.Name != "userName" && attribute.Name != "selected")
					|| !Guid.TryParse((string)element.Attribute("id"), out id)
					|| !bool.TryParse((string)element.Attribute("selected"), out selected))
				{
					throw new InvalidDataException("Некорректная запись в списке компьютеров.");
				}

				string address = (string)element.Attribute(addressAttribute);
				string userName = (string)element.Attribute("userName");
				profiles.Add(version == "1" ? ComputerProfile.FromLegacy(id, address, userName, selected)
					: ComputerProfile.FromStored(id, address, (string)element.Attribute("name"), userName, selected,
						element.Elements("additionalDirectory").Select(directory => directory.Value)));
			}

			Validate(profiles);
			return profiles;
		}

		// Проверяет уникальность до записи и атомарно заменяет существующий файл настроек.
		public void Save(IEnumerable<ComputerProfile> profiles)
		{
			var items = new List<ComputerProfile>(profiles);
			Validate(items);
			var root = new XElement("computers", new XAttribute("version", "3"));
			foreach (ComputerProfile profile in items)
			{
				root.Add(new XElement("computer", new XAttribute("id", profile.Id), new XAttribute("ipAddress", profile.IpAddress),
					new XAttribute("name", profile.DisplayName), new XAttribute("userName", profile.UserName), new XAttribute("selected", profile.Selected),
					profile.AdditionalDirectories.Select(path => new XElement("additionalDirectory", path))));
			}

			Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
			string temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				new XDocument(root).Save(temporaryPath);
				if (File.Exists(FilePath))
				{
					File.Replace(temporaryPath, FilePath, null);
				}
				else
				{
					File.Move(temporaryPath, FilePath);
				}
			}
			finally
			{
				if (File.Exists(temporaryPath))
				{
					File.Delete(temporaryPath);
				}
			}
		}

		// IPv4 уникальны; несколько старых записей могут ждать заполнения адреса, а подписи могут совпадать.
		public static void Validate(IEnumerable<ComputerProfile> profiles)
		{
			var computers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var identifiers = new HashSet<Guid>();
			foreach (ComputerProfile profile in profiles)
			{
				if (profile == null || (profile.HasIpAddress && !computers.Add(profile.IpAddress)) || !identifiers.Add(profile.Id))
				{
					throw new InvalidDataException("Список содержит повторяющийся компьютер или идентификатор.");
				}
			}
		}
	}
}
