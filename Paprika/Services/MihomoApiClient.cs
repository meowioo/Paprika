using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Paprika.Models;

namespace Paprika.Services;

public sealed class MihomoApiClient(AppSettingsService settingsService)
{
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        using var response = await httpClient.GetAsync("/version", cancellationToken);

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(content);

        // mihomo 当前返回 {"version":"x.y.z"}；显式解析可以兼容未来新增字段。
        if (json.RootElement.TryGetProperty("version", out var version))
        {
            return version.GetString() ?? "unknown";
        }

        return content;
    }

    public async Task<string?> TryGetVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetVersionAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<MihomoProxySnapshot> GetProxiesAsync(CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        using var response = await httpClient.GetAsync("/proxies", cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!json.RootElement.TryGetProperty("proxies", out var proxiesElement) ||
            proxiesElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("mihomo /proxies 返回内容中没有 proxies 对象。");
        }

        var items = new List<MihomoProxyInfo>();
        var byName = new Dictionary<string, MihomoProxyInfo>(StringComparer.Ordinal);

        foreach (var property in proxiesElement.EnumerateObject())
        {
            var item = ParseProxyInfo(property.Name, property.Value);
            items.Add(item);
            byName[item.Name] = item;
        }

        return new MihomoProxySnapshot(items, byName);
    }

    public async Task SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        var path = $"/proxies/{Uri.EscapeDataString(groupName)}";
        using var content = new StringContent(
            JsonSerializer.Serialize(new { name = proxyName }),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.PutAsync(path, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task CloseAllConnectionsAsync(CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        using var response = await httpClient.DeleteAsync("/connections", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        using var response = await httpClient.DeleteAsync(
            $"/connections/{Uri.EscapeDataString(connectionId)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<MihomoConnectionSnapshot> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        using var response = await httpClient.GetAsync("/connections", cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var connections = json.RootElement.TryGetPropertyIgnoreCase("connections", out var connectionsElement) &&
                          connectionsElement.ValueKind == JsonValueKind.Array
            ? connectionsElement.EnumerateArray().Select(ParseConnectionInfo).ToArray()
            : Array.Empty<MihomoConnectionInfo>();

        return new MihomoConnectionSnapshot(
            ReadLong(json.RootElement, "upload") ?? 0,
            ReadLong(json.RootElement, "download") ?? 0,
            connections);
    }

    public async Task<MihomoTrafficRate> GetTrafficRateAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);
        using var webSocket = new ClientWebSocket();

        if (!string.IsNullOrWhiteSpace(settings.ControllerSecret))
        {
            webSocket.Options.SetRequestHeader("Authorization", $"Bearer {settings.ControllerSecret}");
        }

        var uri = new UriBuilder("ws", settings.ControllerHost, settings.ControllerPort, "/traffic").Uri;
        await webSocket.ConnectAsync(uri, cancellationToken);

        var message = await ReceiveWebSocketTextAsync(webSocket, cancellationToken);
        await CloseWebSocketQuietlyAsync(webSocket, cancellationToken);

        using var json = JsonDocument.Parse(message);
        var upload = ReadLong(json.RootElement, "up", "upload") ?? 0;
        var download = ReadLong(json.RootElement, "down", "download") ?? 0;

        return new MihomoTrafficRate(upload, download);
    }

    public async Task SetRunModeAsync(string mode, CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        using var content = new StringContent(
            JsonSerializer.Serialize(new { mode }),
            Encoding.UTF8,
            "application/json");

        // mihomo 通过 PATCH /configs 修改运行中的配置。
        using var response = await httpClient.PatchAsync("/configs", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<MihomoProxyProviderInfo>> GetProxyProvidersAsync(CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        using var response = await httpClient.GetAsync("/providers/proxies", cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return EnumerateProviderObjects(json.RootElement)
            .Where(property => IsProxyProviderObject(property.Value))
            .Select(property => ParseProxyProviderInfo(property.Name, property.Value))
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<MihomoRuleProviderInfo>> GetRuleProvidersAsync(CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        using var response = await httpClient.GetAsync("/providers/rules", cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return EnumerateProviderObjects(json.RootElement)
            .Where(property => IsRuleProviderObject(property.Value))
            .Select(property => ParseRuleProviderInfo(property.Name, property.Value))
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task UpdateProxyProviderAsync(string providerName, CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        var path = $"/providers/proxies/{Uri.EscapeDataString(providerName)}";
        using var content = new StringContent(string.Empty, Encoding.UTF8);
        using var response = await httpClient.PutAsync(path, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateRuleProviderAsync(string providerName, CancellationToken cancellationToken)
    {
        using var httpClient = await CreateClientAsync(cancellationToken);
        var path = $"/providers/rules/{Uri.EscapeDataString(providerName)}";
        using var content = new StringContent(string.Empty, Encoding.UTF8);
        using var response = await httpClient.PutAsync(path, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> CreateClientAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);
        // API 地址集中在这里生成，避免各调用点重复拼 host/port。
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{settings.ControllerHost}:{settings.ControllerPort}")
        };

        if (!string.IsNullOrWhiteSpace(settings.ControllerSecret))
        {
            // 配置 secret 时，external-controller 需要 Bearer 认证。
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.ControllerSecret);
        }

        return client;
    }

    private static MihomoProxyInfo ParseProxyInfo(string fallbackName, JsonElement element)
    {
        // /proxies 会混合真实节点和策略组；这里只读取 Paprika 需要的公共字段。
        var name = ReadString(element, "name") ?? fallbackName;
        var type = ReadString(element, "type") ?? "-";
        var current = ReadString(element, "now");
        var all = ReadStringArray(element, "all");
        var alive = ReadBool(element, "alive");
        var delay = ReadLastDelay(element);

        return new MihomoProxyInfo(name, type, current, all, alive, delay);
    }

    private static MihomoConnectionInfo ParseConnectionInfo(JsonElement element)
    {
        var metadata = element.TryGetPropertyIgnoreCase("metadata", out var metadataElement) &&
                       metadataElement.ValueKind == JsonValueKind.Object
            ? metadataElement
            : default;

        var source = FormatEndpoint(
            ReadString(metadata, "sourceIP", "source-ip", "srcIP", "src-ip"),
            ReadInt(metadata, "sourcePort", "source-port", "srcPort", "src-port"));
        var destination = FormatDestination(metadata);

        return new MihomoConnectionInfo(
            ReadString(element, "id") ?? "-",
            ReadString(metadata, "network") ?? "-",
            ReadString(metadata, "type") ?? "-",
            source,
            destination,
            ReadString(metadata, "host", "sniffHost", "sniff-host") ?? string.Empty,
            ReadString(metadata, "process", "processName", "process-name") ?? "-",
            ReadString(metadata, "processPath", "process-path") ?? string.Empty,
            ReadString(element, "rule") ?? "-",
            ReadString(element, "rulePayload", "rule-payload") ?? string.Empty,
            ReadStringArray(element, "chains"),
            ReadLong(element, "upload") ?? 0,
            ReadLong(element, "download") ?? 0,
            ReadDateTimeOffset(element, "start", "startedAt", "started-at"));
    }

    private static async Task<string> ReceiveWebSocketTextAsync(
        ClientWebSocket webSocket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("mihomo /traffic WebSocket 已关闭。");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }

    private static async Task CloseWebSocketQuietlyAsync(
        ClientWebSocket webSocket,
        CancellationToken cancellationToken)
    {
        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Paprika", cancellationToken);
            }
        }
        catch
        {
            // 速率读取已经完成，关闭握手失败不影响展示。
        }
    }

    private static string FormatDestination(JsonElement metadata)
    {
        var host = ReadString(metadata, "host", "sniffHost", "sniff-host");
        var ip = ReadString(metadata, "destinationIP", "destination-ip", "dstIP", "dst-ip");
        var port = ReadInt(metadata, "destinationPort", "destination-port", "dstPort", "dst-port");
        var endpoint = FormatEndpoint(ip, port);

        if (!string.IsNullOrWhiteSpace(host))
        {
            return port is null ? host : $"{host}:{port}";
        }

        return endpoint;
    }

    private static string FormatEndpoint(string? host, int? port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return port is null ? "-" : $":{port}";
        }

        return port is null ? host : $"{host}:{port}";
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetPropertyIgnoreCase(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static int? ReadLastDelay(JsonElement element)
    {
        if (!element.TryGetProperty("history", out var history) ||
            history.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int? delay = null;
        foreach (var item in history.EnumerateArray())
        {
            if (item.TryGetProperty("delay", out var delayElement) &&
                delayElement.ValueKind == JsonValueKind.Number &&
                delayElement.TryGetInt32(out var value))
            {
                delay = value;
            }
        }

        return delay;
    }

    private static IEnumerable<JsonProperty> EnumerateProviderObjects(JsonElement root)
    {
        if (root.TryGetPropertyIgnoreCase("providers", out var providers) &&
            providers.ValueKind == JsonValueKind.Object)
        {
            return providers.EnumerateObject().Where(IsObjectProperty).ToArray();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            // 兼容旧 Clash 风格响应：provider 映射可能直接位于根对象。
            return root.EnumerateObject().Where(IsObjectProperty).ToArray();
        }

        throw new InvalidOperationException("mihomo provider API 返回内容格式不正确。");

        static bool IsObjectProperty(JsonProperty property)
        {
            return property.Value.ValueKind == JsonValueKind.Object;
        }
    }

    private static MihomoProxyProviderInfo ParseProxyProviderInfo(string fallbackName, JsonElement element)
    {
        var name = ReadString(element, "name") ?? fallbackName;
        var type = ReadString(element, "type") ?? "-";
        var vehicleType = ReadString(element, "vehicleType", "vehicle-type") ?? "-";
        var proxyCount = ReadArrayLength(element, "proxies", "all") ?? ReadInt(element, "count") ?? 0;
        var updatedAt = ReadDateTimeOffset(element, "updatedAt", "updated-at", "updated");
        var subscriptionInfo = ReadTrafficInfo(element);

        return new MihomoProxyProviderInfo(name, type, vehicleType, proxyCount, updatedAt, subscriptionInfo);
    }

    private static MihomoRuleProviderInfo ParseRuleProviderInfo(string fallbackName, JsonElement element)
    {
        var name = ReadString(element, "name") ?? fallbackName;
        var type = ReadString(element, "type") ?? "-";
        var vehicleType = ReadString(element, "vehicleType", "vehicle-type") ?? "-";
        var behavior = ReadString(element, "behavior") ?? "-";
        var ruleCount = ReadInt(element, "ruleCount", "rule-count", "count")
                        ?? ReadArrayLength(element, "rules", "payload");
        var updatedAt = ReadDateTimeOffset(element, "updatedAt", "updated-at", "updated");

        return new MihomoRuleProviderInfo(name, type, vehicleType, behavior, ruleCount, updatedAt);
    }

    private static bool IsProxyProviderObject(JsonElement element)
    {
        // 节点源只展示代理 provider，避免兼容响应里混入规则源。
        return HasAnyProperty(element, "proxies", "all", "subscriptionInfo", "subscription-info") &&
               !HasAnyProperty(element, "rules", "payload", "behavior");
    }

    private static bool IsRuleProviderObject(JsonElement element)
    {
        // 规则 provider 只出现在“规则源”页面。
        return HasAnyProperty(element, "rules", "payload", "behavior", "ruleCount", "rule-count");
    }

    private static MihomoTrafficInfo? ReadTrafficInfo(JsonElement element)
    {
        if (!element.TryGetPropertyIgnoreCase("subscriptionInfo", out var subscriptionInfo) &&
            !element.TryGetPropertyIgnoreCase("subscription-info", out subscriptionInfo))
        {
            return null;
        }

        if (subscriptionInfo.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // subscriptionInfo 通常对应订阅头里的 upload/download/total/expire。
        var upload = ReadLong(subscriptionInfo, "upload");
        var download = ReadLong(subscriptionInfo, "download");
        var total = ReadLong(subscriptionInfo, "total");
        var expireAt = ReadDateTimeOffset(subscriptionInfo, "expire", "expiresAt", "expires-at");

        return upload is null && download is null && total is null && expireAt is null
            ? null
            : new MihomoTrafficInfo(upload, download, total, expireAt);
    }

    private static string? ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetPropertyIgnoreCase(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetPropertyIgnoreCase(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt32(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String &&
                int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var textNumber))
            {
                return textNumber;
            }
        }

        return null;
    }

    private static long? ReadLong(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetPropertyIgnoreCase(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt64(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String &&
                long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var textNumber))
            {
                return textNumber;
            }
        }

        return null;
    }

    private static int? ReadArrayLength(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetPropertyIgnoreCase(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.Array)
            {
                return property.GetArrayLength();
            }
        }

        return null;
    }

    private static bool HasAnyProperty(JsonElement element, params string[] propertyNames)
    {
        return propertyNames.Any(propertyName => element.TryGetPropertyIgnoreCase(propertyName, out _));
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetPropertyIgnoreCase(propertyName, out var property))
            {
                continue;
            }

            var value = property.ValueKind switch
            {
                JsonValueKind.String => ParseDateTimeOffset(property.GetString()),
                JsonValueKind.Number when property.TryGetInt64(out var number) => ParseUnixTime(number),
                _ => null
            };

            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixTime))
        {
            return ParseUnixTime(unixTime);
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) &&
               parsed.Year >= 2000
            ? parsed
            : null;
    }

    private static DateTimeOffset? ParseUnixTime(long value)
    {
        if (value <= 0)
        {
            return null;
        }

        try
        {
            // 大于 10^12 基本可视为毫秒；订阅 expire 常见为秒级时间戳。
            return value > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

internal static class JsonElementExtensions
{
    public static bool TryGetPropertyIgnoreCase(
        this JsonElement element,
        string propertyName,
        out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }
}
