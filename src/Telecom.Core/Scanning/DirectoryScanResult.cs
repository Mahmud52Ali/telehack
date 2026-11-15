using System.Collections.Generic;
using Telecom.Core.Certificates;

namespace Telecom.Core.Scanning
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
}
