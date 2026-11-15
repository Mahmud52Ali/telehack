using System;
using System.Collections.Generic;
using System.IO;
using Telecom.App;
using Telecom.Core;

namespace Telecom.Tests
{
	internal static class Check
	{
		// Сравнивает фактическое значение с независимым ожидаемым результатом.
		internal static void Equal<T>(T expected, T actual)
		{
			if (!EqualityComparer<T>.Default.Equals(expected, actual))
			{
				throw new InvalidOperationException("Ожидалось: " + expected + "; получено: " + actual);
			}
		}

		// Проверяет отказ с ожидаемым типом исключения.
		internal static void Throws<T>(Action action) where T : Exception
		{
			try
			{
				action();
			}
			catch (T)
			{
				return;
			}

			throw new InvalidOperationException("Не получено ожидаемое исключение " + typeof(T).Name);
		}
	}

	internal sealed class TestWorkspace : IDisposable
	{
		internal string DirectoryPath { get; private set; }

		// Размещает временные данные проверок вне репозитория.
		internal TestWorkspace()
		{
			DirectoryPath = Path.Combine(Path.GetTempPath(), "telecom-tests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(DirectoryPath);
		}

		// Удаляет только созданный этим экземпляром каталог внутри системной временной папки.
		public void Dispose()
		{
			string fullPath = Path.GetFullPath(DirectoryPath);
			string tempPath = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
			if (!string.Equals(Path.GetDirectoryName(fullPath), tempPath, StringComparison.OrdinalIgnoreCase)
				|| !Path.GetFileName(fullPath).StartsWith("telecom-tests-", StringComparison.Ordinal))
			{
				throw new InvalidOperationException("Отказ удаления каталога вне временной области теста.");
			}

			Directory.Delete(fullPath, true);
		}
	}

	internal sealed class MemoryLogger : ILogger
	{
		internal readonly List<Exception> Errors = new List<Exception>();

		// Не создаёт файлов для обычных событий модульных проверок.
		public void Info(string message)
		{
			Messages.Add(message);
		}

		internal readonly List<string> Messages = new List<string>();

		// Позволяет проверить, что ошибка действительно передана в журнал.
		public void Error(string message, Exception exception)
		{
			Errors.Add(exception);
		}
	}

	internal sealed class RestrictedFileSystem : ICertificateFileSystem
	{
		private readonly WindowsFileSystem actual = new WindowsFileSystem();
		internal string DeniedDirectory;
		internal string LinkedDirectory;
		internal bool RejectPrivateKeyRead;
		internal Action<string> OnFileRead;

		// Моделирует отказ доступа без изменения ACL и системных настроек компьютера.
		public IEnumerable<string> GetFiles(string directoryPath)
		{
			if (directoryPath == DeniedDirectory)
			{
				throw new UnauthorizedAccessException("Тестовый отказ доступа.");
			}

			return actual.GetFiles(directoryPath);
		}

		// Делегирует обход настоящих временных каталогов.
		public IEnumerable<string> GetDirectories(string directoryPath)
		{
			return actual.GetDirectories(directoryPath);
		}

		// Моделирует ссылку, не создавая junction в файловой системе.
		public bool IsReparsePoint(string path)
		{
			return path == LinkedDirectory || actual.IsReparsePoint(path);
		}

		// Не считает моделируемую ссылку системной без подтверждённого назначения.
		public string GetReparsePointTarget(string path)
		{
			return path == LinkedDirectory ? null : actual.GetReparsePointTarget(path);
		}

		// Делает попытку чтения контейнера ключей явной ошибкой проверки.
		public byte[] ReadAllBytes(string filePath)
		{
			if (RejectPrivateKeyRead && Path.GetExtension(filePath).Equals(".p12", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Сканер попытался прочитать закрытый контейнер.");
			}

			byte[] contents = actual.ReadAllBytes(filePath);
			if (OnFileRead != null)
			{
				OnFileRead(filePath);
			}

			return contents;
		}
	}
}
