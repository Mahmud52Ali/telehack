using System;
using System.Collections.Generic;
using System.Data;
using System.IO;

namespace Telecom.Tests
{
	internal static class Check
	{
		// Сравнивает фактическое значение с независимым ожидаемым результатом.
		internal static void Equal<T>(T expected, T actual)
		{
			if (!EqualityComparer<T>.Default.Equals(expected, actual))
			{
				throw new InvalidOperationException("Ожидалось: " + expected + "; получено: " + actual);
			}
		}

		// Проверяет отказ с ожидаемым типом исключения.
		internal static void Throws<T>(Action action) where T : Exception
		{
			try
			{
				action();
			}
			catch (T)
			{
				return;
			}

			throw new InvalidOperationException("Не получено ожидаемое исключение " + typeof(T).Name);
		}
	}
}
