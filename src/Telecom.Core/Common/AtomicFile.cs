using System;
using System.IO;

namespace Telecom.Core.Common
{
	public static class AtomicFile
	{
		// Пишет содержимое во временный файл рядом с целевым и только потом подменяет целевой файл.
		// Сбой записи или проверки внутри writeTemporary оставляет прежний файл нетронутым.
		public static void Write(string filePath, Action<string> writeTemporary, bool replaceExisting = true)
		{
			filePath = Path.GetFullPath(filePath);
			Directory.CreateDirectory(Path.GetDirectoryName(filePath));
			string temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				writeTemporary(temporaryPath);
				if (replaceExisting && File.Exists(filePath))
				{
					File.Replace(temporaryPath, filePath, null);
				}
				else
				{
					File.Move(temporaryPath, filePath);
				}
			}
			finally
			{
				if (File.Exists(temporaryPath))
				{
					File.Delete(temporaryPath);
				}
			}
		}
	}
}
