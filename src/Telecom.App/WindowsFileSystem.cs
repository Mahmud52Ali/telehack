using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telecom.Core;
using Microsoft.Win32.SafeHandles;

namespace Telecom.App
{
	public sealed class WindowsFileSystem : IComputerAccess
	{
		private const int MaximumCertificateFileSize = 4 * 1024 * 1024;
		private const uint OpenReparsePoint = 0x00200000;
		private const uint BackupSemantics = 0x02000000;
		private const uint GetReparsePoint = 0x000900A8;
		private const uint MountPointTag = 0xA0000003;
		private const uint SymbolicLinkTag = 0xA000000C;

		[DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
			FileMode creation, uint flags, IntPtr template);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, int inputSize,
			[Out] byte[] output, int outputSize, out int bytesReturned, IntPtr overlapped);

		// Ограничивает ожидание соединения с SMB без зависимости от ICMP и изменения настроек ПК.
		public void CheckConnection(string computerName, int timeoutSeconds, CancellationToken cancellationToken)
		{
			string computer = RemotePath.NormalizeComputerName(computerName);
			if (timeoutSeconds < 1 || timeoutSeconds > 60)
			{
				throw new ArgumentOutOfRangeException("timeoutSeconds");
			}

			cancellationToken.ThrowIfCancellationRequested();
			using (var client = new TcpClient())
			{
				Task connection = client.ConnectAsync(computer, 445);
				try
				{
					if (!connection.Wait(timeoutSeconds * 1000, cancellationToken))
					{
						throw new TimeoutException("Не удалось подключиться к SMB-порту 445 за отведённое время.");
					}
				}
				catch (AggregateException exception)
				{
					ExceptionDispatchInfo.Capture(exception.GetBaseException()).Throw();
					throw;
				}
				finally
				{
					client.Close();
					// После таймаута/отмены закрытие сокета завершит ConnectAsync ошибкой; наблюдаем её без второго сообщения об ошибке.
					connection.ContinueWith(task => task.Exception.Handle(error => true), CancellationToken.None,
						TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
				}
			}
		}

		// Читает ресурс версии локального или UNC-файла, не запуская csptest.exe и не скрывая отказ доступа.
		public string GetProductVersion(string executablePath)
		{
			using (var readable = new FileStream(executablePath, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete))
			{
				return FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
			}
		}

		// Перечисляет файлы локальной или UNC-папки с правами текущей учётной записи Windows.
		public IEnumerable<string> GetFiles(string directoryPath)
		{
			return Directory.GetFiles(directoryPath);
		}

		// Перечисляет дочерние папки, оставляя контроль рекурсии сканеру.
		public IEnumerable<string> GetDirectories(string directoryPath)
		{
			return Directory.GetDirectories(directoryPath);
		}

		// Определяет ссылки и junction, чтобы сканирование не уходило в циклы и посторонние каталоги.
		public bool IsReparsePoint(string path)
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
		}

		// Читает метаданные junction/symlink, не обходя ссылку и не требуя доступа к её содержимому.
		public string GetReparsePointTarget(string path)
		{
			using (SafeFileHandle file = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
				FileMode.Open, OpenReparsePoint | BackupSemantics, IntPtr.Zero))
			{
				if (file.IsInvalid)
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}

				var data = new byte[16384];
				int bytesReturned;
				if (!DeviceIoControl(file, GetReparsePoint, IntPtr.Zero, 0, data, data.Length, out bytesReturned, IntPtr.Zero))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}

				if (bytesReturned < 8)
				{
					throw new InvalidDataException("Повреждены метаданные ссылки.");
				}
				uint tag = BitConverter.ToUInt32(data, 0);
				if (tag != MountPointTag && tag != SymbolicLinkTag)
				{
					return null;
				}

				// PathBuffer начинается после полей имён; у symlink перед ним дополнительно находится Flags.
				int pathOffset = tag == MountPointTag ? 16 : 20;
				int dataSize = 8 + BitConverter.ToUInt16(data, 4);
				if (dataSize > bytesReturned || dataSize < pathOffset)
				{
					throw new InvalidDataException("Повреждены метаданные ссылки.");
				}
				int offset = BitConverter.ToUInt16(data, 8);
				int length = BitConverter.ToUInt16(data, 10);
				if (length == 0 || (offset % 2) != 0 || (length % 2) != 0 || pathOffset + offset + length > dataSize)
				{
					throw new InvalidDataException("Повреждён путь назначения ссылки.");
				}
				string target = Encoding.Unicode.GetString(data, pathOffset + offset, length);
				if (tag == SymbolicLinkTag && (BitConverter.ToUInt32(data, 16) & 1) != 0)
				{
					return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), target));
				}
				if (target.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
				{
					return @"\\" + target.Substring(8);
				}
				return target.StartsWith(@"\??\", StringComparison.Ordinal) ? target.Substring(4) : target;
			}
		}

		// Читает файл без записи; ограничение размера защищает от случайного чтения огромных файлов.
		public byte[] ReadAllBytes(string filePath)
		{
			using (var source = new FileStream(filePath, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete))
			using (var target = new MemoryStream())
			{
				if (source.Length > MaximumCertificateFileSize)
				{
					throw new InvalidDataException("Размер файла превышает допустимые для сертификата 4 МиБ.");
				}

				var buffer = new byte[8192];
				int bytesRead;
				while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
				{
					if (target.Length + bytesRead > MaximumCertificateFileSize)
					{
						throw new InvalidDataException("Файл увеличился при чтении и превысил 4 МиБ.");
					}

					target.Write(buffer, 0, bytesRead);
				}

				return target.ToArray();
			}
		}
	}
}
