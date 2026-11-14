using System;
using System.Text.RegularExpressions;

namespace Telecom.Core
{
	public static class RemotePath
	{
		// Принимает имя ПК или IPv4, исключая пути, порты и неподдерживаемый UNC-формат IPv6.
		public static string NormalizeComputerName(string computerName)
		{
			string name = (computerName ?? string.Empty).Trim();
			UriHostNameType type = Uri.CheckHostName(name);
			if (name.Length == 0 || name.Length > 253
				|| (type != UriHostNameType.Dns && type != UriHostNameType.IPv4)
				|| name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', ' ', '\t', '\r', '\n' }) >= 0)
			{
				throw new ArgumentException("Укажите имя компьютера или IPv4 без пути и номера порта.", "computerName");
			}

			return name;
		}

		// Проверяет абсолютный Windows-путь; звёздочка допускается только вместо полного имени папки.
		public static string NormalizeLocalPath(string localPath, bool allowWildcard)
		{
			string path = (localPath ?? string.Empty).Trim().Replace('/', '\\');
			if (!Regex.IsMatch(path, @"^[A-Za-z]:\\"))
			{
				throw new ArgumentException("Ожидался путь с буквой диска, например C:\\Certs.", "localPath");
			}

			string[] segments = path.Substring(3).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string segment in segments)
			{
				if (segment == "." || segment == ".." || segment.TrimEnd(' ', '.') != segment
					|| segment.IndexOfAny(new[] { ':', '"', '<', '>', '|', '?', '\0', '\r', '\n', '\t' }) >= 0
					|| (segment.IndexOf('*') >= 0 && (!allowWildcard || segment != "*")))
				{
					throw new ArgumentException("Недопустимый путь. Используйте * только вместо полного имени папки.", "localPath");
				}
			}

			return char.ToUpperInvariant(path[0]) + @":\" + string.Join(@"\", segments);
		}

		// Преобразует путь любого диска в административный UNC-путь, не выполняя сетевых обращений.
		public static string ToUnc(string computerName, string localPath)
		{
			string computer = NormalizeComputerName(computerName);
			string path = NormalizeLocalPath(localPath, true);
			return @"\\" + computer + @"\" + path[0] + "$" + path.Substring(2);
		}
	}
}
