using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Computers;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		// Подключение без сети: заданные IP падают с указанным исключением.
		private sealed class FakeConnector : IComputerConnector
		{
			internal readonly Dictionary<string, Exception> Failures = new Dictionary<string, Exception>();
			internal Action OnConnect;

			// Вызывает OnConnect, затем бросает исключение, если оно задано для IP, и проверяет отмену.
			public void Connect(ComputerProfile profile, int timeoutSeconds, CancellationToken cancellationToken)
			{
				if (OnConnect != null)
				{
					OnConnect();
				}

				cancellationToken.ThrowIfCancellationRequested();
				Exception failure;
				if (Failures.TryGetValue(profile.IpAddress, out failure))
				{
					throw failure;
				}
			}
		}
	}
}
