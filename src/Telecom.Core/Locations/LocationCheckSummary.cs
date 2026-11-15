namespace Telecom.Core.Locations
{
	// Итог проверки наличия: сколько мест проверено, сколько итогов сохранено и была ли отмена.
	public sealed class LocationCheckSummary
	{
		public int Checked { get; internal set; }
		public int Saved { get; internal set; }
		public bool Cancelled { get; internal set; }
	}
}
