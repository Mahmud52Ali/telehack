using System.Threading;

namespace Telecom.Core
{
	public interface IComputerAccess : ICertificateFileSystem
	{
		// Проверяет доступность TCP-порта SMB; это ещё не проверка прав на административные папки.
		void CheckConnection(string computerName, int timeoutSeconds, CancellationToken cancellationToken);

		// Возвращает ProductVersion одного доступного файла, не запуская его.
		string GetProductVersion(string executablePath);
	}
}
