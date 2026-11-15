using System;
using System.Globalization;
using System.IO;
using System.Xml;
using Telecom.Core.Common;

namespace Telecom.App
{
	internal enum AppearanceTheme
	{
		Light,
		Dark,
		System
	}

	internal sealed class AppearanceSettings
	{
		internal const int MinFontPercent = 80;
		internal const int MaxFontPercent = 150;
		internal const int FontPercentStep = 10;
		internal static readonly AppearanceSettings Default = new AppearanceSettings(AppearanceTheme.Light, 100);

		internal AppearanceTheme Theme { get; private set; }
		internal int FontPercent { get; private set; }

		// Хранит выбранную тему и множитель базового размера шрифта.
		internal AppearanceSettings(AppearanceTheme theme, int fontPercent)
		{
			Theme = theme;
			FontPercent = fontPercent;
		}

		// Читает пользовательские параметры. В новой установке используется светлая тема и масштаб 100%.
		internal static AppearanceSettings Load(string path)
		{
			var document = new XmlDocument();
			try
			{
				using (FileStream stream = File.OpenRead(path))
				using (XmlReader reader = SafeXml.CreateReader(stream, 4096))
				{
					document.Load(reader);
				}
			}
			catch (FileNotFoundException)
			{
				return Default;
			}
			catch (DirectoryNotFoundException)
			{
				return Default;
			}

			XmlElement root = document.DocumentElement;
			AppearanceTheme theme;
			int fontPercent;
			if (root == null || root.Name != "appearance"
				|| !Enum.TryParse(root.GetAttribute("theme"), out theme) || !Enum.IsDefined(typeof(AppearanceTheme), theme)
				|| !int.TryParse(root.GetAttribute("fontPercent"), NumberStyles.None, CultureInfo.InvariantCulture, out fontPercent)
				|| fontPercent < MinFontPercent || fontPercent > MaxFontPercent || fontPercent % FontPercentStep != 0)
			{
				throw new InvalidDataException("Неверная тема или масштаб шрифта в appearance.xml.");
			}

			return new AppearanceSettings(theme, fontPercent);
		}

		// Атомарно сохраняет оформление отдельно от рабочей конфигурации сканирования.
		internal void Save(string path)
		{
			AtomicFile.Write(path, temporaryPath =>
			{
				using (XmlWriter writer = XmlWriter.Create(temporaryPath, new XmlWriterSettings { Indent = true }))
				{
					writer.WriteStartElement("appearance");
					writer.WriteAttributeString("theme", Theme.ToString());
					writer.WriteAttributeString("fontPercent", FontPercent.ToString(CultureInfo.InvariantCulture));
					writer.WriteEndElement();
				}
			});
		}
	}
}
