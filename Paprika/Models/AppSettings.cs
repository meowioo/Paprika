namespace Paprika.Models;

public sealed class AppSettings
{
    // 当前使用的配置名，对应 profiles/<name>.yaml。
    public string? CurrentProfile { get; set; }

    // 每个配置的来源信息：本地导入或订阅链接导入。
    public Dictionary<string, ProfileSourceSettings> ProfileSources { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // 保留给旧设置文件兼容；运行时会被归一到 Paprika 管理的默认核心路径。
    public string CorePath { get; set; } = string.Empty;

    // Paprika 通过 mihomo external-controller 调用 REST API。
    public string ControllerHost { get; set; } = "127.0.0.1";

    public int ControllerPort { get; set; } = 9090;

    public string ControllerSecret { get; set; } = string.Empty;

    public int MixedPort { get; set; } = 7890;

    // 代理接管方式：system-proxy 或 tun。
    public string ProxyMode { get; set; } = "system-proxy";

    // mihomo 运行模式：rule/global/direct。
    public string RunMode { get; set; } = "rule";

    // 这些值会在每次启动核心前写入 runtime.yaml。
    public bool AllowLan { get; set; }

    public bool SystemProxyEnabled { get; set; }

    // 切换节点后是否自动清理已有连接，让新流量尽快走新节点。
    public bool AutoCloseConnectionsOnNodeSwitch { get; set; } = true;

    // 进入后台运行前是否显示确认说明。
    public bool ShowRunInBackgroundPrompt { get; set; } = true;

    // TUN 模式相关设置；是否实际启用由 ProxyMode 决定。
    public TunSettings Tun { get; set; } = new();

    // mihomo 外部资源下载地址，文件保存在 Paprika 数据目录。
    public ExternalResourceSettings ExternalResources { get; set; } = new();

    // Windows 系统代理的 ProxyOverride 排除规则。
    public List<string> SystemProxyExcludedDomains { get; set; } =
    [
        "localhost",
        "*.local",
        "127.*",
        "10.*",
        "172.16.*",
        "172.17.*",
        "172.18.*",
        "172.19.*",
        "172.2*",
        "172.30.*",
        "172.31.*",
        "192.168.*",
        "*jd.com"
    ];
}

public sealed class TunSettings
{
    // 与 ProxyMode 保持同步，写 runtime.yaml 时用于生成 tun.enable。
    public bool Enabled { get; set; }

    // mihomo TUN 协议栈：mixed / system / gvisor。
    public string Stack { get; set; } = "gvisor";

    // 保留用户是否手动改过协议栈的历史状态，兼容已有设置文件。
    public bool StackCustomized { get; set; }

    public string Device { get; set; } = "Paprika";

    public bool AutoRoute { get; set; } = true;

    public bool AutoDetectInterface { get; set; } = true;

    public bool DnsHijack { get; set; } = true;

    public bool StrictRoute { get; set; }

    public bool BypassLan { get; set; } = true;

    public int Mtu { get; set; } = 1500;

    public List<string> RouteExcludeAddress { get; set; } =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "169.254.0.0/16",
        "fc00::/7",
        "fe80::/10"
    ];
}
