using System.Collections.Generic;
using Telecom.Core.Certificates;

namespace Telecom.Core.Database
{
	public interface ICertificateWriter
	{
		// Записывает сертификаты одного ПК: обновляет строки по серийному номеру или добавляет новые.
		CertificateSaveResult Save(IEnumerable<CertificateInfo> certificates, string cryptoProVersion, string computerAddress);
	}
}
