using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.App.Theming;
using Telecom.Core.Certificates;
using Telecom.Core.Scanning;

namespace Telecom.App.Forms.Scan
{
	// Вкладки «Сертификаты» и «Ошибки» с результатами последнего запуска.
	internal sealed class ScanResultsPanel : UserControl
	{
		private readonly DataGridView certificatesGrid = new DataGridView();
		private readonly DataGridView errorsGrid = new DataGridView();

		// Строит обе таблицы; данные подставляются при запуске сканирования.
		internal ScanResultsPanel()
		{
			Dock = DockStyle.Fill;
			Margin = Padding.Empty;
			GridFactory.ConfigureGrid(certificatesGrid);
			GridFactory.AddColumn(certificatesGrid, "OwnerName", "Владелец", 110);
			GridFactory.AddColumn(certificatesGrid, "CertificationAuthority", "Удостоверяющий центр", 110);
			GridFactory.AddColumn(certificatesGrid, "SerialNumber", "Серийный номер", 65);
			certificatesGrid.Columns.Add(new DataGridViewCheckBoxColumn
			{
				DataPropertyName = "IsCryptoProLicenseEmbedded", HeaderText = "Лицензия в сертификате", FillWeight = 55
			});
			GridFactory.AddColumn(certificatesGrid, "SourcePath", "Источник", 160);
			GridFactory.ConfigureGrid(errorsGrid);
			GridFactory.AddColumn(errorsGrid, "Kind", "Категория", 55);
			GridFactory.AddColumn(errorsGrid, "Path", "Путь", 120);
			GridFactory.AddColumn(errorsGrid, "Message", "Причина", 140);
			errorsGrid.CellFormatting += OnErrorFormatting;

			var tabs = new AppearanceTabControl { Dock = DockStyle.Fill };
			var certificateTab = new TabPage("Сертификаты");
			certificateTab.Controls.Add(certificatesGrid);
			var errorTab = new TabPage("Ошибки");
			errorTab.Controls.Add(errorsGrid);
			tabs.TabPages.Add(certificateTab);
			tabs.TabPages.Add(errorTab);
			Controls.Add(tabs);
		}

		// Показывает список ошибок, который будет пополняться по ходу запуска.
		internal void ShowErrors(BindingList<ScanError> errors)
		{
			errorsGrid.DataSource = errors;
		}

		// Показывает список сертификатов, который будет пополняться по ходу сканирования.
		internal void ShowCertificates(BindingList<CertificateInfo> certificates)
		{
			certificatesGrid.DataSource = certificates;
		}

		// Показывает локализованную категорию ошибки, сохраняя исходное сообщение Windows в соседнем столбце.
		private void OnErrorFormatting(object sender, DataGridViewCellFormattingEventArgs args)
		{
			if (args.Value is ScanErrorKind)
			{
				args.Value = DisplayNames.ErrorKind((ScanErrorKind)args.Value);
				args.FormattingApplied = true;
			}
		}
	}
}
