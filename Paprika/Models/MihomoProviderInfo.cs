namespace Paprika.Models;

public sealed record MihomoTrafficInfo(
    long? UploadBytes,
    long? DownloadBytes,
    long? TotalBytes,
    DateTimeOffset? ExpireAt)
{
    public long? UsedBytes => UploadBytes is null && DownloadBytes is null
        ? null
        : Math.Max(0, (UploadBytes ?? 0) + (DownloadBytes ?? 0));

    public long? RemainingBytes => TotalBytes is null
        ? null
        : Math.Max(0, TotalBytes.Value - (UsedBytes ?? 0));
}

public sealed record MihomoProxyProviderInfo(
    string Name,
    string Type,
    string VehicleType,
    int ProxyCount,
    DateTimeOffset? UpdatedAt,
    MihomoTrafficInfo? SubscriptionInfo);

public sealed record MihomoRuleProviderInfo(
    string Name,
    string Type,
    string VehicleType,
    string Behavior,
    int? RuleCount,
    DateTimeOffset? UpdatedAt);
