using System.Data.SqlClient;

namespace Telecom.Core.Database
{
	// Создание таблиц dbo.Certificates и dbo.CertificateLocations, если их нет.
	public static class DatabaseSchema
	{
		// Блокировка приложения разрешает одному запуску за раз создать таблицы.
		private const string EnsureSchemaSql = @"
DECLARE @LockResult INT;
EXEC @LockResult = sp_getapplock @Resource = N'Telecom.Certificates.CreateTable', @LockMode = N'Exclusive', @LockOwner = N'Transaction';
IF @LockResult < 0
	THROW 50000, N'Не удалось получить блокировку для создания таблиц сертификатов.', 1;

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
		IsCryptoProLicenseEmbedded BIT NOT NULL,
		ExpiresAtUtc DATETIME2(0) NULL
	);
END

IF OBJECT_ID(N'dbo.CertificateLocations', N'U') IS NULL
BEGIN
	CREATE TABLE dbo.CertificateLocations
	(
		SerialNumber NVARCHAR(128) NOT NULL,
		ComputerAddress NVARCHAR(255) NOT NULL,
		SourcePath NVARCHAR(MAX) NOT NULL,
		CheckStatus TINYINT NULL,
		CheckedAtUtc DATETIME2(3) NULL,
		CONSTRAINT FK_CertificateLocations_Certificates FOREIGN KEY (SerialNumber)
			REFERENCES dbo.Certificates (SerialNumber) ON DELETE CASCADE
	);
	CREATE CLUSTERED INDEX IX_CertificateLocations_SerialNumber_ComputerAddress
		ON dbo.CertificateLocations (SerialNumber, ComputerAddress);
END";

		// Создаёт базу и таблицы, если их нет; существующие записи не меняются.
		public static void EnsureExists(DatabaseConnection database)
		{
			using (SqlConnection connection = database.OpenCreatingDatabase())
			{
				Ensure(connection);
			}
		}

		// Готовит схему в отдельной короткой транзакции и освобождает блокировку после её завершения.
		internal static void Ensure(SqlConnection connection)
		{
			using (SqlTransaction transaction = connection.BeginTransaction())
			{
				using (var command = new SqlCommand(EnsureSchemaSql, connection, transaction))
				{
					command.ExecuteNonQuery();
				}

				transaction.Commit();
			}
		}
	}
}
