using System;
using System.IO;

namespace Telecom.App
{
	// Расположение рабочих файлов приложения.
	// При запуске из репозитория — корень решения (рядом с Telecom.sln), в поставке — папка exe.
	// Если эта папка недоступна на запись (например, Program Files), используется %LOCALAPPDATA%\Telecom.
	internal static class AppPaths
	{
		internal static readonly string ApplicationDirectory = AppDomain.CurrentDomain.BaseDirectory;
		internal static readonly string DataDirectory = ChooseDataDirectory(FindDataDirectory(ApplicationDirectory));
		internal static readonly string LogDirectory = Path.Combine(DataDirectory, "logs");
		internal static readonly string LogFilePath = Path.Combine(LogDirectory, "telecom.log");
		internal static readonly string ErrorLogFilePath = Path.Combine(LogDirectory, "telecomErrors.log");
		internal static readonly string ConfigDirectory = Path.Combine(DataDirectory, "configs");
		internal static readonly string ComputerListPath = Path.Combine(ConfigDirectory, "computers.xml");
		internal static readonly string ScanConfigurationPath = Path.Combine(ConfigDirectory, "scan.config.xml");
		internal static readonly string ScanConfigurationTemplatePath = Path.Combine(ApplicationDirectory, "scan.config.template.xml");

		// Находит корень решения для сборки из репозитория и папку exe для отдельной поставки.
		internal static string FindDataDirectory(string applicationDirectory)
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

		// Оставляет найденную папку, если в неё можно писать журнал, иначе выбирает папку профиля пользователя.
		private static string ChooseDataDirectory(string preferredDirectory)
		{
			if (CanWriteLogs(preferredDirectory))
			{
				return preferredDirectory;
			}

			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Telecom");
		}

		// Проверяет запись пробным открытием файла журнала на дозапись.
		private static bool CanWriteLogs(string dataDirectory)
		{
			try
			{
				string logDirectory = Path.Combine(dataDirectory, "logs");
				Directory.CreateDirectory(logDirectory);
				using (new FileStream(Path.Combine(logDirectory, "telecom.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
				{
				}

				return true;
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}
			catch (IOException)
			{
				return false;
			}
		}
	}
}
