using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.App.Theming;
using Telecom.Core.Access;
using Telecom.Core.Computers;
using Telecom.Core.Configuration;
using Telecom.Core.Database;
using Telecom.Core.Locations;
using Telecom.Core.Logging;

namespace Telecom.App.Forms.Database
{
	// Страница «База данных»: реестр сертификатов и места обнаружения, удаление записей и проверка наличия.
	internal sealed class DatabasePage : UserControl
	{
		private readonly ILogger logger;
		private readonly string configurationFilePath;
		private readonly ICertificateFileSystem computerAccess;
		private readonly IComputerConnector connection;
		private readonly IMachineCertificateStore machineCertificateStore;
		private readonly OperationGate operations;
		private readonly Func<IList<ComputerProfile>> savedProfiles;
		private readonly Func<int> fontPercent;
		private readonly TabControl databaseTables = new AppearanceTabControl { Dock = DockStyle.Fill };
		private readonly TabPage certificateLocationsPage = new TabPage("Сертификаты по компьютерам");
		private readonly DataGridView databaseGrid = new DataGridView();
		private readonly DataGridView certificateLocationsGrid = new DataGridView();
		private readonly Button refreshDatabaseButton = new Button { Text = "Обновить" };
		private readonly Button checkLocationsButton = new Button { Text = "Проверить наличие", AutoSize = true, Visible = false };
		private readonly ToolStripMenuItem checkLocationMenuItem = new ToolStripMenuItem("Проверить наличие");
		private readonly Label databaseStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, AutoEllipsis = true };
		private DatabaseConnection displayedDatabase;
		private bool databaseRefreshPending;
		private bool deletingCertificate;
		private bool checkingLocations;

		// Открыта ли эта страница; загруженные данные показываются только на открытой странице.
		internal bool IsActive { get; set; }

		// Идёт ли удаление записи; окно не закрывается до ответа базы.
		internal bool IsDeleting { get { return deletingCertificate; } }

		// Идёт ли проверка наличия, запущенная с этой страницы.
		internal bool IsCheckingLocations { get { return checkingLocations; } }

		// savedProfiles возвращает профили ПК для проверки наличия или null, пока список не загружен.
		internal DatabasePage(ILogger logger, string configurationFilePath, ICertificateFileSystem computerAccess, IComputerConnector connection,
			IMachineCertificateStore machineCertificateStore, OperationGate operations, Func<IList<ComputerProfile>> savedProfiles,
			Func<int> fontPercent)
		{
			this.logger = logger;
			this.configurationFilePath = configurationFilePath;
			this.computerAccess = computerAccess;
			this.connection = connection;
			this.machineCertificateStore = machineCertificateStore;
			this.operations = operations;
			this.savedProfiles = savedProfiles;
			this.fontPercent = fontPercent;
			Dock = DockStyle.Fill;
			BuildLayout();
		}

		// Даёт переключаться между реестром и местами обнаружения сертификатов.
		private void BuildLayout()
		{
			CertificateGrids.ConfigureRegistryGrid(databaseGrid, OnDeleteCertificate);
			CertificateGrids.ConfigureLocationsGrid(certificateLocationsGrid, OnDeleteCertificate);
			BuildLocationMenu();

			var registryPage = new TabPage("Реестр сертификатов");
			registryPage.Controls.Add(databaseGrid);
			certificateLocationsPage.Controls.Add(certificateLocationsGrid);
			databaseTables.TabPages.Add(registryPage);
			databaseTables.TabPages.Add(certificateLocationsPage);
			databaseTables.SelectedIndexChanged += async (sender, args) =>
			{
				UpdateState();
				await RefreshDatabase();
			};

			refreshDatabaseButton.Click += async (sender, args) => await RefreshDatabase();
			checkLocationsButton.Click += OnCheckLocations;
			var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
			actions.Controls.Add(checkLocationsButton);
			actions.Controls.Add(refreshDatabaseButton);

			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			layout.Controls.Add(databaseStatusLabel, 0, 0);
			layout.Controls.Add(actions, 1, 0);
			layout.Controls.Add(databaseTables, 0, 1);
			layout.SetColumnSpan(databaseTables, 2);
			Controls.Add(layout);
		}

		// Меню по правой кнопке мыши: «Проверить наличие» для одной строки мест обнаружения.
		private void BuildLocationMenu()
		{
			var locationMenu = new ContextMenuStrip();
			locationMenu.Items.Add(checkLocationMenuItem);
			locationMenu.Opening += (sender, args) =>
			{
				locationMenu.Font = certificateLocationsGrid.Font;
				UpdateState();
				args.Cancel = !checkLocationMenuItem.Enabled;
			};
			checkLocationMenuItem.Click += OnCheckLocation;
			certificateLocationsGrid.ContextMenuStrip = locationMenu;
			certificateLocationsGrid.MouseDown += (sender, args) =>
			{
				if (args.Button == MouseButtons.Right)
				{
					DataGridView.HitTestInfo hit = certificateLocationsGrid.HitTest(args.X, args.Y);
					certificateLocationsGrid.ClearSelection();
					certificateLocationsGrid.CurrentCell = hit.RowIndex < 0 ? null
						: certificateLocationsGrid.Rows[hit.RowIndex].Cells[Math.Max(0, hit.ColumnIndex)];
				}
			};
			certificateLocationsGrid.Disposed += (sender, args) => locationMenu.Dispose();
		}

		// Делает кнопку «Обновить» того же размера, что и образец (кнопка навигации окна).
		internal void AlignRefreshButton(Control reference)
		{
			refreshDatabaseButton.Size = reference.Size;
			reference.SizeChanged += (sender, args) => refreshDatabaseButton.Size = reference.Size;
		}

		// Показывает сообщение в строке состояния страницы.
		internal void ShowStatus(string message)
		{
			databaseStatusLabel.Text = message;
		}

		// Включает проверку при открытом списке сохранённых путей, а во время работы даёт её отменить.
		internal void UpdateState()
		{
			checkLocationsButton.Visible = databaseTables.SelectedTab == certificateLocationsPage;
			checkLocationsButton.Text = checkingLocations ? "Отменить проверку" : "Проверить наличие";
			var locations = certificateLocationsGrid.DataSource as DataTable;
			checkLocationsButton.Enabled = checkingLocations ? !operations.IsCancellationRequested
				: savedProfiles() != null && !operations.IsBusy && refreshDatabaseButton.Enabled
					&& displayedDatabase != null && locations != null && locations.Rows.Count > 0;
			checkLocationMenuItem.Enabled = !checkingLocations && checkLocationsButton.Enabled
				&& certificateLocationsGrid.CurrentRow != null;
		}

		// Выполняет один запрос за раз и повторяет его, если вкладку открыли снова во время загрузки.
		internal async Task RefreshDatabase()
		{
			if (!refreshDatabaseButton.Enabled)
			{
				databaseRefreshPending = true;
				return;
			}

			SetBusy(true);
			try
			{
				await LoadDatabaseRows(null);
			}
			finally
			{
				if (!IsDisposed)
				{
					SetBusy(false);
				}
			}
		}

		// Блокирует обновление и действия на время запроса к базе.
		private void SetBusy(bool busy)
		{
			refreshDatabaseButton.Enabled = !busy;
			UpdateState();
		}

		// Загружает таблицу для текущих настроек и связывает результат с его исходной базой.
		private async Task LoadDatabaseRows(string actionStatus)
		{
			displayedDatabase = null;
			databaseGrid.DataSource = null;
			certificateLocationsGrid.DataSource = null;
			databaseStatusLabel.Text = "Загрузка записей…";
			do
			{
				databaseRefreshPending = false;
				bool showingLocations = databaseTables.SelectedTab == certificateLocationsPage;
				DataGridView targetGrid = showingLocations ? certificateLocationsGrid : databaseGrid;
				DatabaseConnection database = null;
				DataTable certificates = null;
				string status;
				try
				{
					certificates = await Task.Run(() =>
					{
						ScanConfiguration configuration = ScanConfigurationFile.Load(configurationFilePath);
						if (configuration.ConnectionString.Length == 0)
						{
							return null;
						}

						database = new DatabaseConnection(configuration.ConnectionString, logger);
						var reader = new CertificateReader(database);
						return showingLocations ? reader.ReadLocations() : reader.ReadAll();
					});
					status = certificates == null
						? "Укажите и сохраните строку подключения в разделе «Настройки → База данных»."
						: "Записей: " + certificates.Rows.Count;
				}
				catch (Exception exception)
				{
					status = "Не удалось загрузить записи. " + exception.Message;
					logger.Error("Не удалось прочитать сертификаты из базы данных.", exception);
				}

				if (!IsDisposed && IsActive && !databaseRefreshPending)
				{
					displayedDatabase = certificates == null ? null : database;
					targetGrid.DataSource = certificates;
					databaseStatusLabel.Text = actionStatus == null ? status : actionStatus + " " + status;
				}
			} while (!IsDisposed && IsActive && !operations.ClosePending && databaseRefreshPending);
		}

		// Кнопка «Проверить наличие» запускает проверку всех мест, во время проверки — отменяет её.
		private async void OnCheckLocations(object sender, EventArgs args)
		{
			if (checkingLocations)
			{
				operations.Cancel();
				databaseStatusLabel.Text = "Отмена… Ожидание завершения текущего чтения.";
				UpdateState();
				return;
			}

			await CheckLocations(null);
		}

		// Проверяет единственную находку, выбранную правой кнопкой мыши.
		private async void OnCheckLocation(object sender, EventArgs args)
		{
			DataGridViewRow selected = certificateLocationsGrid.CurrentRow;
			if (!checkingLocations && selected != null)
			{
				await CheckLocations(LocationCheckCoordinator.FromRow(((DataRowView)selected.DataBoundItem).Row));
			}
		}

		// Сопоставляет сохранённые места со списком ПК и проверяет их независимо от галочек сканирования.
		private async Task CheckLocations(CertificateLocationCheck selectedLocation)
		{
			var rows = certificateLocationsGrid.DataSource as DataTable;
			DatabaseConnection database = displayedDatabase;
			IList<ComputerProfile> profiles = savedProfiles();
			if (database == null || rows == null || rows.Rows.Count == 0 || !refreshDatabaseButton.Enabled
				|| operations.IsBusy || profiles == null)
			{
				return;
			}

			Dictionary<string, ComputerProfile> profilesByAddress = profiles.ToDictionary(
				profile => profile.IsLocal ? Environment.MachineName : profile.IpAddress, StringComparer.OrdinalIgnoreCase);
			checkingLocations = true;
			CancellationToken token = operations.Start();
			SetBusy(true);
			try
			{
				logger.Info(selectedLocation == null ? "Начата проверка наличия всех сохранённых сертификатов."
					: "Начата проверка наличия сертификата. Компьютер: " + selectedLocation.ComputerAddress
						+ "; источник: " + selectedLocation.SourcePath + "; серийный номер: " + selectedLocation.SerialNumber);
				string summary = await RunLocationCheck(database, profilesByAddress, selectedLocation, token);
				if (!IsDisposed && !operations.ClosePending && IsActive)
				{
					await LoadDatabaseRows(summary);
				}
			}
			finally
			{
				checkingLocations = false;
				SetBusy(false);
				operations.Finish();
			}
		}

		// Запускает LocationCheckCoordinator в фоне и возвращает итог для строки состояния.
		private async Task<string> RunLocationCheck(DatabaseConnection database, IDictionary<string, ComputerProfile> profiles,
			CertificateLocationCheck selectedLocation, CancellationToken token)
		{
			try
			{
				ScanConfiguration configuration = await Task.Run(() => ScanConfigurationFile.Load(configurationFilePath), token);
				var coordinator = new LocationCheckCoordinator(computerAccess, machineCertificateStore, connection, logger);
				var progress = new ControlProgress<LocationCheckProgress>(this, step => databaseStatusLabel.Text =
					string.Format("Проверка компьютера {0} ({1} из {2})…", step.ComputerAddress, step.Index, step.Count));
				LocationCheckSummary result = await Task.Run(() => coordinator.Run(database, profiles, selectedLocation,
					configuration.ConnectionTimeoutSeconds, progress, token));
				string summary = result.Cancelled
					? "Проверка отменена. Сохранено результатов: " + result.Saved + "."
					: string.Format("Проверка завершена. Сохранено результатов: {0} из {1}.", result.Saved, result.Checked);
				logger.Info(summary);
				return summary;
			}
			catch (OperationCanceledException)
			{
				const string summary = "Проверка отменена. Сохранено результатов: 0.";
				logger.Info(summary);
				return summary;
			}
			catch (Exception exception)
			{
				logger.Error("Ошибка проверки наличия сертификатов.", exception);
				return "Не удалось завершить проверку. " + exception.Message;
			}
		}

		// Подтверждает конкретную строку и удаляет её из той базы, из которой она была загружена.
		private async void OnDeleteCertificate(object sender, DataGridViewCellEventArgs args)
		{
			var grid = (DataGridView)sender;
			DatabaseConnection database = displayedDatabase;
			if (args.RowIndex < 0 || args.ColumnIndex < 0 || !(grid.Columns[args.ColumnIndex] is DataGridViewButtonColumn)
				|| database == null || !refreshDatabaseButton.Enabled)
			{
				return;
			}

			DataRow row = ((DataRowView)grid.Rows[args.RowIndex].DataBoundItem).Row;
			CertificateDeletion deletion = grid == certificateLocationsGrid
				? CertificateDeletion.ForLocation(row) : CertificateDeletion.ForCertificate(row);
			using (var dialog = new DeleteCertificateDialog(deletion.OwnerName, deletion.SerialNumber, deletion.Details))
			{
				WindowAppearance.ApplyToDialog(dialog, FindForm(), fontPercent());
				if (dialog.ShowDialog(this) != DialogResult.OK)
				{
					return;
				}
			}

			deletingCertificate = true;
			SetBusy(true);
			databaseStatusLabel.Text = "Удаление записи…";
			try
			{
				string status = await Task.Run(() => deletion.Execute(database, logger));
				if (!IsDisposed && !operations.ClosePending && IsActive)
				{
					await LoadDatabaseRows(status);
				}
			}
			finally
			{
				deletingCertificate = false;
				if (!IsDisposed)
				{
					SetBusy(false);
					operations.NotifyChanged();
				}
			}
		}
	}
}
