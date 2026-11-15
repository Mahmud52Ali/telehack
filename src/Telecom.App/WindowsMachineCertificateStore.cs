using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Telecom.Core.Access;
using Telecom.Core.Computers;
using Telecom.Core.Scanning;

namespace Telecom.App
{
	internal sealed class WindowsMachineCertificateStore : IMachineCertificateStore
	{
		private const string PersonalStoreKey = @"SOFTWARE\Microsoft\SystemCertificates\My\Certificates";
		private const int NewCredentialsLogon = 9;
		private const int NegotiateLogonProvider = 3;
		private const int RegistryBinaryValue = 0x00000008;
		private const int FileNotFound = 2;
		private const int PathNotFound = 3;
		private const int UnsupportedType = 1630;
		private const int MaximumBlobSize = 4 * 1024 * 1024;
		private readonly WindowsCredentialStore credentials = new WindowsCredentialStore();

		// Читает локальный реестр напрямую, а удалённый — с сохранённой учётной записью ПК.
		public void Read(ComputerProfile profile, CancellationToken cancellationToken,
			Action<string, byte[]> readCertificate, Action<string, Exception> reportError)
		{
			ReadPersonalStore(profile, null, cancellationToken, readCertificate, reportError);
		}

		// Читает по сохранённым именам ключей без перечисления хранилища.
		public void ReadEntries(ComputerProfile profile, IEnumerable<string> entries, CancellationToken cancellationToken,
			Action<string, byte[]> readCertificate, Action<string, Exception> reportError)
		{
			ReadPersonalStore(profile, entries, cancellationToken, readCertificate, reportError);
		}

		// Выполняет чтение с теми же учётными данными для обхода и адресной проверки.
		private void ReadPersonalStore(ComputerProfile profile, IEnumerable<string> entries, CancellationToken cancellationToken,
			Action<string, byte[]> readCertificate, Action<string, Exception> reportError)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (profile.IsLocal)
			{
				using (RegistryKey localBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
				{
					ReadStore(localBase.Handle, entries, cancellationToken, readCertificate, reportError);
				}

				return;
			}

			NetworkCredential credential;
			try
			{
				credential = credentials.ReadForComputer(profile);
			}
			catch (InvalidOperationException exception)
			{
				throw new UnauthorizedAccessException(exception.Message, exception);
			}

			IntPtr token = LogOnForNetwork(credential);
			try
			{
				using (WindowsIdentity.Impersonate(token))
				{
					cancellationToken.ThrowIfCancellationRequested();
					ReadRegistry(profile.IpAddress, entries, cancellationToken, readCertificate, reportError);
				}
			}
			finally
			{
				CloseHandle(token);
			}
		}

		// Создаёт токен для исходящего подключения с обычным логином, именем ДОМЕН\логин или UPN.
		private static IntPtr LogOnForNetwork(NetworkCredential credential)
		{
			string userName = credential.UserName;
			int separator = userName.IndexOf('\\');
			string domain = userName.IndexOf('@') >= 0 ? null : string.Empty;
			if (separator >= 0)
			{
				domain = userName.Substring(0, separator);
				userName = userName.Substring(separator + 1);
			}

			using (SecureString password = credential.SecurePassword)
			{
				IntPtr passwordBuffer = Marshal.SecureStringToGlobalAllocUnicode(password);
				try
				{
					IntPtr token;
					if (!LogonUser(userName, domain, passwordBuffer, NewCredentialsLogon, NegotiateLogonProvider, out token))
					{
						int error = Marshal.GetLastWin32Error();
						throw new Win32Exception(error, "Не удалось использовать сохранённую учётную запись. " + new Win32Exception(error).Message);
					}

					return token;
				}
				finally
				{
					Marshal.ZeroFreeGlobalAllocUnicode(passwordBuffer);
				}
			}
		}

		// Открывает ветку LocalMachine на чтение. Отсутствующая ветка означает пустое хранилище.
		private static void ReadRegistry(string computer, IEnumerable<string> entries, CancellationToken cancellationToken,
			Action<string, byte[]> readCertificate, Action<string, Exception> reportError)
		{
			SafeRegistryHandle remoteBase;
			int error = RegConnectRegistry(@"\\" + ComputerProfile.NormalizeIpAddress(computer),
				new IntPtr((int)RegistryHive.LocalMachine), out remoteBase);
			using (remoteBase)
			{
				if (error != 0)
				{
					throw new Win32Exception(error, "Не удалось подключиться через службу «Удалённый реестр». " + new Win32Exception(error).Message);
				}

				cancellationToken.ThrowIfCancellationRequested();
				ReadStore(remoteBase, entries, cancellationToken, readCertificate, reportError);
			}
		}

		// Одинаково обрабатывает записи локального и удалённого хранилища без изменения реестра.
		private static void ReadStore(SafeRegistryHandle baseHandle, IEnumerable<string> entries, CancellationToken cancellationToken,
			Action<string, byte[]> readCertificate, Action<string, Exception> reportError)
		{
			SafeRegistryHandle storeHandle;
			int error = RegOpenKeyEx(baseHandle, PersonalStoreKey, 0, (int)RegistryRights.ReadKey, out storeHandle);
			using (storeHandle)
			{
				if (error == FileNotFound || error == PathNotFound)
				{
					if (entries != null)
					{
						foreach (string entry in entries)
						{
							cancellationToken.ThrowIfCancellationRequested();
							readCertificate(entry, null);
						}
					}

					return;
				}
				if (error != 0)
				{
					throw new Win32Exception(error);
				}

				using (RegistryKey store = RegistryKey.FromHandle(storeHandle))
				{
					cancellationToken.ThrowIfCancellationRequested();
					foreach (string entry in entries ?? store.GetSubKeyNames())
					{
						cancellationToken.ThrowIfCancellationRequested();
						byte[] contents;
						try
						{
							contents = entries == null ? ReadBlob(storeHandle, entry) : ReadExistingEntry(storeHandle, entry);
						}
						catch (Exception exception)
						{
							if (!ErrorClassifier.IsRecoverable(exception))
							{
								throw;
							}

							reportError(entry, exception);
							continue;
						}

						readCertificate(entry, contents);
					}
				}
			}
		}

		// Отдельно подтверждает отсутствие ключа; повреждённый Blob остаётся ошибкой чтения.
		private static byte[] ReadExistingEntry(SafeRegistryHandle storeHandle, string entry)
		{
			if (string.IsNullOrEmpty(entry) || entry.IndexOfAny(new[] { '\\', '/', '\0' }) >= 0)
			{
				throw new InvalidDataException("Недопустимое имя записи в хранилище сертификатов.");
			}

			SafeRegistryHandle entryHandle;
			int error = RegOpenKeyEx(storeHandle, entry, 0, (int)RegistryRights.ReadKey, out entryHandle);
			using (entryHandle)
			{
				if (error == FileNotFound || error == PathNotFound)
				{
					return null;
				}
				if (error != 0)
				{
					throw new Win32Exception(error);
				}

				return ReadBlob(entryHandle, null);
			}
		}

		// Читает только двоичное поле сертификата с ограничением размера до выделения буфера.
		private static byte[] ReadBlob(SafeRegistryHandle storeHandle, string entry)
		{
			int size = 0;
			int error = RegGetValue(storeHandle, entry, "Blob", RegistryBinaryValue, IntPtr.Zero, null, ref size);
			if (error == FileNotFound || error == PathNotFound || error == UnsupportedType)
			{
				throw new InvalidDataException("В записи хранилища отсутствует двоичное значение Blob.");
			}
			if (error != 0)
			{
				throw new Win32Exception(error);
			}
			if (size <= 0 || size > MaximumBlobSize)
			{
				throw new InvalidDataException("Некорректный размер записи сертификата в реестре.");
			}

			var contents = new byte[size];
			error = RegGetValue(storeHandle, entry, "Blob", RegistryBinaryValue, IntPtr.Zero, contents, ref size);
			if (error != 0)
			{
				throw new Win32Exception(error);
			}

			if (size != contents.Length)
			{
				Array.Resize(ref contents, size);
			}

			return contents;
		}

		[DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool LogonUser(string userName, string domain, IntPtr password, int logonType, int provider, out IntPtr token);

		[DllImport("kernel32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CloseHandle(IntPtr handle);

		[DllImport("advapi32.dll", EntryPoint = "RegConnectRegistryW", CharSet = CharSet.Unicode)]
		private static extern int RegConnectRegistry(string computer, IntPtr key, out SafeRegistryHandle result);

		[DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode)]
		private static extern int RegOpenKeyEx(SafeRegistryHandle key, string subKey, int options, int access, out SafeRegistryHandle result);

		[DllImport("advapi32.dll", EntryPoint = "RegGetValueW", CharSet = CharSet.Unicode)]
		private static extern int RegGetValue(SafeRegistryHandle key, string subKey, string value, int flags,
			IntPtr valueType, [Out] byte[] data, ref int size);
	}
}
