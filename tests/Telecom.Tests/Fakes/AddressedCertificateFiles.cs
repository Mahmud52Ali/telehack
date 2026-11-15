using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Telecom.Core.Access;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private sealed class AddressedCertificateFiles : ICertificateFileSystem
		{
			internal readonly Dictionary<string, byte[]> Contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
			internal readonly Dictionary<string, Exception> Failures = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
			internal readonly HashSet<string> LinkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			internal readonly List<string> ReadPaths = new List<string>();
			internal Action OnRead;

			public IEnumerable<string> GetFiles(string directoryPath)
			{
				throw new InvalidOperationException("Адресная проверка не должна перечислять файлы.");
			}

			public IEnumerable<string> GetDirectories(string directoryPath)
			{
				throw new InvalidOperationException("Адресная проверка не должна обходить папки.");
			}

			public bool IsReparsePoint(string path)
			{
				Exception failure;
				if (Failures.TryGetValue(path, out failure))
				{
					throw failure;
				}
				if (!Contents.ContainsKey(path))
				{
					throw new FileNotFoundException("Тестовый источник отсутствует.");
				}
				return LinkedPaths.Contains(path);
			}

			public string GetReparsePointTarget(string path)
			{
				throw new InvalidOperationException("Адресная проверка не должна переходить по ссылке.");
			}

			public byte[] ReadAllBytes(string filePath)
			{
				ReadPaths.Add(filePath);
				if (OnRead != null)
				{
					OnRead();
				}
				return Contents[filePath];
			}
		}
	}
}
