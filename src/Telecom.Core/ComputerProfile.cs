using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Telecom.Core
{
	public sealed class ComputerProfile
	{
		public Guid Id { get; private set; }
		public string IpAddress { get; private set; }
		public string DisplayName { get; private set; }
		public string UserName { get; private set; }
		public IList<string> AdditionalDirectories { get; private set; }
		public bool Selected { get; set; }
		public bool HasIpAddress { get { return IpAddress.Length > 0; } }
		public bool HasUserName { get { return UserName.Length > 0; } }

		// Создаёт профиль ПК, использующий общие пути из XML.
		public ComputerProfile(Guid id, string ipAddress, string displayName, string userName, bool selected)
			: this(id, ipAddress, displayName, userName, selected, null)
		{
		}

		// Создаёт профиль с дополнительными путями для удалённого сканирования.
		public ComputerProfile(Guid id, string ipAddress, string displayName, string userName, bool selected,
			IEnumerable<string> additionalDirectories)
			: this(id, ipAddress, displayName, userName, selected, additionalDirectories, false)
		{
		}

		// Пустой адрес допускается только при загрузке записи, которой ещё нужно назначить IPv4.
		private ComputerProfile(Guid id, string ipAddress, string displayName, string userName, bool selected,
			IEnumerable<string> additionalDirectories, bool allowMissingAddress)
		{
			if (id == Guid.Empty)
			{
				throw new ArgumentException("У компьютера должен быть непустой идентификатор.", "id");
			}

			Id = id;
			IpAddress = allowMissingAddress && ipAddress == string.Empty ? string.Empty : NormalizeIpAddress(ipAddress);
			DisplayName = (displayName ?? string.Empty).Trim();
			if (DisplayName.Length > 256 || DisplayName.Any(char.IsControl))
			{
				throw new ArgumentException("Название должно содержать не более 256 символов без переносов строк.");
			}

			UserName = (userName ?? string.Empty).Trim();
			if (UserName.Length > 513 || UserName.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
			{
				throw new ArgumentException("Недопустимое имя учётной записи.");
			}

			AdditionalDirectories = RemotePath.NormalizeLocalPaths((additionalDirectories ?? Enumerable.Empty<string>())
				.Where(path => !string.IsNullOrWhiteSpace(path)), true);
			Selected = selected;
		}

		// Принимает только полный десятичный IPv4; сокращения, IPv6, имена и адреса с портом запрещены.
		public static string NormalizeIpAddress(string value)
		{
			string address = (value ?? string.Empty).Trim();
			if (!IsCanonicalIpv4(address))
			{
				throw new ArgumentException("Укажите IPv4 в формате 192.168.56.101: четыре числа от 0 до 255 без ведущих нулей.");
			}

			return address;
		}

		// Загружает профиль с IPv4 и подписью, сохраняя запись без адреса до его заполнения администратором.
		internal static ComputerProfile FromStored(Guid id, string ipAddress, string displayName, string userName, bool selected,
			IEnumerable<string> additionalDirectories)
		{
			return new ComputerProfile(id, ipAddress, displayName, userName, selected, additionalDirectories, true);
		}

		// Старое сетевое имя становится подписью; автоматическое разрешение имён и смена сохранённого секрета не выполняются.
		internal static ComputerProfile FromLegacy(Guid id, string address, string userName, bool selected)
		{
			string previousAddress = RemotePath.NormalizeComputerName(address);
			bool isIpv4 = IsCanonicalIpv4(previousAddress);
			return new ComputerProfile(id, isIpv4 ? previousAddress : string.Empty,
				isIpv4 ? string.Empty : previousAddress, userName, selected, null, true);
		}

		// Сравнение с каноническим представлением исключает неоднозначные формы, принимаемые IPAddress.TryParse.
		private static bool IsCanonicalIpv4(string value)
		{
			IPAddress address;
			return IPAddress.TryParse(value, out address) && address.AddressFamily == AddressFamily.InterNetwork
				&& address.ToString() == value;
		}
	}
}
