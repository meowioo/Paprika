namespace Paprika.Models;

public sealed record MihomoProxyInfo(
    string Name,
    string Type,
    string? Current,
    IReadOnlyList<string> All,
    bool? Alive,
    int? DelayMs);

public sealed record MihomoProxySnapshot(
    IReadOnlyList<MihomoProxyInfo> Items,
    IReadOnlyDictionary<string, MihomoProxyInfo> ByName);

public sealed record MihomoProxyGroupInfo(
    string Name,
    string Type,
    string Current,
    IReadOnlyList<MihomoProxyNodeInfo> Nodes);

public sealed record MihomoProxyNodeInfo(
    string Name,
    string Type,
    bool IsCurrent,
    bool? Alive,
    int? DelayMs);
