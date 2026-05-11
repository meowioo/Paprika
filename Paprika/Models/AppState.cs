namespace Paprika.Models;

public sealed class AppState
{
    // PID 必须和 CorePath 一起校验，避免系统复用旧 PID 时误杀其他进程。
    public int? CoreProcessId { get; set; }

    public string? CorePath { get; set; }

    public DateTimeOffset? CoreStartedAt { get; set; }

    // 记录本次启动使用的 runtime.yaml，便于排查核心启动问题。
    public string? RuntimeConfigPath { get; set; }

    // 保存接管系统代理前的原始值，应用重开后也能恢复用户原设置。
    public bool SystemProxyManaged { get; set; }

    public int? OriginalProxyEnable { get; set; }

    public string? OriginalProxyServer { get; set; }

    public string? OriginalProxyOverride { get; set; }
}
