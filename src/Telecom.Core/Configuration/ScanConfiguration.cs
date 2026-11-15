using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Telecom.Core.Common;

namespace Telecom.Core.Configuration
{
	public sealed class ScanConfiguration
	{
		public IList<string> Directories { get; private set; }
		public IList<string> CryptoProExecutables { get; private set; }
		public int ConnectionTimeoutSeconds { get; private set; }
		public string ConnectionString { get; private set; }

		// Проверяет настройки независимо от источника: XML, тест или другой интерфейс.
		public ScanConfiguration(IEnumerable<string> directories, IEnumerable<string> cryptoProExecutables,
			int connectionTimeoutSeconds, string connectionString)
		{
			Directories = NormalizePaths(directories, true);
			CryptoProExecutables = NormalizePaths(cryptoProExecutables, false);
			foreach (string executable in CryptoProExecutables)
			{
				if (!executable.EndsWith("\\csptest.exe", StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("Для определения версии КриптоПро укажите путь к csptest.exe.");
				}
			}

			if (connectionTimeoutSeconds < 1 || connectionTimeoutSeconds > 60)
			{
				throw new InvalidDataException("Таймаут подключения должен быть от 1 до 60 секунд.");
			}

			ConnectionTimeoutSeconds = connectionTimeoutSeconds;
			ConnectionString = connectionString ?? string.Empty;
		}

		// Создаёт настройки для одного ПК, добавляя его пути к общему списку.
		public ScanConfiguration WithAdditionalDirectories(IEnumerable<string> additionalDirectories)
		{
			return new ScanConfiguration(Directories.Concat(additionalDirectories), CryptoProExecutables,
				ConnectionTimeoutSeconds, ConnectionString);
		}

		// Требует хотя бы один нормализованный путь в каждом списке общей конфигурации.
		private static IList<string> NormalizePaths(IEnumerable<string> paths, bool allowWildcard)
		{
			IList<string> normalized = RemotePath.NormalizeLocalPaths(paths ?? Enumerable.Empty<string>(), allowWildcard);
			if (normalized.Count == 0)
			{
				throw new InvalidDataException("Список путей конфигурации не должен быть пустым.");
			}

			return normalized;
		}
	}
}
