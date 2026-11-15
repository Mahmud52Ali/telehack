using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Telecom.App.Windows;
using Telecom.Core.Access;

namespace Telecom.Tests
{
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
