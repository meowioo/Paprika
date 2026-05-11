namespace Paprika.Models;

public sealed record SystemProxyStatus(
    bool IsSupported,
    bool IsEnabled,
    bool IsManagedByPaprika,
    string? ProxyServer,
    string Message);
