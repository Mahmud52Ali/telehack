using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.App.Theming;
using Telecom.Core.Configuration;

namespace Telecom.App.Forms.Settings
{
	// Вкладка «Файлы и журнал»: просмотр, загрузка, копия и восстановление XML, ссылки на файлы журнала.
	internal sealed class FilesTab : TabPage
	{
		private readonly string configurationPath;
		private readonly string templatePath;
		private readonly StatusReporter status;
		private readonly Label statusLabel;
		private readonly Func<bool> hasUnsavedChanges;
		private readonly Func<int> fontPercent;
		private Action refreshLinks;

		// Рабочий XML заменён загруженным или восстановленным файлом; передаёт новую конфигурацию и текст для статуса.
		internal event Action<ScanConfiguration, string> ConfigurationReplaced;

		// statusLabel — общая строка состояния панели настроек.
		internal FilesTab(string configurationPath, string templatePath, StatusReporter status, Label statusLabel,
			Func<bool> hasUnsavedChanges, Func<int> fontPercent) : base("Файлы и журнал")
		{
			this.configurationPath = configurationPath;
			this.templatePath = templatePath;
			this.status = status;
			this.statusLabel = statusLabel;
			this.hasUnsavedChanges = hasUnsavedChanges;
			this.fontPercent = fontPercent;

			var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), ColumnCount = 1, RowCount = 4 };
			layout.Controls.Add(CreateFileRow("Файл конфигурации", configurationPath, "Просмотреть XML",
				"Восстановите или загрузите конфигурацию."), 0, 0);
			var import = new Button { Text = "Загрузить XML…", AutoSize = true };
			import.Click += OnImport;
			var saveCopy = new Button { Text = "Сохранить копию XML…", AutoSize = true, Enabled = false };
			saveCopy.Click += OnSaveCopy;
			refreshLinks += () => saveCopy.Enabled = File.Exists(configurationPath);
			status.SetHint(saveCopy, "Копирует сохранённые настройки вместе со строкой подключения к MSSQL.");
			var restore = new Button { Text = "Восстановить настройки первого запуска…", AutoSize = true };
			restore.Click += OnRestore;
			layout.Controls.Add(StatusReporter.CreateActions(import, saveCopy, restore), 0, 1);
			layout.Controls.Add(CreateFileRow("Журнал работы", AppPaths.LogFilePath, "Открыть", "Файл журнала отсутствует."), 0, 2);
			layout.Controls.Add(CreateFileRow("Журнал ошибок", AppPaths.ErrorLogFilePath, "Открыть",
				"Файл появится после первой записанной ошибки."), 0, 3);
			Controls.Add(layout);
		}

		// Перепроверяет, какие файлы существуют, и включает или выключает кнопки.
		internal void RefreshLinks()
		{
			refreshLinks();
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
			refreshLinks += () =>
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
				using (var preview = new ConfigurationPreviewDialog(configurationPath))
				{
					WindowAppearance.ApplyToDialog(preview, FindForm(), fontPercent());
					preview.ShowDialog(this);
				}
			}
			catch (Exception exception)
			{
				status.ReportFailure(this, statusLabel, "Не удалось открыть рабочий XML.", exception);
			}
		}

		// Открывает журнал в Блокноте или выделяет файл в Проводнике.
		private void OpenFile(string path, bool reveal)
		{
			if (!File.Exists(path))
			{
				refreshLinks();
				status.Show(statusLabel, "Файл отсутствует. " + Path.GetFileName(path), true);
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
				status.ReportFailure(this, statusLabel, "Не удалось открыть " + Path.GetFileName(path) + ".", exception);
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
					ScanConfigurationFile.SaveCopy(configurationPath, dialog.FileName);
					status.Show(statusLabel, "Копия сохранена. " + dialog.FileName, false);
				}
				catch (Exception exception)
				{
					status.ReportFailure(this, statusLabel, "Не удалось сохранить копию XML.", exception);
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
			if (hasUnsavedChanges())
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
				ScanConfiguration configuration = ScanConfigurationFile.Import(sourcePath, configurationPath);
				Action<ScanConfiguration, string> replaced = ConfigurationReplaced;
				if (replaced != null)
				{
					replaced(configuration, successMessage);
				}
			}
			catch (Exception exception)
			{
				status.ReportFailure(this, statusLabel, errorMessage, exception);
			}
		}
	}
}
