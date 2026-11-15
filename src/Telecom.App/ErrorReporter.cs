using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Telecom.Core.Logging;

namespace Telecom.App
{
	// Показ ошибок пользователю с записью в журнал и перехват необработанных исключений всего приложения.
	internal static class ErrorReporter
	{
		private static ILogger logger;

		// Запоминает журнал и подписывается на необработанные исключения интерфейса, CLR и фоновых задач.
		// Вызывается до создания первого окна.
		internal static void Install(ILogger applicationLogger)
		{
			logger = applicationLogger;
			Application.ThreadException += OnThreadException;
			AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
			TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
			Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
		}

		// Записывает ошибку в журнал и показывает её; если журнал недоступен, сообщает и об этом.
		internal static void Show(string message, Exception exception)
		{
			string details = message + Environment.NewLine + exception.Message;
			if (logger != null)
			{
				logger.Error(message, exception);
				var fileLogger = logger as FileLogger;
				if (fileLogger != null && fileLogger.LastWriteError != null)
				{
					details += Environment.NewLine + "Журнал недоступен: " + fileLogger.LastWriteError.Message;
					Console.Error.WriteLine(exception);
				}
			}

			MessageBox.Show(details, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}

		// Фиксирует необработанные ошибки потока интерфейса.
		private static void OnThreadException(object sender, ThreadExceptionEventArgs args)
		{
			Show("Ошибка интерфейса.", args.Exception);
		}

		// Фиксирует необработанное исключение до завершения процесса CLR.
		private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
		{
			var exception = args.ExceptionObject as Exception;
			Show("Необработанная ошибка приложения.", exception ?? new Exception("Неизвестная ошибка CLR."));
		}

		// Сохраняет ошибки задач, которые не были получены вызывающим кодом.
		private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
		{
			Show("Необработанная ошибка фоновой задачи.", args.Exception);
			args.SetObserved();
		}
	}
}
