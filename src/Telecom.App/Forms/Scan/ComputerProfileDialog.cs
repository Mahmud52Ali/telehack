using System;
using System.Drawing;
using System.Windows.Forms;
using Telecom.App.Theming;
using Telecom.Core.Common;
using Telecom.Core.Computers;

namespace Telecom.App.Forms.Scan
{
	internal sealed class ComputerProfileDialog : Form
	{
		private readonly TextBox ipAddressInput = new TextBox();
		private readonly TextBox nameInput = new TextBox();
		private readonly TextBox userInput = new TextBox();
		private readonly TextBox passwordInput = new TextBox();
		private readonly TextBox additionalDirectoriesInput = new TextBox();
		private readonly Label errorLabel = new Label();
		private readonly ComputerProfile previous;

		internal ComputerProfile Profile { get; private set; }
		internal string Password { get { return passwordInput.Text; } }
		internal bool KeepPreviousPassword { get; private set; }

		// Собирает адрес, учётные данные для подключения и дополнительные пути.
		internal ComputerProfileDialog(ComputerProfile profile)
		{
			previous = profile;
			bool isLocal = profile != null && profile.IsLocal;
			Text = profile == null ? "Добавить компьютер" : "Изменить компьютер";
			WindowAppearance.Apply(this);
			ClientSize = isLocal ? new Size(570, 330) : new Size(570, 476);
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MaximizeBox = false;
			MinimizeBox = false;
			StartPosition = FormStartPosition.CenterParent;
			AutoScroll = true;

			var layout = new TableLayoutPanel
			{
				Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12), ColumnCount = 2, RowCount = isLocal ? 5 : 9
			};
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			nameInput.MaxLength = 256;
			int row = 0;
			if (isLocal)
			{
				nameInput.ReadOnly = true;
				AddInput(layout, "Название", nameInput, row++);
			}
			else
			{
				AddInput(layout, "IPv4", ipAddressInput, row++);
				AddInput(layout, "Название (необяз.)", nameInput, row++);
				AddInput(layout, "Учётная запись", userInput, row++);
				AddInput(layout, "Пароль", passwordInput, row++);
				passwordInput.UseSystemPasswordChar = true;
				var hint = new Label
				{
					Text = "Пароль сохраняется в диспетчере учётных данных Windows.\r\n"
						+ "При редактировании пустое поле сохраняет прежний пароль той же учётки.",
					AutoSize = true, Dock = DockStyle.Fill
				};
				layout.Controls.Add(hint, 0, row++);
				layout.SetColumnSpan(hint, 2);
			}
			additionalDirectoriesInput.Multiline = true;
			additionalDirectoriesInput.AcceptsReturn = true;
			additionalDirectoriesInput.WordWrap = false;
			additionalDirectoriesInput.ScrollBars = ScrollBars.Both;
			additionalDirectoriesInput.MinimumSize = new Size(0, 85);
			AddInput(layout, "Дополнительные пути", additionalDirectoriesInput, row++);
			var pathsHint = new Label
			{
				Text = "Укажите пути к папкам на ПК, в каждой строке по одному пути, например:\r\n"
					+ "C:\\Certs\r\n"
					+ "D:\\Certificates\r\n"
					+ "Эти пути проверяются вместе с общими из конфига XML.\r\n"
					+ "Пустое поле означает, что проверяются только общие пути.",
				AutoSize = true, Dock = DockStyle.Fill
			};
			layout.Controls.Add(pathsHint, 0, row++);
			layout.SetColumnSpan(pathsHint, 2);
			errorLabel.ForeColor = Color.Firebrick;
			errorLabel.AutoSize = true;
			errorLabel.MaximumSize = new Size(540, 0);
			layout.Controls.Add(errorLabel, 0, row++);
			layout.SetColumnSpan(errorLabel, 2);
			var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
			var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
			var save = new Button { Text = "Сохранить", AutoSize = true };
			save.Click += OnSave;
			buttons.Controls.Add(cancel);
			buttons.Controls.Add(save);
			layout.Controls.Add(buttons, 0, row);
			layout.SetColumnSpan(buttons, 2);
			AcceptButton = save;
			CancelButton = cancel;
			Controls.Add(layout);
			if (profile != null)
			{
				ipAddressInput.Text = profile.IpAddress;
				nameInput.Text = profile.DisplayName;
				userInput.Text = profile.UserName;
				additionalDirectoriesInput.Text = string.Join(Environment.NewLine, profile.AdditionalDirectories);
			}
		}

		// Добавляет подписанное поле с нормальным порядком клавиатурного ввода.
		private static void AddInput(TableLayoutPanel layout, string caption, TextBox input, int row)
		{
			layout.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
			input.Dock = DockStyle.Fill;
			input.AccessibleName = caption;
			layout.Controls.Add(input, 1, row);
		}

		// Если введён адрес этого же компьютера, предупреждает, что он уже есть в списке как «Этот компьютер»:
		// иначе один ПК будет сканироваться дважды. Не запрещает — через 127.0.0.1 можно проверить сетевой путь на одной машине.
		// Повторно не спрашивает, если адрес при редактировании не менялся.
		private bool ConfirmThisComputerAddress(string ipAddress)
		{
			bool addressChanged = previous == null || previous.IpAddress != ipAddress;
			if (!addressChanged || !LocalAddresses.IsThisComputer(ipAddress))
			{
				return true;
			}

			string message = "Адрес " + ipAddress + " принадлежит этому компьютеру. Он уже есть в списке как «Этот компьютер», "
				+ "поэтому будет просканирован дважды, а его сертификаты задвоятся в таблице «Сертификаты по компьютерам».\r\n\r\n"
				+ "Сохранить всё равно?";
			return MessageBox.Show(this, message, "Адрес этого компьютера", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
				MessageBoxDefaultButton.Button2) == DialogResult.Yes;
		}

		// Проверяет ввод до записи настроек и создаёт новый идентификатор для безопасной замены пароля.
		private void OnSave(object sender, EventArgs args)
		{
			try
			{
				if (previous != null && previous.IsLocal)
				{
					Profile = ComputerProfile.CreateLocal(previous.Selected, additionalDirectoriesInput.Lines);
					DialogResult = DialogResult.OK;
					return;
				}

				string userName = userInput.Text.Trim();
				KeepPreviousPassword = previous != null && previous.HasUserName
					&& string.Equals(previous.UserName, userName, StringComparison.OrdinalIgnoreCase) && passwordInput.Text.Length == 0;
				if (userName.Length == 0 || (passwordInput.Text.Length == 0 && !KeepPreviousPassword))
				{
					throw new ArgumentException("Введите учётную запись и пароль.");
				}

				var profile = new ComputerProfile(Guid.NewGuid(), ipAddressInput.Text, nameInput.Text, userName,
					previous == null || previous.Selected, additionalDirectoriesInput.Lines);
				if (!ConfirmThisComputerAddress(profile.IpAddress))
				{
					return;
				}

				Profile = profile;
				DialogResult = DialogResult.OK;
			}
			catch (ArgumentException exception)
			{
				errorLabel.Text = exception.Message;
			}
		}
	}
}
