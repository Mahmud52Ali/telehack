using System;
using System.Data;
using System.Data.SqlClient;

namespace Telecom.Core.Database
{
	// Параметры «версии строки» сертификата: серийный номер и время внесения/обновления, которые видел оператор.
	internal static class CertificateVersionParameters
	{
		// Привязывает подтверждение к версии строки, которую видел оператор.
		internal static void Add(SqlCommand command, string serialNumber, DateTime createdAt, DateTime updatedAt)
		{
			if (string.IsNullOrWhiteSpace(serialNumber) || serialNumber.Length > 128)
			{
				throw new ArgumentException("Требуется полный серийный номер длиной до 128 символов.", "serialNumber");
			}

			command.Parameters.Add("@SerialNumber", SqlDbType.NVarChar, 128).Value = serialNumber;
			command.Parameters.Add(new SqlParameter("@CreatedAt", SqlDbType.DateTime2) { Scale = 3, Value = createdAt });
			command.Parameters.Add(new SqlParameter("@UpdatedAt", SqlDbType.DateTime2) { Scale = 3, Value = updatedAt });
		}
	}
}
