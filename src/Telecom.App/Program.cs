using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualBasic.ApplicationServices;
using Telecom.App.Forms;
using Telecom.Core.Logging;

namespace Telecom.App
{
	internal static class Program
	{
		private static ILogger logger;

		// Один экземпляр на сеанс Windows: повторный запуск активирует уже открытое окно.
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
				MainForm = new Telecom.App.Forms.MainForm(logger);
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
				Directory.CreateDirectory(AppPaths.LogDirectory);
				logger = new FileLogger(AppPaths.LogFilePath, AppPaths.ErrorLogFilePath);
				ErrorReporter.Install(logger);
				Application.EnableVisualStyles();
				Application.SetCompatibleTextRenderingDefault(false);
				var application = new SingleInstanceApplication();
				application.Run(Environment.GetCommandLineArgs());
				if (application.Started)
				{
					logger.Info("Приложение закрыто.");
				}
			}
			catch (Exception exception)
			{
				string message = "Не удалось запустить или завершить приложение.";
				if (exception is IOException || exception is UnauthorizedAccessException)
				{
					message += " Проверьте права на запись в папку " + AppPaths.DataDirectory + ".";
				}

				ErrorReporter.Show(message, exception);
			}
		}
	}
}
