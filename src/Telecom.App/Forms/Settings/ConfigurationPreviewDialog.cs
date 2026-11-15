using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Telecom.App.Forms.Common;
using Telecom.App.Theming;

namespace Telecom.App.Forms.Settings
{
	// Просмотр рабочего XML только для чтения, чтобы просмотр не менял настройки.
	internal sealed class ConfigurationPreviewDialog : Form
	{
		// Показывает содержимое файла в многострочном поле с кнопкой «Закрыть».
		internal ConfigurationPreviewDialog(string configurationPath)
		{
			Text = Path.GetFileName(configurationPath);
			StartPosition = FormStartPosition.CenterParent;
			ClientSize = new Size(840, 450);
			MinimizeBox = false;
			ShowInTaskbar = false;
			WindowAppearance.Apply(this);
			var text = new TextBox
			{
				Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
				ReadOnly = true, Text = File.ReadAllText(configurationPath), AccessibleName = "XML конфигурации"
			};
			var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.OK };
			FlowLayoutPanel actions = StatusReporter.CreateActions(close);
			actions.Dock = DockStyle.Bottom;
			actions.FlowDirection = FlowDirection.RightToLeft;
			Controls.Add(text);
			Controls.Add(actions);
			AcceptButton = close;
			CancelButton = close;
		}
	}
}
