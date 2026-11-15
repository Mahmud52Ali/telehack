using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;

namespace Telecom.Core.Scanning
{
	// Сортирует исключения по категориям для интерфейса и решает, какие ошибки можно пропустить и идти дальше.
	// Опирается на тип исключения и код Windows, а не на текст сообщения (он зависит от языка Windows).
	public static class ErrorClassifier
	{
		// Коды Windows, означающие «нет прав» или неверную учётную запись.
		private static readonly HashSet<int> AccessDeniedCodes = new HashSet<int>
		{
			5,    // ERROR_ACCESS_DENIED — отказано в доступе
			86,   // ERROR_INVALID_PASSWORD — неверный пароль
			1219, // ERROR_SESSION_CREDENTIAL_CONFLICT — к ПК уже есть сеанс с другой учётной записью
			1326, // ERROR_LOGON_FAILURE — неверный логин или пароль
			1385, // ERROR_LOGON_TYPE_NOT_GRANTED — учётной записи запрещён такой вход
			1909  // ERROR_ACCOUNT_LOCKED_OUT — учётная запись заблокирована
		};

		// Коды Windows, означающие «ПК или служба недоступны».
		private static readonly HashSet<int> UnavailableCodes = new HashSet<int>
		{
			53,   // ERROR_BAD_NETPATH — сетевой путь не найден
			64,   // ERROR_NETNAME_DELETED — сетевое имя больше недоступно
			67,   // ERROR_BAD_NET_NAME — сетевое имя не найдено
			121,  // ERROR_SEM_TIMEOUT — превышено время ожидания
			1058, // ERROR_SERVICE_DISABLED — служба отключена (например, «Удалённый реестр»)
			1062, // ERROR_SERVICE_NOT_ACTIVE — служба не запущена
			1231, // ERROR_NETWORK_UNREACHABLE — сеть недоступна
			1232, // ERROR_HOST_UNREACHABLE — узел недоступен
			1722, // RPC_S_SERVER_UNAVAILABLE — сервер RPC недоступен
			1753  // EPT_S_NOT_REGISTERED — нет доступных конечных точек RPC
		};

		// Определяет категорию ошибки: нет доступа, недоступно, не найдено, повреждённые данные или прочее.
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
			if (AccessDeniedCodes.Contains(code))
			{
				return ScanErrorKind.AccessDenied;
			}

			if (UnavailableCodes.Contains(code))
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

		// Ошибки входных данных и окружения пропускаются с записью в журнал; ошибки программирования — нет.
		public static bool IsRecoverable(Exception exception)
		{
			return exception is IOException || exception is InvalidDataException
				|| exception is UnauthorizedAccessException || exception is SecurityException
				|| exception is CryptographicException || exception is FormatException
				|| exception is Win32Exception || exception is TimeoutException;
		}

		// Извлекает код Windows из Win32Exception или из HRESULT вида 0x8007xxxx; для остальных исключений возвращает 0.
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
