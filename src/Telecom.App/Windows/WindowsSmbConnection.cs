using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using Telecom.Core.Access;
using Telecom.Core.Computers;

namespace Telecom.App.Windows
{
	public sealed class WindowsSmbConnection : IComputerConnector
	{
		private readonly WindowsCredentialStore credentials;
		private readonly IComputerAccess access;
		private readonly Func<string, string, string, int> connectResource;

		// Использует Windows SMB и сохранённые приложением учётные данные без запуска net use.
		public WindowsSmbConnection(WindowsCredentialStore credentials)
			: this(credentials, new WindowsFileSystem(), ConnectResource)
		{
		}

		// Позволяет проверять порядок подключения и ошибки без настоящего сервера и изменения сетевых сеансов.
		public WindowsSmbConnection(WindowsCredentialStore credentials, IComputerAccess access, Func<string, string, string, int> connectResource)
		{
			this.credentials = credentials;
			this.access = access;
			this.connectResource = connectResource;
		}

		// После проверки TCP устанавливает сеанс SMB; права на конкретные файлы проверяет сканер.
		public void Connect(ComputerProfile profile, int timeoutSeconds, CancellationToken cancellationToken)
		{
			if (profile.IsLocal)
			{
				throw new InvalidOperationException("Этот компьютер читается локально, SMB-подключение не требуется.");
			}

			if (!profile.HasUserName)
			{
				throw new InvalidOperationException("Укажите логин и пароль этого ПК через «Изменить».");
			}

			access.CheckConnection(profile.IpAddress, timeoutSeconds, cancellationToken);
			NetworkCredential credential = credentials.ReadForComputer(profile);

			cancellationToken.ThrowIfCancellationRequested();
			int code = connectResource(@"\\" + profile.IpAddress + @"\IPC$", credential.UserName, credential.Password);
			if (code != 0)
			{
				// Чужие SMB-сеансы не отключаем: они могут использоваться Проводником или другими программами.
				string message = code == 1219
					? "Windows уже подключена к этому ПК с другой учётной записью (1219). "
						+ "Используйте ту же учётку или вручную завершите прежнее соединение."
					: "Ошибка подключения SMB (" + code + "): " + new Win32Exception(code).Message;
				throw new Win32Exception(code, message);
			}
		}

		// CONNECT_TEMPORARY и отсутствие буквы диска оставляют восстановление сеансов самому приложению.
		private static int ConnectResource(string remotePath, string userName, string password)
		{
			var resource = new NativeMethods.NetworkResource { RemoteName = remotePath };
			return NativeMethods.WNetAddConnection2(ref resource, password, userName, 4);
		}

	}
}
