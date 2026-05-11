namespace Paprika.Services;

public sealed class AppPathService
{
    public AppPathService()
    {
        AppDataDirectory = ResolveAppDataDirectory();
        SettingsPath = Path.Combine(AppDataDirectory, "appsettings.json");
        StatePath = Path.Combine(AppDataDirectory, "state.json");
        CoresDirectory = Path.Combine(AppDataDirectory, "cores");
        ProfilesDirectory = Path.Combine(AppDataDirectory, "profiles");
        RuntimeDirectory = Path.Combine(AppDataDirectory, "runtime");
        LogsDirectory = Path.Combine(AppDataDirectory, "logs");
    }

    public string AppDataDirectory { get; }

    public string SettingsPath { get; }

    public string StatePath { get; }

    public string CoresDirectory { get; }

    public string ProfilesDirectory { get; }

    public string RuntimeDirectory { get; }

    public string LogsDirectory { get; }

    public string DefaultCorePath => Path.Combine(CoresDirectory, GetCoreFileName());

    public string RuntimeConfigPath => Path.Combine(RuntimeDirectory, "runtime.yaml");

    public string CoreLogPath => Path.Combine(LogsDirectory, "mihomo.log");

    public string AppLogPath => Path.Combine(LogsDirectory, "paprika.log");

    public void EnsureDirectories()
    {
        // 提前创建完整数据目录，后续服务写设置、配置、运行时文件和日志时不用重复判断。
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(CoresDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static string ResolveAppDataDirectory()
    {
        // PAPRIKA_HOME 让测试和本地冒烟运行可以隔离真实 AppData。
        var overridePath = Environment.GetEnvironmentVariable("PAPRIKA_HOME");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows 桌面/命令行工具通常把用户状态放在 %APPDATA%。
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(roaming, "Paprika");
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Paprika");
        }

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            return Path.Combine(xdgConfigHome, "paprika");
        }

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".config", "paprika");
    }

    private static string GetCoreFileName()
    {
        // Windows 使用 .exe 后缀，macOS/Linux 通常是无后缀的 mihomo。
        return OperatingSystem.IsWindows() ? "mihomo.exe" : "mihomo";
    }
}
