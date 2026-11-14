using System;
using System.Drawing;
using System.Windows.Forms;
using Telecom.Core;

namespace Telecom.App
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
			Text = profile == null ? "Добавить компьютер" : "Изменить компьютер";
			Font = new Font("Segoe UI", 9F);
			AutoScaleMode = AutoScaleMode.Font;
			ClientSize = new Size(570, 476);
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MaximizeBox = false;
			MinimizeBox = false;
			StartPosition = FormStartPosition.CenterParent;

			var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 9 };
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			AddInput(layout, "IPv4", ipAddressInput, 0);
			nameInput.MaxLength = 256;
			AddInput(layout, "Название (необяз.)", nameInput, 1);
			AddInput(layout, "Учётная запись", userInput, 2);
			AddInput(layout, "Пароль", passwordInput, 3);
			passwordInput.UseSystemPasswordChar = true;
			var hint = new Label
			{
				Text = "Пароль сохраняется в диспетчере учётных данных Windows.\r\nПри редактировании пустое поле сохраняет прежний пароль той же учётки.",
				AutoSize = true, Dock = DockStyle.Fill
			};
			layout.Controls.Add(hint, 0, 4);
			layout.SetColumnSpan(hint, 2);
			additionalDirectoriesInput.Multiline = true;
			additionalDirectoriesInput.AcceptsReturn = true;
			additionalDirectoriesInput.WordWrap = false;
			additionalDirectoriesInput.ScrollBars = ScrollBars.Both;
			additionalDirectoriesInput.MinimumSize = new Size(0, 85);
			AddInput(layout, "Дополнительные пути", additionalDirectoriesInput, 5);
			var pathsHint = new Label
			{
				Text = "Укажите пути к папкам на ПК, в каждой строке по одному пути, например:\r\n"
					+ "C:\\Certs\r\n"
					+ "D:\\Certificates\r\n"
					+ "Эти пути проверяются вместе с общими из конфига XML.\r\n"
					+ "Пустое поле означает, что проверяются только общие пути.",
				AutoSize = true, Dock = DockStyle.Fill
			};
			layout.Controls.Add(pathsHint, 0, 6);
			layout.SetColumnSpan(pathsHint, 2);
			errorLabel.ForeColor = Color.Firebrick;
			errorLabel.AutoSize = true;
			errorLabel.MaximumSize = new Size(540, 0);
			layout.Controls.Add(errorLabel, 0, 7);
			layout.SetColumnSpan(errorLabel, 2);
			var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
			var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
			var save = new Button { Text = "Сохранить", AutoSize = true };
			save.Click += OnSave;
			buttons.Controls.Add(cancel);
			buttons.Controls.Add(save);
			layout.Controls.Add(buttons, 0, 8);
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

		// Проверяет ввод до записи настроек и создаёт новый идентификатор для безопасной замены пароля.
		private void OnSave(object sender, EventArgs args)
		{
			try
			{
				string userName = userInput.Text.Trim();
				KeepPreviousPassword = previous != null && previous.HasUserName
					&& string.Equals(previous.UserName, userName, StringComparison.OrdinalIgnoreCase) && passwordInput.Text.Length == 0;
				if (userName.Length == 0 || (passwordInput.Text.Length == 0 && !KeepPreviousPassword))
				{
					throw new ArgumentException("Введите учётную запись и пароль.");
				}

				Profile = new ComputerProfile(Guid.NewGuid(), ipAddressInput.Text, nameInput.Text, userName,
					previous == null || previous.Selected, additionalDirectoriesInput.Lines);
				DialogResult = DialogResult.OK;
			}
			catch (ArgumentException exception)
			{
				errorLabel.Text = exception.Message;
			}
		}
	}
}
