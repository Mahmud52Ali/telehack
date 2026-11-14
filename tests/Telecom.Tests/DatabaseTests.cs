using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Telecom.Core;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private const string DatabaseVariable = "TELECOM_TEST_SQL";
		private static string databaseConnectionString;

		// Регистрирует проверки MSSQL; без строки подключения они пропускаются, а не считаются успешными.
		private static void AddDatabaseTests(ICollection<KeyValuePair<string, Action>> tests)
		{
			databaseConnectionString = Environment.GetEnvironmentVariable(DatabaseVariable);
			if (string.IsNullOrWhiteSpace(databaseConnectionString))
			{
				Console.WriteLine("SKIP: проверки MSSQL не выполнялись — не задана переменная " + DatabaseVariable);
				return;
			}

			// Проверки удаляют таблицу, поэтому работают только с отдельной тестовой базой.
			string database = new SqlConnectionStringBuilder(databaseConnectionString).InitialCatalog;
			if (!database.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Имя базы для проверок должно оканчиваться на Tests: " + DatabaseVariable);
			}

			tests.Add(new KeyValuePair<string, Action>("БД: отсутствующая таблица создаётся, повторный вызов безопасен", CheckTableCreation));
			tests.Add(new KeyValuePair<string, Action>("БД: первое внесение, время обновления равно времени внесения", CheckFirstSave));
			tests.Add(new KeyValuePair<string, Action>("БД: повторное внесение обновляет строки без дублей", CheckRepeatedSave));
			tests.Add(new KeyValuePair<string, Action>("БД: один серийный номер в нескольких папках — одна строка", CheckDuplicateSerialNumber));
			tests.Add(new KeyValuePair<string, Action>("БД: неизвестная версия КриптоПро и текст с кавычками", CheckMissingVersionAndQuotes));
			tests.Add(new KeyValuePair<string, Action>("БД: одновременные первые запуски не спорят за создание таблицы", CheckParallelTableCreation));
			tests.Add(new KeyValuePair<string, Action>("БД: пересекающиеся записи не дают дублей и времени обновления раньше внесения", CheckParallelSave));
			tests.Add(new KeyValuePair<string, Action>("БД: недоступный сервер даёт исключение, а не пустой успех", CheckUnavailableDatabase));
		}

		// Проверяет создание таблицы на пустой базе и отсутствие ошибки при втором вызове.
		private static void CheckTableCreation()
		{
			DropCertificatesTable();
			var repository = new CertificateRepository(databaseConnectionString, new MemoryLogger());
			repository.EnsureTableExists();
			Check.Equal(0, CountRows());
			repository.EnsureTableExists();
			Check.Equal(0, CountRows());
		}

		// Проверяет, что запись сама создаёт таблицу и заполняет все поля из условий кейса.
		private static void CheckFirstSave()
		{
			DropCertificatesTable();
			var repository = new CertificateRepository(databaseConnectionString, new MemoryLogger());
			CertificateSaveResult result = repository.Save(CreateCertificates("Иванов Иван Иванович"), "5.0.13003");
			Check.Equal(2, result.Inserted);
			Check.Equal(0, result.Updated);
			Check.Equal(2, CountRows());

			StoredCertificate stored = ReadCertificate("03A14016");
			Check.Equal("Иванов Иван Иванович", stored.OwnerName);
			Check.Equal("Тестовый УЦ", stored.CertificationAuthority);
			Check.Equal("5.0.13003", stored.CryptoProVersion);
			Check.Equal(true, stored.IsCryptoProLicenseEmbedded);
			Check.Equal(stored.CreatedAt, stored.UpdatedAt);
			Check.Equal(false, ReadCertificate("378F0E85").IsCryptoProLicenseEmbedded);
		}

		// Проверяет главное требование кейса: повторная запись не добавляет вторую строку.
		private static void CheckRepeatedSave()
		{
			DropCertificatesTable();
			var repository = new CertificateRepository(databaseConnectionString, new MemoryLogger());
			repository.Save(CreateCertificates("Иванов Иван Иванович"), "5.0.13003");
			StoredCertificate first = ReadCertificate("03A14016");

			Thread.Sleep(50);
			CertificateSaveResult result = repository.Save(CreateCertificates("Иванов И. И."), "5.0.13455");
			Check.Equal(0, result.Inserted);
			Check.Equal(2, result.Updated);
			Check.Equal(2, CountRows());

			StoredCertificate second = ReadCertificate("03A14016");
			Check.Equal(first.CreatedAt, second.CreatedAt);
			Check.Equal(true, second.UpdatedAt > first.UpdatedAt);
			Check.Equal("Иванов И. И.", second.OwnerName);
			Check.Equal("5.0.13455", second.CryptoProVersion);
		}

		// Проверяет, что копии одного сертификата из разных папок не считаются разными записями.
		private static void CheckDuplicateSerialNumber()
		{
			DropCertificatesTable();
			var certificates = new List<CertificateInfo>();
			certificates.Add(new CertificateInfo("03A14016", "Иванов", "УЦ", @"C:\Certs\a.cer", false));
			certificates.Add(new CertificateInfo("03a14016", "Иванов", "УЦ", @"D:\Certs\a.cer", false));
			var repository = new CertificateRepository(databaseConnectionString, new MemoryLogger());
			CertificateSaveResult result = repository.Save(certificates, null);
			Check.Equal(1, result.Inserted);
			Check.Equal(0, result.Updated);
			Check.Equal(1, CountRows());
		}

		// Проверяет запись NULL вместо версии и сохранение текста с кавычками без искажения запроса.
		private static void CheckMissingVersionAndQuotes()
		{
			DropCertificatesTable();
			string ownerName = "О'Коннор'); DROP TABLE dbo.Certificates; --";
			var certificates = new List<CertificateInfo>();
			certificates.Add(new CertificateInfo("0A", ownerName, "УЦ \"Тест\"", @"C:\Certs\a.cer", false));
			var repository = new CertificateRepository(databaseConnectionString, new MemoryLogger());
			repository.Save(certificates, null);

			StoredCertificate stored = ReadCertificate("0A");
			Check.Equal(ownerName, stored.OwnerName);
			Check.Equal("УЦ \"Тест\"", stored.CertificationAuthority);
			Check.Equal(null, stored.CryptoProVersion);
		}

		// Проверяет гонку первого обращения: пока таблицы нет, её одновременно создают несколько запусков.
		private static void CheckParallelTableCreation()
		{
			for (int attempt = 0; attempt < 5; attempt++)
			{
				DropCertificatesTable();
				Parallel.For(0, 8, index =>
				{
					var repository = new CertificateRepository(databaseConnectionString, new MemoryLogger());
					if (index % 2 == 0)
					{
						repository.EnsureTableExists();
					}
					else
					{
						repository.Save(CreateCertificates("Иванов Иван Иванович"), "5.0.13003");
					}
				});
				Check.Equal(2, CountRows());
			}
		}

		// Проверяет, что при пересекающихся записях строк не становится больше, а время обновления не раньше внесения.
		private static void CheckParallelSave()
		{
			DropCertificatesTable();
			Parallel.For(0, 40, index =>
			{
				var repository = new CertificateRepository(databaseConnectionString, new MemoryLogger());
				repository.Save(CreateCertificates("Иванов " + index), "5.0.13003");
			});
			Check.Equal(2, CountRows());
			Check.Equal(0, (int)ExecuteScalar("SELECT COUNT(*) FROM dbo.Certificates WHERE UpdatedAt < CreatedAt;"));
		}

		// Проверяет, что ошибка подключения доходит до вызывающего кода.
		private static void CheckUnavailableDatabase()
		{
			var repository = new CertificateRepository(
				"Server=127.0.0.1,1;Database=TelecomTests;User Id=nobody;Password=nothing;Connect Timeout=2", new MemoryLogger());
			Check.Throws<SqlException>(() => repository.Save(CreateCertificates("Иванов"), null));
			Check.Throws<ArgumentException>(() => new CertificateRepository(" ", new MemoryLogger()));
		}

		// Создаёт два сертификата с серийными номерами образцов организаторов.
		private static IList<CertificateInfo> CreateCertificates(string firstOwnerName)
		{
			var certificates = new List<CertificateInfo>();
			certificates.Add(new CertificateInfo("03A14016", firstOwnerName, "Тестовый УЦ", @"C:\Certs\ivanov.cer", true));
			certificates.Add(new CertificateInfo("378F0E85", "Петров Петр Петрович", "Тестовый УЦ", @"C:\Certs\petrov.cer", false));
			return certificates;
		}

		// Удаляет таблицу тестовой базы, чтобы каждая проверка начиналась с пустого состояния.
		private static void DropCertificatesTable()
		{
			ExecuteScalar("IF OBJECT_ID(N'dbo.Certificates', N'U') IS NOT NULL DROP TABLE dbo.Certificates; SELECT 0;");
		}

		// Возвращает число строк таблицы сертификатов.
		private static int CountRows()
		{
			return (int)ExecuteScalar("SELECT COUNT(*) FROM dbo.Certificates;");
		}

		// Выполняет служебный запрос проверки без пользовательских данных в тексте SQL.
		private static object ExecuteScalar(string sql)
		{
			using (var connection = new SqlConnection(databaseConnectionString))
			using (var command = new SqlCommand(sql, connection))
			{
				connection.Open();
				return command.ExecuteScalar();
			}
		}

		// Читает одну строку таблицы независимо от проверяемого класса.
		private static StoredCertificate ReadCertificate(string serialNumber)
		{
			const string sql = "SELECT CreatedAt, UpdatedAt, OwnerName, CertificationAuthority, CryptoProVersion, "
				+ "IsCryptoProLicenseEmbedded FROM dbo.Certificates WHERE SerialNumber = @SerialNumber;";
			using (var connection = new SqlConnection(databaseConnectionString))
			using (var command = new SqlCommand(sql, connection))
			{
				command.Parameters.AddWithValue("@SerialNumber", serialNumber);
				connection.Open();
				using (SqlDataReader reader = command.ExecuteReader())
				{
					if (!reader.Read())
					{
						throw new InvalidOperationException("В таблице нет сертификата " + serialNumber);
					}

					var stored = new StoredCertificate();
					stored.CreatedAt = reader.GetDateTime(0);
					stored.UpdatedAt = reader.GetDateTime(1);
					stored.OwnerName = reader.GetString(2);
					stored.CertificationAuthority = reader.GetString(3);
					stored.CryptoProVersion = reader.IsDBNull(4) ? null : reader.GetString(4);
					stored.IsCryptoProLicenseEmbedded = reader.GetBoolean(5);
					return stored;
				}
			}
		}

		private sealed class StoredCertificate
		{
			public DateTime CreatedAt;
			public DateTime UpdatedAt;
			public string OwnerName;
			public string CertificationAuthority;
			public string CryptoProVersion;
			public bool IsCryptoProLicenseEmbedded;
		}
	}
}
