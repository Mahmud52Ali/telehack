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
		private const int CreateDatabaseDeniedErrorNumber = 262;

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

		// Блокировка приложения пропускает к проверке и созданию таблицы только один запуск за раз.
		private const string CreateTableSql = @"
EXEC sp_getapplock @Resource = N'Telecom.Certificates.CreateTable', @LockMode = N'Exclusive', @LockOwner = N'Transaction';
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
		// Время берётся с сервера базы данных и не уменьшается, даже если запуски пересеклись во времени.
		private const string SaveCertificateSql = @"
DECLARE @SavedAt DATETIME2(3) = SYSDATETIME();

UPDATE dbo.Certificates WITH (UPDLOCK, HOLDLOCK)
SET UpdatedAt = CASE WHEN @SavedAt > UpdatedAt THEN @SavedAt ELSE UpdatedAt END,
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
		private readonly string databaseName;
		private readonly ILogger logger;

		// Запоминает строку подключения; соединение открывается только на время одной операции.
		public CertificateRepository(string connectionString, ILogger logger)
		{
			if (string.IsNullOrWhiteSpace(connectionString))
			{
				throw new ArgumentException("Не указана строка подключения к базе данных.", "connectionString");
			}

			// Без имени базы сервер подключил бы к базе по умолчанию, и таблица появилась бы не там, где её ждут.
			databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
			if (string.IsNullOrWhiteSpace(databaseName))
			{
				throw new ArgumentException("В строке подключения не указано имя базы данных (Database).", "connectionString");
			}

			this.connectionString = connectionString;
			this.logger = logger;
		}

		// Создаёт базу и таблицу сертификатов, если их ещё нет; существующие не изменяет.
		public void EnsureTableExists()
		{
			using (SqlConnection connection = OpenConnection())
			{
				CreateTableIfMissing(connection);
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
			using (SqlConnection connection = OpenConnection())
			{
				CreateTableIfMissing(connection);
				using (SqlTransaction transaction = connection.BeginTransaction())
				{
					foreach (CertificateInfo certificate in certificates)
					{
						// Один сертификат может лежать в нескольких папках; в таблице ему соответствует одна строка.
						if (!savedSerialNumbers.Add(certificate.SerialNumber))
						{
							continue;
						}

						if (SaveCertificate(connection, transaction, certificate, cryptoProVersion))
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

		// Создаёт таблицу в отдельной короткой транзакции: блокировка создания снимается при её завершении.
		private static void CreateTableIfMissing(SqlConnection connection)
		{
			using (SqlTransaction transaction = connection.BeginTransaction())
			{
				using (var command = new SqlCommand(CreateTableSql, connection, transaction))
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
				return (int)command.ExecuteScalar() == 1;
			}
		}
	}
}
