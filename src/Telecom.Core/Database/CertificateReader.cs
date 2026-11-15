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
	location.SourcePath, certificate.ExpiresAtUtc, certificate.CreatedAt, certificate.UpdatedAt, {0}, {1}
FROM dbo.CertificateLocations AS location
INNER JOIN dbo.Certificates AS certificate ON certificate.SerialNumber = location.SerialNumber
ORDER BY location.SerialNumber, location.ComputerAddress, location.SourcePath;";

		private readonly DatabaseConnection database;

		// Запоминает базу, из которой читаются записи.
		public CertificateReader(DatabaseConnection database)
		{
			this.database = database;
		}

		// Читает реестр и поддерживает прежнюю схему до первого сохранения результатов.
		public DataTable ReadAll()
		{
			const string select = "SELECT SerialNumber, CreatedAt, UpdatedAt, OwnerName, CertificationAuthority, "
				+ "CryptoProVersion, IsCryptoProLicenseEmbedded, ";
			using (var connection = new SqlConnection(database.ConnectionString))
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
					if (!DatabaseConnection.HasErrorNumber(exception, DatabaseConnection.TableNotFoundErrorNumber))
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
			using (var connection = new SqlConnection(database.ConnectionString))
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
					if (!DatabaseConnection.HasErrorNumber(exception, DatabaseConnection.TableNotFoundErrorNumber))
					{
						throw;
					}

					return new DataTable();
				}
			}
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
	}
}
