namespace Telecom.Core.Scanning
{
	// Итог запуска: сколько ПК подключено, проверено и сколько раз не удалась запись в БД.
	public sealed class ScanRunSummary
	{
		public int Connected { get; internal set; }
		public int Completed { get; internal set; }
		public int DatabaseFailures { get; internal set; }
	}
}
