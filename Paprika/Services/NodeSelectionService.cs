using Paprika.Models;

namespace Paprika.Services;

public sealed class NodeSelectionService(
    MihomoCoreManager coreManager,
    MihomoApiClient apiClient)
{
    public async Task<IReadOnlyList<MihomoProxyGroupInfo>> GetSelectableGroupsAsync(CancellationToken cancellationToken)
    {
        await EnsureCoreReadyAsync(cancellationToken);

        var snapshot = await apiClient.GetProxiesAsync(cancellationToken);
        var groups = new List<MihomoProxyGroupInfo>();

        foreach (var item in snapshot.Items)
        {
            if (!IsSelectableGroup(item))
            {
                continue;
            }

            groups.Add(BuildGroup(item, snapshot.ByName));
        }

        return groups;
    }

    public async Task SelectNodeAsync(
        string groupName,
        string nodeName,
        CancellationToken cancellationToken)
    {
        await EnsureCoreReadyAsync(cancellationToken);
        await apiClient.SelectProxyAsync(groupName, nodeName, cancellationToken);
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
            throw new InvalidOperationException("mihomo external-controller 当前不可用，暂时无法读取或切换节点。");
        }
    }

    private static bool IsSelectableGroup(MihomoProxyInfo item)
    {
        // 可选择的策略组会通过 all 暴露候选节点，并通过 now 暴露当前选择。
        return item.All.Count > 0 && !string.IsNullOrWhiteSpace(item.Current);
    }

    private static MihomoProxyGroupInfo BuildGroup(
        MihomoProxyInfo group,
        IReadOnlyDictionary<string, MihomoProxyInfo> proxiesByName)
    {
        var nodes = group.All
            .Select(name => BuildNode(name, group.Current, proxiesByName))
            .ToArray();

        return new MihomoProxyGroupInfo(group.Name, group.Type, group.Current!, nodes);
    }

    private static MihomoProxyNodeInfo BuildNode(
        string name,
        string? current,
        IReadOnlyDictionary<string, MihomoProxyInfo> proxiesByName)
    {
        proxiesByName.TryGetValue(name, out var proxy);

        return new MihomoProxyNodeInfo(
            name,
            proxy?.Type ?? "-",
            string.Equals(name, current, StringComparison.Ordinal),
            proxy?.Alive,
            proxy?.DelayMs);
    }
}
