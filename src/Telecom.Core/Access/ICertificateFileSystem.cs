using System.Collections.Generic;

namespace Telecom.Core.Access
{
	public interface ICertificateFileSystem
	{
		// Возвращает файлы одного каталога без рекурсивного обхода.
		IEnumerable<string> GetFiles(string directoryPath);

		// Возвращает непосредственные дочерние каталоги.
		IEnumerable<string> GetDirectories(string directoryPath);

		// Проверяет, является ли путь ссылкой, способной вывести обход за пределы выбранной папки.
		bool IsReparsePoint(string path);

		// Читает назначение ссылки без перехода по ней; неизвестный тип возвращает null.
		string GetReparsePointTarget(string path);

		// Читает содержимое одного файла сертификата.
		byte[] ReadAllBytes(string filePath);
	}
}
