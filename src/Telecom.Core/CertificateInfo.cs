namespace Telecom.Core
{
	public sealed class CertificateInfo
	{
		public string SerialNumber { get; private set; }
		public string OwnerName { get; private set; }
		public string CertificationAuthority { get; private set; }
		public string SourcePath { get; private set; }
		public bool IsCryptoProLicenseEmbedded { get; private set; }

		// Хранит открытые сведения сертификата и путь, по которому он обнаружен.
		public CertificateInfo(string serialNumber, string ownerName, string certificationAuthority,
			string sourcePath, bool isCryptoProLicenseEmbedded)
		{
			SerialNumber = serialNumber;
			OwnerName = ownerName;
			CertificationAuthority = certificationAuthority;
			SourcePath = sourcePath;
			IsCryptoProLicenseEmbedded = isCryptoProLicenseEmbedded;
		}
	}
}
