using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Telecom.Core;

namespace Telecom.App
{
	internal static class Program
	{
		private static ILogger logger;

		// Настраивает журнал, обработчики исключений и запускает окно администратора.
		[STAThread]
		private static void Main()
		{
			try
			{
				logger = new FileLogger(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "telecom.log"));
				logger.Info("Приложение запущено.");
				Application.ThreadException += OnThreadException;
				AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
				TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
				Application.EnableVisualStyles();
				Application.SetCompatibleTextRenderingDefault(false);
				Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
				Application.Run(new MainForm(logger));
				logger.Info("Приложение закрыто.");
			}
			catch (Exception exception)
			{
				ShowError("Не удалось запустить или завершить приложение. Проверьте доступ к папке с программой.", exception);
			}
		}

		// Показывает ошибку и сохраняет диагностику; сбой самого журнала тоже явно сообщается пользователю.
		internal static void ShowError(string message, Exception exception)
		{
			string details = message + Environment.NewLine + exception.Message;
			try
			{
				if (logger != null)
				{
					logger.Error(message, exception);
				}
			}
			catch (Exception loggingException)
			{
				details += Environment.NewLine + "Журнал недоступен: " + loggingException.Message;
				Console.Error.WriteLine(exception);
				Console.Error.WriteLine(loggingException);
			}

			MessageBox.Show(details, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}

		// Фиксирует необработанные ошибки потока интерфейса.
		private static void OnThreadException(object sender, ThreadExceptionEventArgs args)
		{
			ShowError("Ошибка интерфейса.", args.Exception);
		}

		// Фиксирует необработанное исключение до завершения процесса CLR.
		private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
		{
			var exception = args.ExceptionObject as Exception;
			ShowError("Необработанная ошибка приложения.", exception ?? new Exception("Неизвестная ошибка CLR."));
		}

		// Сохраняет ошибки задач, которые не были получены вызывающим кодом.
		private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
		{
			ShowError("Необработанная ошибка фоновой задачи.", args.Exception);
			args.SetObserved();
		}
	}
}
