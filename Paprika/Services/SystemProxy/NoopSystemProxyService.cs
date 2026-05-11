using Paprika.Models;

namespace Paprika.Services.SystemProxy;

public sealed class NoopSystemProxyService : ISystemProxyService
{
    public Task EnableAsync(CancellationToken cancellationToken)
    {
        throw new PlatformNotSupportedException("当前平台暂不支持自动设置系统代理。");
    }

    public Task DisableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<SystemProxyStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SystemProxyStatus(
            IsSupported: false,
            IsEnabled: false,
            IsManagedByPaprika: false,
            ProxyServer: null,
            Message: "当前平台暂不支持自动设置系统代理。"));
    }
}
