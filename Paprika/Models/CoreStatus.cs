namespace Paprika.Models;

public sealed record CoreStatus(
    int? ProcessId,
    bool IsProcessRunning,
    bool IsApiAvailable,
    string? Version,
    string? Message)
{
    // 只有进程存在且 API 可用，才视为核心真正可用。
    public bool IsRunning => IsProcessRunning && IsApiAvailable;
}
