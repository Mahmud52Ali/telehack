using Telecom.Core.Locations;
using Telecom.Core.Scanning;

namespace Telecom.App.Forms.Common
{
	// Подписи для пользователя к техническим статусам из Core.
	internal static class DisplayNames
	{
		// Итог проверки ПК.
		internal static string ScanStatus(ComputerScanStatus status)
		{
			switch (status)
			{
				case ComputerScanStatus.Complete: return "Сканирование завершено";
				case ComputerScanStatus.Partial: return "Проверено частично";
				case ComputerScanStatus.AccessDenied: return "Нет прав доступа";
				case ComputerScanStatus.Unavailable: return "Сетевой доступ недоступен";
				case ComputerScanStatus.Cancelled: return "Проверка отменена";
				default: return "Не удалось проверить настроенные папки";
			}
		}

		// Версия КриптоПро или причина, по которой её нет. Отсутствие файла не выдаётся за отсутствие продукта.
		internal static string CryptoPro(ComputerScanResult result)
		{
			switch (result.CryptoProStatus)
			{
				case CryptoProStatus.Found: return result.CryptoProVersion;
				case CryptoProStatus.NotFound: return "не найден в настроенных путях";
				case CryptoProStatus.Error: return "версию определить не удалось";
				default: return "не проверялось";
			}
		}

		// Категория ошибки сканирования.
		internal static string ErrorKind(ScanErrorKind kind)
		{
			switch (kind)
			{
				case ScanErrorKind.AccessDenied: return "Нет доступа";
				case ScanErrorKind.Unavailable: return "Ресурс недоступен";
				case ScanErrorKind.NotFound: return "Не найдено";
				case ScanErrorKind.InvalidData: return "Некорректные данные";
				default: return "Ошибка чтения";
			}
		}

		// Итог проверки наличия сертификата; 0 — проверка ещё не выполнялась.
		internal static string LocationStatus(CertificateLocationStatus status)
		{
			switch (status)
			{
				case CertificateLocationStatus.Found: return "Найден";
				case CertificateLocationStatus.Missing: return "Не найден по этому пути";
				case CertificateLocationStatus.DifferentCertificate: return "Заменён другим сертификатом";
				case CertificateLocationStatus.Unverified: return "Не удалось проверить";
				default: return "Не проверялся";
			}
		}
	}
}
