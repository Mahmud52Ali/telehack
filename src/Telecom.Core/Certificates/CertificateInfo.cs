using System;

namespace Telecom.Core.Certificates
{
	public sealed class CertificateInfo
	{
		public string SerialNumber { get; private set; }
		public string OwnerName { get; private set; }
		public string CertificationAuthority { get; private set; }
		public string SourcePath { get; private set; }
		public bool IsCryptoProLicenseEmbedded { get; private set; }
		public DateTime? ExpiresAtUtc { get; private set; }

		// Хранит открытые сведения сертификата и путь, по которому он обнаружен.
		public CertificateInfo(string serialNumber, string ownerName, string certificationAuthority,
			string sourcePath, bool isCryptoProLicenseEmbedded)
			: this(serialNumber, ownerName, certificationAuthority, sourcePath, isCryptoProLicenseEmbedded, null)
		{
		}

		// Хранит открытые сведения сертификата, путь обнаружения и дату окончания действия в UTC.
		public CertificateInfo(string serialNumber, string ownerName, string certificationAuthority,
			string sourcePath, bool isCryptoProLicenseEmbedded, DateTime? expiresAtUtc)
		{
			SerialNumber = serialNumber;
			OwnerName = ownerName;
			CertificationAuthority = certificationAuthority;
			SourcePath = sourcePath;
			IsCryptoProLicenseEmbedded = isCryptoProLicenseEmbedded;
			ExpiresAtUtc = expiresAtUtc;
		}
	}
}
