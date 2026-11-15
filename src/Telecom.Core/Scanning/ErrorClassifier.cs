using System;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;

namespace Telecom.Core.Scanning
{
	public static class ErrorClassifier
	{
		// Классифицирует ошибки по типу и Win32-коду, не анализируя локализованный текст Windows.
		public static ScanErrorKind Classify(Exception exception)
		{
			if (exception is UnauthorizedAccessException || exception is SecurityException)
			{
				return ScanErrorKind.AccessDenied;
			}

			if (exception is TimeoutException || exception is SocketException)
			{
				return ScanErrorKind.Unavailable;
			}

			int code = GetWindowsErrorCode(exception);
			if (code == 5 || code == 86 || code == 1219 || code == 1326 || code == 1385 || code == 1909)
			{
				return ScanErrorKind.AccessDenied;
			}

			if (code == 53 || code == 64 || code == 67 || code == 121 || code == 1231 || code == 1232
				|| code == 1058 || code == 1062 || code == 1722 || code == 1753)
			{
				return ScanErrorKind.Unavailable;
			}

			if (exception is FileNotFoundException || exception is DirectoryNotFoundException)
			{
				return ScanErrorKind.NotFound;
			}

			if (exception is InvalidDataException || exception is FormatException || exception is CryptographicException)
			{
				return ScanErrorKind.InvalidData;
			}

			return ScanErrorKind.Other;
		}

		// Отличает ошибки входных данных и окружения от ошибок программирования.
		public static bool IsRecoverable(Exception exception)
		{
			return exception is IOException || exception is InvalidDataException
				|| exception is UnauthorizedAccessException || exception is SecurityException
				|| exception is CryptographicException || exception is FormatException
				|| exception is Win32Exception || exception is TimeoutException;
		}

		// Извлекает Win32-код только из настоящего HRESULT_FROM_WIN32 или Win32Exception.
		private static int GetWindowsErrorCode(Exception exception)
		{
			var windowsException = exception as Win32Exception;
			if (windowsException != null)
			{
				return windowsException.NativeErrorCode;
			}

			uint result = unchecked((uint)exception.HResult);
			return (result & 0xFFFF0000) == 0x80070000 ? (int)(result & 0xFFFF) : 0;
		}
	}
}
