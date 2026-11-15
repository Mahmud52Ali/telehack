using System;
using System.Collections.Generic;
using System.Data;
using System.IO;

namespace Telecom.Tests
{
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
}
