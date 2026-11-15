using System;
using System.Drawing;
using System.Windows.Forms;
using Telecom.App.Theming;
using Telecom.Core.Logging;

namespace Telecom.App.Forms.Common
{
	// Сообщения в строках состояния: обычные и об ошибках. Ошибка всегда пишется в журнал,
	// а полный текст доступен во всплывающей подсказке, если подпись обрезана.
	internal sealed class StatusReporter : IDisposable
	{
		private readonly ILogger logger;
		private readonly ToolTip tips = new ToolTip();

		// Сообщает, что ошибка записана в журнал (например, чтобы обновить ссылку на файл журнала).
		internal event EventHandler FailureLogged;

		// Журнал, в который пишутся ошибки.
		internal StatusReporter(ILogger logger)
		{
			this.logger = logger;
		}

		// Показывает сообщение; ошибка выделяется цветом текущей темы.
		internal void Show(Label label, string message, bool error)
		{
			label.ForeColor = error ? WindowAppearance.ErrorColor(label) : Color.Empty;
			label.Text = message;
			tips.SetToolTip(label, message);
		}

		// Добавляет подсказку к элементу, например к кнопке.
		internal void SetHint(Control control, string hint)
		{
			tips.SetToolTip(control, hint);
		}

		// Пишет ошибку в журнал и показывает её, если элемент ещё не закрыт; label = null — только журнал.
		internal void ReportFailure(Control owner, Label label, string message, Exception exception)
		{
			string details = message + " " + exception.Message;
			logger.Error(message, exception);
			var fileLogger = logger as FileLogger;
			if (fileLogger != null && fileLogger.LastWriteError != null)
			{
				details += " Журнал недоступен. " + fileLogger.LastWriteError.Message;
			}

			if (owner.IsDisposed)
			{
				return;
			}

			if (label != null)
			{
				Show(label, details, true);
			}

			EventHandler logged = FailureLogged;
			if (logged != null)
			{
				logged(this, EventArgs.Empty);
			}
		}

		// Освобождает всплывающие подсказки.
		public void Dispose()
		{
			tips.Dispose();
		}

		// Раскладывает связанные кнопки и поля в одну строку.
		internal static FlowLayoutPanel CreateActions(params Control[] controls)
		{
			var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
			actions.Controls.AddRange(controls);
			return actions;
		}
	}
}
