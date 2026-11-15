namespace Telecom.Core.Scanning
{
	// Итог запуска: сколько ПК подключено, с каким результатом проверено и сколько раз не удалась запись в БД.
	public sealed class ScanRunSummary
	{
		// ПК, к которым удалось подключиться (этот компьютер подключения не требует и считается сразу).
		public int Connected { get; internal set; }

		// ПК, проверенные без ошибок.
		public int Complete { get; internal set; }

		// ПК, с которых прочитана только часть источников.
		public int Partial { get; internal set; }

		// ПК, с которых ничего не прочитано: не подключились, нет доступа или недоступны.
		public int Failed { get; internal set; }

		// ПК, с которых удалось хоть что-то прочитать.
		public int Checked { get { return Complete + Partial; } }

		// Сколько раз не удалось записать результаты ПК в БД.
		public int DatabaseFailures { get; internal set; }
	}
}
