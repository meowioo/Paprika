namespace Paprika.Models;

public sealed record CoreDownloadResult(
    string Version,
    string AssetName,
    string CorePath);

public sealed record MihomoReleaseAsset(
    string Version,
    string Name,
    string DownloadUrl,
    long Size);

public sealed record MihomoCoreUpdateInfo(
    bool CoreExists,
    string CorePath,
    string? InstalledVersion,
    MihomoReleaseAsset LatestAsset)
{
    public bool IsLatest => CoreExists &&
                            !string.IsNullOrWhiteSpace(InstalledVersion) &&
                            MihomoVersionComparer.AreEquivalent(InstalledVersion, LatestAsset.Version);

    public bool IsInstalledVersionUnknown => CoreExists && string.IsNullOrWhiteSpace(InstalledVersion);
}

public static class MihomoVersionComparer
{
    public static bool AreEquivalent(string left, string right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);

        if (Version.TryParse(normalizedLeft, out var leftVersion) &&
            Version.TryParse(normalizedRight, out var rightVersion))
        {
            return leftVersion == rightVersion;
        }

        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\d+(?:\.\d+)+");
        return match.Success ? match.Value : trimmed;
    }
}
