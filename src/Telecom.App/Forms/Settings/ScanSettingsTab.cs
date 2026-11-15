using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.Core.Configuration;

namespace Telecom.App.Forms.Settings
{
	// Вкладка «Сканирование»: общие папки с сертификатами, пути к csptest.exe и таймаут подключения.
	internal sealed class ScanSettingsTab : TabPage
	{
		private readonly DataGridView directoriesGrid = new DataGridView();
		private readonly DataGridView cryptoPathsGrid = new DataGridView();
		private readonly NumericUpDown timeoutInput = new NumericUpDown { Minimum = 1, Maximum = 60, Value = 3, Width = 64 };

		// Пользователь изменил любое поле вкладки.
		internal event EventHandler Changed;

		// Даёт двум спискам отдельную прокрутку и оставляет таймаут под ними.
		internal ScanSettingsTab() : base("Сканирование")
		{
			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 3 };
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.Controls.Add(CreatePathGroup(directoriesGrid, "Общие папки с сертификатами",
				"Укажите путь от буквы диска, например C:\\Certs. Звёздочка * заменяет имя папки целиком.", "Добавить папку"), 0, 0);
			layout.Controls.Add(CreatePathGroup(cryptoPathsGrid, "Версия КриптоПро",
				"Пути проверяются сверху вниз. Версия берётся из ProductVersion файла csptest.exe.", "Добавить путь"), 0, 1);

			timeoutInput.AccessibleName = "Таймаут подключения к ПК, секунды";
			timeoutInput.TextChanged += (sender, args) => OnChanged();
			timeoutInput.ValueChanged += (sender, args) => OnChanged();
			layout.Controls.Add(StatusReporter.CreateActions(
				new Label { AutoSize = true, Text = "Таймаут подключения к ПК", Margin = new Padding(3, 6, 3, 0) },
				timeoutInput,
				new Label { AutoSize = true, Text = "с (1–60, TCP-порт 445)", Margin = new Padding(3, 6, 3, 0) }), 0, 2);
			Controls.Add(layout);
		}

		// Подставляет сохранённые значения.
		internal void ShowConfiguration(ScanConfiguration configuration)
		{
			directoriesGrid.Rows.Clear();
			foreach (string path in configuration.Directories)
			{
				directoriesGrid.Rows.Add(path);
			}

			cryptoPathsGrid.Rows.Clear();
			foreach (string path in configuration.CryptoProExecutables)
			{
				cryptoPathsGrid.Rows.Add(path);
			}

			timeoutInput.Value = configuration.ConnectionTimeoutSeconds;
		}

		// Возвращает общие папки в порядке показа, включая ошибочно оставленное пустое поле.
		internal string[] ReadDirectories()
		{
			return ReadPaths(directoriesGrid, "Добавьте хотя бы одну общую папку с сертификатами.");
		}

		// Возвращает пути к csptest.exe в порядке показа.
		internal string[] ReadCryptoProExecutables()
		{
			return ReadPaths(cryptoPathsGrid, "Добавьте хотя бы один путь к csptest.exe.");
		}

		// Возвращает таймаут или ставит курсор в поле и сообщает об ошибке.
		internal int ReadTimeout()
		{
			int timeout;
			if (!int.TryParse(timeoutInput.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out timeout) || timeout < 1 || timeout > 60)
			{
				timeoutInput.Focus();
				throw new InvalidDataException("Введите целый таймаут от 1 до 60 секунд.");
			}

			return timeout;
		}

		// Завершает редактирование ячеек перед сохранением.
		internal void EndEdit()
		{
			directoriesGrid.EndEdit();
			cryptoPathsGrid.EndEdit();
		}

		// Завершает редактирование и возвращает непустые и пустые строки таблицы по порядку.
		private static string[] ReadPaths(DataGridView grid, string requiredMessage)
		{
			if (grid.Rows.Count == 0)
			{
				throw new InvalidDataException(requiredMessage);
			}

			return grid.Rows.Cast<DataGridViewRow>().Select(row => Convert.ToString(row.Cells[0].Value)).ToArray();
		}

		// Добавляет редактируемые строки в таблицу с фиксированной областью просмотра.
		private Control CreatePathGroup(DataGridView grid, string title, string hint, string addCaption)
		{
			grid.Dock = DockStyle.Fill;
			grid.AccessibleName = title;
			grid.BackgroundColor = SystemColors.Window;
			grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
			grid.ColumnHeadersVisible = false;
			grid.RowHeadersVisible = false;
			grid.AllowUserToAddRows = false;
			grid.AllowUserToDeleteRows = false;
			grid.AllowUserToResizeRows = false;
			grid.MultiSelect = false;
			grid.EditMode = DataGridViewEditMode.EditOnEnter;
			grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Путь", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
			grid.Columns.Add(new DataGridViewButtonColumn
			{
				HeaderText = "Удаление", Text = "Удалить", UseColumnTextForButtonValue = true,
				MinimumWidth = 82, AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells
			});
			grid.CellValueChanged += (sender, args) => OnChanged();
			grid.CurrentCellDirtyStateChanged += (sender, args) =>
			{
				if (grid.IsCurrentCellDirty)
				{
					OnChanged();
				}
			};
			grid.CellContentClick += OnRemovePath;

			var add = new Button { Text = addCaption, AutoSize = true, Anchor = AnchorStyles.Left };
			add.Click += (sender, args) => AddPath(grid);
			var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.Controls.Add(new Label { Text = hint, AutoSize = true }, 0, 0);
			layout.Controls.Add(grid, 0, 1);
			layout.Controls.Add(add, 0, 2);
			group.Controls.Add(layout);
			return group;
		}

		// Добавляет пустое поле и прокручивает до него.
		private void AddPath(DataGridView grid)
		{
			grid.EndEdit();
			int index = grid.Rows.Add(string.Empty);
			grid.CurrentCell = grid.Rows[index].Cells[0];
			grid.Focus();
			grid.BeginEdit(true);
			OnChanged();
		}

		// Удаляет строку из черновика только после подтверждения.
		private void OnRemovePath(object sender, DataGridViewCellEventArgs args)
		{
			if (args.RowIndex < 0 || args.ColumnIndex != 1)
			{
				return;
			}

			var grid = (DataGridView)sender;
			grid.EndEdit();
			string path = Convert.ToString(grid.Rows[args.RowIndex].Cells[0].Value);
			string message = path.Length == 0 ? "Удалить пустую строку?" : "Удалить путь из настроек?\r\n\r\n" + path;
			if (MessageBox.Show(this, message, "Удаление пути", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
				MessageBoxDefaultButton.Button2) == DialogResult.Yes)
			{
				grid.Rows.RemoveAt(args.RowIndex);
				OnChanged();
			}
		}

		// Сообщает панели настроек об изменении.
		private void OnChanged()
		{
			EventHandler changed = Changed;
			if (changed != null)
			{
				changed(this, EventArgs.Empty);
			}
		}
	}
}
