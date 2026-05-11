using System.Net.Http.Headers;
using System.Text;
using Paprika.Models;
using YamlDotNet.RepresentationModel;

namespace Paprika.Services;

public sealed class ProfileService(AppPathService paths, AppSettingsService settingsService)
{
    private static readonly string[] SupportedExtensions = [".yaml", ".yml"];

    public async Task<ProfileInfo> ImportAsync(string sourcePath, string? name, CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("Profile file was not found.", fullSourcePath);
        }

        var extension = Path.GetExtension(fullSourcePath);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only .yaml and .yml profile files are supported.");
        }

        var profileName = NormalizeProfileName(name ?? Path.GetFileNameWithoutExtension(fullSourcePath));
        var yamlText = await File.ReadAllTextAsync(fullSourcePath, Encoding.UTF8, cancellationToken);
        var proxyCount = ValidateAndCountProxies(yamlText);
        var destinationPath = await SaveProfileTextAsync(profileName, yamlText, cancellationToken);

        await SaveProfileSourceAsync(
            profileName,
            new ProfileSourceSettings
            {
                Type = ProfileSourceTypes.Local,
                UpdatedAt = DateTimeOffset.Now,
                ProxyCount = proxyCount
            },
            cancellationToken);

        return BuildProfileInfo(profileName, destinationPath, await settingsService.LoadAsync(cancellationToken));
    }

    public async Task<ProfileInfo> ImportSubscriptionAsync(
        string url,
        string? name,
        CancellationToken cancellationToken)
    {
        var uri = ValidateSubscriptionUrl(url);
        var profileName = NormalizeProfileName(name ?? SuggestSubscriptionName(uri.ToString()));
        var downloaded = await DownloadSubscriptionAsync(uri, cancellationToken);
        var proxyCount = ValidateAndCountProxies(downloaded.YamlText);
        var destinationPath = await SaveProfileTextAsync(profileName, downloaded.YamlText, cancellationToken);

        await SaveProfileSourceAsync(
            profileName,
            new ProfileSourceSettings
            {
                Type = ProfileSourceTypes.Subscription,
                Url = uri.ToString(),
                UpdatedAt = DateTimeOffset.Now,
                ProxyCount = proxyCount,
                SubscriptionInfo = downloaded.SubscriptionInfo
            },
            cancellationToken);

        return BuildProfileInfo(profileName, destinationPath, await settingsService.LoadAsync(cancellationToken));
    }

    public async Task<ProfileInfo> UpdateSubscriptionAsync(string name, CancellationToken cancellationToken)
    {
        var profile = await GetAsync(name, cancellationToken);
        if (!profile.IsSubscription || string.IsNullOrWhiteSpace(profile.SubscriptionUrl))
        {
            throw new InvalidOperationException("该配置不是订阅链接导入的配置，无法自动更新。");
        }

        var uri = ValidateSubscriptionUrl(profile.SubscriptionUrl);
        var downloaded = await DownloadSubscriptionAsync(uri, cancellationToken);
        var proxyCount = ValidateAndCountProxies(downloaded.YamlText);
        var destinationPath = await SaveProfileTextAsync(profile.Name, downloaded.YamlText, cancellationToken);

        await SaveProfileSourceAsync(
            profile.Name,
            new ProfileSourceSettings
            {
                Type = ProfileSourceTypes.Subscription,
                Url = uri.ToString(),
                UpdatedAt = DateTimeOffset.Now,
                ProxyCount = proxyCount,
                SubscriptionInfo = downloaded.SubscriptionInfo
            },
            cancellationToken);

        return BuildProfileInfo(profile.Name, destinationPath, await settingsService.LoadAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<ProfileInfo>> ListAsync(CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        cancellationToken.ThrowIfCancellationRequested();

        var settings = await settingsService.LoadAsync(cancellationToken);
        var profiles = Directory.EnumerateFiles(paths.ProfilesDirectory, "*.y*ml")
            .Select(path => BuildProfileInfo(Path.GetFileNameWithoutExtension(path), path, settings))
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return profiles;
    }

    public async Task<ProfileInfo> GetAsync(string name, CancellationToken cancellationToken)
    {
        var profileName = NormalizeProfileName(name);
        var profilePath = GetProfilePath(profileName);
        if (!File.Exists(profilePath))
        {
            throw new FileNotFoundException("Profile does not exist.", profilePath);
        }

        return BuildProfileInfo(profileName, profilePath, await settingsService.LoadAsync(cancellationToken));
    }

    public async Task UseAsync(string name, CancellationToken cancellationToken)
    {
        var profileName = NormalizeProfileName(name);
        var profilePath = GetProfilePath(profileName);

        // 当前配置必须真实存在，否则核心启动时无法生成 runtime.yaml。
        if (!File.Exists(profilePath))
        {
            throw new FileNotFoundException("Profile does not exist.", profilePath);
        }

        await settingsService.UpdateAsync(settings =>
        {
            settings.CurrentProfile = profileName;
        }, cancellationToken);
    }

    public async Task<string> GetCurrentProfilePathAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.LoadAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(settings.CurrentProfile))
        {
            throw new InvalidOperationException("No active profile. Import one with `paprika profile import <path> --use`.");
        }

        var profilePath = GetProfilePath(settings.CurrentProfile);
        if (!File.Exists(profilePath))
        {
            throw new FileNotFoundException("Active profile file is missing.", profilePath);
        }

        return profilePath;
    }

    public static string SuggestSubscriptionName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host[4..]
                : uri.Host;
            var candidate = Path.GetInvalidFileNameChars()
                .Aggregate(host, (current, invalid) => current.Replace(invalid, '-'));
            return candidate.Length == 0 ? "subscription" : candidate;
        }

        return "subscription";
    }

    private async Task<string> SaveProfileTextAsync(
        string profileName,
        string yamlText,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();

        var destinationPath = GetProfilePath(profileName);
        var temporaryPath = Path.Combine(paths.RuntimeDirectory, $"{profileName}.profile.download");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                yamlText,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            // 先写临时文件，下载或校验失败时不会破坏原有可用配置。
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task SaveProfileSourceAsync(
        string profileName,
        ProfileSourceSettings source,
        CancellationToken cancellationToken)
    {
        await settingsService.UpdateAsync(settings =>
        {
            settings.ProfileSources[profileName] = source;
        }, cancellationToken);
    }

    private ProfileInfo BuildProfileInfo(string name, string path, AppSettings settings)
    {
        settings.ProfileSources.TryGetValue(name, out var source);
        source ??= new ProfileSourceSettings
        {
            Type = ProfileSourceTypes.Local,
            UpdatedAt = File.GetLastWriteTimeUtc(path)
        };

        return new ProfileInfo(name, path, File.GetLastWriteTimeUtc(path))
        {
            SourceType = string.IsNullOrWhiteSpace(source.Type) ? ProfileSourceTypes.Local : source.Type,
            SubscriptionUrl = source.Url,
            SourceUpdatedAt = source.UpdatedAt,
            ProxyCount = source.ProxyCount,
            SubscriptionInfo = source.SubscriptionInfo
        };
    }

    private string GetProfilePath(string name)
    {
        return Path.Combine(paths.ProfilesDirectory, $"{NormalizeProfileName(name)}.yaml");
    }

    private static Uri ValidateSubscriptionUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("订阅链接必须是 http 或 https URL。");
        }

        return uri;
    }

    private static async Task<DownloadedSubscription> DownloadSubscriptionAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient();
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var yamlText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            throw new InvalidOperationException("订阅返回内容为空。");
        }

        return new DownloadedSubscription(
            yamlText,
            ReadSubscriptionInfo(response.Headers, response.Content.Headers));
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        // 部分订阅服务会根据 Clash/mihomo 风格 User-Agent 返回对应 YAML。
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Clash.Meta");
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Paprika", "0.1"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return httpClient;
    }

    private static MihomoTrafficInfo? ReadSubscriptionInfo(
        HttpResponseHeaders responseHeaders,
        HttpContentHeaders contentHeaders)
    {
        var value = TryGetHeader(responseHeaders, "subscription-userinfo")
                    ?? TryGetHeader(contentHeaders, "subscription-userinfo");

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var values = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        var upload = ReadLong(values, "upload");
        var download = ReadLong(values, "download");
        var total = ReadLong(values, "total");
        var expireAt = ReadUnixTime(values, "expire");

        return upload is null && download is null && total is null && expireAt is null
            ? null
            : new MihomoTrafficInfo(upload, download, total, expireAt);
    }

    private static string? TryGetHeader(HttpHeaders headers, string name)
    {
        return headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;
    }

    private static long? ReadLong(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) &&
               long.TryParse(value, out var number)
            ? number
            : null;
    }

    private static DateTimeOffset? ReadUnixTime(IReadOnlyDictionary<string, string> values, string key)
    {
        var value = ReadLong(values, key);
        if (value is null or <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int? ValidateAndCountProxies(string yamlText)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yamlText));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"配置内容不是有效 YAML：{ex.Message}", ex);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("配置文件根节点必须是 YAML 对象。");
        }

        var proxies = FindKey(root, "proxies") is { } proxiesKey
            ? root.Children[proxiesKey]
            : null;

        return proxies is YamlSequenceNode sequence ? sequence.Children.Count : null;
    }

    private static YamlNode? FindKey(YamlMappingNode root, string key)
    {
        foreach (var childKey in root.Children.Keys)
        {
            if (childKey is YamlScalarNode scalar && scalar.Value == key)
            {
                return childKey;
            }
        }

        return null;
    }

    private static string NormalizeProfileName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("Profile name cannot be empty.");
        }

        // 配置名会作为文件名使用，因此拒绝路径分隔符和非法字符。
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("Profile name contains invalid file name characters.");
        }

        return trimmed;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败不应影响导入/更新结果。
        }
    }

    private sealed record DownloadedSubscription(string YamlText, MihomoTrafficInfo? SubscriptionInfo);
}
