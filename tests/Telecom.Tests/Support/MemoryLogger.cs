using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Telecom.Core.Logging;

namespace Telecom.Tests
{
	internal sealed class MemoryLogger : ILogger
	{
		internal readonly List<Exception> Errors = new List<Exception>();

		// Не создаёт файлов для обычных событий модульных проверок.
		public void Info(string message)
		{
			Messages.Add(message);
		}

		internal readonly List<string> Messages = new List<string>();

		// Позволяет проверить, что ошибка действительно передана в журнал.
		public void Error(string message, Exception exception)
		{
			Errors.Add(exception);
		}
	}
}
