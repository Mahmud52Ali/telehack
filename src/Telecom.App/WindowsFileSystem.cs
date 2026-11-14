using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Telecom.Core;

namespace Telecom.App
{
	public sealed class WindowsFileSystem : IComputerAccess
	{
		private const int MaximumCertificateFileSize = 4 * 1024 * 1024;

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

		// Читает ресурс версии через SMB, не запуская csptest.exe и не скрывая отказ доступа под File.Exists.
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
