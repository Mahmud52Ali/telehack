using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Telecom.Core.Database;

namespace Telecom.Core.Locations
{
	// Сохранение итогов «Проверить наличие» в dbo.CertificateLocations.
	public sealed class LocationCheckWriter
	{
		private readonly DatabaseConnection database;

		// Запоминает базу, в которую сохраняются итоги проверки.
		public LocationCheckWriter(DatabaseConnection database)
		{
			this.database = database;
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
			using (var connection = new SqlConnection(database.ConnectionString))
			{
				connection.Open();
				DatabaseSchema.Ensure(connection);
				using (SqlTransaction transaction = connection.BeginTransaction())
				using (var command = new SqlCommand(sql, connection, transaction))
				{
					foreach (CertificateLocationCheck check in checks.OrderBy(value => value.SerialNumber, StringComparer.OrdinalIgnoreCase))
					{
						command.Parameters.Clear();
						CertificateVersionParameters.Add(command, check.SerialNumber, check.CertificateCreatedAt, check.CertificateUpdatedAt);
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
	}
}
