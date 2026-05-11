using System.Globalization;
using System.Text;
using Paprika.Models;
using YamlDotNet.RepresentationModel;

namespace Paprika.Services;

public sealed class RuntimeConfigService(
    AppPathService paths,
    ProfileService profileService,
    AppLogService appLog)
{
    public async Task<string> GenerateAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();

        var profilePath = await profileService.GetCurrentProfilePathAsync(cancellationToken);
        await using var source = File.OpenRead(profilePath);
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var yamlText = await reader.ReadToEndAsync(cancellationToken);

        var stream = new YamlStream();
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            // 空配置按空 YAML 映射处理，方便早期调试时仍能写入运行时默认值。
            stream.Documents.Add(new YamlDocument(new YamlMappingNode()));
        }
        else
        {
            stream.Load(new StringReader(yamlText));
        }

        var document = stream.Documents[0];

        if (document.RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("mihomo profile root must be a YAML mapping.");
        }

        // 这些键由 Paprika 接管，每次启动都强制写入，保证菜单和 API 端口稳定。
        RemoveScalar(root, "port");
        RemoveScalar(root, "socks-port");
        RemoveScalar(root, "redir-port");
        RemoveScalar(root, "tproxy-port");
        UpsertScalar(root, "mixed-port", settings.MixedPort.ToString(CultureInfo.InvariantCulture));
        UpsertScalar(root, "allow-lan", settings.AllowLan ? "true" : "false");
        UpsertScalar(root, "external-controller", $"{settings.ControllerHost}:{settings.ControllerPort}");
        UpsertScalar(root, "secret", settings.ControllerSecret);
        ApplyDnsAndTun(root, settings);

        // 运行模式由主菜单控制，不让导入配置覆盖用户在 Paprika 里的选择。
        UpsertScalar(root, "mode", settings.RunMode);

        // log-level 只在缺失时补默认值，尊重用户配置中的日志级别。
        UpsertScalarIfMissing(root, "log-level", "info");

        await appLog.InfoAsync(
            $"生成 runtime.yaml：proxyMode={settings.ProxyMode}, tunEnabled={IsTunProxyMode(settings)}, tunStack={settings.Tun.Stack}, dnsHijack={settings.Tun.DnsHijack}, sniffer={IsTunProxyMode(settings)}, strictRoute={settings.Tun.StrictRoute}, profile={settings.CurrentProfile ?? "-"}, path={paths.RuntimeConfigPath}",
            cancellationToken);

        await using var destination = File.Create(paths.RuntimeConfigPath);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        stream.Save(writer, assignAnchors: false);

        return paths.RuntimeConfigPath;
    }

    private static void UpsertScalar(YamlMappingNode root, string key, string value)
    {
        // 使用 YAML 节点级别替换，尽量保留用户原配置的结构。
        var existingKey = FindKey(root, key);
        var scalar = new YamlScalarNode(value);

        if (existingKey is null)
        {
            root.Children.Add(new YamlScalarNode(key), scalar);
            return;
        }

        root.Children[existingKey] = scalar;
    }

    private static void UpsertScalarIfMissing(YamlMappingNode root, string key, string value)
    {
        if (FindKey(root, key) is null)
        {
            root.Children.Add(new YamlScalarNode(key), new YamlScalarNode(value));
        }
    }

    private static void RemoveScalar(YamlMappingNode root, string key)
    {
        var existingKey = FindKey(root, key);
        if (existingKey is not null)
        {
            // 当前只暴露 mixed-port，移除旧入站端口可以避免重复绑定。
            root.Children.Remove(existingKey);
        }
    }

    private static void ApplyDnsAndTun(YamlMappingNode root, AppSettings settings)
    {
        if (!IsTunProxyMode(settings))
        {
            DisableLocalDnsListener(root);
            DisableTun(root);
            return;
        }

        ApplyRuntimeDns(root);
        ApplyRuntimeTun(root, settings.Tun);
        ApplyRuntimeSniffer(root);
    }

    private static void ApplyRuntimeDns(YamlMappingNode root)
    {
        var dns = GetOrCreateMapping(root, "dns");

        // TUN 模式必须让 mihomo 接管 DNS，否则域名解析可能绕过规则匹配。
        UpsertScalar(dns, "enable", "true");
        UpsertScalar(dns, "listen", "0.0.0.0:1053");
        UpsertScalar(dns, "ipv6", "false");
        UpsertScalar(dns, "enhanced-mode", "fake-ip");
        UpsertScalar(dns, "fake-ip-range", "198.18.0.1/16");
        UpsertSequenceIfMissing(dns, "fake-ip-filter",
        [
            "*.lan",
            "localhost.ptlogin2.qq.com"
        ]);
        UpsertSequenceIfMissing(dns, "default-nameserver",
        [
            "223.5.5.5",
            "119.29.29.29"
        ]);
        UpsertSequenceIfMissing(dns, "nameserver",
        [
            "https://doh.pub/dns-query",
            "https://dns.alidns.com/dns-query"
        ]);
        UpsertSequenceIfMissing(dns, "proxy-server-nameserver",
        [
            "https://doh.pub/dns-query",
            "https://dns.alidns.com/dns-query"
        ]);
    }

    private static void ApplyRuntimeTun(YamlMappingNode root, TunSettings settings)
    {
        var tun = GetOrCreateMapping(root, "tun");

        UpsertScalar(tun, "enable", "true");
        UpsertScalar(tun, "stack", NormalizeTunStack(settings.Stack));
        UpsertScalar(tun, "device", string.IsNullOrWhiteSpace(settings.Device) ? "Paprika" : settings.Device.Trim());
        UpsertScalar(tun, "auto-route", settings.AutoRoute ? "true" : "false");
        UpsertScalar(tun, "auto-detect-interface", settings.AutoDetectInterface ? "true" : "false");
        UpsertScalar(tun, "strict-route", settings.StrictRoute ? "true" : "false");
        UpsertScalar(tun, "mtu", settings.Mtu.ToString(CultureInfo.InvariantCulture));
        UpsertSequence(tun, "dns-hijack", settings.DnsHijack
            ? ["any:53", "tcp://any:53"]
            : []);
        UpsertSequence(tun, "route-exclude-address", settings.BypassLan
            ? settings.RouteExcludeAddress
            : []);
    }

    private static void ApplyRuntimeSniffer(YamlMappingNode root)
    {
        var sniffer = GetOrCreateMapping(root, "sniffer");

        // TUN 透明代理经常只能看到目标 IP。开启域名嗅探后，mihomo 可以
        // 从 HTTP Host、TLS SNI、QUIC 握手中还原域名，避免 Google 等域名
        // 规则因为缺少域名而落到 IP 规则或兜底规则。
        UpsertScalar(sniffer, "enable", "true");
        UpsertScalar(sniffer, "force-dns-mapping", "true");
        UpsertScalar(sniffer, "parse-pure-ip", "true");
        UpsertScalar(sniffer, "override-destination", "true");

        var sniff = GetOrCreateMapping(sniffer, "sniff");
        var http = GetOrCreateMapping(sniff, "HTTP");
        UpsertSequence(http, "ports", ["80", "8080-8880"]);
        UpsertScalar(http, "override-destination", "true");

        var tls = GetOrCreateMapping(sniff, "TLS");
        UpsertSequence(tls, "ports", ["443", "8443"]);

        var quic = GetOrCreateMapping(sniff, "QUIC");
        UpsertSequence(quic, "ports", ["443", "8443"]);

        UpsertSequenceIfMissing(sniffer, "skip-domain",
        [
            "Mijia Cloud",
            "+.push.apple.com"
        ]);
    }

    private static void DisableLocalDnsListener(YamlMappingNode root)
    {
        var dnsKey = FindKey(root, "dns");
        if (dnsKey is null || root.Children[dnsKey] is not YamlMappingNode dns)
        {
            return;
        }

        // 系统代理模式暂不接管本地 DNS；很多配置会绑定 0.0.0.0:53，容易和系统服务冲突。
        UpsertScalar(dns, "enable", "false");
    }

    private static void DisableTun(YamlMappingNode root)
    {
        var tunKey = FindKey(root, "tun");
        if (tunKey is null || root.Children[tunKey] is not YamlMappingNode tun)
        {
            return;
        }

        // 系统代理模式下显式关闭用户配置里可能自带的 TUN，避免接管方式混乱。
        UpsertScalar(tun, "enable", "false");
    }

    private static YamlMappingNode GetOrCreateMapping(YamlMappingNode root, string key)
    {
        var existingKey = FindKey(root, key);
        if (existingKey is not null && root.Children[existingKey] is YamlMappingNode existingMapping)
        {
            return existingMapping;
        }

        var mapping = new YamlMappingNode();
        if (existingKey is null)
        {
            root.Children.Add(new YamlScalarNode(key), mapping);
        }
        else
        {
            root.Children[existingKey] = mapping;
        }

        return mapping;
    }

    private static void UpsertSequence(YamlMappingNode root, string key, IEnumerable<string> values)
    {
        var existingKey = FindKey(root, key);
        var sequence = new YamlSequenceNode(values.Select(value => new YamlScalarNode(value)));

        if (existingKey is null)
        {
            root.Children.Add(new YamlScalarNode(key), sequence);
            return;
        }

        root.Children[existingKey] = sequence;
    }

    private static void UpsertSequenceIfMissing(YamlMappingNode root, string key, IEnumerable<string> values)
    {
        if (FindKey(root, key) is null)
        {
            UpsertSequence(root, key, values);
        }
    }

    private static bool IsTunProxyMode(AppSettings settings)
    {
        return string.Equals(settings.ProxyMode, "tun", StringComparison.OrdinalIgnoreCase)
               && settings.Tun.Enabled;
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

    private static YamlNode? FindKey(YamlMappingNode root, string key)
    {
        // 按标量 key 查找，避免把 YAML 节点序列化成字符串再比较。
        foreach (var childKey in root.Children.Keys)
        {
            if (childKey is YamlScalarNode scalar && scalar.Value == key)
            {
                return childKey;
            }
        }

        return null;
    }
}
