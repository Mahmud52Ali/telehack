using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Telecom.Core
{
	public sealed class FileLogger : ILogger
	{
		private readonly string filePath;
		private readonly object writeLock = new object();

		// Задаёт файл журнала; каталог должен существовать и быть доступен для записи.
		public FileLogger(string filePath)
		{
			this.filePath = Path.GetFullPath(filePath);
		}

		// Сохраняет обычное событие с датой и временем до миллисекунд.
		public void Info(string message)
		{
			Write(message);
		}

		// Сохраняет полный текст исключения, включая вложенные исключения и стек.
		public void Error(string message, Exception exception)
		{
			Write(message + Environment.NewLine + exception);
		}

		// Сериализует обращения потоков и снабжает временем каждую физическую строку журнала.
		private void Write(string message)
		{
			lock (writeLock)
			{
				string timestamp = DateTime.Now.ToString("dd.MM.yy HH:mm:ss.fff", CultureInfo.InvariantCulture);
				using (var writer = new StreamWriter(filePath, true, new UTF8Encoding(false)))
				using (var reader = new StringReader(message ?? string.Empty))
				{
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						writer.WriteLine(timestamp + " - " + line);
					}
				}
			}
		}
	}
}
