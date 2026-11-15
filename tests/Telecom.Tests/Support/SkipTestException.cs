using System;

namespace Telecom.Tests
{
	// Проверка не может выполняться в текущем окружении; запускалка выводит её как SKIP.
	internal sealed class SkipTestException : Exception
	{
		// Сохраняет причину пропуска.
		internal SkipTestException(string reason) : base(reason)
		{
		}
	}
}
