using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Telecom.Core.Logging
{
	public sealed class FileLogger : ILogger
	{
		private static readonly Encoding LogEncoding = new UTF8Encoding(false);
		private readonly string filePath;
		private readonly string errorFilePath;
		private readonly object writeLock = new object();

		// Задаёт полный журнал; его каталог должен существовать и быть доступен для записи.
		public FileLogger(string filePath)
		{
			this.filePath = Path.GetFullPath(filePath);
		}

		// Задаёт отдельный файл для копий ошибок.
		public FileLogger(string filePath, string errorFilePath) : this(filePath)
		{
			string fullErrorFilePath = Path.GetFullPath(errorFilePath);
			if (!string.Equals(this.filePath, fullErrorFilePath, StringComparison.OrdinalIgnoreCase))
			{
				this.errorFilePath = fullErrorFilePath;
			}
		}

		// Сохраняет обычное событие с датой и временем до миллисекунд.
		public void Info(string message)
		{
			Write(message, null);
		}

		// Сохраняет ошибку с полным текстом исключения в общем журнале и журнале ошибок.
		public void Error(string message, Exception exception)
		{
			Write(message + Environment.NewLine + exception, errorFilePath);
		}

		// Формирует запись с временем на каждой строке и записывает её под общей блокировкой.
		private void Write(string message, string copyFilePath)
		{
			lock (writeLock)
			{
				string timestamp = DateTime.Now.ToString("dd.MM.yy HH:mm:ss.fff", CultureInfo.InvariantCulture);
				var entry = new StringBuilder();
				using (var reader = new StringReader(message ?? string.Empty))
				{
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						entry.Append(timestamp).Append(" - ").AppendLine(line);
					}
				}

				string text = entry.ToString();
				try
				{
					File.AppendAllText(filePath, text, LogEncoding);
				}
				finally
				{
					// Пытаемся сохранить копию ошибки даже при недоступности полного журнала.
					if (copyFilePath != null)
					{
						File.AppendAllText(copyFilePath, text, LogEncoding);
					}
				}
			}
		}
	}
}
