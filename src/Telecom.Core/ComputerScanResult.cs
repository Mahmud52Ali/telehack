using System.Collections.Generic;

namespace Telecom.Core
{
	public enum ComputerScanStatus
	{
		Complete,
		Partial,
		AccessDenied,
		Unavailable,
		Failed,
		Cancelled
	}

	public enum CryptoProStatus
	{
		NotChecked,
		Found,
		NotFound,
		Error
	}

	public sealed class ComputerScanResult
	{
		public string ComputerName { get; private set; }
		public IList<CertificateInfo> Certificates { get; private set; }
		public IList<ScanError> Errors { get; private set; }
		public ComputerScanStatus Status { get; internal set; }
		public CryptoProStatus CryptoProStatus { get; internal set; }
		public string CryptoProVersion { get; internal set; }
		public string CryptoProSource { get; internal set; }
		public int ScannedDirectories { get; internal set; }
		public int SkippedFiles { get; internal set; }

		// Объединяет результаты всех настроенных папок и определения версии на одном ПК.
		public ComputerScanResult(string computerName)
		{
			ComputerName = computerName;
			Certificates = new List<CertificateInfo>();
			Errors = new List<ScanError>();
			Status = ComputerScanStatus.Failed;
		}
	}
}
