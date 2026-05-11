namespace Paprika.Models;

public static class ProfileSourceTypes
{
    public const string Local = "local";
    public const string Subscription = "subscription";
}

public sealed class ProfileSourceSettings
{
    public string Type { get; set; } = ProfileSourceTypes.Local;

    public string? Url { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public int? ProxyCount { get; set; }

    public MihomoTrafficInfo? SubscriptionInfo { get; set; }
}

public sealed record ProfileInfo(string Name, string Path, DateTimeOffset UpdatedAt)
{
    public string SourceType { get; init; } = ProfileSourceTypes.Local;

    public string? SubscriptionUrl { get; init; }

    public DateTimeOffset? SourceUpdatedAt { get; init; }

    public int? ProxyCount { get; init; }

    public MihomoTrafficInfo? SubscriptionInfo { get; init; }

    public bool IsSubscription =>
        string.Equals(SourceType, ProfileSourceTypes.Subscription, StringComparison.OrdinalIgnoreCase);
}
