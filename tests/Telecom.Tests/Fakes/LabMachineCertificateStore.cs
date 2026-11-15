using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Telecom.Core.Access;
using Telecom.Core.Computers;

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private sealed class LabMachineCertificateStore : IMachineCertificateStore
		{
			private readonly Action<ComputerProfile, CancellationToken, Action<string, byte[]>, Action<string, Exception>> read;
			private readonly Action<ComputerProfile, IEnumerable<string>, CancellationToken, Action<string, byte[]>, Action<string, Exception>> readEntries;
			internal int Reads { get; private set; }

			// Задаёт сценарий чтения хранилища без доступа к реестру компьютеров пользователя.
			internal LabMachineCertificateStore(Action<ComputerProfile, CancellationToken, Action<string, byte[]>, Action<string, Exception>> read,
				Action<ComputerProfile, IEnumerable<string>, CancellationToken, Action<string, byte[]>, Action<string, Exception>> readEntries = null)
			{
				this.read = read;
				this.readEntries = readEntries;
			}

			// Учитывает обращения к хранилищу отдельно от подключения SMB.
			public void Read(ComputerProfile profile, CancellationToken cancellationToken,
				Action<string, byte[]> readCertificate, Action<string, Exception> reportError)
			{
				Reads++;
				read(profile, cancellationToken, readCertificate, reportError);
			}

			public void ReadEntries(ComputerProfile profile, IEnumerable<string> entries, CancellationToken cancellationToken,
				Action<string, byte[]> readCertificate, Action<string, Exception> reportError)
			{
				Reads++;
				readEntries(profile, entries, cancellationToken, readCertificate, reportError);
			}
		}
	}
}
