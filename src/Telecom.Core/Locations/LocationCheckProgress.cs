namespace Telecom.Core.Locations
{
	// Ход проверки наличия: какой ПК проверяется и его номер в очереди.
	public sealed class LocationCheckProgress
	{
		public string ComputerAddress { get; private set; }
		public int Index { get; private set; }
		public int Count { get; private set; }

		// Запоминает номер ПК (с единицы) и общее число ПК.
		public LocationCheckProgress(string computerAddress, int index, int count)
		{
			ComputerAddress = computerAddress;
			Index = index;
			Count = count;
		}
	}
}
