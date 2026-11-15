using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Telecom.Core.Common
{
	// Узнаёт, указывает ли IPv4 на этот же компьютер: 127.x.x.x или адрес одной из его сетевых карт.
	public static class LocalAddresses
	{
		// Возвращает true для адресов этого ПК; если список сетевых карт получить не удалось, проверяет только 127.x.x.x.
		public static bool IsThisComputer(string ipAddress)
		{
			IPAddress address;
			if (!IPAddress.TryParse(ipAddress, out address) || address.AddressFamily != AddressFamily.InterNetwork)
			{
				return false;
			}

			if (IPAddress.IsLoopback(address))
			{
				return true;
			}

			try
			{
				return NetworkInterface.GetAllNetworkInterfaces()
					.SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
					.Any(unicast => unicast.Address.Equals(address));
			}
			catch (NetworkInformationException)
			{
				return false;
			}
		}
	}
}
