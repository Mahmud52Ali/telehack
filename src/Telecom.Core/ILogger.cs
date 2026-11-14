using System;

namespace Telecom.Core
{
	public interface ILogger
	{
		// Записывает информационное событие.
		void Info(string message);

		// Записывает ошибку вместе с исключением и стеком вызовов.
		void Error(string message, Exception exception);
	}
}
