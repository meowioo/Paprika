using Paprika.Models;

namespace Paprika.Services;

public sealed class ConfigResourceService(
    MihomoCoreManager coreManager,
    MihomoApiClient apiClient)
{
    public async Task<IReadOnlyList<MihomoProxyProviderInfo>> GetProxyProvidersAsync(
        CancellationToken cancellationToken)
    {
        await EnsureCoreReadyAsync(cancellationToken);
        return await apiClient.GetProxyProvidersAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MihomoRuleProviderInfo>> GetRuleProvidersAsync(
        CancellationToken cancellationToken)
    {
        await EnsureCoreReadyAsync(cancellationToken);
        return await apiClient.GetRuleProvidersAsync(cancellationToken);
    }

    public async Task UpdateProxyProviderAsync(string providerName, CancellationToken cancellationToken)
    {
        await EnsureCoreReadyAsync(cancellationToken);
        await apiClient.UpdateProxyProviderAsync(providerName, cancellationToken);
    }

    public async Task UpdateRuleProviderAsync(string providerName, CancellationToken cancellationToken)
    {
        await EnsureCoreReadyAsync(cancellationToken);
        await apiClient.UpdateRuleProviderAsync(providerName, cancellationToken);
    }

    private async Task EnsureCoreReadyAsync(CancellationToken cancellationToken)
    {
        var status = await coreManager.GetStatusAsync(cancellationToken);
        if (!status.IsProcessRunning)
        {
            throw new InvalidOperationException("mihomo 尚未运行。请先在主菜单选择「启动/关闭代理」启动代理。");
        }

        if (!status.IsApiAvailable)
        {
            throw new InvalidOperationException("mihomo external-controller 当前不可用，暂时无法读取或更新配置资源。");
        }
    }
}
