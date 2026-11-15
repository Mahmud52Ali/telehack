using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Computers;
using Telecom.Core.Database;
using Telecom.Core.Logging;

namespace Telecom.Core.Locations
{
	// Сценарий «Проверить наличие»: для каждого ПК из сохранённых мест обнаружения перечитывает файлы
	// или записи хранилища, сверяет серийный номер и сохраняет итог в БД. Работает синхронно.
	public sealed class LocationCheckCoordinator
	{
		private readonly ICertificateFileSystem fileSystem;
		private readonly IMachineCertificateStore machineStore;
		private readonly IComputerConnector connector;
		private readonly ILogger logger;

		// Использует те же адаптеры доступа к ПК, что и сканирование.
		public LocationCheckCoordinator(ICertificateFileSystem fileSystem, IMachineCertificateStore machineStore,
			IComputerConnector connector, ILogger logger)
		{
			this.fileSystem = fileSystem;
			this.machineStore = machineStore;
			this.connector = connector;
			this.logger = logger;
		}

		// Создаёт задание проверки из строки таблицы мест; время внесения и обновления защищают от изменения строки во время проверки.
		public static CertificateLocationCheck FromRow(DataRow row)
		{
			return new CertificateLocationCheck((string)row["SerialNumber"], (string)row["ComputerAddress"],
				(string)row["SourcePath"], (DateTime)row["CreatedAt"], (DateTime)row["UpdatedAt"]);
		}

		// Проверяет одно выбранное место или (selectedLocation = null) все сохранённые места, по одному ПК за раз.
		// Итоги каждого ПК сохраняются сразу, поэтому при отмене уже проверенное не теряется.
		public LocationCheckSummary Run(DatabaseConnection database, IDictionary<string, ComputerProfile> profiles,
			CertificateLocationCheck selectedLocation, int connectionTimeoutSeconds, IProgress<LocationCheckProgress> progress,
			CancellationToken cancellationToken)
		{
			var summary = new LocationCheckSummary();
			var checker = new CertificateLocationChecker(fileSystem, machineStore, connector, connectionTimeoutSeconds, logger);
			var writer = new LocationCheckWriter(database);
			try
			{
				IEnumerable<CertificateLocationCheck> locations = selectedLocation == null
					? new CertificateReader(database).ReadLocations().Rows.Cast<DataRow>().Select(FromRow)
					: new[] { selectedLocation };
				List<IGrouping<string, CertificateLocationCheck>> groups =
					locations.GroupBy(location => location.ComputerAddress, StringComparer.OrdinalIgnoreCase).ToList();
				for (int index = 0; index < groups.Count; index++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					string computerAddress = groups[index].Key;
					List<CertificateLocationCheck> computerLocations = groups[index].ToList();
					ComputerProfile profile;
					profiles.TryGetValue(computerAddress, out profile);
					progress.Report(new LocationCheckProgress(computerAddress, index + 1, groups.Count));
					try
					{
						checker.CheckComputer(computerAddress, profile, computerLocations, cancellationToken);
					}
					catch (OperationCanceledException)
					{
						if (!cancellationToken.IsCancellationRequested)
						{
							throw;
						}
					}

					List<CertificateLocationCheck> finished = computerLocations.Where(location => location.CheckedAtUtc != default(DateTime)).ToList();
					summary.Saved += writer.SaveLocationChecks(finished);
					summary.Checked += finished.Count;
					cancellationToken.ThrowIfCancellationRequested();
				}
			}
			catch (OperationCanceledException)
			{
				summary.Cancelled = true;
			}

			return summary;
		}
	}
}
