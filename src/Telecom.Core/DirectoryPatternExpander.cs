using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Telecom.Core
{
	public sealed class DirectoryPatternExpander
	{
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
			if (pattern.IndexOf('*') >= 0 && expanded.Count == 0 && errors.Count == errorsBefore)
			{
				var exception = new DirectoryNotFoundException("По маске не найдено ни одной доступной папки.");
				RecordError(pattern, exception, errors);
			}

			return expanded;
		}

		// Последовательно раскрывает каждую звёздочку, не заходя в каталоги-ссылки.
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

		// Делает непроверенные ветки видимыми в результате и сохраняет полную диагностику.
		private void RecordError(string path, Exception exception, IList<ScanError> errors)
		{
			errors.Add(new ScanError(path, exception.Message, ScanFailure.Classify(exception)));
			logger.Error("Не удалось раскрыть путь: " + path, exception);
		}
	}
}
