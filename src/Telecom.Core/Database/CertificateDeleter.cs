using System;
using System.Data;
using System.Data.SqlClient;

namespace Telecom.Core.Database
{
	// Удаление записей, подтверждённых оператором. Удаление срабатывает, только если строка не менялась после загрузки.
	public sealed class CertificateDeleter
	{
		private readonly DatabaseConnection database;

		// Запоминает базу, из которой оператор удаляет записи.
		public CertificateDeleter(DatabaseConnection database)
		{
			this.database = database;
		}

		// Удаляет просмотренную версию записи, если её не обновили после загрузки таблицы.
		public bool TryDelete(string serialNumber, DateTime createdAt, DateTime updatedAt)
		{
			const string sql = @"
DELETE FROM dbo.Certificates
WHERE SerialNumber = @SerialNumber AND CreatedAt = @CreatedAt AND UpdatedAt = @UpdatedAt;
SELECT @@ROWCOUNT;";
			using (var connection = new SqlConnection(database.ConnectionString))
			using (var command = new SqlCommand(sql, connection))
			{
				CertificateVersionParameters.Add(command, serialNumber, createdAt, updatedAt);
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
			using (var connection = new SqlConnection(database.ConnectionString))
			using (var command = new SqlCommand(sql, connection))
			{
				CertificateVersionParameters.Add(command, serialNumber, createdAt, updatedAt);
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
	}
}
