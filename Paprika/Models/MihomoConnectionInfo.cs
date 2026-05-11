namespace Paprika.Models;

public sealed record MihomoConnectionSnapshot(
    long UploadBytes,
    long DownloadBytes,
    IReadOnlyList<MihomoConnectionInfo> Connections);

public sealed record MihomoConnectionInfo(
    string Id,
    string Network,
    string Type,
    string Source,
    string Destination,
    string Host,
    string Process,
    string ProcessPath,
    string Rule,
    string RulePayload,
    IReadOnlyList<string> Chains,
    long UploadBytes,
    long DownloadBytes,
    DateTimeOffset? StartedAt);
