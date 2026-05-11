using Paprika.Models;

namespace Paprika.Services.SystemProxy;

public interface ISystemProxyService
{
    Task EnableAsync(CancellationToken cancellationToken);

    Task DisableAsync(CancellationToken cancellationToken);

    Task<SystemProxyStatus> GetStatusAsync(CancellationToken cancellationToken);
}
