using System;
using Telecom.Core.Computers;
using Telecom.Core.Database;

namespace Telecom.Core.Scanning
{
	// Этап обработки одного ПК, о котором ScanCoordinator сообщает интерфейсу.
	public enum ComputerStage
	{
		Connecting,
		Connected,
		ConnectionFailed,
		ConnectionCancelled,
		Scanning,
		Scanned,
		Saving,
		Saved,
		SaveFailed,
		SaveSkippedNoCertificates,
		SaveSkippedNoDatabase
	}

	// Событие хода работы по одному ПК. Заполнены только поля, относящиеся к этапу.
	public sealed class ComputerProgress
	{
		public ComputerProfile Profile { get; private set; }
		public ComputerStage Stage { get; private set; }
		public ComputerScanResult ScanResult { get; private set; }
		public CertificateSaveResult SaveResult { get; private set; }
		public Exception Error { get; private set; }

		// Создаёт событие этапа с необязательными результатом сканирования, итогом записи или ошибкой.
		public ComputerProgress(ComputerProfile profile, ComputerStage stage, ComputerScanResult scanResult = null,
			CertificateSaveResult saveResult = null, Exception error = null)
		{
			Profile = profile;
			Stage = stage;
			ScanResult = scanResult;
			SaveResult = saveResult;
			Error = error;
		}
	}
}
