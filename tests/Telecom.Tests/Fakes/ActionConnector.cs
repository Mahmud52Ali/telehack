using System;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Computers;

namespace Telecom.Tests
{
	// Подключение без сети: вызывает переданное действие, чтобы тест мог посчитать вызовы или смоделировать отказ.
	internal sealed class ActionConnector : IComputerConnector
	{
		private readonly Action<ComputerProfile, CancellationToken> connect;

		// Запоминает действие, которое выполняется вместо подключения.
		internal ActionConnector(Action<ComputerProfile, CancellationToken> connect)
		{
			this.connect = connect;
		}

		// Выполняет действие; таймаут в тестах не используется.
		public void Connect(ComputerProfile profile, int timeoutSeconds, CancellationToken cancellationToken)
		{
			connect(profile, cancellationToken);
		}
	}
}
