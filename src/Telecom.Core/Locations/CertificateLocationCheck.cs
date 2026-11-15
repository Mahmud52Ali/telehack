using System;

namespace Telecom.Core.Locations
{
	public enum CertificateLocationStatus : byte
	{
		Found = 1,
		Missing,
		DifferentCertificate,
		Unverified
	}

	public sealed class CertificateLocationCheck
	{
		public string SerialNumber { get; private set; }
		public string ComputerAddress { get; private set; }
		public string SourcePath { get; private set; }
		public DateTime CertificateCreatedAt { get; private set; }
		public DateTime CertificateUpdatedAt { get; private set; }
		public CertificateLocationStatus Status { get; internal set; }
		public DateTime CheckedAtUtc { get; internal set; }

		// Сохраняет ключ находки и версию строки на момент запуска проверки.
		public CertificateLocationCheck(string serialNumber, string computerAddress, string sourcePath,
			DateTime certificateCreatedAt, DateTime certificateUpdatedAt)
		{
			SerialNumber = serialNumber;
			ComputerAddress = computerAddress;
			SourcePath = sourcePath;
			CertificateCreatedAt = certificateCreatedAt;
			CertificateUpdatedAt = certificateUpdatedAt;
		}
	}
}
