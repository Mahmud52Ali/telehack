using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Telecom.Core.Common
{
	public static class SafeXml
	{
		// Создаёт читатель XML без DTD и внешних сущностей; лимит защищает от случайно выбранного огромного файла.
		public static XmlReader CreateReader(Stream stream, int maxCharacters)
		{
			var settings = new XmlReaderSettings
			{
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null,
				MaxCharactersInDocument = maxCharacters
			};
			return XmlReader.Create(stream, settings);
		}

		// Читает XML-документ из потока по тем же безопасным правилам.
		public static XDocument Load(Stream stream, int maxCharacters)
		{
			using (XmlReader reader = CreateReader(stream, maxCharacters))
			{
				return XDocument.Load(reader);
			}
		}
	}
}
