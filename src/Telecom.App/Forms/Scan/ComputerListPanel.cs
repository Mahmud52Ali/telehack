using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.App.Theming;
using Telecom.App.Windows;
using Telecom.Core.Computers;

namespace Telecom.App.Forms.Scan
{
	// Список «Компьютеры»: первая строка — этот ПК, дальше удалённые. Галочки задают состав запуска и сохраняются в computers.xml.
	internal sealed class ComputerListPanel : UserControl
	{
		private readonly DataGridView computersGrid = new DataGridView();
		private readonly BindingList<ComputerRow> computers = new BindingList<ComputerRow>();
		private readonly Button addComputerButton = new Button();
		private readonly Button editComputerButton = new Button();
		private readonly Button removeComputerButton = new Button();
		private readonly Button connectButton = new Button();
		private readonly ComputerListFile computerListFile;
		private readonly WindowsCredentialStore credentialStore;
		private readonly OperationGate operations;
		private readonly Action<string> showStatus;
		private readonly Func<int> fontPercent;
		private bool changingSelection;

		// Запрос на подключение отмеченных ПК после добавления, изменения или нажатия «Подключить выбранные».
		internal event EventHandler ConnectRequested;

		// Загружен ли список ПК; до этого действия со списком недоступны.
		internal bool ProfilesLoaded { get; private set; }

		// Строки таблицы в порядке показа.
		internal IList<ComputerRow> Rows { get { return computers; } }

		// Показывает локальный ПК первым в общем списке с удалёнными компьютерами.
		internal ComputerListPanel(ComputerListFile computerListFile, WindowsCredentialStore credentialStore, OperationGate operations,
			Action<string> showStatus, Func<int> fontPercent)
		{
			this.computerListFile = computerListFile;
			this.credentialStore = credentialStore;
			this.operations = operations;
			this.showStatus = showStatus;
			this.fontPercent = fontPercent;
			Dock = DockStyle.Fill;
			computers.Add(new ComputerRow(ComputerProfile.CreateLocal(false)));

			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 6), ColumnCount = 1, RowCount = 2 };
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			BuildGrid();
			layout.Controls.Add(computersGrid, 0, 0);
			var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
			addComputerButton.Margin = new Padding(0, 3, 3, 3);
			AddAction(actions, addComputerButton, "Добавить", OnAddComputer);
			AddAction(actions, editComputerButton, "Изменить", OnEditComputer);
			AddAction(actions, removeComputerButton, "Удалить", OnRemoveComputer);
			AddAction(actions, connectButton, "Подключить выбранные", (sender, args) => RequestConnect());
			layout.Controls.Add(actions, 0, 1);
			Controls.Add(layout);
		}

		// Создаёт столбцы; редактировать можно только галочку выбора.
		private void BuildGrid()
		{
			GridFactory.ConfigureGrid(computersGrid);
			computersGrid.Margin = new Padding(0, 3, 0, 3);
			computersGrid.ReadOnly = false;
			computersGrid.MultiSelect = false;
			computersGrid.AccessibleName = "Компьютеры для подключения и сканирования";
			computersGrid.Columns.Add(new DataGridViewCheckBoxColumn
			{
				Name = "Selected", DataPropertyName = "Selected", HeaderText = "Выбор", FillWeight = 30
			});
			GridFactory.AddColumn(computersGrid, "DisplayName", "Название", 90);
			GridFactory.AddColumn(computersGrid, "IpAddress", "IPv4", 80);
			GridFactory.AddColumn(computersGrid, "Account", "Учётная запись", 110);
			GridFactory.AddColumn(computersGrid, "ConnectionStatus", "Подключение", 140);
			GridFactory.AddColumn(computersGrid, "ScanStatus", "Сканирование", 140);
			GridFactory.AddColumn(computersGrid, "CryptoProVersion", "КриптоПро", 85);
			GridFactory.AddColumn(computersGrid, "DatabaseStatus", "База данных", 110);
			foreach (DataGridViewColumn column in computersGrid.Columns)
			{
				column.ReadOnly = column.Name != "Selected";
			}

			computersGrid.DataSource = computers;
			computersGrid.CurrentCellDirtyStateChanged += OnComputerSelectionDirty;
			computersGrid.CellValueChanged += OnComputerSelectionChanged;
			computersGrid.SelectionChanged += (sender, args) => UpdateState();
		}

		// Добавляет кнопку действия над списком.
		private static void AddAction(FlowLayoutPanel panel, Button button, string text, EventHandler handler)
		{
			button.Text = text;
			button.AutoSize = true;
			button.Click += handler;
			panel.Controls.Add(button);
		}

		// Загружает сохранённый список; повреждённый файл не перезаписывается, а показывается ошибка.
		internal void LoadProfiles()
		{
			try
			{
				changingSelection = true;
				foreach (ComputerProfile profile in computerListFile.Load())
				{
					if (profile.IsLocal)
					{
						computers[0] = new ComputerRow(profile);
					}
					else
					{
						computers.Add(new ComputerRow(profile));
					}
				}

				ProfilesLoaded = true;
			}
			catch (Exception exception)
			{
				ErrorReporter.Show("Не удалось загрузить список ПК: " + computerListFile.FilePath, exception);
				showStatus("Исправьте файл списка ПК: " + computerListFile.FilePath);
			}
			finally
			{
				changingSelection = false;
			}
		}

		// Завершает редактирование галочки, чтобы запуск учитывал последний выбор.
		internal void EndEdit()
		{
			computersGrid.EndEdit();
		}

		// Перерисовывает статусы строк без повторного сохранения галочек через события привязки.
		internal void RefreshRows()
		{
			changingSelection = true;
			computers.ResetBindings();
			changingSelection = false;
		}

		// Согласует доступность действий с текущей строкой и идущей операцией; локальный ПК удалить нельзя.
		internal void UpdateState()
		{
			bool idle = !operations.IsBusy && ProfilesLoaded;
			Enabled = !operations.IsBusy;
			addComputerButton.Enabled = idle;
			ComputerRow current = CurrentComputer();
			editComputerButton.Enabled = idle && current != null;
			removeComputerButton.Enabled = idle && current != null && !current.Profile.IsLocal;
			connectButton.Enabled = idle && computers.Any(item => item.Selected && !item.Profile.IsLocal);
		}

		// Сразу фиксирует галочку, чтобы нажатие кнопки учитывало последний выбор.
		private void OnComputerSelectionDirty(object sender, EventArgs args)
		{
			if (computersGrid.IsCurrentCellDirty)
			{
				computersGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		}

		// Сохраняет выбранный набор между запусками; при ошибке восстанавливает прежнее значение.
		private void OnComputerSelectionChanged(object sender, DataGridViewCellEventArgs args)
		{
			if (changingSelection || !ProfilesLoaded || args.RowIndex < 0 || args.ColumnIndex != 0)
			{
				return;
			}

			var item = (ComputerRow)computersGrid.Rows[args.RowIndex].DataBoundItem;
			item.Selected = Convert.ToBoolean(computersGrid.Rows[args.RowIndex].Cells[0].Value);
			try
			{
				computerListFile.Save(computers.Select(value => value.Profile));
				item.SavedSelection = item.Selected;
			}
			catch (Exception exception)
			{
				changingSelection = true;
				item.Selected = item.SavedSelection;
				computers.ResetBindings();
				changingSelection = false;
				ErrorReporter.Show("Не удалось сохранить выбор компьютеров.", exception);
			}

			UpdateState();
		}

		// Сохраняет новый ПК и сразу подключает отмеченные компьютеры.
		private void OnAddComputer(object sender, EventArgs args)
		{
			if (EditComputer(null))
			{
				RequestConnect();
			}
		}

		// Меняет настройки одной строки и повторяет подключение с новой учёткой.
		private void OnEditComputer(object sender, EventArgs args)
		{
			ComputerRow item = CurrentComputer();
			if (item != null && EditComputer(item) && !item.Profile.IsLocal)
			{
				RequestConnect();
			}
		}

		// Сначала сохраняет новый список, затем удаляет только собственный секрет исключённого ПК.
		private void OnRemoveComputer(object sender, EventArgs args)
		{
			ComputerRow item = CurrentComputer();
			if (item == null || item.Profile.IsLocal)
			{
				return;
			}

			try
			{
				computerListFile.Save(computers.Where(value => value != item).Select(value => value.Profile));
				changingSelection = true;
				computers.Remove(item);
				changingSelection = false;
				RemoveCredential(item.Profile);
				showStatus("Компьютер удалён из списка. Действующие соединения Windows сохраняются.");
			}
			catch (Exception exception)
			{
				changingSelection = false;
				ErrorReporter.Show("Не удалось удалить компьютер или его сохранённую учётку.", exception);
			}
		}

		// Сообщает странице сканирования, что нужно подключить отмеченные ПК.
		private void RequestConnect()
		{
			EventHandler handler = ConnectRequested;
			if (handler != null)
			{
				handler(this, EventArgs.Empty);
			}
		}

		// Возвращает строку под курсором; галочки задают состав запуска, а не объект редактирования.
		private ComputerRow CurrentComputer()
		{
			return computersGrid.CurrentRow == null ? null : computersGrid.CurrentRow.DataBoundItem as ComputerRow;
		}

		// Открывает редактор ПК и сохраняет результат.
		// Новая запись секрета сохраняется до смены XML, поэтому неудачная запись списка не портит прежнюю учётку.
		private bool EditComputer(ComputerRow previous)
		{
			using (var dialog = new ComputerProfileDialog(previous == null ? null : previous.Profile))
			{
				WindowAppearance.ApplyToDialog(dialog, FindForm(), fontPercent());
				if (dialog.ShowDialog(this) != DialogResult.OK)
				{
					return false;
				}

				ComputerProfile profile = dialog.Profile;
				bool secretSaved = false;
				bool profileSaved = false;
				try
				{
					var profiles = computers.Select(item => item == previous ? profile : item.Profile).ToList();
					if (previous == null)
					{
						profiles.Add(profile);
					}
					ComputerListFile.Validate(profiles);
					if (!profile.IsLocal)
					{
						NetworkCredential retained = dialog.KeepPreviousPassword ? credentialStore.Read(previous.Profile.Id) : null;
						if (retained != null && !string.Equals(retained.UserName, profile.UserName, StringComparison.OrdinalIgnoreCase))
						{
							throw new InvalidOperationException("Сохранённая учётная запись изменилась. Введите пароль заново.");
						}

						credentialStore.Save(profile.Id, profile.UserName, retained == null ? dialog.Password : retained.Password);
						secretSaved = true;
					}

					computerListFile.Save(profiles);
					profileSaved = true;
					changingSelection = true;
					if (previous == null)
					{
						computers.Add(new ComputerRow(profile));
					}
					else
					{
						computers[computers.IndexOf(previous)] = new ComputerRow(profile);
						RemoveCredential(previous.Profile);
					}

					return true;
				}
				catch (Exception exception)
				{
					if (secretSaved && !profileSaved)
					{
						RemoveCredential(profile);
					}

					ErrorReporter.Show("Не удалось сохранить компьютер.", exception);
					return false;
				}
				finally
				{
					changingSelection = false;
					UpdateState();
				}
			}
		}

		// Ошибка очистки собственного секрета фиксируется отдельно от уже сохранённого списка.
		private void RemoveCredential(ComputerProfile profile)
		{
			if (profile.HasUserName)
			{
				try
				{
					credentialStore.Delete(profile.Id);
				}
				catch (Exception exception)
				{
					ErrorReporter.Show("Список ПК сохранён, но прежнюю запись Telecom/Computer/" + profile.Id.ToString("N")
						+ " для " + profile.Description + " нужно удалить из диспетчера учётных данных вручную.", exception);
				}
			}
		}
	}
}
