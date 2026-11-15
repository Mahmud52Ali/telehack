using System.Drawing;
using System.Windows.Forms;

namespace Telecom.App.Forms.Common
{
	// Единое оформление таблиц приложения.
	internal static class GridFactory
	{
		// Настраивает таблицу только для просмотра: без добавления и удаления строк, выделение целой строкой.
		internal static void ConfigureGrid(DataGridView grid)
		{
			grid.Dock = DockStyle.Fill;
			grid.AutoGenerateColumns = false;
			grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
			grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
			grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
			grid.ReadOnly = true;
			grid.AllowUserToAddRows = false;
			grid.AllowUserToDeleteRows = false;
			grid.RowHeadersVisible = false;
			grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
			grid.BackgroundColor = SystemColors.Window;
			grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
		}

		// Добавляет текстовый столбец, привязанный к свойству модели или столбцу таблицы данных.
		internal static DataGridViewTextBoxColumn AddColumn(DataGridView grid, string property, string title, float weight)
		{
			var column = new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, FillWeight = weight };
			grid.Columns.Add(column);
			return column;
		}
	}
}
