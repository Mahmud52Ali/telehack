using System;
using System.Drawing;
using System.Windows.Forms;
using Telecom.App.Theming;

namespace Telecom.App.Forms.Database
{
	internal sealed class DeleteCertificateDialog : Form
	{
		private readonly TextBox serialNumberInput = new TextBox { Dock = DockStyle.Fill, AccessibleName = "Серийный номер для подтверждения" };
		private readonly TextBox deletionDetails = new TextBox
		{
			Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, TabStop = false,
			Height = 90, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Последствия удаления"
		};

		// Показывает запись и разрешает подтверждение при совпадении полного номера.
		internal DeleteCertificateDialog(string ownerName, string serialNumber, string details)
		{
			Text = "Удаление записи из базы данных";
			WindowAppearance.Apply(this);
			StartPosition = FormStartPosition.CenterParent;
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowInTaskbar = false;
			ClientSize = new Size(490, 290);
			AutoSize = true;
			AutoSizeMode = AutoSizeMode.GrowOnly;
			AutoScroll = true;

			var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.Controls.Add(new Label { Text = "Владелец: " + ownerName, AutoSize = true, MaximumSize = new Size(450, 0), UseMnemonic = false }, 0, 0);
			layout.Controls.Add(new Label { Text = "Серийный номер записи", AutoSize = true }, 0, 1);
			layout.Controls.Add(new TextBox { Text = serialNumber, ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = "Серийный номер записи", TabStop = false }, 0, 2);
			deletionDetails.Text = details;
			layout.Controls.Add(deletionDetails, 0, 3);
			layout.Controls.Add(new Label { Text = "Введите полный серийный номер для подтверждения.", AutoSize = true }, 0, 4);
			layout.Controls.Add(serialNumberInput, 0, 5);

			var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
			var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
			var delete = new Button { Text = "Удалить запись", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
			serialNumberInput.TextChanged += (sender, args) =>
				delete.Enabled = string.Equals(serialNumberInput.Text.Trim(), serialNumber, StringComparison.OrdinalIgnoreCase);
			actions.Controls.Add(cancel);
			actions.Controls.Add(delete);
			layout.Controls.Add(actions, 0, 6);
			AcceptButton = delete;
			CancelButton = cancel;
			Controls.Add(layout);
			ActiveControl = serialNumberInput;
		}
	}
}
