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

namespace Telecom.Tests
{
	internal static partial class Program
	{
		private sealed class SessionCheckedAccess : IComputerAccess
		{
			private readonly Dictionary<string, LabComputerAccess> computers;
			internal readonly List<string> AttemptedComputers = new List<string>();
			internal readonly HashSet<string> AuthenticatedComputers = new HashSet<string>();

			// Сохраняет файловую часть существующего стенда и добавляет проверку предварительного входа.
			internal SessionCheckedAccess(Dictionary<string, LabComputerAccess> computers)
			{
				this.computers = computers;
			}

			// Моделирует рабочий и недоступный ПК без обращения к DNS и сети.
			public void CheckConnection(string computerName, int timeoutSeconds, CancellationToken cancellationToken)
			{
				cancellationToken.ThrowIfCancellationRequested();
				AttemptedComputers.Add(computerName);
				if (computerName == ProfileOfflineIp)
				{
					throw new TimeoutException("Тестовый компьютер недоступен.");
				}

				computers[computerName].CheckConnection(computerName, timeoutSeconds, cancellationToken);
			}

			// Проверяет отдельный сеанс для каждого ПК перед обращением к его каталогу.
			private LabComputerAccess AuthorizedAccess(string path)
			{
				string computer = path.Split('\\')[2];
				Check.Equal(true, AuthenticatedComputers.Contains(computer));
				return computers[computer];
			}

			// Перечисляет файлы тестового диска после авторизации.
			public IEnumerable<string> GetFiles(string directoryPath)
			{
				return AuthorizedAccess(directoryPath).GetFiles(directoryPath);
			}

			// Делегирует обход локального тестового диска.
			public IEnumerable<string> GetDirectories(string directoryPath)
			{
				return AuthorizedAccess(directoryPath).GetDirectories(directoryPath);
			}

			// Проверяет признак ссылки через исходный адаптер.
			public bool IsReparsePoint(string path)
			{
				return AuthorizedAccess(path).IsReparsePoint(path);
			}

			// Читает метаданные ссылки только после авторизации на соответствующем тестовом ПК.
			public string GetReparsePointTarget(string path)
			{
				return AuthorizedAccess(path).GetReparsePointTarget(path);
			}

			// Учитывает только действительно прочитанные открытые сертификаты.
			public byte[] ReadAllBytes(string filePath)
			{
				return AuthorizedAccess(filePath).ReadAllBytes(filePath);
			}

			// Моделирует отсутствие КриптоПро после успешной авторизации.
			public string GetProductVersion(string executablePath)
			{
				return AuthorizedAccess(executablePath).GetProductVersion(executablePath);
			}
		}
	}
}
