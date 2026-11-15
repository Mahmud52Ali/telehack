using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Telecom.App.Windows
{
	// Объявления функций Windows API, которые используют адаптеры в этой папке.
	internal static class NativeMethods
	{
		// Описание сетевого ресурса для WNetAddConnection2 (NETRESOURCE).
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		internal struct NetworkResource
		{
			public int Scope;
			public int Type;
			public int DisplayType;
			public int Usage;
			public string LocalName;
			public string RemoteName;
			public string Comment;
			public string Provider;
		}

		// Запись диспетчера учётных данных (CREDENTIAL).
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		internal struct NativeCredential
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

		// Открывает файл или каталог и возвращает дескриптор (CreateFileW).
		[DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
		internal static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
			FileMode creation, uint flags, IntPtr template);

		// Отправляет управляющий код устройству; здесь — чтение данных ссылки.
		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, int inputSize,
			[Out] byte[] output, int outputSize, out int bytesReturned, IntPtr overlapped);

		// Устанавливает сетевое подключение к ресурсу с указанной учётной записью.
		[DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
		internal static extern int WNetAddConnection2(ref NetworkResource resource, string password, string userName, int flags);

		// Записывает учётные данные в диспетчер учётных данных Windows.
		[DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CredWrite(ref NativeCredential credential, int flags);

		// Читает учётные данные из диспетчера по имени записи.
		[DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

		// Удаляет запись из диспетчера учётных данных.
		[DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CredDelete(string target, int type, int flags);

		// Освобождает память, выделенную CredRead.
		[DllImport("advapi32.dll")]
		internal static extern void CredFree(IntPtr credential);

		// Создаёт токен входа для указанной учётной записи.
		[DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool LogonUser(string userName, string domain, IntPtr password, int logonType, int provider, out IntPtr token);

		// Закрывает дескриптор Windows.
		[DllImport("kernel32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CloseHandle(IntPtr handle);

		// Подключается к ветке реестра удалённого компьютера.
		[DllImport("advapi32.dll", EntryPoint = "RegConnectRegistryW", CharSet = CharSet.Unicode)]
		internal static extern int RegConnectRegistry(string computer, IntPtr key, out SafeRegistryHandle result);

		// Открывает раздел реестра.
		[DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode)]
		internal static extern int RegOpenKeyEx(SafeRegistryHandle key, string subKey, int options, int access, out SafeRegistryHandle result);

		// Читает значение реестра с проверкой типа.
		[DllImport("advapi32.dll", EntryPoint = "RegGetValueW", CharSet = CharSet.Unicode)]
		internal static extern int RegGetValue(SafeRegistryHandle key, string subKey, string value, int flags,
			IntPtr valueType, [Out] byte[] data, ref int size);
	}
}
