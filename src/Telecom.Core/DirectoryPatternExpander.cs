using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Telecom.Core
{
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

		// Раскрывает полные сегменты *; отсутствие папки у отдельного профиля допустимо, отказ доступа — ошибка.
		public IList<string> Expand(string pattern, IList<ScanError> errors, CancellationToken cancellationToken)
		{
			var expanded = new List<string>();
			int errorsBefore = errors.Count;
			ExpandBranch(pattern, expanded, errors, cancellationToken, false);
			// Windows может ещё не создать каталоги «Личное» ни в одном доступном профиле.
			if (pattern.IndexOf('*') >= 0 && expanded.Count == 0 && errors.Count == errorsBefore
				&& !pattern.EndsWith(UserCertificateStorePatternSuffix, StringComparison.OrdinalIgnoreCase))
			{
				var exception = new DirectoryNotFoundException("По маске не найдено ни одной доступной папки.");
				RecordError(pattern, exception, errors);
			}

			return expanded;
		}

		// Раскрывает каждую звёздочку и проверяет ссылки среди найденных каталогов.
		private void ExpandBranch(string pattern, IList<string> expanded, IList<ScanError> errors,
			CancellationToken cancellationToken, bool expandedProfile)
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
					try
					{
						if (fileSystem.IsReparsePoint(child))
						{
							if (IsProfileCompatibilityLink(parent, child))
							{
								logger.Info("Пропущена системная ссылка в папке профилей: " + child);
								continue;
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
						if (!ScanFailure.IsRecoverable(exception))
						{
							throw;
						}

						if (ScanFailure.Classify(exception) == ScanErrorKind.NotFound)
						{
							logger.Info("У профиля нет настроенной папки: " + child + suffix + Environment.NewLine + exception);
						}
						else
						{
							RecordError(child + suffix, exception, errors);
						}
					}
				}
			}
			catch (Exception exception)
			{
				if (!ScanFailure.IsRecoverable(exception))
				{
					throw;
				}

				if (expandedProfile && ScanFailure.Classify(exception) == ScanErrorKind.NotFound)
				{
					logger.Info("У профиля нет настроенной ветки: " + pattern + Environment.NewLine + exception);
				}
				else
				{
					RecordError(pattern, exception, errors);
				}
			}
		}

		// Отличает штатные ссылки в корневой папке Users от ссылок на реальные профили.
		private bool IsProfileCompatibilityLink(string parent, string child)
		{
			string root = Path.GetPathRoot(parent);
			string usersDirectory = Path.Combine(root, "Users");
			if (!parent.TrimEnd('\\').Equals(usersDirectory, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			string target = fileSystem.GetReparsePointTarget(child);
			if (string.IsNullOrEmpty(target))
			{
				return false;
			}

			// В метаданных удалённой ссылки хранится путь её ПК, например C:\ProgramData, а не UNC.
			if (root.StartsWith(@"\\", StringComparison.Ordinal) && target.Length >= 3 && target[1] == ':' && target[2] == '\\')
			{
				string share = root.TrimEnd('\\');
				string drive = Path.GetFileName(share);
				if (drive.Length != 2 || drive[1] != '$' || char.ToUpperInvariant(drive[0]) != char.ToUpperInvariant(target[0]))
				{
					return false;
				}
				target = root.TrimEnd('\\') + target.Substring(2);
			}

			if (!Path.IsPathRooted(target))
			{
				return false;
			}
			target = Path.GetFullPath(target).TrimEnd('\\');
			return !target.Equals(child.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
				&& (target.Equals(Path.Combine(root, "ProgramData"), StringComparison.OrdinalIgnoreCase)
					|| target.Equals(Path.Combine(usersDirectory, "Default"), StringComparison.OrdinalIgnoreCase));
		}

		// Делает непроверенные ветки видимыми в результате и сохраняет полную диагностику.
		private void RecordError(string path, Exception exception, IList<ScanError> errors)
		{
			errors.Add(new ScanError(path, exception.Message, ScanFailure.Classify(exception)));
			logger.Error("Не удалось раскрыть путь: " + path, exception);
		}
	}
}
