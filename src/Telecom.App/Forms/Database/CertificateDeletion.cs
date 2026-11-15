using System;
using System.Data;
using System.Linq;
using Telecom.Core.Database;
using Telecom.Core.Logging;

namespace Telecom.App.Forms.Database
{
	// Удаление строки из вкладки «База данных»: что именно удаляется, текст подтверждения и сам запрос с записью в журнал.
	// Удаление привязано к версии строки (время внесения и обновления), которую видел оператор.
	internal sealed class CertificateDeletion
	{
		internal string OwnerName { get; private set; }
		internal string SerialNumber { get; private set; }
		internal DateTime CreatedAt { get; private set; }
		internal DateTime UpdatedAt { get; private set; }
		internal string ComputerAddress { get; private set; }
		internal string SourcePath { get; private set; }
		internal int RemainingLocations { get; private set; }
		internal string Details { get; private set; }

		// Удаление сертификата из реестра вместе со всеми привязками.
		internal static CertificateDeletion ForCertificate(DataRow row)
		{
			CertificateDeletion deletion = FromRow(row);
			deletion.Details = "Сертификат и записи обо всех привязанных к нему компьютерах будут удалены из базы данных.";
			return deletion;
		}

		// Удаление одного места обнаружения. Если других мест у сертификата нет, удаляется и сам сертификат.
		internal static CertificateDeletion ForLocation(DataRow row)
		{
			CertificateDeletion deletion = FromRow(row);
			deletion.ComputerAddress = (string)row["ComputerAddress"];
			deletion.SourcePath = (string)row["SourcePath"];
			string[] remainingComputers = row.Table.Rows.Cast<DataRow>()
				.Where(location => location != row && (string)location["SerialNumber"] == deletion.SerialNumber)
				.Select(location => (string)location["ComputerAddress"]).ToArray();
			deletion.RemainingLocations = remainingComputers.Length;
			string details = deletion.RemainingLocations == 0
				? "Это последний компьютер, привязанный к сертификату под серийным номером " + deletion.SerialNumber
					+ ". При удалении исчезнет запись как о компьютере, так и о сертификате в обеих таблицах."
					+ "\r\n\r\nКомпьютер " + deletion.ComputerAddress
				: "Будет удалена запись о компьютере для выбранного пути. Сертификат останется в реестре."
					+ "\r\nКомпьютер " + deletion.ComputerAddress + ", привязанный к сертификату с серийным номером " + deletion.SerialNumber + ".";
			details += "\r\n\r\n" + deletion.SourcePath;
			if (deletion.RemainingLocations > 0)
			{
				details += "\r\n\r\nОставшиеся компьютеры\r\n" + string.Join(", ", remainingComputers.Distinct(StringComparer.OrdinalIgnoreCase));
			}

			deletion.Details = details;
			return deletion;
		}

		// Общие поля строки обеих таблиц.
		private static CertificateDeletion FromRow(DataRow row)
		{
			return new CertificateDeletion
			{
				OwnerName = (string)row["OwnerName"],
				SerialNumber = (string)row["SerialNumber"],
				CreatedAt = (DateTime)row["CreatedAt"],
				UpdatedAt = (DateTime)row["UpdatedAt"]
			};
		}

		// Выполняет удаление и записывает его исход в журнал вместе с учётной записью оператора; возвращает текст для статуса.
		internal string Execute(DatabaseConnection database, ILogger logger)
		{
			string auditDetails = string.Format("Серийный номер: {0}. Оператор: {1}\\{2}.",
				SerialNumber, Environment.UserDomainName, Environment.UserName);
			try
			{
				string status;
				var deleter = new CertificateDeleter(database);
				if (ComputerAddress == null)
				{
					status = deleter.TryDelete(SerialNumber, CreatedAt, UpdatedAt)
						? "Запись удалена." : "Запись уже удалена или обновлена с момента загрузки.";
				}
				else
				{
					auditDetails += " Компьютер: " + ComputerAddress + ". Путь: " + SourcePath + ".";
					bool deleted = deleter.TryDeleteLocation(SerialNumber, CreatedAt, UpdatedAt, ComputerAddress, SourcePath, RemainingLocations);
					status = !deleted ? "Данные изменились. Проверьте список и подтвердите удаление снова."
						: RemainingLocations == 0 ? "Записи о компьютере и сертификате удалены из базы данных."
						: "Запись о компьютере для выбранного пути удалена. Сертификат остаётся в реестре.";
				}

				logger.Info(status + " " + auditDetails);
				return status;
			}
			catch (Exception exception)
			{
				logger.Error("Ошибка удаления записи из базы данных. " + auditDetails, exception);
				return "Не удалось удалить запись. Подробности в журнале.";
			}
		}
	}
}
