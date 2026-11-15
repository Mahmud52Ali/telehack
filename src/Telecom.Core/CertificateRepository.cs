using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;

namespace Telecom.Core
{
	public sealed class CertificateSaveResult
	{
		public int Inserted { get; internal set; }
		public int Updated { get; internal set; }
	}

	public sealed class CertificateRepository
	{
		private const int CreateDatabaseDeniedErrorNumber = 262;
		private const int TableNotFoundErrorNumber = 208;
		private const string ReadLocationsSql = @"
SELECT certificate.SerialNumber, certificate.OwnerName, certificate.CertificationAuthority, location.ComputerAddress,
	location.SourcePath, certificate.ExpiresAtUtc, certificate.CreatedAt, certificate.UpdatedAt, {0}, {1}
FROM dbo.CertificateLocations AS location
INNER JOIN dbo.Certificates AS certificate ON certificate.SerialNumber = location.SerialNumber
ORDER BY location.SerialNumber, location.ComputerAddress, location.SourcePath;";

		// Имя базы экранирует сам сервер (QUOTENAME); блокировка не даёт двум запускам создавать базу одновременно.
		private const string CreateDatabaseSql = @"
EXEC sp_getapplock @Resource = N'Telecom.Certificates.CreateDatabase', @LockMode = N'Exclusive', @LockOwner = N'Session';
BEGIN TRY
	DECLARE @Created BIT = 0;
	IF DB_ID(@DatabaseName) IS NULL
	BEGIN
		DECLARE @CreateSql NVARCHAR(300) = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName);
		EXEC (@CreateSql);
		SET @Created = 1;
	END
	EXEC sp_releaseapplock @Resource = N'Telecom.Certificates.CreateDatabase', @LockOwner = N'Session';
	SELECT @Created;
END TRY
BEGIN CATCH
	EXEC sp_releaseapplock @Resource = N'Telecom.Certificates.CreateDatabase', @LockOwner = N'Session';
	THROW;
END CATCH";

		// Блокировка приложения разрешает одному запуску за раз создать или обновить схему.
		private const string EnsureSchemaSql = @"
DECLARE @LockResult INT;
EXEC @LockResult = sp_getapplock @Resource = N'Telecom.Certificates.CreateTable', @LockMode = N'Exclusive', @LockOwner = N'Transaction';
IF @LockResult < 0
	THROW 50000, N'Не удалось получить блокировку для обновления схемы сертификатов.', 1;

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
ELSE IF COL_LENGTH(N'dbo.Certificates', N'ExpiresAtUtc') IS NULL
BEGIN
	ALTER TABLE dbo.Certificates ADD ExpiresAtUtc DATETIME2(0) NULL;
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
END
ELSE
BEGIN
	IF COL_LENGTH(N'dbo.CertificateLocations', N'CheckStatus') IS NULL
		ALTER TABLE dbo.CertificateLocations ADD CheckStatus TINYINT NULL;
	IF COL_LENGTH(N'dbo.CertificateLocations', N'CheckedAtUtc') IS NULL
		ALTER TABLE dbo.CertificateLocations ADD CheckedAtUtc DATETIME2(3) NULL;
END";

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

		private readonly string connectionString;
		private readonly string databaseName;
		private readonly ILogger logger;

		// Запоминает строку подключения; соединение открывается только на время одной операции.
		public CertificateRepository(string connectionString, ILogger logger)
		{
			databaseName = ParseConnectionString(connectionString).InitialCatalog;
			this.connectionString = connectionString;
			this.logger = logger;
		}

		// Проверяет параметры MSSQL и требует явного имени целевой базы.
		public static SqlConnectionStringBuilder ParseConnectionString(string connectionString)
		{
			if (string.IsNullOrWhiteSpace(connectionString))
			{
				throw new ArgumentException("Не указана строка подключения к базе данных.", "connectionString");
			}

			var settings = new SqlConnectionStringBuilder(connectionString);
			if (string.IsNullOrWhiteSpace(settings.InitialCatalog))
			{
				throw new ArgumentException("В строке подключения не указано имя базы данных (Database).", "connectionString");
			}

			return settings;
		}

		// Готовит базу и схему, сохраняя существующие записи.
		public void EnsureTableExists()
		{
			using (SqlConnection connection = OpenConnection())
			{
				EnsureSchema(connection);
			}
		}

		// Читает реестр и поддерживает прежнюю схему до первого сохранения результатов.
		public DataTable ReadAll()
		{
			const string select = "SELECT SerialNumber, CreatedAt, UpdatedAt, OwnerName, CertificationAuthority, "
				+ "CryptoProVersion, IsCryptoProLicenseEmbedded, ";
			using (var connection = new SqlConnection(connectionString))
			using (var schema = new SqlCommand("SELECT COL_LENGTH(N'dbo.Certificates', N'ExpiresAtUtc');", connection))
			{
				try
				{
					connection.Open();
					string expiresAt = schema.ExecuteScalar() == DBNull.Value
						? "CAST(NULL AS DATETIME2(0)) AS ExpiresAtUtc" : "ExpiresAtUtc";
					return ReadRows(connection, select + expiresAt + " FROM dbo.Certificates ORDER BY UpdatedAt DESC, SerialNumber;");
				}
				catch (SqlException exception)
				{
					if (!HasErrorNumber(exception, TableNotFoundErrorNumber))
					{
						throw;
					}

					throw new InvalidOperationException("Таблица dbo.Certificates ещё не создана. Она появится после записи результатов сканирования.",
						exception);
				}
			}
		}

		// Читает места обнаружения и подставляет срок из реестра сертификатов.
		public DataTable ReadLocations()
		{
			using (var connection = new SqlConnection(connectionString))
			using (var schema = new SqlCommand(@"
SELECT COL_LENGTH(N'dbo.CertificateLocations', N'CheckStatus'),
	COL_LENGTH(N'dbo.CertificateLocations', N'CheckedAtUtc');", connection))
			{
				try
				{
					connection.Open();
					string status;
					string checkedAt;
					using (SqlDataReader reader = schema.ExecuteReader())
					{
						reader.Read();
						status = reader.IsDBNull(0) ? "CAST(NULL AS TINYINT) AS CheckStatus" : "location.CheckStatus";
						checkedAt = reader.IsDBNull(1) ? "CAST(NULL AS DATETIME2(3)) AS CheckedAtUtc" : "location.CheckedAtUtc";
					}

					return ReadRows(connection, string.Format(ReadLocationsSql, status, checkedAt));
				}
				catch (SqlException exception)
				{
					if (!HasErrorNumber(exception, TableNotFoundErrorNumber))
					{
						throw;
					}

					return new DataTable();
				}
			}
		}

		// Сохраняет итоги проверки одного ПК; обновлённые или удалённые находки пропускает.
		public int SaveLocationChecks(IEnumerable<CertificateLocationCheck> checks)
		{
			const string sql = @"
IF EXISTS (
	SELECT 1 FROM dbo.Certificates WITH (UPDLOCK, HOLDLOCK)
	WHERE SerialNumber = @SerialNumber AND CreatedAt = @CreatedAt AND UpdatedAt = @UpdatedAt
)
BEGIN
	UPDATE dbo.CertificateLocations
	SET CheckStatus = @CheckStatus, CheckedAtUtc = @CheckedAtUtc
	WHERE SerialNumber = @SerialNumber AND ComputerAddress = @ComputerAddress AND SourcePath = @SourcePath
		AND (CheckedAtUtc IS NULL OR CheckedAtUtc <= @CheckedAtUtc);
	SELECT @@ROWCOUNT;
END
ELSE
	SELECT 0;";
			int saved = 0;
			using (var connection = new SqlConnection(connectionString))
			{
				connection.Open();
				EnsureSchema(connection);
				using (SqlTransaction transaction = connection.BeginTransaction())
				using (var command = new SqlCommand(sql, connection, transaction))
				{
					foreach (CertificateLocationCheck check in checks.OrderBy(value => value.SerialNumber, StringComparer.OrdinalIgnoreCase))
					{
						command.Parameters.Clear();
						AddCertificateVersionParameters(command, check.SerialNumber, check.CertificateCreatedAt, check.CertificateUpdatedAt);
						command.Parameters.Add("@ComputerAddress", SqlDbType.NVarChar, 255).Value = check.ComputerAddress;
						command.Parameters.Add("@SourcePath", SqlDbType.NVarChar, -1).Value = check.SourcePath;
						command.Parameters.Add("@CheckStatus", SqlDbType.TinyInt).Value = (byte)check.Status;
						command.Parameters.Add(new SqlParameter("@CheckedAtUtc", SqlDbType.DateTime2) { Scale = 3, Value = check.CheckedAtUtc });
						saved += (int)command.ExecuteScalar();
					}
					transaction.Commit();
				}
			}
			return saved;
		}

		// Загружает результат запроса для привязки к таблице приложения.
		private static DataTable ReadRows(SqlConnection connection, string sql)
		{
			using (var adapter = new SqlDataAdapter(sql, connection))
			{
				var rows = new DataTable();
				adapter.Fill(rows);
				return rows;
			}
		}

		// Удаляет просмотренную версию записи, если её не обновили после загрузки таблицы.
		public bool TryDelete(string serialNumber, DateTime createdAt, DateTime updatedAt)
		{
			const string sql = @"
DELETE FROM dbo.Certificates
WHERE SerialNumber = @SerialNumber AND CreatedAt = @CreatedAt AND UpdatedAt = @UpdatedAt;
SELECT @@ROWCOUNT;";
			using (var connection = new SqlConnection(connectionString))
			using (var command = new SqlCommand(sql, connection))
			{
				AddCertificateVersionParameters(command, serialNumber, createdAt, updatedAt);
				connection.Open();
				return (int)command.ExecuteScalar() == 1;
			}
		}

		// Удаляет выбранную привязку, если число оставшихся находок совпадает с подтверждением.
		public bool TryDeleteLocation(string serialNumber, DateTime createdAt, DateTime updatedAt,
			string computerAddress, string sourcePath, int remainingLocations)
		{
			const string sql = @"
IF NOT EXISTS (
	SELECT 1 FROM dbo.Certificates WITH (UPDLOCK, HOLDLOCK)
	WHERE SerialNumber = @SerialNumber AND CreatedAt = @CreatedAt AND UpdatedAt = @UpdatedAt
)
BEGIN
	SELECT CAST(0 AS bit);
	RETURN;
END;

DELETE FROM dbo.CertificateLocations
WHERE SerialNumber = @SerialNumber AND ComputerAddress = @ComputerAddress AND SourcePath = @SourcePath;

IF @@ROWCOUNT <> 1 OR (
	SELECT COUNT(*) FROM dbo.CertificateLocations WITH (HOLDLOCK) WHERE SerialNumber = @SerialNumber
) <> @RemainingLocations
BEGIN
	SELECT CAST(0 AS bit);
	RETURN;
END;

IF @RemainingLocations = 0
	DELETE FROM dbo.Certificates WHERE SerialNumber = @SerialNumber;

SELECT CAST(1 AS bit);";
			using (var connection = new SqlConnection(connectionString))
			using (var command = new SqlCommand(sql, connection))
			{
				AddCertificateVersionParameters(command, serialNumber, createdAt, updatedAt);
				command.Parameters.Add("@ComputerAddress", SqlDbType.NVarChar, 255).Value = computerAddress;
				command.Parameters.Add("@SourcePath", SqlDbType.NVarChar, -1).Value = sourcePath;
				command.Parameters.Add("@RemainingLocations", SqlDbType.Int).Value = remainingLocations;
				connection.Open();
				using (SqlTransaction transaction = connection.BeginTransaction())
				{
					command.Transaction = transaction;
					if (!(bool)command.ExecuteScalar())
					{
						transaction.Rollback();
						return false;
					}

					transaction.Commit();
					return true;
				}
			}
		}

		// Привязывает подтверждение к версии строки, которую видел оператор.
		private static void AddCertificateVersionParameters(SqlCommand command, string serialNumber, DateTime createdAt, DateTime updatedAt)
		{
			if (string.IsNullOrWhiteSpace(serialNumber) || serialNumber.Length > 128)
			{
				throw new ArgumentException("Требуется полный серийный номер длиной до 128 символов.", "serialNumber");
			}

			command.Parameters.Add("@SerialNumber", SqlDbType.NVarChar, 128).Value = serialNumber;
			command.Parameters.Add(new SqlParameter("@CreatedAt", SqlDbType.DateTime2) { Scale = 3, Value = createdAt });
			command.Parameters.Add(new SqlParameter("@UpdatedAt", SqlDbType.DateTime2) { Scale = 3, Value = updatedAt });
		}

		// Сохраняет сертификаты, для которых вызывающий код не передал адрес компьютера.
		public CertificateSaveResult Save(IEnumerable<CertificateInfo> certificates, string cryptoProVersion)
		{
			return Save(certificates, cryptoProVersion, null);
		}

		// Записывает сертификаты и их места обнаружения в одной транзакции.
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
			using (SqlConnection connection = OpenConnection())
			{
				EnsureSchema(connection);
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

		// Открывает соединение с базой из строки подключения, перед этим создавая базу, если её ещё нет.
		// Проверка идёт до подключения: после неудачного входа драйвер несколько секунд повторяет ту же ошибку.
		private SqlConnection OpenConnection()
		{
			CreateDatabaseIfMissing();
			var connection = new SqlConnection(connectionString);
			try
			{
				connection.Open();
				return connection;
			}
			catch
			{
				connection.Dispose();
				throw;
			}
		}

		// Создаёт базу через служебную базу master: к несуществующей базе подключиться нельзя.
		private void CreateDatabaseIfMissing()
		{
			var builder = new SqlConnectionStringBuilder(connectionString);
			builder.InitialCatalog = "master";
			bool created;
			try
			{
				using (var connection = new SqlConnection(builder.ConnectionString))
				using (var command = new SqlCommand(CreateDatabaseSql, connection))
				{
					command.Parameters.Add("@DatabaseName", SqlDbType.NVarChar, 128).Value = databaseName;
					connection.Open();
					created = (bool)command.ExecuteScalar();
				}
			}
			catch (SqlException exception)
			{
				// Остальные ошибки (сервер недоступен, неверный пароль) передаются вызывающему коду без изменений.
				if (!HasErrorNumber(exception, CreateDatabaseDeniedErrorNumber))
				{
					throw;
				}

				throw new InvalidOperationException("База данных \"" + databaseName + "\" не найдена на сервере, а у учётной записи нет права её создать. "
					+ "Создайте базу вручную или выдайте учётной записи право на создание баз.", exception);
			}

			if (created)
			{
				logger.Info("Создана база данных: " + databaseName);
			}
		}

		// Ищет номер ошибки SQL Server среди всех ошибок, вернувшихся с сервера.
		private static bool HasErrorNumber(SqlException exception, int number)
		{
			foreach (SqlError error in exception.Errors)
			{
				if (error.Number == number)
				{
					return true;
				}
			}

			return false;
		}

		// Готовит схему в отдельной короткой транзакции и освобождает блокировку после её завершения.
		private static void EnsureSchema(SqlConnection connection)
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
