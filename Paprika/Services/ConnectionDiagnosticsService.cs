using Paprika.Models;

namespace Paprika.Services;

public sealed class ConnectionDiagnosticsService(
    MihomoCoreManager coreManager,
    MihomoApiClient apiClient)
{
    public async Task<MihomoConnectionSnapshot> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        await EnsureCoreReadyAsync(cancellationToken);
        return await apiClient.GetConnectionsAsync(cancellationToken);
    }

    public async Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        await EnsureCoreReadyAsync(cancellationToken);
        await apiClient.CloseConnectionAsync(connectionId, cancellationToken);
    }

    public async Task CloseAllConnectionsAsync(CancellationToken cancellationToken)
    {
        await EnsureCoreReadyAsync(cancellationToken);
        await apiClient.CloseAllConnectionsAsync(cancellationToken);
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
            throw new InvalidOperationException("mihomo external-controller 当前不可用，暂时无法读取连接列表。");
        }
    }
}
