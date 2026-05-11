using System.Text.Json;
using Paprika.Models;

namespace Paprika.Services;

public sealed class AppSettingsService(AppPathService paths)
{
    private static readonly IReadOnlyDictionary<string, string> DefaultExternalResourceUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["geoip"] = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip.dat",
            ["geosite"] = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geosite.dat",
            ["mmdb"] = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip.metadb",
            ["asn"] = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/GeoLite2-ASN.mmdb"
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();

        if (!File.Exists(paths.SettingsPath))
        {
            // 读取设置时不主动建文件，避免多个只读入口同时启动时抢写。
            return ApplyDefaults(new AppSettings());
        }

        await using var stream = File.OpenRead(paths.SettingsPath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                       ?? new AppSettings();

        return ApplyDefaults(settings);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();

        // 当前阶段只有前台菜单写设置，直接覆盖即可；以后有后台任务再引入写锁。
        await using var stream = File.Create(paths.SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }

    public async Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken);
        update(settings);
        await SaveAsync(settings, cancellationToken);
    }

    private AppSettings ApplyDefaults(AppSettings settings)
    {
        // Paprika 只管理一个默认核心目录；旧设置里的自定义 CorePath 会被忽略。
        settings.CorePath = paths.DefaultCorePath;
        settings.ProfileSources = new Dictionary<string, ProfileSourceSettings>(
            settings.ProfileSources ?? new Dictionary<string, ProfileSourceSettings>(),
            StringComparer.OrdinalIgnoreCase);
        settings.ProxyMode = NormalizeProxyMode(settings.ProxyMode);
        settings.RunMode = NormalizeRunMode(settings.RunMode);
        settings.Tun ??= new TunSettings();
        settings.Tun.Enabled = settings.ProxyMode == "tun";
        settings.Tun.Stack = NormalizeTunStack(settings.Tun.Stack);
        settings.Tun.Device = string.IsNullOrWhiteSpace(settings.Tun.Device)
            ? "Paprika"
            : settings.Tun.Device.Trim();
        settings.Tun.Mtu = settings.Tun.Mtu is >= 576 and <= 9000 ? settings.Tun.Mtu : 1500;
        settings.Tun.RouteExcludeAddress ??= new TunSettings().RouteExcludeAddress.ToList();
        settings.ExternalResources ??= new ExternalResourceSettings();
        settings.ExternalResources.Urls ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 新增内置资源时补齐默认值，但不覆盖用户编辑过的地址。
        foreach (var (id, url) in DefaultExternalResourceUrls)
        {
            if (!settings.ExternalResources.Urls.ContainsKey(id))
            {
                settings.ExternalResources.Urls[id] = url;
            }
        }

        if (settings.SystemProxyExcludedDomains is null)
        {
            settings.SystemProxyExcludedDomains = new AppSettings().SystemProxyExcludedDomains.ToList();
        }

        return settings;
    }

    private static string NormalizeProxyMode(string? proxyMode)
    {
        return proxyMode?.Trim().ToLowerInvariant() switch
        {
            "tun" => "tun",
            _ => "system-proxy"
        };
    }

    private static string NormalizeRunMode(string? runMode)
    {
        return runMode?.Trim().ToLowerInvariant() switch
        {
            "global" => "global",
            "direct" => "direct",
            _ => "rule"
        };
    }

    private static string NormalizeTunStack(string? stack)
    {
        return stack?.Trim().ToLowerInvariant() switch
        {
            "system" => "system",
            "mixed" => "mixed",
            _ => "gvisor"
        };
    }
}
