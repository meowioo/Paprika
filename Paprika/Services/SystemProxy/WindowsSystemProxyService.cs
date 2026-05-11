using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Paprika.Models;

namespace Paprika.Services.SystemProxy;

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemProxyService(
    AppSettingsService settingsService,
    AppStateService stateService) : ISystemProxyService
{
    private const string InternetSettingsPath =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;

    public async Task EnableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = await settingsService.LoadAsync(cancellationToken);
        var state = await stateService.LoadAsync(cancellationToken);
        using var key = OpenInternetSettingsKey();

        if (!state.SystemProxyManaged)
        {
            // 首次接管前保存用户原始代理状态，退出时按原样恢复。
            state.SystemProxyManaged = true;
            state.OriginalProxyEnable = ReadProxyEnable(key);
            state.OriginalProxyServer = ReadStringValue(key, "ProxyServer");
            state.OriginalProxyOverride = ReadStringValue(key, "ProxyOverride");
        }

        var proxyServer = $"127.0.0.1:{settings.MixedPort}";
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
        key.SetValue(
            "ProxyOverride",
            BuildProxyOverride(settings.SystemProxyExcludedDomains),
            RegistryValueKind.String);

        await stateService.SaveAsync(state, cancellationToken);
        await settingsService.UpdateAsync(value =>
        {
            value.SystemProxyEnabled = true;
        }, cancellationToken);

        RefreshInternetSettings();
    }

    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = await stateService.LoadAsync(cancellationToken);
        using var key = OpenInternetSettingsKey();

        if (state.SystemProxyManaged)
        {
            // 恢复 Paprika 接管前的原始值。
            key.SetValue("ProxyEnable", state.OriginalProxyEnable ?? 0, RegistryValueKind.DWord);
            RestoreStringValue(key, "ProxyServer", state.OriginalProxyServer);
            RestoreStringValue(key, "ProxyOverride", state.OriginalProxyOverride);
        }
        else
        {
            // 没有备份时保守关闭系统代理，避免继续指向本地端口。
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        }

        await stateService.UpdateAsync(value =>
        {
            value.SystemProxyManaged = false;
            value.OriginalProxyEnable = null;
            value.OriginalProxyServer = null;
            value.OriginalProxyOverride = null;
        }, cancellationToken);

        await settingsService.UpdateAsync(value =>
        {
            value.SystemProxyEnabled = false;
        }, cancellationToken);

        RefreshInternetSettings();
    }

    public async Task<SystemProxyStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = await stateService.LoadAsync(cancellationToken);
        using var key = OpenInternetSettingsKey();
        var enabled = ReadProxyEnable(key) != 0;
        var server = ReadStringValue(key, "ProxyServer");

        return new SystemProxyStatus(
            IsSupported: true,
            IsEnabled: enabled,
            IsManagedByPaprika: state.SystemProxyManaged,
            ProxyServer: server,
            Message: enabled ? "系统代理已开启。" : "系统代理已关闭。");
    }

    private static RegistryKey OpenInternetSettingsKey()
    {
        return Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
               ?? Registry.CurrentUser.CreateSubKey(InternetSettingsPath, writable: true)
               ?? throw new InvalidOperationException("无法打开 Windows Internet Settings 注册表项。");
    }

    private static int ReadProxyEnable(RegistryKey key)
    {
        var value = key.GetValue("ProxyEnable");
        return value is null ? 0 : Convert.ToInt32(value);
    }

    private static string? ReadStringValue(RegistryKey key, string name)
    {
        return key.GetValue(name) as string;
    }

    private static void RestoreStringValue(RegistryKey key, string name, string? value)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        key.SetValue(name, value, RegistryValueKind.String);
    }

    private static string BuildProxyOverride(IEnumerable<string> excludedDomains)
    {
        // Windows 用分号分隔 ProxyOverride；这里保留顺序并去掉空项和重复项。
        return string.Join(
            ';',
            excludedDomains
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static void RefreshInternetSettings()
    {
        // 通知 WinINet 客户端重新读取 HKCU 代理设置。
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);
}
