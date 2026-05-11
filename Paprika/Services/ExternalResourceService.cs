using System.Net.Http.Headers;
using Paprika.Models;

namespace Paprika.Services;

public sealed class ExternalResourceService(
    AppPathService paths,
    AppSettingsService settingsService)
{
    private static readonly ExternalResourceDefinition[] Definitions =
    [
        new("geoip", "GEOIP", "geoip.dat"),
        new("geosite", "GEOSITE", "geosite.dat"),
        new("mmdb", "MMDB", "geoip.metadb"),
        new("asn", "ASN", "GeoLite2-ASN.mmdb")
    ];

    public async Task<IReadOnlyList<ExternalResourceInfo>> ListAsync(CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        var settings = await settingsService.LoadAsync(cancellationToken);

        return Definitions
            .Select(definition => BuildInfo(definition, settings))
            .ToArray();
    }

    public async Task<ExternalResourceInfo> GetAsync(string id, CancellationToken cancellationToken)
    {
        var resources = await ListAsync(cancellationToken);
        return resources.FirstOrDefault(resource => string.Equals(resource.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"未知外部资源：{id}");
    }

    public async Task UpdateUrlAsync(string id, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("资源地址必须是 http 或 https URL。");
        }

        EnsureKnownResource(id);
        await settingsService.UpdateAsync(settings =>
        {
            settings.ExternalResources ??= new ExternalResourceSettings();
            settings.ExternalResources.Urls ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings.ExternalResources.Urls[id] = uri.ToString();
        }, cancellationToken);
    }

    public async Task DownloadAsync(
        string id,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        var resource = await GetAsync(id, cancellationToken);
        var downloadsDirectory = Path.Combine(paths.RuntimeDirectory, "downloads");
        Directory.CreateDirectory(downloadsDirectory);

        var temporaryPath = Path.Combine(downloadsDirectory, $"{resource.FileName}.download");
        try
        {
            await DownloadToFileAsync(resource.Url, temporaryPath, progress, cancellationToken);

            // 下载完成后才替换正式资源，避免失败同步留下截断文件。
            File.Move(temporaryPath, resource.FilePath, overwrite: true);
            progress.Report(100);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private ExternalResourceInfo BuildInfo(
        ExternalResourceDefinition definition,
        AppSettings settings)
    {
        var filePath = Path.Combine(paths.AppDataDirectory, definition.FileName);
        var fileInfo = new FileInfo(filePath);
        settings.ExternalResources.Urls.TryGetValue(definition.Id, out var url);

        return new ExternalResourceInfo(
            definition.Id,
            definition.Name,
            definition.FileName,
            filePath,
            url ?? string.Empty,
            fileInfo.Exists ? fileInfo.Length : null,
            fileInfo.Exists ? fileInfo.LastWriteTime : null);
    }

    private static async Task DownloadToFileAsync(
        string url,
        string path,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient();
        using var response = await httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(path);

        var buffer = new byte[1024 * 128];
        long downloadedBytes = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloadedBytes += read;

            if (totalBytes is > 0)
            {
                progress.Report(Math.Min(99, (double)downloadedBytes / totalBytes.Value * 100));
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Paprika", "0.1"));
        return httpClient;
    }

    private static void EnsureKnownResource(string id)
    {
        if (!Definitions.Any(definition => string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"未知外部资源：{id}");
        }
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
            // 临时文件清理失败不应掩盖原始下载结果。
        }
    }

    private sealed record ExternalResourceDefinition(string Id, string Name, string FileName);
}
