using System;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Telecom.Core;

namespace Telecom.App
{
	internal sealed class SettingsPanel : UserControl
	{
		private const string ExampleConnectionString =
			"Server=YOUR_SQL_SERVER;Database=Certificates;Integrated Security=True;Connect Timeout=5";

		private readonly string configurationPath;
		private readonly string templatePath;
		private readonly string appearancePath;
		private readonly ILogger logger;
		private readonly TabControl tabs = new AppearanceTabControl { Dock = DockStyle.Fill };
		private readonly TabPage scanPage = new TabPage("Сканирование");
		private readonly TabPage databasePage = new TabPage("База данных");
		private readonly TabPage filesPage = new TabPage("Файлы и журнал");
		private readonly TabPage personalizationPage = new TabPage("Персонализация");
		private readonly ComboBox themeInput = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
		private readonly ComboBox fontScaleInput = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
		private readonly Label appearanceStatus = new Label { AutoSize = true };
		private readonly DataGridView directoriesGrid = new DataGridView();
		private readonly DataGridView cryptoPathsGrid = new DataGridView();
		private readonly NumericUpDown timeoutInput = new NumericUpDown { Minimum = 1, Maximum = 60, Value = 3, Width = 64 };
		private readonly TextBox connectionInput = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
		private readonly CheckBox showConnection = new CheckBox { Text = "Показать", AutoSize = true, Anchor = AnchorStyles.Left };
		private readonly Button checkDatabase = new Button { Text = "Проверить подключение", AutoSize = true };
		private readonly Button saveButton = new Button { Text = "Сохранить", AutoSize = true, Enabled = false };
		private readonly Button discardButton = new Button { Text = "Отменить изменения", AutoSize = true, Enabled = false };
		private readonly Label statusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
		private readonly Label databaseStatus = new Label { Dock = DockStyle.Top, AutoSize = true };
		private readonly ToolTip statusTip = new ToolTip();
		private Action refreshFileLinks;
		private bool updating;
		private bool updatingAppearance;

		internal bool HasUnsavedChanges { get; private set; }
		internal AppearanceSettings Appearance { get; private set; }
		internal event EventHandler ConfigurationChanged;
		internal event EventHandler AppearanceChanged;

		// Создаёт редакторы конфигурации и оформления.
		internal SettingsPanel(string configurationPath, string templatePath, ILogger logger)
		{
			this.configurationPath = configurationPath;
			this.templatePath = templatePath;
			appearancePath = Path.Combine(Path.GetDirectoryName(configurationPath), "appearance.xml");
			this.logger = logger;
			Dock = DockStyle.Fill;

			scanPage.Controls.Add(BuildScanningPage());
			databasePage.Controls.Add(BuildDatabasePage());
			filesPage.Controls.Add(BuildFilesPage());
			personalizationPage.Controls.Add(BuildPersonalizationPage());
			tabs.TabPages.AddRange(new[] { scanPage, databasePage, filesPage, personalizationPage });
			tabs.Selecting += (sender, args) => args.Cancel = args.TabPage != null && !args.TabPage.Enabled;

			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.Controls.Add(tabs, 0, 0);
			layout.SetColumnSpan(tabs, 2);
			layout.Controls.Add(statusLabel, 0, 1);
			FlowLayoutPanel configurationActions = CreateActions(discardButton, saveButton);
			layout.Controls.Add(configurationActions, 1, 1);
			tabs.SelectedIndexChanged += (sender, args) =>
			{
				bool configurationPage = tabs.SelectedTab != personalizationPage;
				statusLabel.Visible = configurationPage;
				configurationActions.Visible = configurationPage;
				refreshFileLinks();
			};
			saveButton.Click += OnSave;
			discardButton.Click += (sender, args) => LoadConfiguration();
			Controls.Add(layout);
			LoadAppearance();
			themeInput.SelectedIndexChanged += OnAppearanceChanged;
			fontScaleInput.SelectedIndexChanged += OnAppearanceChanged;
		}

		// Даёт выбрать тему и размер шрифта независимо от настроек сканирования.
		private Control BuildPersonalizationPage()
		{
			var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 3, Padding = new Padding(8) };
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			themeInput.AccessibleName = "Тема";
			themeInput.Items.AddRange(new object[] { "Светлая", "Тёмная", "Системная" });
			fontScaleInput.AccessibleName = "Масштаб шрифта";
			for (int percent = AppearanceSettings.MinFontPercent; percent <= AppearanceSettings.MaxFontPercent; percent += AppearanceSettings.FontPercentStep)
			{
				fontScaleInput.Items.Add(percent.ToString(CultureInfo.InvariantCulture) + "%");
			}

			layout.Controls.Add(new Label { Text = "Тема", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
			layout.Controls.Add(themeInput, 1, 0);
			layout.Controls.Add(new Label { Text = "Масштаб шрифта", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
			layout.Controls.Add(fontScaleInput, 1, 1);
			layout.Controls.Add(appearanceStatus, 0, 2);
			layout.SetColumnSpan(appearanceStatus, 2);
			return layout;
		}

		// Повреждённые параметры оформления не мешают открыть главное окно.
		private void LoadAppearance()
		{
			Appearance = AppearanceSettings.Default;
			try
			{
				Appearance = AppearanceSettings.Load(appearancePath);
			}
			catch (Exception exception)
			{
				ReportFailure(appearanceStatus, "Не удалось прочитать оформление. Применены стандартные настройки.", exception);
			}

			ShowAppearanceChoices();
		}

		// Показывает сохранённые значения без повторной записи при программном выборе.
		private void ShowAppearanceChoices()
		{
			updatingAppearance = true;
			try
			{
				themeInput.SelectedIndex = (int)Appearance.Theme;
				fontScaleInput.SelectedIndex = (Appearance.FontPercent - AppearanceSettings.MinFontPercent) / AppearanceSettings.FontPercentStep;
			}
			finally
			{
				updatingAppearance = false;
			}
		}

		// Сохраняет выбор сразу, а при ошибке возвращает предыдущие значения в выпадающие списки.
		private void OnAppearanceChanged(object sender, EventArgs args)
		{
			if (updatingAppearance || themeInput.SelectedIndex < 0 || fontScaleInput.SelectedIndex < 0)
			{
				return;
			}

			int fontPercent = AppearanceSettings.MinFontPercent + fontScaleInput.SelectedIndex * AppearanceSettings.FontPercentStep;
			var selection = new AppearanceSettings((AppearanceTheme)themeInput.SelectedIndex, fontPercent);
			if (selection.Theme == Appearance.Theme && selection.FontPercent == Appearance.FontPercent)
			{
				return;
			}

			try
			{
				selection.Save(appearancePath);
			}
			catch (Exception exception)
			{
				ShowAppearanceChoices();
				ReportFailure(appearanceStatus, "Не удалось сохранить оформление.", exception);
				return;
			}

			Appearance = selection;
			ShowStatus(appearanceStatus, string.Empty, false);
			EventHandler changed = AppearanceChanged;
			if (changed != null)
			{
				changed(this, EventArgs.Empty);
			}
		}

		// Перечитывает файлы при входе в настройки, сохраняя редактируемый черновик.
		internal void RefreshFromDisk()
		{
			refreshFileLinks();
			if (!HasUnsavedChanges)
			{
				LoadConfiguration();
			}
		}

		// Скрывает строку подключения при выходе из настроек.
		internal void HideConnectionString()
		{
			showConnection.Checked = false;
		}

		// Освобождает подсказку с полным текстом статуса.
		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				statusTip.Dispose();
			}

			base.Dispose(disposing);
		}

		// Даёт двум спискам отдельную прокрутку и оставляет таймаут под ними.
		private Control BuildScanningPage()
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
			timeoutInput.TextChanged += (sender, args) => MarkChanged();
			timeoutInput.ValueChanged += (sender, args) => MarkChanged();
			layout.Controls.Add(CreateActions(
				new Label { AutoSize = true, Text = "Таймаут подключения к ПК", Margin = new Padding(3, 6, 3, 0) },
				timeoutInput,
				new Label { AutoSize = true, Text = "с (1–60, TCP-порт 445)", Margin = new Padding(3, 6, 3, 0) }), 0, 2);
			return layout;
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
			grid.CellValueChanged += (sender, args) => MarkChanged();
			grid.CurrentCellDirtyStateChanged += (sender, args) =>
			{
				if (grid.IsCurrentCellDirty)
				{
					MarkChanged();
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
			MarkChanged();
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
				MarkChanged();
			}
		}

		// Показывает строку MSSQL и проверяет вход с введёнными параметрами.
		private Control BuildDatabasePage()
		{
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
				MarkChanged();
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
			FlowLayoutPanel actions = CreateActions(checkDatabase, example);
			layout.Controls.Add(actions, 0, 3);
			layout.SetColumnSpan(actions, 2);
			layout.Controls.Add(databaseStatus, 0, 4);
			layout.SetColumnSpan(databaseStatus, 2);
			return layout;
		}

		// Открывает новое соединение без команд SQL и сохраняет результат только для проверенной строки.
		private async void OnCheckDatabase(object sender, EventArgs args)
		{
			string connectionString = connectionInput.Text.Trim();
			if (connectionString.Length == 0)
			{
				ShowStatus(databaseStatus, "Введите строку подключения.", true);
				connectionInput.Focus();
				return;
			}

			checkDatabase.Enabled = false;
			ShowStatus(databaseStatus, "Проверка подключения…", false);
			try
			{
				SqlConnectionStringBuilder settings = CertificateRepository.ParseConnectionString(connectionString);
				settings.Pooling = false;
				using (var connection = new SqlConnection(settings.ConnectionString))
				{
					await connection.OpenAsync();
				}

				if (!IsDisposed && connectionInput.Text.Trim() == connectionString)
				{
					ShowStatus(databaseStatus, "Подключение к базе данных установлено.", false);
				}
			}
			catch (Exception exception)
			{
				Label status = !IsDisposed && connectionInput.Text.Trim() == connectionString ? databaseStatus : null;
				ReportFailure(status, "Не удалось подключиться к MSSQL.", exception);
			}
			finally
			{
				if (!IsDisposed)
				{
					checkDatabase.Enabled = true;
				}
			}
		}

		// Группирует действия с XML, затем показывает два файла журналов.
		private Control BuildFilesPage()
		{
			var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), ColumnCount = 1, RowCount = 4 };
			layout.Controls.Add(CreateFileRow("Файл конфигурации", configurationPath, "Просмотреть XML", "Восстановите или загрузите конфигурацию."), 0, 0);
			var import = new Button { Text = "Загрузить XML…", AutoSize = true };
			import.Click += OnImport;
			var saveCopy = new Button { Text = "Сохранить копию XML…", AutoSize = true, Enabled = false };
			saveCopy.Click += OnSaveCopy;
			refreshFileLinks += () => saveCopy.Enabled = File.Exists(configurationPath);
			statusTip.SetToolTip(saveCopy, "Копирует сохранённые настройки вместе со строкой подключения к MSSQL.");
			var restore = new Button { Text = "Восстановить настройки первого запуска…", AutoSize = true };
			restore.Click += OnRestore;
			layout.Controls.Add(CreateActions(import, saveCopy, restore), 0, 1);
			layout.Controls.Add(CreateFileRow("Журнал работы", Program.LogFilePath, "Открыть", "Файл журнала отсутствует."), 0, 2);
			layout.Controls.Add(CreateFileRow("Журнал ошибок", Program.ErrorLogFilePath, "Открыть", "Файл появится после первой записанной ошибки."), 0, 3);
			return layout;
		}

		// Показывает путь и разрешает открывать или выделять только существующий файл.
		private Control CreateFileRow(string title, string path, string openCaption, string missingHint)
		{
			var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 3 };
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			var titleLabel = new Label { Text = title, AutoSize = true };
			layout.Controls.Add(titleLabel, 0, 0);
			layout.SetColumnSpan(titleLabel, 3);
			layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Text = path, ReadOnly = true, AccessibleName = title }, 0, 1);
			var open = new Button { Text = openCaption, AutoSize = true, AccessibleName = "Открыть: " + title };
			var reveal = new Button { Text = "Перейти", AutoSize = true, AccessibleName = "Показать в Проводнике: " + title };
			open.Click += (sender, args) =>
			{
				if (path == configurationPath)
				{
					PreviewConfiguration();
				}
				else
				{
					OpenFile(path, false);
				}
			};
			reveal.Click += (sender, args) => OpenFile(path, true);
			layout.Controls.Add(open, 1, 1);
			layout.Controls.Add(reveal, 2, 1);
			var hint = new Label { Text = missingHint, AutoSize = true };
			layout.Controls.Add(hint, 0, 2);
			layout.SetColumnSpan(hint, 3);
			refreshFileLinks += () =>
			{
				bool exists = File.Exists(path);
				open.Enabled = exists;
				reveal.Enabled = exists;
				hint.Visible = !exists;
			};
			return layout;
		}

		// Открывает XML только для чтения, чтобы просмотр не менял рабочие настройки.
		private void PreviewConfiguration()
		{
			try
			{
				using (var preview = new Form
				{
					Text = Path.GetFileName(configurationPath), StartPosition = FormStartPosition.CenterParent,
					ClientSize = new Size(840, 450), MinimizeBox = false, ShowInTaskbar = false
				})
				{
					WindowAppearance.Apply(preview);
					var text = new TextBox
					{
						Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
						ReadOnly = true, Text = File.ReadAllText(configurationPath), AccessibleName = "XML конфигурации"
					};
					var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.OK };
					FlowLayoutPanel actions = CreateActions(close);
					actions.Dock = DockStyle.Bottom;
					actions.FlowDirection = FlowDirection.RightToLeft;
					preview.Controls.Add(text);
					preview.Controls.Add(actions);
					preview.AcceptButton = close;
					preview.CancelButton = close;
					WindowAppearance.ApplyToDialog(preview, FindForm(), Appearance.FontPercent);
					preview.ShowDialog(this);
				}
			}
			catch (Exception exception)
			{
				ReportFailure(statusLabel, "Не удалось открыть рабочий XML.", exception);
			}
		}

		// Открывает журнал в Блокноте или выделяет файл в Проводнике.
		private void OpenFile(string path, bool reveal)
		{
			if (!File.Exists(path))
			{
				refreshFileLinks();
				ShowStatus(statusLabel, "Файл отсутствует. " + Path.GetFileName(path), true);
				return;
			}

			try
			{
				string executable = reveal
					? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
					: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
				string arguments = (reveal ? "/select," : string.Empty) + "\"" + path + "\"";
				using (var process = new Process { StartInfo = new ProcessStartInfo(executable, arguments) { UseShellExecute = false } })
				{
					process.Start();
				}
			}
			catch (Exception exception)
			{
				ReportFailure(statusLabel, "Не удалось открыть " + Path.GetFileName(path) + ".", exception);
			}
		}

		// Предлагает выбрать место для копии сохранённого рабочего файла.
		private void OnSaveCopy(object sender, EventArgs args)
		{
			using (var dialog = new SaveFileDialog
			{
				Title = "Сохранить копию конфигурации",
				Filter = "XML-конфигурация (*.xml)|*.xml",
				DefaultExt = "xml",
				FileName = "scan.config.backup.xml",
				OverwritePrompt = true,
				RestoreDirectory = true
			})
			{
				if (dialog.ShowDialog(this) != DialogResult.OK)
				{
					return;
				}

				try
				{
					ScanConfiguration.SaveCopy(configurationPath, dialog.FileName);
					ShowStatus(statusLabel, "Копия сохранена. " + dialog.FileName, false);
				}
				catch (Exception exception)
				{
					ReportFailure(statusLabel, "Не удалось сохранить копию XML.", exception);
				}
			}
		}

		// Просит выбрать XML и предупреждает о замене рабочего файла.
		private void OnImport(object sender, EventArgs args)
		{
			using (var dialog = new OpenFileDialog
			{
				Title = "Выберите файл конфигурации",
				Filter = "XML-конфигурация (*.xml)|*.xml",
				CheckFileExists = true,
				InitialDirectory = Path.GetDirectoryName(configurationPath),
				RestoreDirectory = true
			})
			{
				if (dialog.ShowDialog(this) == DialogResult.OK && ConfirmReplacement("Загрузить XML",
					"Содержимое выбранного файла заменит рабочий scan.config.xml. Продолжить?"))
				{
					ReplaceConfiguration(dialog.FileName, "Конфигурация загружена.", "Не удалось загрузить выбранный XML.");
				}
			}
		}

		// Возвращает точное содержимое поставляемого шаблона, в том числе пустую строку MSSQL.
		private void OnRestore(object sender, EventArgs args)
		{
			if (ConfirmReplacement("Восстановить настройки первого запуска",
				"Пути и таймаут будут восстановлены из шаблона. Строка подключения к MSSQL станет пустой. Продолжить?"))
			{
				ReplaceConfiguration(templatePath, "Настройки первого запуска восстановлены.", "Не удалось восстановить настройки.");
			}
		}

		// Подтверждает замену на диске и предупреждает, если в форме есть черновик.
		private bool ConfirmReplacement(string title, string message)
		{
			if (HasUnsavedChanges)
			{
				message += "\r\n\r\nНесохранённые изменения будут сброшены.";
			}

			return MessageBox.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
				MessageBoxDefaultButton.Button2) == DialogResult.Yes;
		}

		// Обновляет поля только после успешной проверки и замены рабочего XML.
		private void ReplaceConfiguration(string sourcePath, string successMessage, string errorMessage)
		{
			try
			{
				ScanConfiguration configuration = ScanConfiguration.Import(sourcePath, configurationPath);
				ApplyConfiguration(configuration);
				OnConfigurationSaved(successMessage);
			}
			catch (Exception exception)
			{
				ReportFailure(statusLabel, errorMessage, exception);
			}
		}

		// Проверяет редактируемые пути и сохраняет их вместе с параметрами MSSQL и таймаутом.
		private void OnSave(object sender, EventArgs args)
		{
			try
			{
				directoriesGrid.EndEdit();
				cryptoPathsGrid.EndEdit();
				string[] directories = ReadPaths(directoriesGrid, "Добавьте хотя бы одну общую папку с сертификатами.");
				string[] cryptoPaths = ReadPaths(cryptoPathsGrid, "Добавьте хотя бы один путь к csptest.exe.");
				int timeout;
				if (!int.TryParse(timeoutInput.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out timeout) || timeout < 1 || timeout > 60)
				{
					tabs.SelectedTab = scanPage;
					timeoutInput.Focus();
					throw new InvalidDataException("Введите целый таймаут от 1 до 60 секунд.");
				}

				var configuration = new ScanConfiguration(directories, cryptoPaths, timeout, connectionInput.Text.Trim());
				configuration.Save(configurationPath);
				ApplyConfiguration(configuration);
				OnConfigurationSaved("Настройки сохранены.");
			}
			catch (Exception exception)
			{
				ReportFailure(statusLabel, "Не удалось сохранить настройки.", exception);
			}
		}

		// Перечитывает сохранённое, оставляя импорт и восстановление доступными при повреждении файла.
		private void LoadConfiguration()
		{
			try
			{
				ApplyConfiguration(ScanConfiguration.Load(configurationPath));
				ShowStatus(statusLabel, string.Empty, false);
			}
			catch (Exception exception)
			{
				scanPage.Enabled = false;
				databasePage.Enabled = false;
				if (tabs.SelectedTab != personalizationPage)
				{
					tabs.SelectedTab = filesPage;
				}
				SetUnsavedChanges(false);
				ReportFailure(statusLabel, "Не удалось прочитать рабочий XML. Загрузите или восстановите конфигурацию.", exception);
			}
		}

		// Подставляет проверенные значения и сбрасывает состояние черновика.
		private void ApplyConfiguration(ScanConfiguration configuration)
		{
			updating = true;
			try
			{
				scanPage.Enabled = true;
				databasePage.Enabled = true;
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
				connectionInput.Text = configuration.ConnectionString;
				showConnection.Checked = false;
				databaseStatus.Text = string.Empty;
				SetUnsavedChanges(false);
			}
			finally
			{
				updating = false;
			}
		}

		// Возвращает значения строк в порядке показа, включая ошибочно оставленное пустое поле.
		private static string[] ReadPaths(DataGridView grid, string requiredMessage)
		{
			if (grid.Rows.Count == 0)
			{
				throw new InvalidDataException(requiredMessage);
			}

			return grid.Rows.Cast<DataGridViewRow>().Select(row => Convert.ToString(row.Cells[0].Value)).ToArray();
		}

		// Оповещает основное окно о сохранении и перечитывает доступность файлов.
		private void OnConfigurationSaved(string message)
		{
			refreshFileLinks();
			ShowStatus(statusLabel, message, false);
			EventHandler changed = ConfigurationChanged;
			if (changed != null)
			{
				changed(this, EventArgs.Empty);
			}
		}

		// Включает действия черновика после изменения любого поля.
		private void MarkChanged()
		{
			if (!updating)
			{
				SetUnsavedChanges(true);
				ShowStatus(statusLabel, "Есть несохранённые изменения.", false);
			}
		}

		// Согласует доступность сохранения и отмены с состоянием формы.
		private void SetUnsavedChanges(bool changed)
		{
			HasUnsavedChanges = changed;
			saveButton.Enabled = changed;
			discardButton.Enabled = changed;
		}

		// Сохраняет ошибку независимо от состояния панели, затем обновляет только действующий интерфейс.
		private void ReportFailure(Label label, string message, Exception exception)
		{
			string details = message + " " + exception.Message;
			try
			{
				logger.Error(message, exception);
			}
			catch (Exception loggingException)
			{
				details += " Журнал недоступен. " + loggingException.Message;
				Console.Error.WriteLine(exception);
				Console.Error.WriteLine(loggingException);
			}

			if (IsDisposed)
			{
				return;
			}

			if (label != null)
			{
				ShowStatus(label, details, true);
			}

			refreshFileLinks();
		}

		// Оставляет полный текст доступным через подсказку при сокращении подписи.
		private void ShowStatus(Label label, string message, bool error)
		{
			label.ForeColor = error ? WindowAppearance.ErrorColor(label) : Color.Empty;
			label.Text = message;
			statusTip.SetToolTip(label, message);
		}

		// Раскладывает связанные кнопки и поля в одну строку.
		private static FlowLayoutPanel CreateActions(params Control[] controls)
		{
			var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
			actions.Controls.AddRange(controls);
			return actions;
		}
	}
}
