using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Telecom.App;
using Telecom.Core;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		// Регистрирует проверки файла журнала, параллельной записи и диагностики исключений.
		private static void AddLoggerTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			tests.Add(new KeyValuePair<string, Action>("Параллельный журнал: 100 полных строк с миллисекундами", CheckConcurrentLogging));
			tests.Add(new KeyValuePair<string, Action>("Полный журнал и копия ошибок с одинаковым временем и стеком", CheckExceptionLogging));
			tests.Add(new KeyValuePair<string, Action>("Недоступный журнал не скрывает ошибку записи", CheckUnavailableLog));
			tests.Add(new KeyValuePair<string, Action>("Ошибка проверки MSSQL попадает в журнал и интерфейс", () => CheckDatabaseFailureLogging(false)));
			tests.Add(new KeyValuePair<string, Action>("Ошибка проверки MSSQL попадает в журнал после уничтожения панели", () => CheckDatabaseFailureLogging(true)));
		}

		// Моделирует таймаут MSSQL локальным TCP-портом без SQL Server и проверяет позднюю ошибку панели.
		private static void CheckDatabaseFailureLogging(bool disposeBeforeCompletion)
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			try
			{
				using (var workspace = new TestWorkspace())
				{
					var logger = new MemoryLogger();
					using (var form = new MainForm(logger,
						new ComputerProfileStore(Path.Combine(workspace.DirectoryPath, "computers.xml")), new WindowsFileSystem(), null))
					{
						Control panel = FormField<Control>(form, "settingsPanel");
						panel.CreateControl();
						int port = ((IPEndPoint)listener.LocalEndpoint).Port;
						FormField<TextBox>(panel, "connectionInput").Text = "Server=tcp:127.0.0.1," + port
							+ ";Database=TelecomLoggingTests;User ID=telecom_logging_test;Password=dummy-password;Connect Timeout=1";
						Label status = FormField<Label>(panel, "databaseStatus");
						panel.GetType().GetMethod("OnCheckDatabase", BindingFlags.Instance | BindingFlags.NonPublic)
							.Invoke(panel, new object[] { null, EventArgs.Empty });
						Check.Equal(0, logger.Errors.Count);
						Check.Equal(false, FormField<Button>(panel, "checkDatabase").Enabled);
						string pendingStatus = status.Text;
						if (disposeBeforeCompletion)
						{
							panel.Dispose();
						}

						var timer = Stopwatch.StartNew();
						while (logger.Errors.Count == 0 && timer.Elapsed < TimeSpan.FromSeconds(10))
						{
							Application.DoEvents();
							Thread.Sleep(1);
						}

						Check.Equal(1, logger.Errors.Count);
						Check.Equal(true, logger.Errors[0] is System.Data.SqlClient.SqlException);
						if (disposeBeforeCompletion)
						{
							Check.Equal(pendingStatus, status.Text);
						}
						else
						{
							Check.Equal(true, status.Text.Contains("Не удалось подключиться к MSSQL."));
							Check.Equal(true, FormField<Button>(panel, "checkDatabase").Enabled);
						}
					}
				}
			}
			finally
			{
				listener.Stop();
			}
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

		// Проверяет полный журнал и точную копию исключения фонового потока в журнале ошибок.
		private static void CheckExceptionLogging()
		{
			using (var workspace = new TestWorkspace())
			{
				string logPath = Path.Combine(workspace.DirectoryPath, "telecom.log");
				string errorLogPath = Path.Combine(workspace.DirectoryPath, "telecomErrors.log");
				const string infoMessage = "Фоновая задача запущена";
				const string errorMessage = "Ошибка фоновой задачи";
				var logger = new FileLogger(logPath, errorLogPath);
				logger.Info(infoMessage);
				Check.Equal(false, File.Exists(errorLogPath));
				try
				{
					Task.Run((Action)ThrowBackgroundFailure).GetAwaiter().GetResult();
				}
				catch (InvalidOperationException exception)
				{
					logger.Error(errorMessage, exception);
				}

				string[] logLines = File.ReadAllLines(logPath);
				string[] errorLines = File.ReadAllLines(errorLogPath);
				Check.Equal(errorLines.Length + 1, logLines.Length);
				CheckTimestamp(logLines[0]);
				Check.Equal(infoMessage, logLines[0].Substring(24));
				Check.Equal(errorMessage, errorLines[0].Substring(24));
				for (int index = 0; index < errorLines.Length; index++)
				{
					CheckTimestamp(errorLines[index]);
					Check.Equal(errorLines[index], logLines[index + 1]);
				}

				string text = string.Join(Environment.NewLine, errorLines);
				Check.Equal(true, text.Contains("System.InvalidOperationException"));
				Check.Equal(true, text.Contains("background-probe"));
				Check.Equal(true, text.Contains("ThrowBackgroundFailure"));
			}
		}

		// Проверяет, что сбой записи виден вызывающему коду, а доступный журнал получает ошибку.
		private static void CheckUnavailableLog()
		{
			using (var workspace = new TestWorkspace())
			{
				string unavailablePath = Path.Combine(workspace.DirectoryPath, "missing", "test.log");
				string errorLogPath = Path.Combine(workspace.DirectoryPath, "telecomErrors.log");
				var logger = new FileLogger(unavailablePath, errorLogPath);
				Check.Throws<DirectoryNotFoundException>(() => logger.Info("Информационное событие"));
				Check.Throws<DirectoryNotFoundException>(() => logger.Error("Ошибка", new InvalidOperationException("log-probe")));
				Check.Equal(true, File.ReadAllText(errorLogPath).Contains("log-probe"));
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
