using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Paprika.Models;

namespace Paprika.Services;

public sealed class MihomoCoreDownloadService(AppPathService paths, AppSettingsService settingsService)
{
    private static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/MetaCubeX/mihomo/releases/latest");

    public async Task<MihomoReleaseAsset> ResolveLatestAssetAsync(CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient();
        using var response = await httpClient.GetAsync(LatestReleaseUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        var version = json.RootElement.GetProperty("tag_name").GetString()
                      ?? throw new InvalidOperationException("GitHub release did not contain tag_name.");

        var assets = json.RootElement.GetProperty("assets").EnumerateArray()
            .Select(ReadAsset)
            .Where(asset => asset is not null)
            .Select(asset => asset!)
            .ToArray();

        return SelectBestAsset(version, assets);
    }

    public async Task<MihomoCoreUpdateInfo> GetUpdateInfoAsync(CancellationToken cancellationToken)
    {
        var latestAsset = await ResolveLatestAssetAsync(cancellationToken);
        var corePath = paths.DefaultCorePath;
        var coreExists = File.Exists(corePath);
        var installedVersion = coreExists
            ? await TryReadInstalledVersionAsync(corePath, cancellationToken)
            : null;

        return new MihomoCoreUpdateInfo(coreExists, corePath, installedVersion, latestAsset);
    }

    public Task<string?> TryGetInstalledVersionAsync(
        string corePath,
        CancellationToken cancellationToken)
    {
        return TryReadInstalledVersionAsync(corePath, cancellationToken);
    }

    public async Task<CoreDownloadResult> DownloadAndInstallAsync(
        MihomoReleaseAsset asset,
        IProgress<long> downloadProgress,
        Action beforeInstall,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();

        var downloadsDirectory = Path.Combine(paths.RuntimeDirectory, "downloads");
        Directory.CreateDirectory(downloadsDirectory);

        var archivePath = Path.Combine(downloadsDirectory, asset.Name);
        var tempCorePath = Path.Combine(downloadsDirectory, $"{Path.GetFileName(paths.DefaultCorePath)}.download");

        try
        {
            await DownloadArchiveAsync(asset, archivePath, downloadProgress, cancellationToken);

            beforeInstall();

            // 先解压到临时文件，最后一步才覆盖正式核心，避免留下写到一半的可执行文件。
            await ExtractArchiveAsync(archivePath, tempCorePath, cancellationToken);
            SetExecutablePermission(tempCorePath);

            File.Move(tempCorePath, paths.DefaultCorePath, overwrite: true);

            await settingsService.UpdateAsync(settings =>
            {
                // 下载的核心始终安装到 Paprika 管理的默认核心目录。
                settings.CorePath = paths.DefaultCorePath;
            }, cancellationToken);

            return new CoreDownloadResult(asset.Version, asset.Name, paths.DefaultCorePath);
        }
        finally
        {
            TryDelete(archivePath);
            TryDelete(tempCorePath);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Paprika", "0.1"));
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return httpClient;
    }

    private static MihomoReleaseAsset? ReadAsset(JsonElement asset)
    {
        var name = asset.GetProperty("name").GetString();
        var url = asset.GetProperty("browser_download_url").GetString();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var size = asset.TryGetProperty("size", out var sizeElement)
            ? sizeElement.GetInt64()
            : 0;

        return new MihomoReleaseAsset(string.Empty, name, url, size);
    }

    private static MihomoReleaseAsset SelectBestAsset(string version, IReadOnlyList<MihomoReleaseAsset> assets)
    {
        var osToken = GetOperatingSystemToken();
        var archToken = GetArchitectureToken();
        var prefix = $"mihomo-{osToken}-{archToken}";

        var matches = assets
            .Where(asset =>
                asset.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (asset.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
                 asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(asset => ScoreAsset(asset.Name, version))
            .ToArray();

        if (matches.Length == 0)
        {
            throw new NotSupportedException(
                $"No mihomo release asset matched this platform: {osToken}/{archToken}.");
        }

        var selected = matches[0];
        return selected with { Version = version };
    }

    private static int ScoreAsset(string name, string version)
    {
        var score = 0;

        // 多个资产匹配时优先选择当前版本的普通构建，避开 compatible 或特殊 Go 版本。
        if (name.Contains(version, StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (!name.Contains("compatible", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (!name.Contains("-go", StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        if (!name.Contains("-v1-", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("-v2-", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("-v3-", StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        return score;
    }

    private static string GetOperatingSystemToken()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "darwin";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        throw new NotSupportedException("Current operating system is not supported by the downloader.");
    }

    private static string GetArchitectureToken()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.X86 => "386",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "armv7",
            _ => throw new NotSupportedException(
                $"Current architecture is not supported: {RuntimeInformation.ProcessArchitecture}.")
        };
    }

    private static async Task DownloadArchiveAsync(
        MihomoReleaseAsset asset,
        string archivePath,
        IProgress<long> downloadProgress,
        CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient();
        using var response = await httpClient.GetAsync(
            asset.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(archivePath);

        var buffer = new byte[1024 * 128];
        long downloaded = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            downloadProgress.Report(downloaded);
        }
    }

    private static async Task<string?> TryReadInstalledVersionAsync(
        string corePath,
        CancellationToken cancellationToken)
    {
        var output = await TryReadVersionOutputAsync(corePath, "-v", cancellationToken);
        if (!string.IsNullOrWhiteSpace(output))
        {
            return ExtractVersion(output);
        }

        output = await TryReadVersionOutputAsync(corePath, "--version", cancellationToken);
        return string.IsNullOrWhiteSpace(output) ? null : ExtractVersion(output);
    }

    private static async Task<string?> TryReadVersionOutputAsync(
        string corePath,
        string argument,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = corePath,
                    Arguments = argument,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return string.Join(Environment.NewLine, stdout, stderr).Trim();
        }
        catch
        {
            // 版本识别尽力而为；识别失败时 UI 仍可提供覆盖更新。
            return null;
        }
    }

    private static string? ExtractVersion(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(output, @"v?\d+(?:\.\d+)+");
        return match.Success ? match.Value : null;
    }

    private static async Task ExtractArchiveAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (archivePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            await using var archiveStream = File.OpenRead(archivePath);
            await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
            await using var destination = File.Create(destinationPath);
            await gzipStream.CopyToAsync(destination, cancellationToken);
            return;
        }

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.Entries.FirstOrDefault(IsLikelyCoreEntry)
                        ?? throw new InvalidOperationException("Downloaded zip did not contain a mihomo binary.");

            await using var entryStream = entry.Open();
            await using var destination = File.Create(destinationPath);
            await entryStream.CopyToAsync(destination, cancellationToken);
            return;
        }

        throw new InvalidOperationException("Unsupported mihomo archive format.");
    }

    private static bool IsLikelyCoreEntry(ZipArchiveEntry entry)
    {
        if (entry.Length == 0)
        {
            return false;
        }

        var fileName = Path.GetFileName(entry.FullName);
        if (OperatingSystem.IsWindows())
        {
            return fileName.Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        return fileName.Equals("mihomo", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("mihomo-", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetExecutablePermission(string corePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            corePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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
            // 临时文件清理失败不应掩盖成功安装结果。
        }
    }
}
