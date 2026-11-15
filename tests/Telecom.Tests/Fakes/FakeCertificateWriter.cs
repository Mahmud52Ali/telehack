using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Telecom.Core.Certificates;
using Telecom.Core.Database;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		// Запись в БД без MSSQL: запоминает вызовы или бросает заданное исключение.
		private sealed class FakeCertificateWriter : ICertificateWriter
		{
			internal readonly List<SaveCall> Calls = new List<SaveCall>();
			internal Exception Failure;

			// Запоминает переданные данные и возвращает «всё добавлено».
			public CertificateSaveResult Save(IEnumerable<CertificateInfo> certificates, string cryptoProVersion, string computerAddress)
			{
				if (Failure != null)
				{
					throw Failure;
				}

				Calls.Add(new SaveCall { Certificates = certificates.ToList(), CryptoProVersion = cryptoProVersion,
					ComputerAddress = computerAddress });
				return new CertificateSaveResult();
			}
		}

		// Параметры одного вызова записи.
		private sealed class SaveCall
		{
			internal List<CertificateInfo> Certificates;
			internal string CryptoProVersion;
			internal string ComputerAddress;
		}
	}
}
