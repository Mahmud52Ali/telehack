using System;
using System.Threading;

namespace Telecom.App
{
	// Общее для всего окна правило: одновременно идёт не больше одной длительной операции
	// (подключение, сканирование, проверка наличия). Хранит её токен отмены и признак «закрыть окно после завершения».
	internal sealed class OperationGate
	{
		private CancellationTokenSource cancellation;

		// Идёт ли сейчас длительная операция.
		internal bool IsBusy { get { return cancellation != null; } }

		// Запрошена ли отмена текущей операции.
		internal bool IsCancellationRequested { get { return cancellation != null && cancellation.IsCancellationRequested; } }

		// Пользователь закрыл окно во время работы: окно закроется, когда операции завершатся.
		internal bool ClosePending { get; set; }

		// Сообщает окну, что состояние кнопок нужно пересчитать.
		internal event EventHandler StateChanged;

		// Начинает операцию и возвращает её токен отмены. Вызывающий код обязан вызвать Finish в finally.
		internal CancellationToken Start()
		{
			if (cancellation != null)
			{
				throw new InvalidOperationException("Уже выполняется другая операция.");
			}

			cancellation = new CancellationTokenSource();
			NotifyChanged();
			return cancellation.Token;
		}

		// Просит текущую операцию остановиться; уже начатый системный вызов завершится сам.
		internal void Cancel()
		{
			if (cancellation != null)
			{
				cancellation.Cancel();
				NotifyChanged();
			}
		}

		// Завершает операцию и освобождает её токен.
		internal void Finish()
		{
			if (cancellation != null)
			{
				cancellation.Dispose();
				cancellation = null;
			}

			NotifyChanged();
		}

		// Просит окно пересчитать доступность кнопок, например после загрузки списка ПК.
		internal void NotifyChanged()
		{
			EventHandler changed = StateChanged;
			if (changed != null)
			{
				changed(this, EventArgs.Empty);
			}
		}
	}
}
