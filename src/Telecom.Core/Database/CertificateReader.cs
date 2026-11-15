using System;
using System.Data;
using System.Data.SqlClient;

namespace Telecom.Core.Database
{
	// Чтение таблиц для вкладки «База данных».
	public sealed class CertificateReader
	{
		private const string ReadLocationsSql = @"
SELECT certificate.SerialNumber, certificate.OwnerName, certificate.CertificationAuthority, location.ComputerAddress,
	location.SourcePath, certificate.ExpiresAtUtc, certificate.CreatedAt, certificate.UpdatedAt,
	location.CheckStatus, location.CheckedAtUtc
FROM dbo.CertificateLocations AS location
INNER JOIN dbo.Certificates AS certificate ON certificate.SerialNumber = location.SerialNumber
ORDER BY location.SerialNumber, location.ComputerAddress, location.SourcePath;";

		private const string ReadAllSql = @"
SELECT SerialNumber, CreatedAt, UpdatedAt, OwnerName, CertificationAuthority,
	CryptoProVersion, IsCryptoProLicenseEmbedded, ExpiresAtUtc
FROM dbo.Certificates
ORDER BY UpdatedAt DESC, SerialNumber;";

		private readonly DatabaseConnection database;

		// Запоминает базу, из которой читаются записи.
		public CertificateReader(DatabaseConnection database)
		{
			this.database = database;
		}

		// Читает реестр сертификатов; если таблицы ещё нет, сообщает, что она появится после первого сканирования.
		public DataTable ReadAll()
		{
			try
			{
				return ReadRows(ReadAllSql);
			}
			catch (SqlException exception)
			{
				ThrowIfOutdatedTables(exception);
				if (!DatabaseConnection.HasErrorNumber(exception, DatabaseConnection.TableNotFoundErrorNumber))
				{
					throw;
				}

				throw new InvalidOperationException("Таблица dbo.Certificates ещё не создана. Она появится после записи результатов сканирования.",
					exception);
			}
		}

		// Читает места обнаружения вместе со сроком из реестра; если таблиц ещё нет, возвращает пустую таблицу.
		public DataTable ReadLocations()
		{
			try
			{
				return ReadRows(ReadLocationsSql);
			}
			catch (SqlException exception)
			{
				ThrowIfOutdatedTables(exception);
				if (!DatabaseConnection.HasErrorNumber(exception, DatabaseConnection.TableNotFoundErrorNumber))
				{
					throw;
				}

				return new DataTable();
			}
		}

		// Ошибка «нет такого столбца» означает таблицы ранней версии программы; заменяет её понятным сообщением.
		private void ThrowIfOutdatedTables(SqlException exception)
		{
			if (DatabaseConnection.HasErrorNumber(exception, DatabaseConnection.InvalidColumnErrorNumber))
			{
				throw new InvalidOperationException(DatabaseSchema.OutdatedTablesMessage(database.DatabaseName), exception);
			}
		}

		// Загружает результат запроса для привязки к таблице приложения.
		private DataTable ReadRows(string sql)
		{
			using (var connection = new SqlConnection(database.ConnectionString))
			using (var adapter = new SqlDataAdapter(sql, connection))
			{
				var rows = new DataTable();
				adapter.Fill(rows);
				return rows;
			}
		}
	}
}
