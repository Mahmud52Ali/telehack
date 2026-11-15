using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic.ApplicationServices;
using Telecom.Core;

namespace Telecom.App
{
	internal static class Program
	{
		internal static readonly string DataDirectory = FindDataDirectory(AppDomain.CurrentDomain.BaseDirectory);
		private static readonly string LogDirectory = Path.Combine(DataDirectory, "logs");
		internal static readonly string LogFilePath = Path.Combine(LogDirectory, "telecom.log");
		internal static readonly string ErrorLogFilePath = Path.Combine(LogDirectory, "telecomErrors.log");
		private static ILogger logger;

		private sealed class SingleInstanceApplication : WindowsFormsApplicationBase
		{
			internal bool Started { get; private set; }

			// Использует штатную межпроцессную синхронизацию .NET Framework для одного экземпляра в сеансе Windows.
			internal SingleInstanceApplication()
			{
				IsSingleInstance = true;
			}

			// Создаёт главное окно только в первом процессе.
			protected override void OnCreateMainForm()
			{
				logger.Info("Приложение запущено.");
				MainForm = new Telecom.App.MainForm(logger);
				Started = true;
			}

			// Повторный запуск просит штатный механизм восстановить и активировать первое окно.
			protected override void OnStartupNextInstance(StartupNextInstanceEventArgs args)
			{
				args.BringToForeground = true;
				base.OnStartupNextInstance(args);
			}
		}

		// Настраивает журнал, обработчики исключений и запускает окно администратора.
		[STAThread]
		private static void Main()
		{
			try
			{
				Directory.CreateDirectory(LogDirectory);
				logger = new FileLogger(LogFilePath, ErrorLogFilePath);
				Application.ThreadException += OnThreadException;
				AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
				TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
				Application.EnableVisualStyles();
				Application.SetCompatibleTextRenderingDefault(false);
				Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
				var application = new SingleInstanceApplication();
				application.Run(Environment.GetCommandLineArgs());
				if (application.Started)
				{
					logger.Info("Приложение закрыто.");
				}
			}
			catch (Exception exception)
			{
				ShowError("Не удалось запустить или завершить приложение. Проверьте права на запись в папку " + DataDirectory + ".", exception);
			}
		}

		// Находит корень решения для сборки из репозитория и папку exe для отдельной поставки.
		private static string FindDataDirectory(string applicationDirectory)
		{
			for (var directory = new DirectoryInfo(applicationDirectory); directory != null; directory = directory.Parent)
			{
				if (File.Exists(Path.Combine(directory.FullName, "Telecom.sln")))
				{
					return directory.FullName;
				}
			}

			return applicationDirectory;
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
		private static void OnUnhandledException(object sender, System.UnhandledExceptionEventArgs args)
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
