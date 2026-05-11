namespace Paprika.Models;

public sealed class ExternalResourceSettings
{
    public Dictionary<string, string> Urls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ExternalResourceInfo(
    string Id,
    string Name,
    string FileName,
    string FilePath,
    string Url,
    long? SizeBytes,
    DateTimeOffset? UpdatedAt);
