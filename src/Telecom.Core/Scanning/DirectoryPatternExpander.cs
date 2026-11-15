using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Logging;

namespace Telecom.Core.Scanning
{
	// Раскрывает маски путей вида C:\Users\*\AppData\...: звёздочка заменяется именем каждой подпапки.
	// Ссылки (junction, symlink) не обходятся, чтобы не уйти в цикл или за пределы профилей;
	// исключение — служебные ссылки Windows в папке Users («All Users», «Default User»), они просто пропускаются.
	public sealed class DirectoryPatternExpander
	{
		private const string UserCertificateStorePatternSuffix = @"\*\AppData\Roaming\Microsoft\SystemCertificates\My\Certificates";
		private readonly ICertificateFileSystem fileSystem;
		private readonly ILogger logger;

		// Использует тот же файловый доступ, что и сканер, без собственных сетевых механизмов.
		public DirectoryPatternExpander(ICertificateFileSystem fileSystem, ILogger logger)
		{
			this.fileSystem = fileSystem;
			this.logger = logger;
		}

		// Возвращает существующие папки по маске; путь без звёздочки возвращается как есть.
		// Отсутствие папки у отдельного профиля — норма, отказ доступа и ссылки попадают в errors.
		public IList<string> Expand(string pattern, IList<ScanError> errors, CancellationToken cancellationToken)
		{
			var expanded = new List<string>();
			int errorsBefore = errors.Count;
			ExpandBranch(pattern, expanded, errors, cancellationToken, false);
			// Для стандартной маски «Личного» пустой результат нормален: Windows создаёт папку только при первом сертификате.
			if (pattern.IndexOf('*') >= 0 && expanded.Count == 0 && errors.Count == errorsBefore
				&& !pattern.EndsWith(UserCertificateStorePatternSuffix, StringComparison.OrdinalIgnoreCase))
			{
				var exception = new DirectoryNotFoundException("По маске не найдено ни одной доступной папки.");
				RecordError(pattern, exception, errors);
			}

			return expanded;
		}

		// Раскрывает первую звёздочку шаблона: перебирает подпапки части до неё и для каждой продолжает с остатком шаблона.
		// insideProfile = true, если шаблон уже получен подстановкой профиля: тогда отсутствие ветки у профиля — норма.
		private void ExpandBranch(string pattern, IList<string> expanded, IList<ScanError> errors,
			CancellationToken cancellationToken, bool insideProfile)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int wildcard = pattern.IndexOf('*');
			if (wildcard < 0)
			{
				expanded.Add(pattern);
				return;
			}

			string parent = pattern.Substring(0, wildcard);
			string suffix = pattern.Substring(wildcard + 1);
			try
			{
				if (fileSystem.IsReparsePoint(parent))
				{
					throw new IOException("Каталог-ссылка в маске не проверялся.");
				}

				foreach (string child in fileSystem.GetDirectories(parent))
				{
					cancellationToken.ThrowIfCancellationRequested();
					ExpandChild(parent, child, suffix, expanded, errors, cancellationToken);
				}
			}
			catch (Exception exception)
			{
				if (!ErrorClassifier.IsRecoverable(exception))
				{
					throw;
				}

				ReportFailure(pattern, exception, errors, insideProfile);
			}
		}

		// Подставляет одну подпапку вместо звёздочки и либо продолжает раскрытие, либо добавляет готовый путь.
		private void ExpandChild(string parent, string child, string suffix, IList<string> expanded, IList<ScanError> errors,
			CancellationToken cancellationToken)
		{
			try
			{
				if (fileSystem.IsReparsePoint(child))
				{
					if (IsProfileCompatibilityLink(parent, child))
					{
						logger.Info("Пропущена системная ссылка в папке профилей: " + child);
						return;
					}

					throw new IOException("Профиль или каталог-ссылка не проверялся.");
				}

				string candidate = child.TrimEnd('\\') + suffix;
				if (suffix.IndexOf('*') >= 0)
				{
					ExpandBranch(candidate, expanded, errors, cancellationToken, true);
				}
				else if (fileSystem.IsReparsePoint(candidate))
				{
					throw new IOException("Найденная папка является ссылкой и не проверялась.");
				}
				else
				{
					expanded.Add(candidate);
				}
			}
			catch (Exception exception)
			{
				if (!ErrorClassifier.IsRecoverable(exception))
				{
					throw;
				}

				ReportFailure(child + suffix, exception, errors, true);
			}
		}

		// Отсутствующая у профиля папка (если это допустимо) пишется в журнал одной строкой, остальное — ошибка в результате.
		private void ReportFailure(string path, Exception exception, IList<ScanError> errors, bool missingIsNormal)
		{
			if (missingIsNormal && ErrorClassifier.Classify(exception) == ScanErrorKind.NotFound)
			{
				logger.Info("У профиля нет папки: " + path);
			}
			else
			{
				RecordError(path, exception, errors);
			}
		}

		// Служебные ссылки Windows в корневой папке Users ведут в ProgramData или Users\Default того же диска.
		// Назначение читается из метаданных ссылки, поэтому имя ссылки и язык Windows не важны.
		private bool IsProfileCompatibilityLink(string parent, string child)
		{
			string root = Path.GetPathRoot(parent);
			string usersDirectory = Path.Combine(root, "Users");
			if (!parent.TrimEnd('\\').Equals(usersDirectory, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			string target = ResolveLinkTarget(root, fileSystem.GetReparsePointTarget(child));
			return target != null
				&& !target.Equals(child.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
				&& (target.Equals(Path.Combine(root, "ProgramData"), StringComparison.OrdinalIgnoreCase)
					|| target.Equals(Path.Combine(usersDirectory, "Default"), StringComparison.OrdinalIgnoreCase));
		}

		// Приводит назначение ссылки к виду сканируемого пути. У удалённого ПК ссылка хранит его локальный путь
		// (C:\ProgramData), поэтому он переводится в \\IP\C$\ProgramData. Null — назначение неизвестно или на другом диске.
		private static string ResolveLinkTarget(string root, string target)
		{
			if (string.IsNullOrEmpty(target))
			{
				return null;
			}

			bool remoteRoot = root.StartsWith(@"\\", StringComparison.Ordinal);
			if (remoteRoot && target.Length >= 3 && target[1] == ':' && target[2] == '\\')
			{
				string share = root.TrimEnd('\\');
				string drive = Path.GetFileName(share);
				if (drive.Length != 2 || drive[1] != '$' || char.ToUpperInvariant(drive[0]) != char.ToUpperInvariant(target[0]))
				{
					return null;
				}

				target = share + target.Substring(2);
			}

			return Path.IsPathRooted(target) ? Path.GetFullPath(target).TrimEnd('\\') : null;
		}

		// Делает непроверенные ветки видимыми в результате и сохраняет полную диагностику в журнале.
		private void RecordError(string path, Exception exception, IList<ScanError> errors)
		{
			errors.Add(new ScanError(path, exception.Message, ErrorClassifier.Classify(exception)));
			logger.Error("Не удалось раскрыть путь: " + path, exception);
		}
	}
}
