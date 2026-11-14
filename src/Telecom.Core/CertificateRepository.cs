using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace Telecom.Core
{
	public sealed class CertificateSaveResult
	{
		public int Inserted { get; internal set; }
		public int Updated { get; internal set; }
	}

	public sealed class CertificateRepository
	{
		private const string CreateTableSql = @"
IF OBJECT_ID(N'dbo.Certificates', N'U') IS NULL
BEGIN
	CREATE TABLE dbo.Certificates
	(
		SerialNumber NVARCHAR(128) NOT NULL CONSTRAINT PK_Certificates PRIMARY KEY,
		CreatedAt DATETIME2(3) NOT NULL,
		UpdatedAt DATETIME2(3) NOT NULL,
		OwnerName NVARCHAR(512) NOT NULL,
		CertificationAuthority NVARCHAR(512) NOT NULL,
		CryptoProVersion NVARCHAR(64) NULL,
		IsCryptoProLicenseEmbedded BIT NOT NULL
	);
END";

		// Блокировка строки держится до конца транзакции, поэтому два запуска не вставят один серийный номер дважды.
		private const string SaveCertificateSql = @"
UPDATE dbo.Certificates WITH (UPDLOCK, HOLDLOCK)
SET UpdatedAt = @SavedAt,
	OwnerName = @OwnerName,
	CertificationAuthority = @CertificationAuthority,
	CryptoProVersion = @CryptoProVersion,
	IsCryptoProLicenseEmbedded = @IsCryptoProLicenseEmbedded
WHERE SerialNumber = @SerialNumber;

IF @@ROWCOUNT = 0
BEGIN
	INSERT INTO dbo.Certificates (SerialNumber, CreatedAt, UpdatedAt, OwnerName, CertificationAuthority,
		CryptoProVersion, IsCryptoProLicenseEmbedded)
	VALUES (@SerialNumber, @SavedAt, @SavedAt, @OwnerName, @CertificationAuthority,
		@CryptoProVersion, @IsCryptoProLicenseEmbedded);
	SELECT 1;
END
ELSE
BEGIN
	SELECT 0;
END";

		private readonly string connectionString;
		private readonly ILogger logger;

		// Запоминает строку подключения; соединение открывается только на время одной операции.
		public CertificateRepository(string connectionString, ILogger logger)
		{
			if (string.IsNullOrWhiteSpace(connectionString))
			{
				throw new ArgumentException("Не указана строка подключения к базе данных.", "connectionString");
			}

			this.connectionString = connectionString;
			this.logger = logger;
		}

		// Создаёт таблицу сертификатов, если её ещё нет; существующую таблицу не изменяет.
		public void EnsureTableExists()
		{
			using (var connection = new SqlConnection(connectionString))
			{
				connection.Open();
				CreateTableIfMissing(connection, null);
			}
		}

		// Сохраняет сертификаты одного ПК: новые добавляет, существующие обновляет по серийному номеру.
		// Исключения базы данных не перехватываются: их показывает и записывает в журнал вызывающий код.
		public CertificateSaveResult Save(IEnumerable<CertificateInfo> certificates, string cryptoProVersion)
		{
			if (certificates == null)
			{
				throw new ArgumentNullException("certificates");
			}

			var result = new CertificateSaveResult();
			var savedSerialNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			DateTime savedAt = DateTime.Now;
			using (var connection = new SqlConnection(connectionString))
			{
				connection.Open();
				using (SqlTransaction transaction = connection.BeginTransaction())
				{
					CreateTableIfMissing(connection, transaction);
					foreach (CertificateInfo certificate in certificates)
					{
						// Один сертификат может лежать в нескольких папках; в таблице ему соответствует одна строка.
						if (!savedSerialNumbers.Add(certificate.SerialNumber))
						{
							continue;
						}

						if (SaveCertificate(connection, transaction, certificate, cryptoProVersion, savedAt))
						{
							result.Inserted++;
						}
						else
						{
							result.Updated++;
						}
					}

					transaction.Commit();
				}
			}

			logger.Info(string.Format("Запись в базу данных: добавлено сертификатов: {0}; обновлено: {1}.",
				result.Inserted, result.Updated));
			return result;
		}

		// Выполняет создание таблицы в уже открытом соединении.
		private static void CreateTableIfMissing(SqlConnection connection, SqlTransaction transaction)
		{
			using (var command = new SqlCommand(CreateTableSql, connection, transaction))
			{
				command.ExecuteNonQuery();
			}
		}

		// Обновляет строку сертификата или добавляет новую; возвращает true, если строка добавлена.
		private static bool SaveCertificate(SqlConnection connection, SqlTransaction transaction,
			CertificateInfo certificate, string cryptoProVersion, DateTime savedAt)
		{
			using (var command = new SqlCommand(SaveCertificateSql, connection, transaction))
			{
				command.Parameters.Add("@SerialNumber", SqlDbType.NVarChar, 128).Value = certificate.SerialNumber;
				command.Parameters.Add("@SavedAt", SqlDbType.DateTime2).Value = savedAt;
				command.Parameters.Add("@OwnerName", SqlDbType.NVarChar, 512).Value = certificate.OwnerName ?? string.Empty;
				command.Parameters.Add("@CertificationAuthority", SqlDbType.NVarChar, 512).Value =
					certificate.CertificationAuthority ?? string.Empty;
				command.Parameters.Add("@CryptoProVersion", SqlDbType.NVarChar, 64).Value =
					string.IsNullOrEmpty(cryptoProVersion) ? (object)DBNull.Value : cryptoProVersion;
				command.Parameters.Add("@IsCryptoProLicenseEmbedded", SqlDbType.Bit).Value = certificate.IsCryptoProLicenseEmbedded;
				return (int)command.ExecuteScalar() == 1;
			}
		}
	}
}
