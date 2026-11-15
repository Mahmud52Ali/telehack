using System;
using System.Data;
using System.Data.SqlClient;
using Telecom.Core.Logging;

namespace Telecom.Core.Database
{
	// Строка подключения к MSSQL и открытие соединений. Каждая операция открывает своё короткое соединение.
	public sealed class DatabaseConnection
	{
		private const int CreateDatabaseDeniedErrorNumber = 262;
		internal const int TableNotFoundErrorNumber = 208;
		internal const int InvalidColumnErrorNumber = 207;

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

		private readonly string databaseName;
		private readonly ILogger logger;

		public string ConnectionString { get; private set; }

		// Имя базы из строки подключения.
		public string DatabaseName { get { return databaseName; } }

		// Проверяет строку подключения сразу, чтобы ошибка настройки была видна до первого запроса.
		public DatabaseConnection(string connectionString, ILogger logger)
		{
			databaseName = ParseConnectionString(connectionString).InitialCatalog;
			ConnectionString = connectionString;
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

		// Открывает соединение с базой, перед этим создавая её, если её ещё нет.
		// Проверка идёт до подключения: после неудачного входа драйвер несколько секунд повторяет ту же ошибку.
		public SqlConnection OpenCreatingDatabase()
		{
			CreateDatabaseIfMissing();
			return OpenExisting();
		}

		// Открывает соединение с уже существующей базой.
		public SqlConnection OpenExisting()
		{
			var connection = new SqlConnection(ConnectionString);
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
			var builder = new SqlConnectionStringBuilder(ConnectionString);
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

				throw new InvalidOperationException("База данных \"" + databaseName + "\" не найдена на сервере, "
					+ "а у учётной записи нет права её создать. "
					+ "Создайте базу вручную или выдайте учётной записи право на создание баз.", exception);
			}

			if (created)
			{
				logger.Info("Создана база данных: " + databaseName);
			}
		}

		// Ищет номер ошибки SQL Server среди всех ошибок, вернувшихся с сервера.
		internal static bool HasErrorNumber(SqlException exception, int number)
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
	}
}
