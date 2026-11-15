using System;
using System.Data.SqlClient;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.Core.Database;

namespace Telecom.App.Forms.Settings
{
	// Вкладка «База данных»: строка подключения к MSSQL и проверка входа.
	internal sealed class DatabaseSettingsTab : TabPage
	{
		private const string ExampleConnectionString =
			"Server=YOUR_SQL_SERVER;Database=Certificates;Integrated Security=True;Connect Timeout=5";

		private readonly StatusReporter status;
		private readonly TextBox connectionInput = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
		private readonly CheckBox showConnection = new CheckBox { Text = "Показать", AutoSize = true, Anchor = AnchorStyles.Left };
		private readonly Button checkDatabase = new Button { Text = "Проверить подключение", AutoSize = true };
		private readonly Label databaseStatus = new Label { Dock = DockStyle.Top, AutoSize = true };

		// Пользователь изменил строку подключения.
		internal event EventHandler Changed;

		// Строка подключения без пробелов по краям.
		internal string ConnectionString { get { return connectionInput.Text.Trim(); } }

		// Показывает строку MSSQL и проверяет вход с введёнными параметрами.
		internal DatabaseSettingsTab(StatusReporter status) : base("База данных")
		{
			this.status = status;
			var layout = new TableLayoutPanel
			{
				Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), ColumnCount = 2, RowCount = 5
			};
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			var label = new Label { Text = "Строка подключения Microsoft SQL Server", AutoSize = true };
			layout.Controls.Add(label, 0, 0);
			layout.SetColumnSpan(label, 2);
			connectionInput.AccessibleName = label.Text;
			connectionInput.TextChanged += (sender, args) =>
			{
				databaseStatus.Text = string.Empty;
				EventHandler changed = Changed;
				if (changed != null)
				{
					changed(this, EventArgs.Empty);
				}
			};
			layout.Controls.Add(connectionInput, 0, 1);
			showConnection.CheckedChanged += (sender, args) => connectionInput.UseSystemPasswordChar = !showConnection.Checked;
			layout.Controls.Add(showConnection, 1, 1);
			var hint = new Label { AutoSize = true, Text = "Укажите сервер и имя базы. Пустая строка отключает запись результатов в MSSQL." };
			layout.Controls.Add(hint, 0, 2);
			layout.SetColumnSpan(hint, 2);
			var example = new Button { Text = "Подставить пример", AutoSize = true, Enabled = connectionInput.TextLength == 0 };
			connectionInput.TextChanged += (sender, args) => example.Enabled = string.IsNullOrWhiteSpace(connectionInput.Text);
			example.Click += (sender, args) =>
			{
				connectionInput.Text = ExampleConnectionString;
				showConnection.Checked = true;
				connectionInput.Focus();
			};
			checkDatabase.Click += OnCheckDatabase;
			FlowLayoutPanel actions = StatusReporter.CreateActions(checkDatabase, example);
			layout.Controls.Add(actions, 0, 3);
			layout.SetColumnSpan(actions, 2);
			layout.Controls.Add(databaseStatus, 0, 4);
			layout.SetColumnSpan(databaseStatus, 2);
			Controls.Add(layout);
		}

		// Подставляет сохранённую строку и снова скрывает её символы.
		internal void ShowConnectionString(string connectionString)
		{
			connectionInput.Text = connectionString;
			showConnection.Checked = false;
			databaseStatus.Text = string.Empty;
		}

		// Скрывает строку подключения при выходе из настроек.
		internal void HideConnectionString()
		{
			showConnection.Checked = false;
		}

		// Открывает новое соединение без команд SQL и показывает результат только для проверенной строки.
		private async void OnCheckDatabase(object sender, EventArgs args)
		{
			string connectionString = ConnectionString;
			if (connectionString.Length == 0)
			{
				status.Show(databaseStatus, "Введите строку подключения.", true);
				connectionInput.Focus();
				return;
			}

			checkDatabase.Enabled = false;
			status.Show(databaseStatus, "Проверка подключения…", false);
			try
			{
				SqlConnectionStringBuilder settings = DatabaseConnection.ParseConnectionString(connectionString);
				settings.Pooling = false;
				using (var connection = new SqlConnection(settings.ConnectionString))
				{
					await connection.OpenAsync();
				}

				if (!IsDisposed && ConnectionString == connectionString)
				{
					status.Show(databaseStatus, "Подключение к базе данных установлено.", false);
				}
			}
			catch (Exception exception)
			{
				Label target = !IsDisposed && ConnectionString == connectionString ? databaseStatus : null;
				status.ReportFailure(this, target, "Не удалось подключиться к MSSQL.", exception);
			}
			finally
			{
				if (!IsDisposed)
				{
					checkDatabase.Enabled = true;
				}
			}
		}
	}
}
