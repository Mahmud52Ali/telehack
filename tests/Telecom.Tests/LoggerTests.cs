using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Telecom.Core;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		// Регистрирует проверки файла журнала, параллельной записи и диагностики исключений.
		private static void AddLoggerTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("Параллельный журнал: 100 полных строк с миллисекундами", CheckConcurrentLogging));
			tests.Add(new KeyValuePair<string, Action>("Исключение фоновой задачи: тип, сообщение и стек в файле", CheckExceptionLogging));
			tests.Add(new KeyValuePair<string, Action>("Недоступный журнал не скрывает ошибку записи", CheckUnavailableLog));
		}

		// Проверяет отсутствие потерь и смешивания строк при одновременных обращениях потоков.
		private static void CheckConcurrentLogging()
		{
			using (var workspace = new TestWorkspace())
			{
				string logPath = Path.Combine(workspace.DirectoryPath, "parallel.log");
				var logger = new FileLogger(logPath);
				Parallel.For(0, 100, index => logger.Info("event-" + index));
				string[] lines = File.ReadAllLines(logPath);
				Check.Equal(100, lines.Length);
				var messages = new HashSet<string>();
				foreach (string line in lines)
				{
					CheckTimestamp(line);
					messages.Add(line.Substring(24));
				}

				Check.Equal(100, messages.Count);
				for (int index = 0; index < 100; index++)
				{
					Check.Equal(true, messages.Contains("event-" + index));
				}
			}
		}

		// Проверяет сохранение настоящего стека исключения, возникшего в другом потоке.
		private static void CheckExceptionLogging()
		{
			using (var workspace = new TestWorkspace())
			{
				string logPath = Path.Combine(workspace.DirectoryPath, "exception.log");
				var logger = new FileLogger(logPath);
				try
				{
					Task.Run((Action)ThrowBackgroundFailure).GetAwaiter().GetResult();
				}
				catch (InvalidOperationException exception)
				{
					logger.Error("Ошибка фоновой задачи", exception);
				}

				string text = File.ReadAllText(logPath);
				Check.Equal(true, text.Contains("System.InvalidOperationException"));
				Check.Equal(true, text.Contains("background-probe"));
				Check.Equal(true, text.Contains("ThrowBackgroundFailure"));
				foreach (string line in File.ReadAllLines(logPath))
				{
					CheckTimestamp(line);
				}
			}
		}

		// Проверяет, что невозможность записать журнал явно возвращается вызывающему коду.
		private static void CheckUnavailableLog()
		{
			using (var workspace = new TestWorkspace())
			{
				var logger = new FileLogger(Path.Combine(workspace.DirectoryPath, "missing", "test.log"));
				Check.Throws<DirectoryNotFoundException>(() => logger.Info("test"));
			}
		}

		// Создаёт исключение в именованном методе, чтобы проверить наличие стека в журнале.
		private static void ThrowBackgroundFailure()
		{
			throw new InvalidOperationException("background-probe");
		}

		// Проверяет формат времени на каждой строке, включая продолжение стека исключения.
		private static void CheckTimestamp(string line)
		{
			Check.Equal(true, Regex.IsMatch(line, @"^\d{2}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2}\.\d{3} - "));
		}
	}
}
