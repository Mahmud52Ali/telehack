using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.App.Theming;
using Telecom.Core.Locations;

namespace Telecom.App.Forms.Database
{
	// Столбцы и форматирование двух таблиц вкладки «База данных»: реестра сертификатов и мест обнаружения.
	internal static class CertificateGrids
	{
		internal const string ExpiryField = "ExpiresAtUtc";
		private const string TimestampFormat = "dd.MM.yyyy HH:mm:ss.fff";

		// Реестр: одна строка на серийный номер, как в dbo.Certificates.
		internal static void ConfigureRegistryGrid(DataGridView grid, DataGridViewCellEventHandler onDelete)
		{
			GridFactory.ConfigureGrid(grid);
			grid.AccessibleName = "Сохранённые сертификаты";
			GridFactory.AddColumn(grid, "SerialNumber", "Серийный номер", 145);
			GridFactory.AddColumn(grid, "CreatedAt", "Внесён", 155).DefaultCellStyle.Format = TimestampFormat;
			GridFactory.AddColumn(grid, "UpdatedAt", "Обновлён", 155).DefaultCellStyle.Format = TimestampFormat;
			GridFactory.AddColumn(grid, "OwnerName", "Владелец", 165);
			GridFactory.AddColumn(grid, "CertificationAuthority", "Удостоверяющий центр", 165);
			GridFactory.AddColumn(grid, "CryptoProVersion", "Версия КриптоПро", 95).DefaultCellStyle.NullValue = "—";
			grid.Columns.Add(new DataGridViewCheckBoxColumn
			{
				DataPropertyName = "IsCryptoProLicenseEmbedded", HeaderText = "Лицензия в сертификате", FillWeight = 95
			});
			AddExpiryAndDeleteColumns(grid, "Удалить сертификат и записи о привязанных к нему компьютерах", onDelete);
		}

		// Места обнаружения: одна строка на сертификат, компьютер и путь, со статусом последней проверки наличия.
		internal static void ConfigureLocationsGrid(DataGridView grid, DataGridViewCellEventHandler onDelete)
		{
			GridFactory.ConfigureGrid(grid);
			grid.AccessibleName = "Сертификаты по компьютерам";
			GridFactory.AddColumn(grid, "SerialNumber", "Серийный номер", 120);
			GridFactory.AddColumn(grid, "OwnerName", "Владелец", 150);
			GridFactory.AddColumn(grid, "CertificationAuthority", "Удостоверяющий центр", 150);
			GridFactory.AddColumn(grid, "ComputerAddress", "IP компьютера", 105).ToolTipText = "Для локального сканирования здесь указано имя компьютера";
			GridFactory.AddColumn(grid, "SourcePath", "Путь к сертификату", 265);
			GridFactory.AddColumn(grid, "CheckStatus", "Наличие", 240).MinimumWidth = 200;
			GridFactory.AddColumn(grid, "CheckedAtUtc", "Проверено", 150).MinimumWidth = 125;
			grid.CellFormatting += OnLocationCheckFormatting;
			AddExpiryAndDeleteColumns(grid, "Удалить выбранную запись о компьютере", onDelete);
		}

		// Добавляет дату окончания и кнопку удаления с подсказкой для конкретной таблицы.
		private static void AddExpiryAndDeleteColumns(DataGridView grid, string deleteHint, DataGridViewCellEventHandler onDelete)
		{
			DataGridViewColumn expiration = GridFactory.AddColumn(grid, ExpiryField, "Дата истечения", 155);
			expiration.MinimumWidth = 125;
			expiration.DefaultCellStyle.NullValue = "—";
			grid.CellFormatting += OnCertificateExpiryFormatting;
			grid.Columns.Add(new DataGridViewButtonColumn
			{
				HeaderText = "Удалить", Text = "×", UseColumnTextForButtonValue = true, Width = 75,
				AutoSizeMode = DataGridViewAutoSizeColumnMode.None, ToolTipText = deleteHint
			});
			grid.CellContentClick += onDelete;
		}

		// Показывает срок в местном времени и окрашивает ячейку: меньше 30 дней — оранжевая, истёк — красная.
		private static void OnCertificateExpiryFormatting(object sender, DataGridViewCellFormattingEventArgs args)
		{
			var grid = (DataGridView)sender;
			if (!(args.Value is DateTime) || grid.Columns[args.ColumnIndex].DataPropertyName != ExpiryField)
			{
				return;
			}

			DateTime expiresAtUtc = DateTime.SpecifyKind((DateTime)args.Value, DateTimeKind.Utc);
			args.Value = expiresAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
			args.FormattingApplied = true;

			TimeSpan remaining = expiresAtUtc - DateTime.UtcNow;
			if (remaining > TimeSpan.FromDays(30) || SystemInformation.HighContrast)
			{
				return;
			}

			bool expired = remaining <= TimeSpan.Zero;
			bool dark = WindowAppearance.IsDark(grid);
			args.CellStyle.BackColor = dark
				? (expired ? Color.FromArgb(75, 41, 47) : Color.FromArgb(62, 48, 31))
				: (expired ? Color.MistyRose : Color.PeachPuff);
			args.CellStyle.ForeColor = dark
				? (expired ? Color.FromArgb(255, 174, 174) : Color.FromArgb(255, 218, 153))
				: (expired ? Color.DarkRed : Color.SaddleBrown);
			args.CellStyle.SelectionBackColor = args.CellStyle.BackColor;
			args.CellStyle.SelectionForeColor = args.CellStyle.ForeColor;
		}

		// Показывает итог проверки наличия и её время в часовом поясе администратора.
		private static void OnLocationCheckFormatting(object sender, DataGridViewCellFormattingEventArgs args)
		{
			string field = ((DataGridView)sender).Columns[args.ColumnIndex].DataPropertyName;
			if (field == "CheckStatus")
			{
				args.Value = DisplayNames.LocationStatus(args.Value is byte ? (CertificateLocationStatus)(byte)args.Value : 0);
				args.FormattingApplied = true;
			}
			else if (field == "CheckedAtUtc")
			{
				args.Value = args.Value is DateTime
					? DateTime.SpecifyKind((DateTime)args.Value, DateTimeKind.Utc).ToLocalTime()
						.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)
					: "—";
				args.FormattingApplied = true;
			}
		}
	}
}
