using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Telecom.App.Windows
{
	// Разбор структуры REPARSE_DATA_BUFFER: куда указывает junction или символическая ссылка.
	internal static class ReparsePointReader
	{
		private const uint OpenReparsePoint = 0x00200000;
		private const uint BackupSemantics = 0x02000000;
		private const uint GetReparsePoint = 0x000900A8;
		private const uint MountPointTag = 0xA0000003;
		private const uint SymbolicLinkTag = 0xA000000C;

		// Читает метаданные junction/symlink, не обходя ссылку и не требуя доступа к её содержимому.
		// Неизвестный тип ссылки возвращает null.
		internal static string ReadTarget(string path)
		{
			using (SafeFileHandle file = NativeMethods.CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
				FileMode.Open, OpenReparsePoint | BackupSemantics, IntPtr.Zero))
			{
				if (file.IsInvalid)
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}

				var data = new byte[16384];
				int bytesReturned;
				if (!NativeMethods.DeviceIoControl(file, GetReparsePoint, IntPtr.Zero, 0, data, data.Length, out bytesReturned, IntPtr.Zero))
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
	}
}
