using System;
using System.Collections.Generic;
using System.Threading;
using Telecom.Core.Computers;

namespace Telecom.Core.Access
{
	public interface IMachineCertificateStore
	{
		// Передаёт найденные сертификаты и ошибки отдельных записей; сбой доступа к хранилищу возвращает исключением.
		void Read(ComputerProfile profile, CancellationToken cancellationToken,
			Action<string, byte[]> readCertificate, Action<string, Exception> reportError);

		// Читает только указанные записи; null означает, что запись отсутствует.
		void ReadEntries(ComputerProfile profile, IEnumerable<string> entries, CancellationToken cancellationToken,
			Action<string, byte[]> readCertificate, Action<string, Exception> reportError);
	}
}
