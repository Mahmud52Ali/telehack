namespace Telecom.Core.Scanning
{
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
