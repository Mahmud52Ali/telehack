using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;
using Telecom.App.Windows;
using Telecom.Core.Access;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private sealed class LocalComputerAccess : IComputerAccess
		{
			private readonly WindowsFileSystem files = new WindowsFileSystem();
			internal string DeniedDirectory;
			internal string CryptoPath;

			// Любая попытка сетевого подключения в локальном сценарии означает ошибку реализации.
			public void CheckConnection(string computerName, int timeoutSeconds, CancellationToken cancellationToken)
			{
				throw new InvalidOperationException("Для этого компьютера не нужна проверка порта SMB.");
			}

			// Читает только локальные временные файлы с публичными образцами.
			public IEnumerable<string> GetFiles(string directoryPath)
			{
				return files.GetFiles(directoryPath);
			}

			// Раскрывает маски на настоящих временных каталогах.
			public IEnumerable<string> GetDirectories(string directoryPath)
			{
				return files.GetDirectories(directoryPath);
			}

			// Моделирует отказ чтения одной папки без изменения ACL пользователя.
			public bool IsReparsePoint(string path)
			{
				if (path == DeniedDirectory)
				{
					throw new UnauthorizedAccessException("Тестовый отказ доступа к локальной папке.");
				}

				return files.IsReparsePoint(path);
			}

			// Читает назначение локальной ссылки через системный адаптер.
			public string GetReparsePointTarget(string path)
			{
				return files.GetReparsePointTarget(path);
			}

			// Делегирует чтение открытых сертификатов системному адаптеру.
			public byte[] ReadAllBytes(string filePath)
			{
				return files.ReadAllBytes(filePath);
			}

			// Требует локальный путь к исполняемому файлу вместо административного UNC-пути.
			public string GetProductVersion(string executablePath)
			{
				Check.Equal(CryptoPath, executablePath);
				return "5.0.13003.0";
			}
		}
	}
}
