using System;
using System.Windows.Forms;

namespace Telecom.App.Forms.Common
{
	// Передаёт события хода работы из фоновой задачи в поток окна синхронно и по порядку.
	// В отличие от Progress<T> не зависит от текущего SynchronizationContext.
	internal sealed class ControlProgress<T> : IProgress<T>
	{
		private readonly Control owner;
		private readonly Action<T> handler;

		// owner — элемент окна, в потоке которого выполняется handler.
		internal ControlProgress(Control owner, Action<T> handler)
		{
			this.owner = owner;
			this.handler = handler;
		}

		// Выполняет обработчик в потоке окна и ждёт его завершения; закрытое окно событие пропускает.
		// Если у самого элемента ещё нет дескриптора, Windows Forms использует дескриптор ближайшего родителя.
		public void Report(T value)
		{
			if (owner.IsDisposed)
			{
				return;
			}

			if (owner.InvokeRequired)
			{
				owner.Invoke(handler, value);
			}
			else
			{
				handler(value);
			}
		}
	}
}
