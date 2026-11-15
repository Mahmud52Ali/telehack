using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Telecom.Core.Certificates;
using Telecom.Core.Logging;

namespace Telecom.Core.Database
{
	// Запись результатов сканирования: обновление строки по серийному номеру или вставка новой, без дублей.
	public sealed class CertificateWriter : ICertificateWriter
	{
		// Блокировка строки держится до конца транзакции, поэтому два запуска не вставят один серийный номер дважды.
		// Время берётся с сервера базы данных и не уменьшается, даже если запуски пересеклись во времени.
		private const string SaveCertificateSql = @"
DECLARE @SavedAt DATETIME2(3) = SYSDATETIME();

UPDATE dbo.Certificates WITH (UPDLOCK, HOLDLOCK)
SET UpdatedAt = CASE WHEN @SavedAt > UpdatedAt THEN @SavedAt ELSE UpdatedAt END,
	OwnerName = @OwnerName,
	CertificationAuthority = @CertificationAuthority,
	CryptoProVersion = @CryptoProVersion,
	IsCryptoProLicenseEmbedded = @IsCryptoProLicenseEmbedded,
	ExpiresAtUtc = COALESCE(@ExpiresAtUtc, ExpiresAtUtc)
WHERE SerialNumber = @SerialNumber;

IF @@ROWCOUNT = 0
BEGIN
	INSERT INTO dbo.Certificates (SerialNumber, CreatedAt, UpdatedAt, OwnerName, CertificationAuthority,
		CryptoProVersion, IsCryptoProLicenseEmbedded, ExpiresAtUtc)
	VALUES (@SerialNumber, @SavedAt, @SavedAt, @OwnerName, @CertificationAuthority,
		@CryptoProVersion, @IsCryptoProLicenseEmbedded, @ExpiresAtUtc);
	SELECT 1;
END
ELSE
BEGIN
	SELECT 0;
END";

		private readonly DatabaseConnection database;
		private readonly ILogger logger;

		// Запоминает базу; соединение открывается только на время одной записи.
		public CertificateWriter(DatabaseConnection database, ILogger logger)
		{
			this.database = database;
			this.logger = logger;
		}

		// Записывает сертификаты и их места обнаружения в одной транзакции; без адреса компьютера места не записываются.
		public CertificateSaveResult Save(IEnumerable<CertificateInfo> certificates, string cryptoProVersion, string computerAddress)
		{
			if (certificates == null)
			{
				throw new ArgumentNullException("certificates");
			}
			if (computerAddress != null && computerAddress.Length > 255)
			{
				throw new ArgumentException("Адрес компьютера должен содержать не более 255 символов.", "computerAddress");
			}

			var result = new CertificateSaveResult();
			using (SqlConnection connection = database.OpenCreatingDatabase())
			{
				DatabaseSchema.Ensure(connection);
				using (SqlTransaction transaction = connection.BeginTransaction())
				{
					var bySerialNumber = certificates.GroupBy(certificate => certificate.SerialNumber, StringComparer.OrdinalIgnoreCase);
					foreach (IGrouping<string, CertificateInfo> group in bySerialNumber.OrderBy(records => records.Key, StringComparer.OrdinalIgnoreCase))
					{
						CertificateInfo certificate = group.First();
						if (SaveCertificate(connection, transaction, certificate, cryptoProVersion))
						{
							result.Inserted++;
						}
						else
						{
							result.Updated++;
						}

						if (!string.IsNullOrEmpty(computerAddress))
						{
							foreach (CertificateInfo finding in group)
							{
								SaveLocation(connection, transaction, certificate.SerialNumber, computerAddress, finding.SourcePath);
							}
						}
					}

					transaction.Commit();
				}
			}

			logger.Info(string.Format("Запись в базу данных: добавлено сертификатов: {0}; обновлено: {1}.",
				result.Inserted, result.Updated));
			return result;
		}

		// Обновляет строку сертификата или добавляет новую; возвращает true, если строка добавлена.
		private static bool SaveCertificate(SqlConnection connection, SqlTransaction transaction,
			CertificateInfo certificate, string cryptoProVersion)
		{
			using (var command = new SqlCommand(SaveCertificateSql, connection, transaction))
			{
				command.Parameters.Add("@SerialNumber", SqlDbType.NVarChar, 128).Value = certificate.SerialNumber;
				command.Parameters.Add("@OwnerName", SqlDbType.NVarChar, 512).Value = certificate.OwnerName ?? string.Empty;
				command.Parameters.Add("@CertificationAuthority", SqlDbType.NVarChar, 512).Value =
					certificate.CertificationAuthority ?? string.Empty;
				command.Parameters.Add("@CryptoProVersion", SqlDbType.NVarChar, 64).Value =
					string.IsNullOrEmpty(cryptoProVersion) ? (object)DBNull.Value : cryptoProVersion;
				command.Parameters.Add("@IsCryptoProLicenseEmbedded", SqlDbType.Bit).Value = certificate.IsCryptoProLicenseEmbedded;
				command.Parameters.Add("@ExpiresAtUtc", SqlDbType.DateTime2).Value = (object)certificate.ExpiresAtUtc ?? DBNull.Value;
				return (int)command.ExecuteScalar() == 1;
			}
		}

		// Записывает новый путь под блокировкой соответствующей строки сертификата.
		private static void SaveLocation(SqlConnection connection, SqlTransaction transaction, string serialNumber,
			string computerAddress, string sourcePath)
		{
			if (string.IsNullOrEmpty(sourcePath))
			{
				return;
			}

			const string sql = @"
INSERT INTO dbo.CertificateLocations (SerialNumber, ComputerAddress, SourcePath)
SELECT @SerialNumber, @ComputerAddress, @SourcePath
WHERE NOT EXISTS
(
	SELECT 1 FROM dbo.CertificateLocations
	WHERE SerialNumber = @SerialNumber AND ComputerAddress = @ComputerAddress AND SourcePath = @SourcePath
);";
			using (var command = new SqlCommand(sql, connection, transaction))
			{
				command.Parameters.Add("@SerialNumber", SqlDbType.NVarChar, 128).Value = serialNumber;
				command.Parameters.Add("@ComputerAddress", SqlDbType.NVarChar, 255).Value = computerAddress;
				command.Parameters.Add("@SourcePath", SqlDbType.NVarChar, -1).Value = sourcePath;
				command.ExecuteNonQuery();
			}
		}
	}
}
