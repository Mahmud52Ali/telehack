using System.Threading;
using Telecom.Core.Computers;

namespace Telecom.Core.Access
{
	public interface IComputerConnector
	{
		// Устанавливает сетевой сеанс с удалённым ПК под учётной записью его профиля.
		void Connect(ComputerProfile profile, int timeoutSeconds, CancellationToken cancellationToken);
	}
}
