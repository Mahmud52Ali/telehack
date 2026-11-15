using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Telecom.Core.Certificates;
using Telecom.Core.Database;
using Telecom.Core.Locations;
using Telecom.Core.Logging;

namespace Telecom.Tests
{
	// Собирает классы работы с MSSQL в один объект с прежними именами методов, чтобы тесты БД читались компактно.
	internal sealed class TestDatabase
	{
		private readonly DatabaseConnection database;
		private readonly ILogger logger;

		// Проверяет строку подключения так же, как приложение.
		internal TestDatabase(string connectionString, ILogger logger)
		{
			database = new DatabaseConnection(connectionString, logger);
			this.logger = logger;
		}

		// Создаёт базу и таблицы, если их нет.
		internal void EnsureTableExists()
		{
			DatabaseSchema.EnsureExists(database);
		}

		// Читает реестр сертификатов.
		internal DataTable ReadAll()
		{
			return new CertificateReader(database).ReadAll();
		}

		// Читает места обнаружения сертификатов.
		internal DataTable ReadLocations()
		{
			return new CertificateReader(database).ReadLocations();
		}

		// Записывает сертификаты без адреса компьютера.
		internal CertificateSaveResult Save(IEnumerable<CertificateInfo> certificates, string cryptoProVersion)
		{
			return new CertificateWriter(database, logger).Save(certificates, cryptoProVersion, null);
		}

		// Записывает сертификаты и места их обнаружения на компьютере.
		internal CertificateSaveResult Save(IEnumerable<CertificateInfo> certificates, string cryptoProVersion, string computerAddress)
		{
			return new CertificateWriter(database, logger).Save(certificates, cryptoProVersion, computerAddress);
		}

		// Сохраняет итоги проверки наличия.
		internal int SaveLocationChecks(IEnumerable<CertificateLocationCheck> checks)
		{
			return new LocationCheckWriter(database).SaveLocationChecks(checks);
		}

		// Удаляет сертификат, если строка не менялась.
		internal bool TryDelete(string serialNumber, DateTime createdAt, DateTime updatedAt)
		{
			return new CertificateDeleter(database).TryDelete(serialNumber, createdAt, updatedAt);
		}

		// Удаляет одно место обнаружения, если данные не менялись.
		internal bool TryDeleteLocation(string serialNumber, DateTime createdAt, DateTime updatedAt,
			string computerAddress, string sourcePath, int remainingLocations)
		{
			return new CertificateDeleter(database).TryDeleteLocation(serialNumber, createdAt, updatedAt,
				computerAddress, sourcePath, remainingLocations);
		}
	}
}
