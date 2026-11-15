using System;
using System.Diagnostics;
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

		// Последняя ошибка записи журнала (null, если запись удалась).
		// Журнал не бросает исключения: недоступный файл не должен останавливать сканирование.
		public Exception LastWriteError { get; private set; }

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

		// Ставит время перед каждой строкой записи и дописывает её в файлы под общей блокировкой.
		private void Write(string message, string copyFilePath)
		{
			lock (writeLock)
			{
				string text = FormatEntry(DateTime.Now, message);
				LastWriteError = null;
				Append(filePath, text);
				if (copyFilePath != null)
				{
					Append(copyFilePath, text);
				}
			}
		}

		// Формирует строки вида «16.09.25 08:43:23.739 - текст»; пустое сообщение даёт одну строку со временем.
		private static string FormatEntry(DateTime time, string message)
		{
			string timestamp = time.ToString("dd.MM.yy HH:mm:ss.fff", CultureInfo.InvariantCulture);
			var entry = new StringBuilder();
			using (var reader = new StringReader(message ?? string.Empty))
			{
				string line;
				while ((line = reader.ReadLine()) != null)
				{
					entry.Append(timestamp).Append(" - ").AppendLine(line);
				}
			}

			if (entry.Length == 0)
			{
				entry.Append(timestamp).Append(" - ").AppendLine();
			}

			return entry.ToString();
		}

		// Дописывает текст в один файл; сбой запоминается и уходит в отладочный вывод.
		private void Append(string path, string text)
		{
			try
			{
				File.AppendAllText(path, text, LogEncoding);
			}
			catch (Exception exception)
			{
				if (!(exception is IOException) && !(exception is UnauthorizedAccessException)
					&& !(exception is System.Security.SecurityException) && !(exception is NotSupportedException))
				{
					throw;
				}

				LastWriteError = exception;
				Trace.WriteLine("Не удалось записать журнал " + path + ": " + exception.Message);
			}
		}
	}
}
