using System.Collections.Generic;

namespace Telecom.Core
{
	public sealed class DirectoryScanResult
	{
		public IList<CertificateInfo> Certificates { get; private set; }
		public IList<ScanError> Errors { get; private set; }
		public int SkippedFiles { get; internal set; }
		public int ScannedDirectories { get; internal set; }
		public bool Cancelled { get; internal set; }

		// Создаёт отдельный результат для одного запуска сканирования.
		public DirectoryScanResult()
		{
			Certificates = new List<CertificateInfo>();
			Errors = new List<ScanError>();
		}
	}

	public sealed class ScanError
	{
		public string Path { get; private set; }
		public string Message { get; private set; }
		public ScanErrorKind Kind { get; private set; }

		// Сохраняет путь и понятное описание ошибки для отображения в интерфейсе.
		public ScanError(string path, string message)
			: this(path, message, ScanErrorKind.Other)
		{
		}

		// Сохраняет также категорию ошибки для сетевой диагностики без разбора её текста.
		public ScanError(string path, string message, ScanErrorKind kind)
		{
			Path = path;
			Message = message;
			Kind = kind;
		}
	}
}
