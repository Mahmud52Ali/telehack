using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using FileTime = System.Runtime.InteropServices.ComTypes.FILETIME;
using Telecom.Core.Computers;

namespace Telecom.App
{
	public sealed class WindowsCredentialStore
	{
		private const int GenericCredential = 1;
		private const int PersistOnLocalComputer = 2;
		private const int NotFound = 1168;

		// Записывает только собственную запись приложения в диспетчер учётных данных текущего пользователя.
		public void Save(Guid id, string userName, string password)
		{
			if (string.IsNullOrWhiteSpace(userName) || password == null || password.IndexOf('\0') >= 0)
			{
				throw new ArgumentException("Укажите учётную запись и допустимый пароль.");
			}

			IntPtr secret = Marshal.StringToCoTaskMemUni(password);
			try
			{
				var credential = new NativeCredential
				{
					Type = GenericCredential, TargetName = Target(id), UserName = userName,
					CredentialBlob = secret, CredentialBlobSize = checked(password.Length * 2), Persist = PersistOnLocalComputer
				};
				if (!CredWrite(ref credential, 0))
				{
					throw CredentialError(Marshal.GetLastWin32Error(), "Не удалось сохранить учётные данные Windows.");
				}
			}
			finally
			{
				Marshal.ZeroFreeCoTaskMemUnicode(secret);
			}
		}

		// Читает одну известную приложению запись, не перечисляя чужие сохранённые пароли.
		public NetworkCredential Read(Guid id)
		{
			IntPtr pointer;
			if (!CredRead(Target(id), GenericCredential, 0, out pointer))
			{
				int code = Marshal.GetLastWin32Error();
				throw CredentialError(code, code == NotFound
					? "Пароль этого компьютера не сохранён. Откройте «Изменить» и задайте пароль заново."
					: "Не удалось прочитать учётные данные Windows.");
			}

			try
			{
				var credential = (NativeCredential)Marshal.PtrToStructure(pointer, typeof(NativeCredential));
				if (credential.CredentialBlobSize < 0 || credential.CredentialBlobSize % 2 != 0)
				{
					throw new InvalidOperationException("Некорректная сохранённая запись учётных данных.");
				}

				string password = credential.CredentialBlobSize == 0 ? string.Empty
					: Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
				return new NetworkCredential(credential.UserName, password);
			}
			finally
			{
				CredFree(pointer);
			}
		}

		// Возвращает учётные данные только при совпадении логина с настройками ПК.
		internal NetworkCredential ReadForComputer(ComputerProfile profile)
		{
			NetworkCredential credential = Read(profile.Id);
			if (!string.Equals(credential.UserName, profile.UserName, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Сохранённая учётная запись не совпадает с настройками компьютера. Задайте пароль заново.");
			}

			return credential;
		}

		// Удаляет только запись с идентификатором удалённого или изменённого профиля.
		public void Delete(Guid id)
		{
			if (!CredDelete(Target(id), GenericCredential, 0))
			{
				int code = Marshal.GetLastWin32Error();
				if (code != NotFound)
				{
					throw CredentialError(code, "Не удалось удалить учётные данные Windows.");
				}
			}
		}

		// Сохраняет код и системное описание ошибки в сообщении, которое попадёт в файловый журнал.
		private static Win32Exception CredentialError(int code, string message)
		{
			return new Win32Exception(code, message + " Код Windows: " + code + ". " + new Win32Exception(code).Message);
		}

		// Отделяет учётные данные продукта от любых других записей диспетчера.
		private static string Target(Guid id)
		{
			if (id == Guid.Empty)
			{
				throw new ArgumentException("Не задан идентификатор компьютера.", "id");
			}

			return "Telecom/Computer/" + id.ToString("N");
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct NativeCredential
		{
			public int Flags;
			public int Type;
			public string TargetName;
			public string Comment;
			public FileTime LastWritten;
			public int CredentialBlobSize;
			public IntPtr CredentialBlob;
			public int Persist;
			public int AttributeCount;
			public IntPtr Attributes;
			public string TargetAlias;
			public string UserName;
		}

		[DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CredWrite(ref NativeCredential credential, int flags);

		[DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

		[DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CredDelete(string target, int type, int flags);

		[DllImport("advapi32.dll")]
		private static extern void CredFree(IntPtr credential);
	}
}
