using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Telecom.App;
using Telecom.Core;

namespace Telecom.Tests
{
	internal sealed class LabComputerAccess : IComputerAccess
	{
		private readonly string computer;
		private readonly string networkPrefix;
		private readonly string root;
		private readonly WindowsFileSystem actual = new WindowsFileSystem();
		internal readonly Dictionary<string, string> ProductVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		internal readonly Dictionary<string, Exception> VersionErrors = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
		internal readonly List<string> DeniedPaths = new List<string>();
		internal readonly List<string> ReadPaths = new List<string>();
		internal Exception ConnectionFailure;
		internal Action OnFileRead;
		internal int ConnectionChecks;
		internal int RequestedTimeout;

		// Отображает административные папки тестового ПК на временный каталог дисков, не используя сеть.
		internal LabComputerAccess(string root)
			: this(root, "LAB-PC")
		{
		}

		// Позволяет одному файловому стенду проверять как прежний внутренний адаптер, так и профили с IPv4.
		internal LabComputerAccess(string root, string computer)
		{
			this.computer = computer;
			networkPrefix = @"\\" + computer + @"\";
			this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
		}

		// Проверяет переданные параметры и позволяет независимо смоделировать сетевой отказ.
		public void CheckConnection(string computerName, int timeoutSeconds, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Check.Equal(computer, computerName);
			ConnectionChecks++;
			RequestedTimeout = timeoutSeconds;
			if (ConnectionFailure != null)
			{
				throw ConnectionFailure;
			}
		}

		// Перечисляет реальные тестовые файлы, возвращая сканеру только UNC-пути.
		public IEnumerable<string> GetFiles(string directoryPath)
		{
			foreach (string file in actual.GetFiles(Map(directoryPath)))
			{
				yield return ToNetworkPath(file);
			}
		}

		// Возвращает тестовые профили под сетевыми именами, включая пустые профили.
		public IEnumerable<string> GetDirectories(string directoryPath)
		{
			foreach (string directory in actual.GetDirectories(Map(directoryPath)))
			{
				yield return ToNetworkPath(directory);
			}
		}

		// Проверяет настоящий временный путь после имитации прав доступа.
		public bool IsReparsePoint(string path)
		{
			return actual.IsReparsePoint(Map(path));
		}

		// Читает публичный образец и позволяет запросить отмену после первого файла.
		public byte[] ReadAllBytes(string filePath)
		{
			byte[] contents = actual.ReadAllBytes(Map(filePath));
			ReadPaths.Add(filePath);
			if (OnFileRead != null)
			{
				OnFileRead();
			}

			return contents;
		}

		// Моделирует версии файлов и отдельно отличает отсутствие файла от отказа доступа.
		public string GetProductVersion(string executablePath)
		{
			Map(executablePath);
			Exception failure;
			if (VersionErrors.TryGetValue(executablePath, out failure))
			{
				throw failure;
			}

			string version;
			if (ProductVersions.TryGetValue(executablePath, out version))
			{
				return version;
			}

			throw new FileNotFoundException("Тестовый файл csptest.exe отсутствует.");
		}

		// Не позволяет ошибке преобразования привести к настоящему сетевому обращению или выходу из тестовой области.
		private string Map(string networkPath)
		{
			if (!networkPath.StartsWith(networkPrefix, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Ожидался путь тестового компьютера " + computer + ".");
			}

			foreach (string denied in DeniedPaths)
			{
				if (networkPath.TrimEnd('\\').Equals(denied.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
					|| networkPath.StartsWith(denied.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
				{
					throw new UnauthorizedAccessException("Тестовый отказ доступа к ресурсу.");
				}
			}

			string relative = networkPath.Substring(networkPrefix.Length).Replace('\\', Path.DirectorySeparatorChar);
			string local = Path.GetFullPath(Path.Combine(root, relative));
			if (!local.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Тестовый путь вышел за пределы временного каталога.");
			}

			return local;
		}

		// Возвращает путь в том же формате, что и настоящий SMB-адаптер.
		private string ToNetworkPath(string localPath)
		{
			return networkPrefix + localPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '\\');
		}
	}
}
