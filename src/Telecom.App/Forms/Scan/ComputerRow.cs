using Telecom.Core.Computers;

namespace Telecom.App.Forms.Scan
{
	// Строка таблицы «Компьютеры»: сохранённый профиль и статусы последнего запуска, которые на диск не пишутся.
	internal sealed class ComputerRow
	{
		public ComputerProfile Profile { get; private set; }
		public bool Selected { get { return Profile.Selected; } set { Profile.Selected = value; } }
		public bool SavedSelection { get; set; }
		public string IpAddress { get { return Profile.IsLocal ? "—" : Profile.IpAddress; } }
		public string DisplayName { get { return Profile.DisplayName; } }
		public string Description { get { return Profile.Description; } }
		public string Account { get { return Profile.IsLocal ? "Текущий пользователь" : Profile.HasUserName ? Profile.UserName : "Не задана"; } }
		public string ConnectionStatus { get; set; }
		public string ScanStatus { get; set; }
		public string CryptoProVersion { get; set; }
		public string DatabaseStatus { get; set; }

		// Отделяет отображаемые статусы от метаданных, которые сохраняются на диск.
		public ComputerRow(ComputerProfile profile)
		{
			Profile = profile;
			SavedSelection = profile.Selected;
			ConnectionStatus = profile.IsLocal ? "Локально" : "Не подключено";
		}
	}
}
