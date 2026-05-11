using System.Text;
using System.Globalization;
using System.Net;
using System.Diagnostics;
using Paprika.Models;
using Paprika.Services;
using Spectre.Console;
using Spectre.Console.Rendering;
using YamlDotNet.RepresentationModel;

namespace Paprika.Ui;

internal sealed class InteractiveApp
{
    private readonly PaprikaServices _services;
    private readonly ShutdownCleanupService _shutdownCleanup;

    private InteractiveApp(PaprikaServices services)
    {
        _services = services;
        _shutdownCleanup = new ShutdownCleanupService(services);
    }

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var app = new InteractiveApp(PaprikaServices.Create());
        await app._services.AppLog.InfoAsync("Paprika 前台启动。", cancellationToken);

        if (!IsInteractiveConsole())
        {
            // 非交互宿主无法处理方向键菜单，输出状态摘要后直接返回。
            await app.ShowNonInteractiveSummaryAsync(cancellationToken);
            return 0;
        }

        using var shutdownHooks = new ConsoleShutdownHooks(app.CleanupOnShutdownAsync);
        await app.RunMainLoopAsync(cancellationToken);
        return 0;
    }

    private async Task RunMainLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);
            var settings = await _services.SettingsService.LoadAsync(cancellationToken);

            var stay = MenuItem.Stay();
            var selected = PromptMenu("选择要执行的操作", stay, BuildMainMenuItems(settings).ToArray());

            if (selected.ShouldStay)
            {
                continue;
            }

            if (selected.ShouldExit)
            {
                TryClearScreen();
                await RenderHeaderAsync(cancellationToken);

                try
                {
                    await ExitApplicationAsync(cancellationToken);
                    return;
                }
                catch (Exception ex)
                {
                    // 清理失败时保留界面，让用户能读到错误并重新尝试退出。
                    await _services.AppLog.ErrorAsync("退出前清理失败。", ex, cancellationToken);
                    AnsiConsole.MarkupLine($"[red]退出前清理失败：[/]{Markup.Escape(ex.Message)}");
                    PauseForResult();
                }

                continue;
            }

            if (selected.ShouldBackground)
            {
                TryClearScreen();
                await RenderHeaderAsync(cancellationToken);
                var shouldPauseBeforeExit =
                    (await _services.SettingsService.LoadAsync(cancellationToken)).ShowRunInBackgroundPrompt;

                try
                {
                    await selected.RunAsync(cancellationToken);
                }
                catch (MenuBackException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    await _services.AppLog.ErrorAsync("后台运行前处理失败。", ex, cancellationToken);
                    AnsiConsole.MarkupLine($"[red]操作失败：[/]{Markup.Escape(ex.Message)}");
                    PauseForResult();
                    continue;
                }

                if (shouldPauseBeforeExit)
                {
                    PauseBeforeForegroundExit();
                }

                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private async Task CoreMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var back = MenuItem.Back("↩️ 返回主菜单");
            var selected = PromptMenu(
                "核心管理",
                back,
                new MenuItem("🔄 重启 mihomo", RestartCoreAsync),
                new MenuItem("📊 查看核心状态", ShowCoreStatusAsync),
                new MenuItem("🏷️ 查看 mihomo 版本", ShowCoreVersionAsync),
                new MenuItem("⬇️ 下载/更新核心", DownloadCoreAsync),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private List<MenuItem> BuildMainMenuItems(AppSettings settings)
    {
        var runModeText = FormatRunModeText(settings.RunMode);
        var proxyModeText = FormatProxyModeText(settings.ProxyMode);
        return
        [
            new MenuItem("🚀 启动/关闭代理", ToggleProxyAsync),
            new MenuItem(
                $"🧩 接管方式 [{proxyModeText}]",
                ConfigureProxyModeAsync,
                MarkupLabel: $"🧩 接管方式 [[{FormatProxyModeMarkup(settings.ProxyMode)}]]"),
            new MenuItem(
                $"🎛️ 运行模式 [{runModeText}]",
                ConfigureRunModeAsync,
                MarkupLabel: $"🎛️ 运行模式 [[{FormatRunModeMarkup(settings.RunMode)}]]"),
            MenuItem.SubMenu("🧰 核心管理", CoreMenuAsync),
            MenuItem.SubMenu("📄 配置管理", ProfileMenuAsync),
            MenuItem.SubMenu("🧭 节点选择", NodeMenuAsync),
            MenuItem.SubMenu("🔎 连接诊断", ConnectionDiagnosticsMenuAsync),
            new MenuItem("📈 实时网络速率", ShowTrafficRateAsync),
            MenuItem.SubMenu("🌐 系统代理", SystemProxyMenuAsync),
            MenuItem.SubMenu("📜 日志", LogsMenuAsync),
            MenuItem.SubMenu("⚙️ 应用设置", SettingsMenuAsync),
            new MenuItem("🪟 后台运行", RunInBackgroundAsync, ShouldBackground: true),
            MenuItem.Exit("🚪 退出")
        ];
    }

    private async Task<MihomoTrafficRate> ReadTrafficRateForHeaderAsync(CancellationToken cancellationToken)
    {
        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        if (!status.IsRunning)
        {
            return MihomoTrafficRate.Unavailable("已停止");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            return await _services.ApiClient.GetTrafficRateAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MihomoTrafficRate.Unavailable("核心未运行或 /traffic 接口不可用");
        }
    }

    private async Task ProfileMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var back = MenuItem.Back("↩️ 返回主菜单");
            var selected = PromptMenu(
                "配置管理",
                back,
                new MenuItem("📥 导入本地配置", ImportProfileAsync),
                new MenuItem("🔗 导入订阅链接", ImportSubscriptionProfileAsync),
                new MenuItem("🔄 更新当前订阅配置", UpdateCurrentSubscriptionProfileAsync),
                new MenuItem("♻️ 更新全部订阅配置", UpdateAllSubscriptionProfilesAsync),
                new MenuItem("🔀 切换当前配置", SwitchProfileAsync),
                MenuItem.SubMenu("📦 当前配置资源", CurrentConfigResourcesMenuAsync),
                new MenuItem("📋 查看配置列表", ShowProfilesAsync),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private async Task LogsMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var back = MenuItem.Back("↩️ 返回主菜单");
            var selected = PromptMenu(
                "日志",
                back,
                new MenuItem("📜 查看最新 100 条 mihomo 日志", ShowRecentLogsAsync),
                new MenuItem("🔴 实时查看 mihomo 日志", TailLogsAsync),
                new MenuItem("🧾 查看最新 100 条 Paprika 日志", ShowRecentAppLogsAsync),
                new MenuItem("🟢 实时查看 Paprika 日志", TailAppLogsAsync),
                new MenuItem("📁 查看日志文件路径", ShowLogPathAsync),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private async Task ConnectionDiagnosticsMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var back = MenuItem.Back("↩️ 返回主菜单");
            var selected = PromptMenu(
                "连接诊断",
                back,
                new MenuItem("🔴 实时查看连接", TailConnectionsAsync),
                new MenuItem("🔍 搜索连接", SearchConnectionsAsync),
                new MenuItem("✂️ 关闭单个连接", CloseSingleConnectionAsync),
                new MenuItem("🧹 清理全部连接", CloseAllDiagnosticConnectionsAsync),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private async Task NodeMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var back = MenuItem.Back("↩️ 返回主菜单");
            var settings = await _services.SettingsService.LoadAsync(cancellationToken);
            var autoCloseConnections = settings.AutoCloseConnectionsOnNodeSwitch ? "开启" : "关闭";
            var autoCloseConnectionsMarkup = settings.AutoCloseConnectionsOnNodeSwitch
                ? "[lime]开启[/]"
                : "[red]关闭[/]";
            var selected = PromptMenu(
                "节点选择",
                back,
                new MenuItem("🧭 选择策略组节点", SelectNodeAsync),
                new MenuItem("📌 查看当前选择", ShowNodeSelectionsAsync),
                new MenuItem(
                    $"🔁 自动关闭连接(切换节点后自动关闭连接) [{autoCloseConnections}]",
                    ConfigureAutoCloseConnectionsAsync,
                    MarkupLabel: $"🔁 自动关闭连接(切换节点后自动关闭连接) [[{autoCloseConnectionsMarkup}]]"),
                new MenuItem("🧹 清理当前连接", CloseConnectionsAsync),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private async Task SystemProxyMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var back = MenuItem.Back("↩️ 返回主菜单");
            var settings = await _services.SettingsService.LoadAsync(cancellationToken);
            if (IsTunProxyMode(settings))
            {
                AnsiConsole.MarkupLine("[yellow]当前接管方式为 TUN 模式，一般不需要再开启系统代理。[/]");
                AnsiConsole.WriteLine();
            }

            var selected = PromptMenu(
                "系统代理",
                back,
                new MenuItem("🔁 开启/关闭系统代理", ToggleSystemProxyAsync),
                new MenuItem("✅ 开启系统代理", EnableSystemProxyAsync),
                new MenuItem("⛔ 关闭系统代理", DisableSystemProxyAsync),
                new MenuItem("📊 查看系统代理状态", ShowSystemProxyStatusAsync),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private async Task SettingsMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var back = MenuItem.Back("↩️ 返回主菜单");
            var settings = await _services.SettingsService.LoadAsync(cancellationToken);
            var selected = PromptMenu(
                "应用设置",
                back,
                new MenuItem("📋 查看当前设置", ShowSettingsAsync),
                new MenuItem("🔌 设置 mixed-port", SetMixedPortAsync),
                new MenuItem("🎛️ 设置 external-controller 端口", SetControllerPortAsync),
                MenuItem.SubMenu("🌉 TUN 设置", TunSettingsMenuAsync),
                new MenuItem(
                    $"🪟 开启/关闭后台提示 [{(settings.ShowRunInBackgroundPrompt ? "开启" : "关闭")}]",
                    ConfigureRunInBackgroundPromptAsync,
                    MarkupLabel: $"🪟 开启/关闭后台提示 [[{(settings.ShowRunInBackgroundPrompt ? "[lime]开启[/]" : "[red]关闭[/]")}]]"),
                MenuItem.SubMenu("🌐 外部资源", ExternalResourcesMenuAsync),
                MenuItem.SubMenu("🚫 系统代理排除域名", SystemProxyExcludedDomainsMenuAsync),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private async Task ToggleProxyAsync(CancellationToken cancellationToken)
    {
        var coreStatus = await _services.CoreManager.GetStatusAsync(cancellationToken);
        var proxyStatus = await _services.SystemProxyService.GetStatusAsync(cancellationToken);
        if (coreStatus.IsProcessRunning || proxyStatus.IsManagedByPaprika)
        {
            await StopProxyAsync(cancellationToken);
            return;
        }

        await StartProxyAsync(cancellationToken);
    }

    private async Task RunInBackgroundAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        if (!settings.ShowRunInBackgroundPrompt)
        {
            return;
        }

        var panel = new Panel(new Rows(
                new Markup("[yellow]程序会关闭，但会保留代理和核心运行。[/]"),
                new Markup("之后需要管理代理时，请重新打开 Paprika。"),
                new Markup("若需要不显示提示并直接进入后台，请到 [green]⚙️ 应用设置 -> 开启/关闭后台提示[/] 里关闭。")))
            .Header("后台运行")
            .RoundedBorder()
            .Expand();

        AnsiConsole.Write(panel);

        if (!AskYesNo("确认进入后台运行？", defaultValue: true))
        {
            throw new MenuBackException();
        }
    }

    private async Task ConfigureProxyModeAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var cancel = new ProxyModeOption("__paprika_cancel__", "返回");
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<ProxyModeOption>()
                .Title($"选择代理接管方式 [grey](当前：{Markup.Escape(FormatProxyModeText(settings.ProxyMode))}，Esc 返回上一层)[/]")
                .PageSize(4)
                .WrapAround()
                .AddCancelResult(cancel)
                .UseConverter(option => FormatProxyModeChoice(option, settings.ProxyMode))
                .AddChoices(
                    new ProxyModeOption("system-proxy", "系统代理"),
                    new ProxyModeOption("tun", "TUN 模式")));

        if (ReferenceEquals(selected, cancel))
        {
            throw new MenuBackException();
        }

        if (string.Equals(settings.ProxyMode, selected.Mode, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[yellow]接管方式已经是 {Markup.Escape(selected.DisplayName)}。[/]");
            return;
        }

        await _services.SettingsService.UpdateAsync(value =>
        {
            value.ProxyMode = selected.Mode;
            value.Tun.Enabled = selected.Mode == "tun";
        }, cancellationToken);
        await _services.AppLog.InfoAsync(
            $"切换接管方式：from={settings.ProxyMode}, to={selected.Mode}",
            cancellationToken);

        AnsiConsole.MarkupLine($"[green]接管方式已切换为：[/]{Markup.Escape(selected.DisplayName)}");

        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        if (!status.IsProcessRunning)
        {
            AnsiConsole.MarkupLine("[grey]当前核心未运行，下次启动代理时生效。[/]");
            return;
        }

        if (!AskYesNo("核心正在运行，是否立即重启 mihomo 使接管方式生效？", defaultValue: true))
        {
            AnsiConsole.MarkupLine("[yellow]已保存设置，重启代理后生效。[/]");
            return;
        }

        await RestartCoreForCurrentProxyModeAsync(cancellationToken);
    }

    private async Task ConfigureRunModeAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var cancel = new RunModeOption("__paprika_cancel__", "返回");
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<RunModeOption>()
                .Title($"选择运行模式 [grey](当前：{Markup.Escape(FormatRunModeText(settings.RunMode))}，Esc 返回上一层)[/]")
                .PageSize(5)
                .WrapAround()
                .AddCancelResult(cancel)
                .UseConverter(option => FormatRunModeChoice(option, settings.RunMode))
                .AddChoices(
                    new RunModeOption("rule", "规则分流"),
                    new RunModeOption("global", "全局代理"),
                    new RunModeOption("direct", "全局直连")));

        if (ReferenceEquals(selected, cancel))
        {
            throw new MenuBackException();
        }

        if (string.Equals(settings.RunMode, selected.Mode, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[yellow]运行模式已经是 {Markup.Escape(selected.DisplayName)}。[/]");
            return;
        }

        await _services.SettingsService.UpdateAsync(value =>
        {
            // 每次启动核心都会把该值写入 runtime.yaml；若核心已运行，则继续 PATCH /configs。
            value.RunMode = selected.Mode;
        }, cancellationToken);

        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        if (status.IsApiAvailable)
        {
            await _services.ApiClient.SetRunModeAsync(selected.Mode, cancellationToken);
            Exception? closeConnectionsError = null;
            try
            {
                // 模式切换只影响新的路由判断，旧连接需要清理后才会立刻生效。
                await _services.ApiClient.CloseAllConnectionsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                closeConnectionsError = ex;
            }

            if (closeConnectionsError is null)
            {
                AnsiConsole.MarkupLine($"[green]运行模式已切换为：[/]{Markup.Escape(selected.DisplayName)}，现有连接已清理。");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]运行模式已切换为 {Markup.Escape(selected.DisplayName)}，但清理现有连接失败：[/]{Markup.Escape(closeConnectionsError.Message)}");
            }

            return;
        }

        if (status.IsProcessRunning)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]运行模式已保存为 {Markup.Escape(selected.DisplayName)}，但 external-controller 当前不可用，无法立即应用。下次启动会生效。[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[green]运行模式已保存为：[/]{Markup.Escape(selected.DisplayName)}[grey]（下次启动生效）[/]");
    }

    private async Task ExitApplicationAsync(CancellationToken cancellationToken)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在关闭系统代理和 mihomo...", async _ =>
            {
                await CleanupOnShutdownAsync(cancellationToken);
            });
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await EnsureStartupRequirementsAsync(cancellationToken);
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        if (IsTunProxyMode(settings))
        {
            EnsureTunStartupRequirements(settings);
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在启动 mihomo...", async _ =>
            {
                // CoreManager 统一负责 runtime.yaml、进程启动、状态保存、日志和 /version 就绪检查。
                await _services.CoreManager.StartAsync(cancellationToken);
            });

        AnsiConsole.MarkupLine("[green]mihomo 已启动。[/]");
    }

    private async Task StartProxyAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        await _services.AppLog.InfoAsync(
            $"一键启动代理：proxyMode={settings.ProxyMode}, runMode={settings.RunMode}, profile={settings.CurrentProfile ?? "-"}",
            cancellationToken);
        if (IsTunProxyMode(settings))
        {
            await StartTunProxyAsync(settings, cancellationToken);
            return;
        }

        var proxyStatus = await _services.SystemProxyService.GetStatusAsync(cancellationToken);
        await _services.AppLog.InfoAsync(
            $"系统代理模式启动检查：supported={proxyStatus.IsSupported}, enabled={proxyStatus.IsEnabled}, managed={proxyStatus.IsManagedByPaprika}",
            cancellationToken);
        if (!proxyStatus.IsSupported)
        {
            throw new InvalidOperationException(proxyStatus.Message);
        }

        await EnsureStartupRequirementsAsync(cancellationToken);

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在启动代理...", async _ =>
            {
                // 一键启动代理时，核心与系统代理要保持一致：核心先可用，
                // 再把系统代理指向 mihomo 的 mixed-port。
                await _services.CoreManager.StartAsync(cancellationToken);
                try
                {
                    await _services.SystemProxyService.EnableAsync(cancellationToken);
                    await _services.AppLog.InfoAsync($"系统代理已开启：127.0.0.1:{settings.MixedPort}", cancellationToken);
                }
                catch (Exception ex)
                {
                    // 如果系统代理开启失败，回滚刚启动的核心，避免留下
                    // “核心开着但代理没接上”的半启动状态。
                    await _services.CoreManager.StopAsync(CancellationToken.None);
                    await _services.AppLog.ErrorAsync("系统代理开启失败，已回滚 mihomo。", ex, CancellationToken.None);
                    throw;
                }
            });

        settings = await _services.SettingsService.LoadAsync(cancellationToken);
        AnsiConsole.MarkupLine($"[green]代理已启动：[/]127.0.0.1:{settings.MixedPort}");
    }

    private async Task StartTunProxyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await EnsureStartupRequirementsAsync(cancellationToken);
        EnsureTunStartupRequirements(settings);
        await _services.AppLog.InfoAsync(
            $"TUN 模式启动检查通过：admin={_services.WindowsPrivilegeService.IsAdministrator()}, stack={settings.Tun.Stack}, autoRoute={settings.Tun.AutoRoute}, autoDetectInterface={settings.Tun.AutoDetectInterface}, dnsHijack={settings.Tun.DnsHijack}, strictRoute={settings.Tun.StrictRoute}, bypassLan={settings.Tun.BypassLan}, mtu={settings.Tun.Mtu}",
            cancellationToken);

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在启动 TUN 代理...", async _ =>
            {
                // TUN 接管系统流量，不需要 Windows 系统代理；这里只恢复
                // Paprika 自己接管过的代理，不碰用户由其他工具设置的代理。
                await DisableSystemProxyIfManagedWithoutStatusAsync(cancellationToken);
                await _services.CoreManager.StartAsync(cancellationToken);
            });

        AnsiConsole.MarkupLine("[green]TUN 代理已启动：[/]系统流量将由 mihomo 虚拟网卡接管。");
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await DisableSystemProxyIfManagedAsync(cancellationToken);

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在停止 mihomo...", async _ =>
            {
                // 停止核心统一走 CoreManager，避免 PID 过期和进程身份校验散落在界面层。
                await _services.CoreManager.StopAsync(cancellationToken);
            });

        AnsiConsole.MarkupLine("[green]mihomo 已停止。[/]");
    }

    private async Task StopProxyAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        await _services.AppLog.InfoAsync(
            $"一键关闭代理：proxyMode={settings.ProxyMode}",
            cancellationToken);
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在关闭代理...", async _ =>
            {
                // 一键关闭代理时，先恢复系统代理，再停止核心，避免系统继续
                // 指向一个即将消失的本地端口。
                await DisableSystemProxyIfManagedWithoutStatusAsync(cancellationToken);
                await _services.CoreManager.StopAsync(cancellationToken);
            });

        var message = IsTunProxyMode(settings)
            ? "代理已关闭：mihomo 已停止，TUN 接管已释放。"
            : "代理已关闭：系统代理已恢复，mihomo 已停止。";
        await _services.AppLog.InfoAsync(message, cancellationToken);
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");
    }

    private async Task ToggleSystemProxyAsync(CancellationToken cancellationToken)
    {
        var status = await _services.SystemProxyService.GetStatusAsync(cancellationToken);
        if (status.IsEnabled && status.IsManagedByPaprika)
        {
            await DisableSystemProxyAsync(cancellationToken);
            return;
        }

        await EnableSystemProxyAsync(cancellationToken);
    }

    private async Task EnableSystemProxyAsync(CancellationToken cancellationToken)
    {
        await EnsureCoreRunningForProxyAsync(cancellationToken);
        await _services.AppLog.InfoAsync("请求开启系统代理。", cancellationToken);

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在开启系统代理...", async _ =>
            {
                await _services.SystemProxyService.EnableAsync(cancellationToken);
            });

        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        AnsiConsole.MarkupLine($"[green]系统代理已开启：[/]127.0.0.1:{settings.MixedPort}");
    }

    private async Task DisableSystemProxyAsync(CancellationToken cancellationToken)
    {
        await _services.AppLog.InfoAsync("请求关闭系统代理。", cancellationToken);
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在关闭系统代理...", async _ =>
            {
                await _services.SystemProxyService.DisableAsync(cancellationToken);
            });

        AnsiConsole.MarkupLine("[green]系统代理已关闭并恢复原始设置。[/]");
    }

    private async Task DisableSystemProxyIfManagedAsync(CancellationToken cancellationToken)
    {
        var status = await _services.SystemProxyService.GetStatusAsync(cancellationToken);
        if (!status.IsManagedByPaprika)
        {
            await _services.AppLog.InfoAsync("系统代理未由 Paprika 接管，跳过恢复。", cancellationToken);
            return;
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在恢复系统代理设置...", async _ =>
            {
                await _services.SystemProxyService.DisableAsync(cancellationToken);
            });
    }

    private async Task DisableSystemProxyIfManagedWithoutStatusAsync(CancellationToken cancellationToken)
    {
        var status = await _services.SystemProxyService.GetStatusAsync(cancellationToken);
        if (status.IsManagedByPaprika)
        {
            // 这里只恢复 Paprika 自己接管过的系统代理，不碰用户原本由
            // 其他工具设置的代理。
            await _services.AppLog.InfoAsync("恢复 Paprika 接管的系统代理。", cancellationToken);
            await _services.SystemProxyService.DisableAsync(cancellationToken);
            return;
        }

        await _services.AppLog.InfoAsync("系统代理未由 Paprika 接管，跳过静默恢复。", cancellationToken);
    }

    private Task CleanupOnShutdownAsync(CancellationToken cancellationToken)
    {
        // 菜单退出、Ctrl+C、进程退出和窗口关闭共用同一套清理逻辑，避免行为不一致。
        return _shutdownCleanup.CleanupAsync(cancellationToken);
    }

    private async Task ShowSystemProxyStatusAsync(CancellationToken cancellationToken)
    {
        var status = await _services.SystemProxyService.GetStatusAsync(cancellationToken);

        var table = new Table()
            .RoundedBorder()
            .AddColumn("项目")
            .AddColumn("值");

        table.AddRow("平台支持", status.IsSupported ? "[green]支持[/]" : "[yellow]不支持[/]");
        table.AddRow("系统代理", status.IsEnabled ? "[green]开启[/]" : "[grey]关闭[/]");
        table.AddRow("Paprika 管理", status.IsManagedByPaprika ? "[green]是[/]" : "[grey]否[/]");
        table.AddRow("代理服务器", Markup.Escape(status.ProxyServer ?? "-"));
        table.AddRow("说明", Markup.Escape(status.Message));

        AnsiConsole.Write(table);
    }

    private async Task ShowTrafficRateAsync(CancellationToken cancellationToken)
    {
        var trafficRate = MihomoTrafficRate.Unavailable("正在读取网络速率...");

        await AnsiConsole.Live(CreateTrafficRatePanel(trafficRate))
            .AutoClear(false)
            .StartAsync(async context =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (TryConsumeEscapeKey())
                    {
                        throw new MenuBackException();
                    }

                    trafficRate = await ReadTrafficRateForHeaderAsync(cancellationToken);
                    context.UpdateTarget(CreateTrafficRatePanel(trafficRate));
                    await Task.Delay(1000, cancellationToken);
                }
            });
    }

    private async Task TailConnectionsAsync(CancellationToken cancellationToken)
    {
        await AnsiConsole.Live(CreateMessagePanel("正在读取连接列表...", "实时连接"))
            .AutoClear(false)
            .StartAsync(async context =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (TryConsumeEscapeKey())
                    {
                        throw new MenuBackException();
                    }

                    try
                    {
                        var snapshot = await _services.ConnectionDiagnosticsService.GetConnectionsAsync(cancellationToken);
                        context.UpdateTarget(BuildConnectionSnapshotRenderable(
                            "实时连接 (Esc 返回)",
                            snapshot,
                            snapshot.Connections));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        context.UpdateTarget(CreateMessagePanel(
                            $"读取连接失败：{ex.Message}",
                            "实时连接 (Esc 返回)",
                            Color.Yellow));
                    }

                    await Task.Delay(1000, cancellationToken);
                }
            });
    }

    private async Task SearchConnectionsAsync(CancellationToken cancellationToken)
    {
        var keyword = AskText("请输入搜索关键词（域名/IP/进程/规则/节点）").Trim();
        var snapshot = await _services.ConnectionDiagnosticsService.GetConnectionsAsync(cancellationToken);
        var filtered = snapshot.Connections
            .Where(connection => ConnectionMatches(connection, keyword))
            .ToArray();

        RenderConnectionSnapshot($"搜索结果：{keyword}", snapshot, filtered);
    }

    private async Task CloseSingleConnectionAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _services.ConnectionDiagnosticsService.GetConnectionsAsync(cancellationToken);
        if (snapshot.Connections.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]当前没有活动连接。[/]");
            return;
        }

        var cancel = new MihomoConnectionInfo(
            "__paprika_cancel__",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            0,
            0,
            null);
        var choices = snapshot.Connections
            .OrderByDescending(connection => connection.StartedAt ?? DateTimeOffset.MinValue)
            .ToArray();
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<MihomoConnectionInfo>()
                .Title("选择要关闭的连接 [grey](Esc 返回上一层)[/]")
                .PageSize(14)
                .WrapAround()
                .AddCancelResult(cancel)
                .UseConverter(FormatConnectionChoice)
                .AddChoices(choices));

        if (ReferenceEquals(selected, cancel))
        {
            throw new MenuBackException();
        }

        if (!AskYesNo($"确定关闭连接「{BuildConnectionTitle(selected)}」？", defaultValue: false))
        {
            throw new MenuBackException();
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在关闭连接...", async _ =>
            {
                await _services.ConnectionDiagnosticsService.CloseConnectionAsync(selected.Id, cancellationToken);
            });

        AnsiConsole.MarkupLine("[green]连接已关闭。[/]");
    }

    private async Task CloseAllDiagnosticConnectionsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _services.ConnectionDiagnosticsService.GetConnectionsAsync(cancellationToken);
        if (snapshot.Connections.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]当前没有活动连接。[/]");
            return;
        }

        if (!AskYesNo($"确定关闭全部 {snapshot.Connections.Count} 个连接？", defaultValue: false))
        {
            throw new MenuBackException();
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在清理全部连接...", async _ =>
            {
                await _services.ConnectionDiagnosticsService.CloseAllConnectionsAsync(cancellationToken);
            });

        AnsiConsole.MarkupLine("[green]全部连接已清理。[/]");
    }

    private async Task SelectNodeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var groups = await _services.NodeSelectionService.GetSelectableGroupsAsync(cancellationToken);
            if (groups.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]当前配置没有可选择的策略组。[/]");
                return;
            }

            var cancelGroup = new MihomoProxyGroupInfo("__paprika_cancel__", string.Empty, string.Empty, Array.Empty<MihomoProxyNodeInfo>());
            var selectedGroup = AnsiConsole.Prompt(
                new SelectionPrompt<MihomoProxyGroupInfo>()
                    .Title("选择策略组 [grey](Esc 返回上一层)[/]")
                    .PageSize(12)
                    .WrapAround()
                    .AddCancelResult(cancelGroup)
                    .UseConverter(FormatProxyGroupChoice)
                    .AddChoices(groups));

            if (ReferenceEquals(selectedGroup, cancelGroup))
            {
                throw new MenuBackException();
            }

            var cancelNode = new MihomoProxyNodeInfo("__paprika_cancel__", string.Empty, false, null, null);
            var selectedNode = AnsiConsole.Prompt(
                new SelectionPrompt<MihomoProxyNodeInfo>()
                    .Title($"选择「{EscapeDisplayText(selectedGroup.Name)}」使用的节点 [grey](Esc 返回上一层)[/]")
                    .PageSize(16)
                    .WrapAround()
                    .AddCancelResult(cancelNode)
                    .UseConverter(FormatProxyNodeChoice)
                    .AddChoices(selectedGroup.Nodes));

            if (ReferenceEquals(selectedNode, cancelNode))
            {
                // 节点列表里的 Esc 只退出当前策略组，回到“选择策略组”。
                continue;
            }

            if (selectedNode.IsCurrent)
            {
                AnsiConsole.MarkupLine("[yellow]该节点已经是当前选择。[/]");
                PauseForResult();
                continue;
            }

            var settings = await _services.SettingsService.LoadAsync(cancellationToken);
            Exception? closeConnectionsError = null;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(
                    settings.AutoCloseConnectionsOnNodeSwitch ? "正在切换节点并清理连接..." : "正在切换节点...",
                    async _ =>
                {
                    // 节点切换本身很轻量，完成后直接回到策略组列表，方便
                    // 用户连续调整多个策略组。
                    await _services.NodeSelectionService.SelectNodeAsync(
                        selectedGroup.Name,
                        selectedNode.Name,
                        cancellationToken);

                    if (settings.AutoCloseConnectionsOnNodeSwitch)
                    {
                        try
                        {
                            await _services.NodeSelectionService.CloseAllConnectionsAsync(cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // 节点已经切换成功，清理连接失败只提示，不回滚选择。
                            closeConnectionsError = ex;
                        }
                    }
                });

            if (closeConnectionsError is null)
            {
                var suffix = settings.AutoCloseConnectionsOnNodeSwitch ? "，当前连接已清理" : string.Empty;
                AnsiConsole.MarkupLine(
                    $"[green]已切换：[/]{EscapeDisplayText(selectedGroup.Name)} -> {EscapeDisplayText(selectedNode.Name)}{suffix}");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]节点已切换，但自动清理连接失败：[/]{Markup.Escape(closeConnectionsError.Message)}");
                PauseForResult();
            }

            await Task.Delay(650, cancellationToken);
        }
    }

    private async Task ConfigureAutoCloseConnectionsAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var cancel = "__paprika_cancel__";
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("切换节点后是否自动关闭连接？ [grey](Esc 返回上一层)[/]")
                .PageSize(3)
                .WrapAround()
                .AddCancelResult(cancel)
                .AddChoices("开启", "关闭"));

        if (selected == cancel)
        {
            throw new MenuBackException();
        }

        var enabled = selected == "开启";
        if (settings.AutoCloseConnectionsOnNodeSwitch == enabled)
        {
            AnsiConsole.MarkupLine($"[yellow]自动关闭连接已经是 {selected} 状态。[/]");
            return;
        }

        await _services.SettingsService.UpdateAsync(value =>
        {
            // 这里只控制切换节点后的自动清理，不影响用户手动清理连接。
            value.AutoCloseConnectionsOnNodeSwitch = enabled;
        }, cancellationToken);

        AnsiConsole.MarkupLine($"[green]自动关闭连接已{selected}。[/]");
    }

    private async Task ShowNodeSelectionsAsync(CancellationToken cancellationToken)
    {
        var groups = await _services.NodeSelectionService.GetSelectableGroupsAsync(cancellationToken);
        if (groups.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]当前配置没有可选择的策略组。[/]");
            return;
        }

        var table = new Table()
            .RoundedBorder()
            .Expand()
            .AddColumn(new TableColumn("策略组").NoWrap())
            .AddColumn(new TableColumn("类型").NoWrap())
            .AddColumn(new TableColumn("当前节点"))
            .AddColumn(new TableColumn("候选数").RightAligned().NoWrap());

        foreach (var group in groups)
        {
            // 表格渲染前统一把旗帜转成 [HK]/[JP]/[US]，避免终端宽度计算错位。
            table.AddRow(
                EscapeDisplayText(group.Name),
                Markup.Escape(group.Type),
                EscapeDisplayText(group.Current),
                group.Nodes.Count.ToString());
        }

        AnsiConsole.Write(table);
    }

    private async Task CloseConnectionsAsync(CancellationToken cancellationToken)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在清理连接...", async _ =>
            {
                await _services.NodeSelectionService.CloseAllConnectionsAsync(cancellationToken);
            });

        AnsiConsole.MarkupLine("[green]当前连接已清理。[/]");
    }

    private async Task EnsureCoreRunningForProxyAsync(CancellationToken cancellationToken)
    {
        var coreStatus = await _services.CoreManager.GetStatusAsync(cancellationToken);
        if (coreStatus.IsRunning)
        {
            return;
        }

        AnsiConsole.MarkupLine("[yellow]mihomo 尚未运行。系统代理需要核心运行后才有意义。[/]");
        if (!AskYesNo("是否先启动 mihomo？", defaultValue: true))
        {
            throw new InvalidOperationException("已取消开启系统代理。");
        }

        await StartCoreAsync(cancellationToken);
    }

    private async Task RestartCoreAsync(CancellationToken cancellationToken)
    {
        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        if (!status.IsRunning)
        {
            AnsiConsole.MarkupLine("[yellow]mihomo 当前是关闭或不可用状态，无法重启。请先在主菜单选择「启动/关闭代理」启动。[/]");
            return;
        }

        await EnsureStartupRequirementsAsync(cancellationToken);
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        if (IsTunProxyMode(settings))
        {
            EnsureTunStartupRequirements(settings);
            await DisableSystemProxyIfManagedAsync(cancellationToken);
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在重启 mihomo...", async _ =>
            {
                await _services.CoreManager.RestartAsync(cancellationToken);
            });

        AnsiConsole.MarkupLine("[green]mihomo 已重启。[/]");
    }

    private async Task RestartCoreForCurrentProxyModeAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        await _services.AppLog.InfoAsync(
            $"按当前接管方式重启 mihomo：proxyMode={settings.ProxyMode}, tunStack={settings.Tun.Stack}",
            cancellationToken);

        await EnsureStartupRequirementsAsync(cancellationToken);

        if (IsTunProxyMode(settings))
        {
            EnsureTunStartupRequirements(settings);

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("正在以 TUN 模式重启 mihomo...", async _ =>
                {
                    await DisableSystemProxyIfManagedWithoutStatusAsync(cancellationToken);
                    await _services.CoreManager.RestartAsync(cancellationToken);
                });

            await _services.AppLog.InfoAsync("mihomo 已按 TUN 模式重启。", cancellationToken);
            AnsiConsole.MarkupLine("[green]mihomo 已按 TUN 模式重启。[/]");
            return;
        }

        var proxyStatus = await _services.SystemProxyService.GetStatusAsync(cancellationToken);
        if (!proxyStatus.IsSupported)
        {
            throw new InvalidOperationException(proxyStatus.Message);
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在以系统代理模式重启 mihomo...", async _ =>
            {
                await _services.CoreManager.RestartAsync(cancellationToken);
                await _services.SystemProxyService.EnableAsync(cancellationToken);
            });

        await _services.AppLog.InfoAsync("mihomo 已按系统代理模式重启，系统代理已开启。", cancellationToken);
        AnsiConsole.MarkupLine("[green]mihomo 已按系统代理模式重启，系统代理已指向 Paprika。[/]");
    }

    private async Task ShowCoreStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        var proxyStatus = await _services.SystemProxyService.GetStatusAsync(cancellationToken);

        AnsiConsole.Write(CreateStatusPanel(settings, status, proxyStatus));
    }

    private async Task ShowCoreVersionAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var corePath = string.IsNullOrWhiteSpace(settings.CorePath)
            ? _services.Paths.DefaultCorePath
            : settings.CorePath;

        var table = new Table()
            .RoundedBorder()
            .AddColumn("项目")
            .AddColumn("值");

        table.AddRow("核心路径", Markup.Escape(corePath));

        if (!File.Exists(corePath))
        {
            table.AddRow("本地核心版本", "[red]核心文件不存在[/]");
            table.AddRow("运行中 API 版本", "[grey]不可用[/]");
            AnsiConsole.Write(table);
            return;
        }

        var installedVersion = await _services.CoreDownloadService.TryGetInstalledVersionAsync(
            corePath,
            cancellationToken);
        var apiVersion = await _services.ApiClient.TryGetVersionAsync(cancellationToken);

        table.AddRow(
            "本地核心版本",
            string.IsNullOrWhiteSpace(installedVersion)
                ? "[yellow]无法识别[/]"
                : $"[green]{Markup.Escape(installedVersion)}[/]");
        table.AddRow(
            "运行中 API 版本",
            string.IsNullOrWhiteSpace(apiVersion)
                ? "[grey]未运行或 external-controller 不可用[/]"
                : $"[green]{Markup.Escape(apiVersion)}[/]");

        AnsiConsole.Write(table);
    }

    private async Task DownloadCoreAsync(CancellationToken cancellationToken)
    {
        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        if (status.IsProcessRunning)
        {
            AnsiConsole.MarkupLine("[yellow]mihomo 正在运行。请先停止代理或核心，再下载/更新核心文件。[/]");
            return;
        }

        MihomoCoreUpdateInfo? updateInfo = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在检查最新核心版本...", async _ =>
            {
                updateInfo = await _services.CoreDownloadService.GetUpdateInfoAsync(cancellationToken);
            });

        if (updateInfo is null)
        {
            throw new InvalidOperationException("检查核心更新失败：没有获取到版本信息。");
        }

        var table = new Table()
            .RoundedBorder()
            .AddColumn("项目")
            .AddColumn("值");

        table.AddRow("下载源", "GitHub: MetaCubeX/mihomo latest release");
        table.AddRow("核心目录", Markup.Escape(_services.Paths.CoresDirectory));
        table.AddRow("目标文件", Markup.Escape(updateInfo.CorePath));
        table.AddRow("本地版本", Markup.Escape(updateInfo.InstalledVersion ?? (updateInfo.CoreExists ? "未知" : "未安装")));
        table.AddRow("最新版本", Markup.Escape(updateInfo.LatestAsset.Version));
        table.AddRow("当前文件状态", BuildCoreUpdateStatus(updateInfo));

        AnsiConsole.Write(table);

        if (updateInfo.IsLatest)
        {
            AnsiConsole.MarkupLine("[green]mihomo 核心已存在，且已经是最新版。[/]");
            return;
        }

        var prompt = BuildCoreDownloadPrompt(updateInfo);
        if (!AskYesNo(prompt, defaultValue: false))
        {
            return;
        }

        CoreDownloadResult? result = null;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async context =>
            {
                var resolveTask = context.AddTask("获取最新发布信息", maxValue: 1);
                var asset = updateInfo.LatestAsset;
                resolveTask.Value = 1;
                resolveTask.StopTask();

                var downloadMax = asset.Size > 0 ? asset.Size : 1;
                var downloadTask = context.AddTask($"下载 {asset.Name}", maxValue: downloadMax);
                var installTask = context.AddTask("解压并安装", autoStart: false, maxValue: 1);

                var downloadProgress = new Progress<long>(downloaded =>
                {
                    downloadTask.Value = Math.Min(downloaded, downloadTask.MaxValue);
                });

                result = await _services.CoreDownloadService.DownloadAndInstallAsync(
                    asset,
                    downloadProgress,
                    beforeInstall: () =>
                    {
                        downloadTask.Value = downloadTask.MaxValue;
                        downloadTask.StopTask();
                        installTask.StartTask();
                    },
                    cancellationToken);

                installTask.Value = 1;
                installTask.StopTask();
            });

        if (result is null)
        {
            throw new InvalidOperationException("下载核心失败：没有安装结果。");
        }

        AnsiConsole.MarkupLine($"[green]核心下载/更新完成：[/]{Markup.Escape(result.Version)}");
        AnsiConsole.MarkupLine($"文件：{Markup.Escape(result.CorePath)}");
    }

    private static string BuildCoreUpdateStatus(MihomoCoreUpdateInfo updateInfo)
    {
        if (!updateInfo.CoreExists)
        {
            return "[yellow]核心不存在，需要下载[/]";
        }

        if (updateInfo.IsLatest)
        {
            return "[green]核心已存在，且为最新版[/]";
        }

        if (updateInfo.IsInstalledVersionUnknown)
        {
            return "[yellow]核心已存在，但无法识别版本，可下载覆盖[/]";
        }

        return "[yellow]核心已存在，但不是最新版，可更新[/]";
    }

    private static string BuildCoreDownloadPrompt(MihomoCoreUpdateInfo updateInfo)
    {
        if (!updateInfo.CoreExists)
        {
            return "未找到 mihomo 核心，是否下载最新核心？";
        }

        if (updateInfo.IsInstalledVersionUnknown)
        {
            return "已找到 mihomo 核心，但无法识别版本，是否下载最新核心并覆盖？";
        }

        return $"当前核心版本为 {updateInfo.InstalledVersion}，最新版本为 {updateInfo.LatestAsset.Version}，是否更新？";
    }

    private async Task CurrentConfigResourcesMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var back = MenuItem.Back("↩️ 返回配置管理");
            var selected = PromptMenu(
                "当前配置资源",
                back,
                MenuItem.SubMenu("🛰️ 节点源", ProxyProviderResourcesMenuAsync),
                MenuItem.SubMenu("📚 规则源", RuleProviderResourcesMenuAsync),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private async Task ProxyProviderResourcesMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            IReadOnlyList<MihomoProxyProviderInfo> providers;
            try
            {
                providers = await _services.ConfigResourceService.GetProxyProvidersAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[red]读取节点源失败：[/]{Markup.Escape(ex.Message)}");
                PauseForResult();
                return;
            }

            var visibleProviders = SelectVisibleProxyProviders(providers);
            RenderProxyProviders(visibleProviders);

            var back = MenuItem.Back("↩️ 返回当前配置资源");
            var selected = PromptMenu(
                "节点源",
                back,
                new MenuItem("🔄 更新", ct => UpdateProxyProvidersAsync(visibleProviders, ct)),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private async Task RuleProviderResourcesMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            IReadOnlyList<MihomoRuleProviderInfo> providers;
            try
            {
                providers = await _services.ConfigResourceService.GetRuleProvidersAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[red]读取规则源失败：[/]{Markup.Escape(ex.Message)}");
                PauseForResult();
                return;
            }

            if (providers.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]当前配置没有 rule-providers。[/]");
                var backOnly = MenuItem.Back("↩️ 返回当前配置资源");
                var selected = PromptMenu("规则源", backOnly, backOnly);
                if (selected.ShouldReturn)
                {
                    return;
                }

                continue;
            }

            var cancel = new MihomoRuleProviderInfo(
                "__paprika_cancel__",
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                null);
            var selectedRule = AnsiConsole.Prompt(
                new SelectionPrompt<MihomoRuleProviderInfo>()
                    .Title("选中规则，按回车可进行更新 [grey](↑/↓ 选择，Esc 返回上一层)[/]")
                    .PageSize(12)
                    .WrapAround()
                    .AddCancelResult(cancel)
                    .UseConverter(FormatRuleProviderChoice)
                    .AddChoices(providers));

            if (ReferenceEquals(selectedRule, cancel))
            {
                return;
            }

            bool confirmed;
            try
            {
                confirmed = AskYesNo($"是否更新规则源「{ReplaceFlagEmojiWithRegionCodes(selectedRule.Name)}」？", defaultValue: false);
            }
            catch (MenuBackException)
            {
                continue;
            }

            if (!confirmed)
            {
                continue;
            }

            try
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"正在更新规则源 {EscapeDisplayText(selectedRule.Name)}...", async _ =>
                    {
                        // 规则源更新交给 mihomo 执行，避免 Paprika 误处理 provider 的缓存路径和代理设置。
                        await _services.ConfigResourceService.UpdateRuleProviderAsync(
                            selectedRule.Name,
                            cancellationToken);
                    });

                AnsiConsole.MarkupLine($"[green]规则源已更新：[/]{EscapeDisplayText(selectedRule.Name)}");
                await Task.Delay(650, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[red]更新规则源失败：[/]{Markup.Escape(ex.Message)}");
                PauseForResult();
            }
        }
    }

    private void RenderProxyProviders(IReadOnlyList<MihomoProxyProviderInfo> providers)
    {
        if (providers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]当前配置没有 Sub 节点源。[/]");
            return;
        }

        foreach (var provider in providers)
        {
            AnsiConsole.MarkupLine(EscapeDisplayText(provider.Name));
            AnsiConsole.MarkupLine(
                $"[grey]{Markup.Escape(FormatRelativeTime(provider.UpdatedAt))} · {provider.ProxyCount.ToString("N0", CultureInfo.CurrentCulture)} 个条目[/]");
            AnsiConsole.MarkupLine(BuildTrafficBarMarkup(provider.SubscriptionInfo));
            AnsiConsole.WriteLine();
        }
    }

    private static IReadOnlyList<MihomoProxyProviderInfo> SelectVisibleProxyProviders(
        IReadOnlyList<MihomoProxyProviderInfo> providers)
    {
        // 节点源页面只展示订阅节点源。多数配置把这块命名为 Sub；
        // 若用户配置换了名字，则退回到带订阅流量信息的 provider。
        var subProviders = providers
            .Where(provider => string.Equals(provider.Name, "Sub", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (subProviders.Length > 0)
        {
            return subProviders;
        }

        return providers
            .Where(provider => provider.SubscriptionInfo is not null)
            .ToArray();
    }

    private async Task UpdateProxyProvidersAsync(
        IReadOnlyList<MihomoProxyProviderInfo> providers,
        CancellationToken cancellationToken)
    {
        if (providers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]当前配置没有可更新的 Sub 节点源。[/]");
            return;
        }

        var prompt = providers.Count == 1
            ? $"是否更新节点源「{ReplaceFlagEmojiWithRegionCodes(providers[0].Name)}」？"
            : $"是否更新全部 {providers.Count} 个节点源？";
        if (!AskYesNo(prompt, defaultValue: false))
        {
            return;
        }

        var failures = new List<string>();
        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async context =>
            {
                foreach (var provider in providers)
                {
                    var task = context.AddTask($"更新 {EscapeDisplayText(provider.Name)}", maxValue: 1);
                    try
                    {
                        await _services.ConfigResourceService.UpdateProxyProviderAsync(
                            provider.Name,
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures.Add($"{ReplaceFlagEmojiWithRegionCodes(provider.Name)}：{ex.Message}");
                    }
                    finally
                    {
                        task.Value = 1;
                        task.StopTask();
                    }
                }
            });

        if (failures.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]节点源已更新。[/]");
            return;
        }

        AnsiConsole.MarkupLine("[yellow]部分节点源更新失败：[/]");
        foreach (var failure in failures)
        {
            AnsiConsole.MarkupLine($"[yellow]-[/] {Markup.Escape(failure)}");
        }
    }

    private async Task ImportProfileAsync(CancellationToken cancellationToken)
    {
        var sourcePath = AskPath("请输入本地 mihomo 配置文件路径");
        var defaultName = Path.GetFileNameWithoutExtension(sourcePath);

        // 配置名会变成 Paprika 数据目录下的文件名，因此导入时显式确认一次。
        var name = AskText("配置名称", defaultName);

        var profile = await _services.ProfileService.ImportAsync(sourcePath, name, cancellationToken);

        if (AskYesNo("是否立即使用这个配置？", defaultValue: true))
        {
            await UseProfileAndReloadCoreAsync(profile.Name, "当前配置已切换为", cancellationToken);
        }

        AnsiConsole.MarkupLine($"[green]已导入配置：[/]{Markup.Escape(profile.Name)}");
    }

    private async Task ImportSubscriptionProfileAsync(CancellationToken cancellationToken)
    {
        var url = AskText("请输入订阅链接").Trim();
        var defaultName = ProfileService.SuggestSubscriptionName(url);

        // 订阅配置仍然落地为 profiles/<name>.yaml，后续启动和切换逻辑无需分叉。
        var name = AskText("配置名称", defaultName);
        ProfileInfo? profile = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在下载并导入订阅配置...", async _ =>
            {
                profile = await _services.ProfileService.ImportSubscriptionAsync(url, name, cancellationToken);
            });

        if (profile is null)
        {
            throw new InvalidOperationException("订阅导入失败：没有生成配置。");
        }

        if (AskYesNo("是否立即使用这个订阅配置？", defaultValue: true))
        {
            await UseProfileAndReloadCoreAsync(profile.Name, "当前配置已切换为", cancellationToken);
        }

        AnsiConsole.MarkupLine($"[green]已导入订阅配置：[/]{Markup.Escape(profile.Name)}");
        RenderProfileSummary(profile);
    }

    private async Task UpdateCurrentSubscriptionProfileAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.CurrentProfile))
        {
            AnsiConsole.MarkupLine("[yellow]当前没有正在使用的配置。[/]");
            return;
        }

        var profile = await _services.ProfileService.GetAsync(settings.CurrentProfile, cancellationToken);
        if (!profile.IsSubscription)
        {
            AnsiConsole.MarkupLine("[yellow]当前配置不是订阅链接导入的配置，无法自动更新。[/]");
            return;
        }

        if (!AskYesNo($"是否更新当前订阅配置「{profile.Name}」？", defaultValue: true))
        {
            throw new MenuBackException();
        }

        ProfileInfo? updatedProfile = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在更新当前订阅配置...", async _ =>
            {
                updatedProfile = await _services.ProfileService.UpdateSubscriptionAsync(profile.Name, cancellationToken);
            });

        if (updatedProfile is null)
        {
            throw new InvalidOperationException("订阅更新失败：没有生成配置。");
        }

        AnsiConsole.MarkupLine($"[green]订阅配置已更新：[/]{Markup.Escape(updatedProfile.Name)}");
        RenderProfileSummary(updatedProfile);
        await ReloadCoreIfActiveProfileChangedAsync(updatedProfile.Name, "订阅配置已更新", cancellationToken);
    }

    private async Task UpdateAllSubscriptionProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = (await _services.ProfileService.ListAsync(cancellationToken))
            .Where(profile => profile.IsSubscription)
            .ToArray();

        if (profiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]还没有订阅链接导入的配置。[/]");
            return;
        }

        if (!AskYesNo($"是否更新全部 {profiles.Length} 个订阅配置？", defaultValue: false))
        {
            throw new MenuBackException();
        }

        var failures = new List<string>();
        var updatedNames = new List<string>();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async context =>
            {
                foreach (var profile in profiles)
                {
                    var task = context.AddTask($"更新 {profile.Name}", maxValue: 100);
                    try
                    {
                        await _services.ProfileService.UpdateSubscriptionAsync(profile.Name, cancellationToken);
                        updatedNames.Add(profile.Name);
                        task.Value = 100;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures.Add($"{profile.Name}：{ex.Message}");
                        task.Value = 100;
                    }
                    finally
                    {
                        task.StopTask();
                    }
                }
            });

        if (updatedNames.Count > 0)
        {
            AnsiConsole.MarkupLine($"[green]已更新 {updatedNames.Count} 个订阅配置。[/]");
        }

        if (failures.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]部分订阅更新失败，原配置已保留：[/]");
            foreach (var failure in failures)
            {
                AnsiConsole.MarkupLine($"[yellow]-[/] {Markup.Escape(failure)}");
            }
        }

        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.CurrentProfile) &&
            updatedNames.Any(name => string.Equals(name, settings.CurrentProfile, StringComparison.OrdinalIgnoreCase)))
        {
            await ReloadCoreIfActiveProfileChangedAsync(settings.CurrentProfile, "当前订阅配置已更新", cancellationToken);
        }
    }

    private async Task SwitchProfileAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var profiles = await _services.ProfileService.ListAsync(cancellationToken);

        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]还没有导入任何配置。[/]");
            return;
        }

        var cancelProfile = new ProfileInfo("__paprika_cancel__", string.Empty, DateTimeOffset.MinValue);
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<ProfileInfo>()
                .Title("选择要使用的配置 [grey](Esc 返回上一层)[/]")
                .PageSize(10)
                .WrapAround()
                .AddCancelResult(cancelProfile)
                .UseConverter(profile =>
                {
                    var active = profile.Name == settings.CurrentProfile ? "[green]*[/] " : "  ";
                    return $"{active}{Markup.Escape(profile.Name)} [grey]({FormatProfileSourceText(profile)})[/]";
                })
                .AddChoices(profiles));

        if (ReferenceEquals(selected, cancelProfile))
        {
            throw new MenuBackException();
        }

        await UseProfileAndReloadCoreAsync(selected.Name, "当前配置已切换为", cancellationToken);
    }

    private async Task ShowProfilesAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var profiles = await _services.ProfileService.ListAsync(cancellationToken);

        var table = new Table()
            .RoundedBorder()
            .AddColumn("当前")
            .AddColumn("名称")
            .AddColumn("来源")
            .AddColumn("节点")
            .AddColumn("订阅流量")
            .AddColumn("更新时间")
            .AddColumn("路径");

        foreach (var profile in profiles)
        {
            table.AddRow(
                profile.Name == settings.CurrentProfile ? "[green]*[/]" : string.Empty,
                Markup.Escape(profile.Name),
                FormatProfileSourceMarkup(profile),
                profile.ProxyCount is null
                    ? "[grey]-[/]"
                    : Markup.Escape(profile.ProxyCount.Value.ToString("N0", CultureInfo.CurrentCulture)),
                FormatProfileTrafficSummary(profile.SubscriptionInfo),
                profile.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Markup.Escape(profile.Path));
        }

        AnsiConsole.Write(table);
    }

    private void RenderProfileSummary(ProfileInfo profile)
    {
        var table = new Table()
            .RoundedBorder()
            .AddColumn("项目")
            .AddColumn("值");

        table.AddRow("名称", Markup.Escape(profile.Name));
        table.AddRow("来源", FormatProfileSourceMarkup(profile));
        table.AddRow(
            "节点数量",
            profile.ProxyCount is null
                ? "[grey]未知[/]"
                : Markup.Escape(profile.ProxyCount.Value.ToString("N0", CultureInfo.CurrentCulture)));
        table.AddRow("订阅流量", FormatProfileTrafficSummary(profile.SubscriptionInfo));
        table.AddRow("更新时间", Markup.Escape(profile.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));

        if (!string.IsNullOrWhiteSpace(profile.SubscriptionUrl))
        {
            table.AddRow("订阅链接", Markup.Escape(profile.SubscriptionUrl));
        }

        AnsiConsole.Write(table);
    }

    private async Task UseProfileAndReloadCoreAsync(
        string profileName,
        string successMessagePrefix,
        CancellationToken cancellationToken)
    {
        await _services.ProfileService.UseAsync(profileName, cancellationToken);
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(successMessagePrefix)}：[/]{Markup.Escape(profileName)}");

        // 节点选择、配置资源等页面读取的是 mihomo 运行时 API。
        // 当前配置变更后必须重启核心，否则 API 里仍是旧配置的节点和 provider。
        await ReloadCoreIfActiveProfileChangedAsync(profileName, "当前配置已变更", cancellationToken);
    }

    private async Task ReloadCoreIfActiveProfileChangedAsync(
        string profileName,
        string reason,
        CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        if (!string.Equals(settings.CurrentProfile, profileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        if (!status.IsProcessRunning)
        {
            return;
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"{reason}，正在重启 mihomo 使其生效...", async _ =>
            {
                await _services.CoreManager.RestartAsync(cancellationToken);
            });

        AnsiConsole.MarkupLine("[green]mihomo 已重启，节点选择和配置资源已切换到当前配置。[/]");
    }

    private async Task SetMixedPortAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var port = AskPort("mixed-port", settings.MixedPort);

        await _services.SettingsService.UpdateAsync(value =>
        {
            // 该端口会在启动核心前写入 runtime.yaml。
            value.MixedPort = port;
        }, cancellationToken);

        AnsiConsole.MarkupLine($"[green]mixed-port 已设置为 {port}。[/]");
    }

    private async Task SetControllerPortAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var port = AskPort("external-controller 端口", settings.ControllerPort);

        await _services.SettingsService.UpdateAsync(value =>
        {
            // Paprika 通过该端口访问 mihomo 的 REST API。
            value.ControllerPort = port;
        }, cancellationToken);

        AnsiConsole.MarkupLine($"[green]external-controller 端口已设置为 {port}。[/]");
    }

    private async Task ConfigureRunInBackgroundPromptAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var cancel = "__paprika_cancel__";
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("后台运行前是否显示提示？ [grey](Esc 返回上一层)[/]")
                .PageSize(3)
                .WrapAround()
                .AddCancelResult(cancel)
                .AddChoices("开启", "关闭"));

        if (selected == cancel)
        {
            throw new MenuBackException();
        }

        var enabled = selected == "开启";
        if (settings.ShowRunInBackgroundPrompt == enabled)
        {
            AnsiConsole.MarkupLine($"[yellow]后台提示已经是 {selected} 状态。[/]");
            return;
        }

        await _services.SettingsService.UpdateAsync(value =>
        {
            value.ShowRunInBackgroundPrompt = enabled;
        }, cancellationToken);

        AnsiConsole.MarkupLine($"[green]后台提示已{selected}。[/]");
    }

    private async Task TunSettingsMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var settings = await _services.SettingsService.LoadAsync(cancellationToken);
            RenderTunSettingsSummary(settings);

            var back = MenuItem.Back("↩️ 返回应用设置");
            var selected = PromptMenu(
                "TUN 设置",
                back,
                new MenuItem("📊 查看 TUN 状态", ShowTunStatusAsync),
                new MenuItem("📄 查看运行时配置", ShowTunRuntimeConfigAsync),
                new MenuItem("🩺 TUN 健康检查", ShowTunHealthCheckAsync),
                new MenuItem("🛠️ 一键修复 TUN", RunTunOneClickRepairAsync),
                new MenuItem("🌐 TUN 连通性测试", RunTunConnectivityTestAsync),
                new MenuItem(
                    $"🧱 协议栈 [{settings.Tun.Stack}]",
                    ConfigureTunStackAsync,
                    MarkupLabel: $"🧱 协议栈 [[{FormatTunStackMarkup(settings.Tun.Stack)}]]"),
                new MenuItem("🏷️ 设置网卡名称", SetTunDeviceAsync),
                new MenuItem(
                    $"🛣️ 自动路由 [{FormatToggleText(settings.Tun.AutoRoute)}]",
                    ct => ToggleTunSettingAsync("自动路由", tun => tun.AutoRoute, (tun, enabled) => tun.AutoRoute = enabled, ct),
                    MarkupLabel: $"🛣️ 自动路由 [[{FormatToggleMarkup(settings.Tun.AutoRoute)}]]"),
                new MenuItem(
                    $"🧭 自动选择出口网卡 [{FormatToggleText(settings.Tun.AutoDetectInterface)}]",
                    ct => ToggleTunSettingAsync("自动选择出口网卡", tun => tun.AutoDetectInterface, (tun, enabled) => tun.AutoDetectInterface = enabled, ct),
                    MarkupLabel: $"🧭 自动选择出口网卡 [[{FormatToggleMarkup(settings.Tun.AutoDetectInterface)}]]"),
                new MenuItem(
                    $"🧬 DNS 劫持 [{FormatToggleText(settings.Tun.DnsHijack)}]",
                    ct => ToggleTunSettingAsync("DNS 劫持", tun => tun.DnsHijack, (tun, enabled) => tun.DnsHijack = enabled, ct),
                    MarkupLabel: $"🧬 DNS 劫持 [[{FormatToggleMarkup(settings.Tun.DnsHijack)}]]"),
                new MenuItem(
                    $"🔒 严格路由 [{FormatToggleText(settings.Tun.StrictRoute)}]",
                    ct => ToggleTunSettingAsync("严格路由", tun => tun.StrictRoute, (tun, enabled) => tun.StrictRoute = enabled, ct),
                    MarkupLabel: $"🔒 严格路由 [[{FormatToggleMarkup(settings.Tun.StrictRoute)}]]"),
                new MenuItem(
                    $"🏠 绕过局域网 [{FormatToggleText(settings.Tun.BypassLan)}]",
                    ct => ToggleTunSettingAsync("绕过局域网", tun => tun.BypassLan, (tun, enabled) => tun.BypassLan = enabled, ct),
                    MarkupLabel: $"🏠 绕过局域网 [[{FormatToggleMarkup(settings.Tun.BypassLan)}]]"),
                new MenuItem(
                    $"🧱 排除网段 [{settings.Tun.RouteExcludeAddress.Count} 条]",
                    TunRouteExcludeMenuAsync),
                new MenuItem("📏 设置 MTU", SetTunMtuAsync),
                new MenuItem("🧪 TUN 诊断", ShowTunDiagnosticsAsync),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private void RenderTunSettingsSummary(AppSettings settings)
    {
        var table = new Table()
            .RoundedBorder()
            .AddColumn("项目")
            .AddColumn("值");

        table.AddRow("接管方式", FormatProxyModeMarkup(settings.ProxyMode));
        table.AddRow("TUN 写入", IsTunProxyMode(settings) ? "[green]启用[/]" : "[grey]未启用[/]");
        table.AddRow("协议栈", FormatTunStackMarkup(settings.Tun.Stack));
        table.AddRow("网卡名称", Markup.Escape(settings.Tun.Device));
        table.AddRow("自动路由", FormatToggleMarkup(settings.Tun.AutoRoute));
        table.AddRow("自动出口", FormatToggleMarkup(settings.Tun.AutoDetectInterface));
        table.AddRow("DNS 劫持", FormatToggleMarkup(settings.Tun.DnsHijack));
        table.AddRow("严格路由", FormatToggleMarkup(settings.Tun.StrictRoute));
        table.AddRow("绕过局域网", FormatToggleMarkup(settings.Tun.BypassLan));
        table.AddRow("排除网段", $"{settings.Tun.RouteExcludeAddress.Count.ToString(CultureInfo.InvariantCulture)} 条");
        table.AddRow("MTU", settings.Tun.Mtu.ToString(CultureInfo.InvariantCulture));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private async Task ShowTunStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        var proxyStatus = await _services.SystemProxyService.GetStatusAsync(cancellationToken);

        AnsiConsole.Write(BuildTunStatusTable(settings, status, proxyStatus));
    }

    private async Task ShowTunRuntimeConfigAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtimePath = _services.Paths.RuntimeConfigPath;
        if (!File.Exists(runtimePath))
        {
            AnsiConsole.Write(CreateMessagePanel(
                $"还没有生成 runtime.yaml。\n路径：{runtimePath}\n请先启动代理，或执行「TUN 健康检查 / 一键修复」。",
                "运行时配置",
                Color.Yellow));
            return;
        }

        try
        {
            await using var file = new FileStream(
                runtimePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var stream = new YamlStream();
            stream.Load(reader);

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                AnsiConsole.Write(CreateMessagePanel(
                    "runtime.yaml 不是有效的 YAML 映射，无法预览关键字段。",
                    "运行时配置",
                    Color.Yellow));
                return;
            }

            AnsiConsole.MarkupLine($"[grey]配置文件：[/]{Markup.Escape(runtimePath)}");
            AnsiConsole.WriteLine();
            AnsiConsole.Write(BuildTunRuntimeConfigTable(root));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.Write(CreateMessagePanel(
                $"读取 runtime.yaml 失败：{ex.Message}",
                "运行时配置",
                Color.Yellow));
        }
    }

    private async Task ShowTunDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await ShowTunStatusAsync(cancellationToken);
        AnsiConsole.WriteLine();

        var logs = ReadTunDiagnosticLogs(20);
        if (logs.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]最近日志中没有找到 TUN/DNS/路由相关条目。[/]");
            return;
        }

        var panel = new Panel(string.Join(Environment.NewLine, logs.Select(Markup.Escape)))
            .Header("最近 TUN 关键日志")
            .RoundedBorder()
            .Expand();
        AnsiConsole.Write(panel);
    }

    private async Task ShowTunHealthCheckAsync(CancellationToken cancellationToken)
    {
        var report = await BuildTunHealthReportAsync(cancellationToken);
        RenderTunHealthReport(report);

        if (!report.HasRepairableIssues)
        {
            return;
        }

        AnsiConsole.WriteLine();
        if (!AskYesNo($"发现 {report.RepairableIssueCount} 项可以自动修复，是否立即执行一键修复？", defaultValue: true))
        {
            return;
        }

        var result = await ApplyTunOneClickRepairAsync(cancellationToken);
        RenderTunRepairResult(result);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]修复后复查：[/]");
        RenderTunHealthReport(await BuildTunHealthReportAsync(cancellationToken));
    }

    private async Task RunTunOneClickRepairAsync(CancellationToken cancellationToken)
    {
        var report = await BuildTunHealthReportAsync(cancellationToken);
        RenderTunHealthReport(report);

        AnsiConsole.WriteLine();
        if (!AskYesNo("是否执行 TUN 一键修复？", defaultValue: true))
        {
            return;
        }

        var result = await ApplyTunOneClickRepairAsync(cancellationToken);
        RenderTunRepairResult(result);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]修复后复查：[/]");
        RenderTunHealthReport(await BuildTunHealthReportAsync(cancellationToken));
    }

    private async Task RunTunConnectivityTestAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var coreStatus = await _services.CoreManager.GetStatusAsync(cancellationToken);
        var preflight = BuildTunConnectivityPreflight(settings, coreStatus);

        if (preflight.Count > 0)
        {
            AnsiConsole.Write(CreateMessagePanel(
                string.Join(Environment.NewLine, preflight),
                "TUN 连通性测试",
                Color.Yellow));
            return;
        }

        var targets = GetTunConnectivityTargets();
        var results = new List<TunConnectivityResult>();
        await _services.AppLog.InfoAsync("开始 TUN 连通性测试。", cancellationToken);

        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async context =>
            {
                foreach (var target in targets)
                {
                    var task = context.AddTask($"测试 {target.Name}", maxValue: 2);
                    var dns = await TestDnsAsync(target, cancellationToken);
                    task.Increment(1);

                    var http = dns.IsSuccess
                        ? await TestHttpAsync(target, cancellationToken)
                        : TunHttpTestResult.Skipped("DNS 失败，跳过访问测试。");
                    task.Increment(1);
                    task.StopTask();

                    results.Add(new TunConnectivityResult(target, dns, http));
                }
            });

        await _services.AppLog.InfoAsync(
            $"TUN 连通性测试完成：success={results.Count(result => result.Http.IsSuccess)}/{results.Count}",
            cancellationToken);

        RenderTunConnectivityResults(settings, results);
    }

    private static IReadOnlyList<string> BuildTunConnectivityPreflight(AppSettings settings, CoreStatus coreStatus)
    {
        var messages = new List<string>();
        if (!IsTunProxyMode(settings))
        {
            messages.Add("当前接管方式不是 TUN 模式。请先在主菜单切换「接管方式 [TUN 模式]」。");
        }

        if (!coreStatus.IsProcessRunning)
        {
            messages.Add("mihomo 尚未运行。请先在主菜单选择「启动/关闭代理」启动代理。");
        }
        else if (!coreStatus.IsApiAvailable)
        {
            messages.Add("mihomo external-controller 当前不可用。请先查看核心日志或执行 TUN 健康检查。");
        }

        return messages;
    }

    private static IReadOnlyList<TunConnectivityTarget> GetTunConnectivityTargets()
    {
        return
        [
            new TunConnectivityTarget("百度", "www.baidu.com", "https://www.baidu.com", "baidu", false),
            new TunConnectivityTarget("Google", "www.google.com", "https://www.google.com/generate_204", "google", true),
            new TunConnectivityTarget("GitHub", "github.com", "https://github.com", "github", true),
            new TunConnectivityTarget("Telegram", "telegram.org", "https://telegram.org", "telegram", true)
        ];
    }

    private static async Task<TunDnsTestResult> TestDnsAsync(
        TunConnectivityTarget target,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target.Host, timeout.Token);
            stopwatch.Stop();
            var displayAddresses = addresses
                .Take(3)
                .Select(address => address.ToString())
                .ToArray();
            var message = displayAddresses.Length == 0
                ? "无地址"
                : string.Join(", ", displayAddresses);
            return new TunDnsTestResult(true, message, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new TunDnsTestResult(false, "DNS 解析超时", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new TunDnsTestResult(false, ex.Message, stopwatch.Elapsed);
        }
    }

    private static async Task<TunHttpTestResult> TestHttpAsync(
        TunConnectivityTarget target,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var handler = new HttpClientHandler
        {
            // TUN 测试要验证透明接管，因此这里显式不使用 Windows 系统代理。
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
        request.Headers.UserAgent.ParseAdd("Paprika/1.0");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            stopwatch.Stop();

            var code = (int)response.StatusCode;
            var isReachable = code is >= 200 and < 400;
            var status = $"{code} {response.ReasonPhrase}".Trim();
            return new TunHttpTestResult(isReachable, status, stopwatch.Elapsed, code);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new TunHttpTestResult(false, "访问超时", stopwatch.Elapsed, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new TunHttpTestResult(false, ex.Message, stopwatch.Elapsed, null);
        }
    }

    private void RenderTunConnectivityResults(
        AppSettings settings,
        IReadOnlyList<TunConnectivityResult> results)
    {
        AnsiConsole.Write(BuildTunConnectivityTable(results));
        AnsiConsole.WriteLine();

        var matchedLogs = ReadConnectivityLogs(results.Select(result => result.Target).ToArray(), 16);
        if (matchedLogs.Count > 0)
        {
            var logTable = new Table()
                .RoundedBorder()
                .Title("最近相关 mihomo 日志")
                .AddColumn(new TableColumn("时间").NoWrap())
                .AddColumn(new TableColumn("级别").NoWrap())
                .AddColumn("内容");

            foreach (var entry in matchedLogs)
            {
                logTable.AddRow(
                    FormatLogTime(entry.Timestamp),
                    FormatLogLevel(entry.Level),
                    FormatLogMessage(entry.Message));
            }

            AnsiConsole.Write(logTable);
            AnsiConsole.WriteLine();
        }

        var suggestions = BuildTunConnectivitySuggestions(settings, results, matchedLogs);
        if (suggestions.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]连通性测试通过，TUN 透明接管看起来工作正常。[/]");
            return;
        }

        var rows = suggestions
            .Select(text => new Markup($"[yellow]•[/] {Markup.Escape(text)}"))
            .Cast<IRenderable>()
            .ToArray();
        AnsiConsole.Write(new Panel(new Rows(rows))
            .Header("建议")
            .RoundedBorder()
            .Expand());
    }

    private static Table BuildTunConnectivityTable(IReadOnlyList<TunConnectivityResult> results)
    {
        var table = new Table()
            .RoundedBorder()
            .Title("TUN 连通性测试")
            .AddColumn("目标")
            .AddColumn("DNS")
            .AddColumn("访问")
            .AddColumn("耗时")
            .AddColumn("说明");

        foreach (var result in results)
        {
            table.AddRow(
                Markup.Escape(result.Target.Name),
                FormatConnectivityState(result.Dns.IsSuccess, result.Dns.Message),
                FormatConnectivityState(result.Http.IsSuccess, result.Http.Message),
                FormatConnectivityDuration(result),
                Markup.Escape(BuildConnectivitySummary(result)));
        }

        return table;
    }

    private IReadOnlyList<CoreLogEntry> ReadConnectivityLogs(
        IReadOnlyList<TunConnectivityTarget> targets,
        int lineCount)
    {
        if (!File.Exists(_services.Paths.CoreLogPath))
        {
            return Array.Empty<CoreLogEntry>();
        }

        var keywords = targets
            .SelectMany(target => new[] { target.Host, target.LogKeyword })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return CoreLogFile.ReadLinesShared(_services.Paths.CoreLogPath)
            .Select(ParseCoreLogEntry)
            .Where(entry => keywords.Any(keyword => entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .TakeLast(lineCount)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildTunConnectivitySuggestions(
        AppSettings settings,
        IReadOnlyList<TunConnectivityResult> results,
        IReadOnlyList<CoreLogEntry> matchedLogs)
    {
        var suggestions = new List<string>();
        var domesticOk = results.Any(result => !result.Target.IsForeign && result.Http.IsSuccess);
        var foreignFailures = results
            .Where(result => result.Target.IsForeign && !result.Http.IsSuccess)
            .ToArray();

        if (results.Any(result => !result.Dns.IsSuccess))
        {
            suggestions.Add("存在 DNS 解析失败，优先检查 TUN 健康检查里的 DNS 劫持、runtime DNS 和 sniffer 状态。");
        }

        if (domesticOk && foreignFailures.Length > 0)
        {
            suggestions.Add("国内可达但海外目标失败，通常是规则/节点/出口问题，不太像 TUN 网卡完全失效。");
        }

        if (foreignFailures.Length > 0 &&
            matchedLogs.Any(entry => entry.Message.Contains(" DIRECT ", StringComparison.OrdinalIgnoreCase) ||
                                     entry.Message.Contains(" dial DIRECT ", StringComparison.OrdinalIgnoreCase)))
        {
            suggestions.Add("相关日志里出现 DIRECT，海外目标可能被直连；请检查运行模式、规则分流和当前策略组选择。");
        }

        if (foreignFailures.Length > 0 &&
            matchedLogs.Any(entry => entry.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)))
        {
            suggestions.Add("相关日志里出现 timeout，若规则已走代理，请优先尝试切换节点或清理连接。");
        }

        if (foreignFailures.Length > 0 &&
            !matchedLogs.Any(entry => foreignFailures.Any(result =>
                entry.Message.Contains(result.Target.LogKeyword, StringComparison.OrdinalIgnoreCase))))
        {
            suggestions.Add("海外目标失败但最近日志里没有对应域名，可能请求没有进入 mihomo 或 sniffer/DNS 映射没有命中。");
        }

        if (!settings.Tun.DnsHijack)
        {
            suggestions.Add("DNS 劫持当前关闭，TUN 下建议开启。");
        }

        return suggestions;
    }

    private static string FormatConnectivityState(bool success, string message)
    {
        var color = success ? "green" : "red";
        var label = success ? "成功" : "失败";
        return $"[{color}]{label}[/] [grey]{Markup.Escape(TruncateDisplayText(message, 44))}[/]";
    }

    private static string FormatConnectivityDuration(TunConnectivityResult result)
    {
        var dns = FormatDuration(result.Dns.Duration);
        var http = result.Http.Duration is null ? "-" : FormatDuration(result.Http.Duration.Value);
        return Markup.Escape($"DNS {dns} / HTTP {http}");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return $"{Math.Max(1, (int)Math.Round(duration.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture)}ms";
    }

    private static string BuildConnectivitySummary(TunConnectivityResult result)
    {
        if (result.Dns.IsSuccess && result.Http.IsSuccess)
        {
            return "可访问";
        }

        if (!result.Dns.IsSuccess)
        {
            return "DNS 阶段失败";
        }

        if (result.Http.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            result.Http.Message.Contains("超时", StringComparison.OrdinalIgnoreCase))
        {
            return "请求超时";
        }

        if (result.Http.StatusCode is >= 400)
        {
            return "目标返回错误状态码";
        }

        return "请求失败";
    }

    private static string TruncateDisplayText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(0, maxLength - 1)] + "…";
    }

    private async Task<TunHealthReport> BuildTunHealthReportAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var coreStatus = await _services.CoreManager.GetStatusAsync(cancellationToken);
        var proxyStatus = await _services.SystemProxyService.GetStatusAsync(cancellationToken);
        var profileAvailable = await IsCurrentProfileAvailableAsync(settings, cancellationToken);
        var isWindows = OperatingSystem.IsWindows();
        var isAdministrator = _services.WindowsPrivilegeService.IsAdministrator();
        var corePath = string.IsNullOrWhiteSpace(settings.CorePath)
            ? _services.Paths.DefaultCorePath
            : settings.CorePath;
        var coreExists = File.Exists(corePath);
        var isTunMode = IsTunProxyMode(settings);

        var items = new List<TunHealthCheckItem>
        {
            isWindows
                ? TunHealthCheckItem.Ok("平台", "Windows，支持当前 TUN 实现。")
                : TunHealthCheckItem.Error("平台", "当前第一版 TUN 模式只支持 Windows。", canRepair: false),
            isAdministrator
                ? TunHealthCheckItem.Ok("管理员权限", "已具备创建虚拟网卡和写路由所需权限。")
                : TunHealthCheckItem.Error("管理员权限", "TUN 模式需要以管理员身份运行 Paprika。", canRepair: false),
            isTunMode
                ? TunHealthCheckItem.Ok("接管方式", "当前为 TUN 模式。")
                : TunHealthCheckItem.Warning("接管方式", "当前不是 TUN 模式，一键修复会切换为 TUN。"),
            coreExists
                ? TunHealthCheckItem.Ok("核心文件", $"已找到 {corePath}")
                : TunHealthCheckItem.Error("核心文件", "默认核心目录没有 mihomo.exe，请先到核心管理下载/更新核心。", canRepair: false),
            profileAvailable
                ? TunHealthCheckItem.Ok("当前配置", $"当前配置为 {settings.CurrentProfile}。")
                : TunHealthCheckItem.Error("当前配置", "没有可用的当前配置，请先导入并选择配置。", canRepair: false)
        };

        items.Add(BuildCoreHealthItem(coreStatus, isWindows, isAdministrator, coreExists, profileAvailable));
        items.Add(BuildSystemProxyHealthItem(settings, proxyStatus));
        items.AddRange(BuildTunSettingHealthItems(settings));
        items.AddRange(BuildRuntimeHealthItems(settings, coreStatus, profileAvailable));

        return new TunHealthReport(settings, coreStatus, proxyStatus, items);
    }

    private static TunHealthCheckItem BuildCoreHealthItem(
        CoreStatus coreStatus,
        bool isWindows,
        bool isAdministrator,
        bool coreExists,
        bool profileAvailable)
    {
        if (coreStatus.IsRunning)
        {
            return TunHealthCheckItem.Ok("核心/API", $"mihomo 运行中，API 可用，版本：{coreStatus.Version ?? "-"}。");
        }

        if (coreStatus.IsProcessRunning)
        {
            var canRepair = isWindows && isAdministrator && coreExists && profileAvailable;
            return TunHealthCheckItem.Warning(
                "核心/API",
                "mihomo 进程存在，但 external-controller 不可用；一键修复会尝试重启核心。",
                canRepair);
        }

        return TunHealthCheckItem.Info("核心/API", "核心未运行；修复后的 TUN 配置会在下次启动代理时生效。");
    }

    private static TunHealthCheckItem BuildSystemProxyHealthItem(AppSettings settings, SystemProxyStatus proxyStatus)
    {
        if (!proxyStatus.IsEnabled)
        {
            return TunHealthCheckItem.Ok("系统代理", "系统代理未开启，不会和 TUN 接管冲突。");
        }

        if (proxyStatus.IsManagedByPaprika || IsPaprikaLocalProxy(proxyStatus, settings))
        {
            return TunHealthCheckItem.Warning("系统代理", "系统代理仍指向 Paprika，本地 TUN 模式下应恢复关闭。");
        }

        return TunHealthCheckItem.Warning(
            "系统代理",
            "检测到系统代理已开启，但不是 Paprika 管理；请确认是否由其他工具设置。",
            canRepair: false);
    }

    private static IReadOnlyList<TunHealthCheckItem> BuildTunSettingHealthItems(AppSettings settings)
    {
        var items = new List<TunHealthCheckItem>();

        items.Add(TunHealthCheckItem.Ok("协议栈", $"当前为 {NormalizeTunStack(settings.Tun.Stack)}。"));
        items.Add(settings.Tun.AutoRoute
            ? TunHealthCheckItem.Ok("自动路由", "已开启。")
            : TunHealthCheckItem.Warning("自动路由", "未开启时 TUN 可能无法接管系统流量。"));
        items.Add(settings.Tun.AutoDetectInterface
            ? TunHealthCheckItem.Ok("自动出口", "已开启。")
            : TunHealthCheckItem.Warning("自动出口", "未开启时多网卡环境可能选错出口。"));
        items.Add(settings.Tun.DnsHijack
            ? TunHealthCheckItem.Ok("DNS 劫持", "已开启，DNS 会进入 mihomo。")
            : TunHealthCheckItem.Warning("DNS 劫持", "未开启时域名解析可能绕过 mihomo。"));
        items.Add(!settings.Tun.StrictRoute
            ? TunHealthCheckItem.Ok("严格路由", "已关闭，桌面日常使用更宽容。")
            : TunHealthCheckItem.Warning("严格路由", "已开启，部分局域网或特殊应用可能受影响。"));
        items.Add(settings.Tun.BypassLan && ContainsDefaultTunRouteExcludes(settings.Tun.RouteExcludeAddress)
            ? TunHealthCheckItem.Ok("局域网绕过", "已保留常见局域网和链路本地地址。")
            : TunHealthCheckItem.Warning("局域网绕过", "建议绕过常见局域网网段，避免访问路由器/NAS 时被接管。"));
        items.Add(settings.Tun.Mtu is >= 576 and <= 9000
            ? TunHealthCheckItem.Ok("MTU", $"当前 MTU 为 {settings.Tun.Mtu}。")
            : TunHealthCheckItem.Warning("MTU", "MTU 超出有效范围，一键修复会恢复为 1500。"));
        items.Add(string.IsNullOrWhiteSpace(settings.Tun.Device)
            ? TunHealthCheckItem.Warning("网卡名称", "TUN 网卡名称为空，一键修复会恢复为 Paprika。")
            : TunHealthCheckItem.Ok("网卡名称", $"当前网卡名称为 {settings.Tun.Device}。"));

        return items;
    }

    private IReadOnlyList<TunHealthCheckItem> BuildRuntimeHealthItems(
        AppSettings settings,
        CoreStatus coreStatus,
        bool profileAvailable)
    {
        var items = new List<TunHealthCheckItem>();
        var runtimePath = _services.Paths.RuntimeConfigPath;

        if (!IsTunProxyMode(settings))
        {
            items.Add(TunHealthCheckItem.Info("runtime.yaml", "当前不是 TUN 模式，暂不检查运行时 TUN 配置。"));
            return items;
        }

        if (!File.Exists(runtimePath))
        {
            var status = coreStatus.IsProcessRunning ? TunHealthStatus.Error : TunHealthStatus.Warning;
            items.Add(new TunHealthCheckItem(
                "runtime.yaml",
                status,
                $"未找到运行时配置文件：{runtimePath}",
                profileAvailable));
            return items;
        }

        try
        {
            using var file = new FileStream(
                runtimePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var stream = new YamlStream();
            stream.Load(reader);

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                items.Add(RuntimeHealthItem("runtime.yaml", "运行时配置不是有效的 YAML 映射。", coreStatus, profileAvailable));
                return items;
            }

            items.Add(ReadYamlBool(root, "tun", "enable") == true
                ? TunHealthCheckItem.Ok("runtime TUN", "tun.enable 已写入 true。")
                : RuntimeHealthItem("runtime TUN", "tun.enable 不是 true，需要重新生成/重启。", coreStatus, profileAvailable));
            var expectedStack = NormalizeTunStack(settings.Tun.Stack);
            var runtimeStack = NormalizeTunStack(ReadYamlScalar(root, "tun", "stack"));
            items.Add(string.Equals(runtimeStack, expectedStack, StringComparison.OrdinalIgnoreCase)
                ? TunHealthCheckItem.Ok("runtime 协议栈", $"运行时协议栈为 {runtimeStack}。")
                : RuntimeHealthItem("runtime 协议栈", $"运行时协议栈为 {runtimeStack}，与当前设置 {expectedStack} 不一致，需要重新生成/重启。", coreStatus, profileAvailable));
            items.Add(ReadYamlBool(root, "dns", "enable") == true &&
                      string.Equals(ReadYamlScalar(root, "dns", "enhanced-mode"), "fake-ip", StringComparison.OrdinalIgnoreCase)
                ? TunHealthCheckItem.Ok("runtime DNS", "DNS 已开启并使用 fake-ip。")
                : RuntimeHealthItem("runtime DNS", "DNS 未按 TUN 运行配置写入。", coreStatus, profileAvailable));
            items.Add(!settings.Tun.DnsHijack || ReadYamlSequence(root, "tun", "dns-hijack").Count > 0
                ? TunHealthCheckItem.Ok("runtime DNS 劫持", "dns-hijack 已写入。")
                : RuntimeHealthItem("runtime DNS 劫持", "dns-hijack 为空，DNS 可能绕过 mihomo。", coreStatus, profileAvailable));
            items.Add(ReadYamlBool(root, "sniffer", "enable") == true &&
                      ReadYamlBool(root, "sniffer", "force-dns-mapping") == true &&
                      ReadYamlBool(root, "sniffer", "parse-pure-ip") == true
                ? TunHealthCheckItem.Ok("runtime 域名嗅探", "sniffer 已开启，并启用 DNS 映射和纯 IP 解析。")
                : RuntimeHealthItem("runtime 域名嗅探", "sniffer 未完整开启，TUN 下域名规则可能无法命中。", coreStatus, profileAvailable));
        }
        catch (Exception ex)
        {
            items.Add(RuntimeHealthItem(
                "runtime.yaml",
                $"读取运行时配置失败：{ex.Message}",
                coreStatus,
                profileAvailable));
        }

        return items;
    }

    private static TunHealthCheckItem RuntimeHealthItem(
        string name,
        string message,
        CoreStatus coreStatus,
        bool canRepair)
    {
        var status = coreStatus.IsProcessRunning ? TunHealthStatus.Error : TunHealthStatus.Warning;
        return new TunHealthCheckItem(name, status, message, canRepair);
    }

    private async Task<TunRepairResult> ApplyTunOneClickRepairAsync(CancellationToken cancellationToken)
    {
        var actions = new List<string>();
        var warnings = new List<string>();

        await _services.AppLog.InfoAsync("开始 TUN 一键修复。", cancellationToken);
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("正在执行 TUN 一键修复...", async _ =>
            {
                var before = await _services.SettingsService.LoadAsync(cancellationToken);
                var profileAvailable = await IsCurrentProfileAvailableAsync(before, cancellationToken);

                AddTunSettingRepairActions(before, actions);
                await _services.SettingsService.UpdateAsync(settings =>
                {
                    settings.ProxyMode = "tun";
                    settings.Tun.Enabled = true;
                    settings.Tun.Device = string.IsNullOrWhiteSpace(settings.Tun.Device)
                        ? "Paprika"
                        : settings.Tun.Device.Trim();
                    settings.Tun.AutoRoute = true;
                    settings.Tun.AutoDetectInterface = true;
                    settings.Tun.DnsHijack = true;
                    settings.Tun.StrictRoute = false;
                    settings.Tun.BypassLan = true;
                    settings.Tun.Mtu = settings.Tun.Mtu is >= 576 and <= 9000 ? settings.Tun.Mtu : 1500;
                    settings.Tun.RouteExcludeAddress = MergeDefaultTunRouteExcludes(settings.Tun.RouteExcludeAddress);
                }, cancellationToken);

                var repairedSettings = await _services.SettingsService.LoadAsync(cancellationToken);
                var proxyStatus = await _services.SystemProxyService.GetStatusAsync(cancellationToken);
                if (proxyStatus.IsManagedByPaprika || IsPaprikaLocalProxy(proxyStatus, repairedSettings))
                {
                    await _services.SystemProxyService.DisableAsync(cancellationToken);
                    actions.Add("已恢复 Paprika 接管的 Windows 系统代理，避免和 TUN 同时接管。");
                }
                else if (proxyStatus.IsEnabled)
                {
                    warnings.Add("检测到系统代理已开启但不是 Paprika 管理，已保留原样。");
                }

                if (profileAvailable)
                {
                    try
                    {
                        await _services.RuntimeConfigService.GenerateAsync(repairedSettings, cancellationToken);
                        actions.Add("已重新生成 runtime.yaml，包含当前 TUN、DNS 和 sniffer 运行配置。");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        warnings.Add($"runtime.yaml 重新生成失败：{ex.Message}");
                        await _services.AppLog.ErrorAsync("TUN 一键修复生成 runtime.yaml 失败。", ex, CancellationToken.None);
                    }
                }
                else
                {
                    warnings.Add("当前没有可用配置，暂时无法生成 runtime.yaml。");
                }

                await RestartCoreAfterTunRepairIfNeededAsync(repairedSettings, profileAvailable, actions, warnings, cancellationToken);
            });

        if (actions.Count == 0)
        {
            actions.Add("TUN 设置无需修复。");
        }

        await _services.AppLog.InfoAsync(
            $"TUN 一键修复完成：actions={actions.Count}, warnings={warnings.Count}",
            cancellationToken);
        return new TunRepairResult(actions, warnings);
    }

    private async Task RestartCoreAfterTunRepairIfNeededAsync(
        AppSettings settings,
        bool profileAvailable,
        ICollection<string> actions,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        if (!status.IsProcessRunning)
        {
            actions.Add("核心当前未运行，修复后的配置会在下次启动代理时生效。");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            warnings.Add("当前平台不是 Windows，未自动重启 TUN 核心。");
            return;
        }

        if (!_services.WindowsPrivilegeService.IsAdministrator())
        {
            warnings.Add("当前不是管理员权限，无法自动重启 TUN 核心。");
            return;
        }

        if (!File.Exists(settings.CorePath))
        {
            warnings.Add("未找到 mihomo 核心，无法自动重启。");
            return;
        }

        if (!profileAvailable)
        {
            warnings.Add("当前配置不可用，无法自动重启核心。");
            return;
        }

        try
        {
            await _services.CoreManager.RestartAsync(cancellationToken);
            actions.Add("核心已按当前 TUN 配置重启。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"核心重启失败：{ex.Message}");
            await _services.AppLog.ErrorAsync("TUN 一键修复重启核心失败。", ex, CancellationToken.None);
        }
    }

    private async Task<bool> IsCurrentProfileAvailableAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.CurrentProfile))
        {
            return false;
        }

        try
        {
            await _services.ProfileService.GetAsync(settings.CurrentProfile, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private void RenderTunHealthReport(TunHealthReport report)
    {
        var table = new Table()
            .RoundedBorder()
            .Title("TUN 健康检查")
            .AddColumn("项目")
            .AddColumn("状态")
            .AddColumn("说明");

        foreach (var item in report.Items)
        {
            table.AddRow(
                Markup.Escape(item.Name),
                FormatTunHealthStatus(item.Status),
                Markup.Escape(item.Message));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var errorCount = report.Items.Count(item => item.Status == TunHealthStatus.Error);
        var warningCount = report.Items.Count(item => item.Status == TunHealthStatus.Warning);
        var summary = errorCount == 0 && warningCount == 0
            ? "[green]TUN 健康状态良好。[/]"
            : $"[yellow]发现 {errorCount} 个错误、{warningCount} 个提醒。[/]";
        var repair = report.HasRepairableIssues
            ? $"[aqua]可自动修复 {report.RepairableIssueCount} 项。[/]"
            : "[grey]没有可自动修复项。[/]";

        AnsiConsole.MarkupLine($"{summary} {repair}");
    }

    private static void RenderTunRepairResult(TunRepairResult result)
    {
        var table = new Table()
            .RoundedBorder()
            .Title("一键修复结果")
            .AddColumn("结果")
            .AddColumn("说明");

        foreach (var action in result.Actions)
        {
            table.AddRow("[green]完成[/]", Markup.Escape(action));
        }

        foreach (var warning in result.Warnings)
        {
            table.AddRow("[yellow]提醒[/]", Markup.Escape(warning));
        }

        AnsiConsole.Write(table);
    }

    private Table BuildTunStatusTable(
        AppSettings settings,
        CoreStatus status,
        SystemProxyStatus proxyStatus)
    {
        var table = new Table()
            .RoundedBorder()
            .AddColumn("项目")
            .AddColumn("值");

        table.AddRow("接管方式", FormatProxyModeMarkup(settings.ProxyMode));
        table.AddRow("核心状态", status.IsRunning ? "[green]运行中[/]" : "[yellow]已停止[/]");
        table.AddRow("管理员权限", _services.WindowsPrivilegeService.IsAdministrator() ? "[green]是[/]" : "[red]否[/]");
        table.AddRow("TUN 配置", IsTunProxyMode(settings) ? "[green]启动时写入[/]" : "[grey]未启用[/]");
        table.AddRow("DNS 配置", IsTunProxyMode(settings) ? "[green]启动时写入[/]" : "[grey]未启用[/]");
        table.AddRow("域名嗅探", IsTunProxyMode(settings) ? "[green]启动时开启[/]" : "[grey]未启用[/]");
        table.AddRow("协议栈", FormatTunStackMarkup(settings.Tun.Stack));
        table.AddRow("网卡名称", Markup.Escape(settings.Tun.Device));
        table.AddRow("自动路由", FormatToggleMarkup(settings.Tun.AutoRoute));
        table.AddRow("DNS 劫持", FormatToggleMarkup(settings.Tun.DnsHijack));
        table.AddRow("严格路由", FormatToggleMarkup(settings.Tun.StrictRoute));
        table.AddRow("排除网段", $"{settings.Tun.RouteExcludeAddress.Count.ToString(CultureInfo.InvariantCulture)} 条");
        table.AddRow("系统代理", proxyStatus.IsManagedByPaprika ? "[yellow]Paprika 接管中[/]" : "[grey]未由 Paprika 接管[/]");
        table.AddRow("runtime.yaml", Markup.Escape(_services.Paths.RuntimeConfigPath));

        return table;
    }

    private IReadOnlyList<string> ReadTunDiagnosticLogs(int lineCount)
    {
        if (!File.Exists(_services.Paths.CoreLogPath))
        {
            return Array.Empty<string>();
        }

        string[] keywords = ["tun", "wintun", "route", "dns", "sniffer", "sniff", "firewall", "strict"];
        return CoreLogFile.ReadLinesShared(_services.Paths.CoreLogPath)
            .Where(line => keywords.Any(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .TakeLast(lineCount)
            .ToArray();
    }

    private async Task ConfigureTunStackAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var cancel = new TunStackOption("__paprika_cancel__", "返回");
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<TunStackOption>()
                .Title($"选择 TUN 协议栈 [grey](当前：{Markup.Escape(settings.Tun.Stack)}，Esc 返回上一层)[/]")
                .PageSize(5)
                .WrapAround()
                .AddCancelResult(cancel)
                .UseConverter(option => FormatTunStackChoice(option, settings.Tun.Stack))
                .AddChoices(
                    new TunStackOption("mixed", "Mixed"),
                    new TunStackOption("system", "System"),
                    new TunStackOption("gvisor", "gVisor")));

        if (ReferenceEquals(selected, cancel))
        {
            throw new MenuBackException();
        }

        if (string.Equals(settings.Tun.Stack, selected.Stack, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[yellow]TUN 协议栈已经是 {Markup.Escape(selected.DisplayName)}。[/]");
            return;
        }

        await UpdateTunSettingsAsync(
            tun =>
            {
                tun.Stack = selected.Stack;
                tun.StackCustomized = true;
            },
            $"TUN 协议栈已设置为 {selected.DisplayName}。",
            cancellationToken);
    }

    private async Task SetTunDeviceAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        AnsiConsole.MarkupLine($"当前网卡名称：{Markup.Escape(settings.Tun.Device)}");
        var device = AskText("请输入 TUN 网卡名称", settings.Tun.Device).Trim();

        await UpdateTunSettingsAsync(
            tun => tun.Device = device,
            $"TUN 网卡名称已设置为 {device}。",
            cancellationToken);
    }

    private async Task SetTunMtuAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var mtu = AskNumber("请输入 MTU", settings.Tun.Mtu, 576, 9000);

        await UpdateTunSettingsAsync(
            tun => tun.Mtu = mtu,
            $"TUN MTU 已设置为 {mtu}。",
            cancellationToken);
    }

    private async Task ToggleTunSettingAsync(
        string label,
        Func<TunSettings, bool> getter,
        Action<TunSettings, bool> setter,
        CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var cancel = "__paprika_cancel__";
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"{label} [grey](Esc 返回上一层)[/]")
                .PageSize(3)
                .WrapAround()
                .AddCancelResult(cancel)
                .AddChoices("开启", "关闭"));

        if (selected == cancel)
        {
            throw new MenuBackException();
        }

        var enabled = selected == "开启";
        if (getter(settings.Tun) == enabled)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(label)}已经是 {selected} 状态。[/]");
            return;
        }

        await UpdateTunSettingsAsync(
            tun => setter(tun, enabled),
            $"{label}已{selected}。",
            cancellationToken);
    }

    private async Task UpdateTunSettingsAsync(
        Action<TunSettings> update,
        string successMessage,
        CancellationToken cancellationToken)
    {
        await _services.SettingsService.UpdateAsync(value =>
        {
            update(value.Tun);
            value.Tun.Enabled = string.Equals(value.ProxyMode, "tun", StringComparison.OrdinalIgnoreCase);
        }, cancellationToken);
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        await _services.AppLog.InfoAsync(
            $"更新 TUN 设置：{successMessage} stack={settings.Tun.Stack}, autoRoute={settings.Tun.AutoRoute}, autoDetectInterface={settings.Tun.AutoDetectInterface}, dnsHijack={settings.Tun.DnsHijack}, strictRoute={settings.Tun.StrictRoute}, bypassLan={settings.Tun.BypassLan}, mtu={settings.Tun.Mtu}, device={settings.Tun.Device}",
            cancellationToken);

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(successMessage)}[/]");
        await OfferRestartForTunSettingsChangeAsync(cancellationToken);
    }

    private async Task OfferRestartForTunSettingsChangeAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        if (!IsTunProxyMode(settings))
        {
            AnsiConsole.MarkupLine("[grey]当前接管方式不是 TUN，设置会在切换到 TUN 后生效。[/]");
            return;
        }

        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        if (!status.IsProcessRunning)
        {
            AnsiConsole.MarkupLine("[grey]核心未运行，下次启动代理时生效。[/]");
            return;
        }

        if (AskYesNo("核心正在运行，是否立即重启 mihomo 使 TUN 设置生效？", defaultValue: true))
        {
            await RestartCoreForCurrentProxyModeAsync(cancellationToken);
        }
    }

    private async Task TunRouteExcludeMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var settings = await _services.SettingsService.LoadAsync(cancellationToken);
            if (!settings.Tun.BypassLan)
            {
                AnsiConsole.MarkupLine("[yellow]当前「绕过局域网」为关闭，排除网段会保存，但启动时不会写入 runtime.yaml。[/]");
                AnsiConsole.WriteLine();
            }

            var choices = BuildTunRouteExclusionChoices(settings.Tun.RouteExcludeAddress);
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<TunRouteExclusionChoice>()
                    .Title("TUN 排除网段 [grey](↑/↓ 选择，Enter 确认，Esc 返回上一层)[/]")
                    .PageSize(16)
                    .WrapAround()
                    .AddCancelResult(TunRouteExclusionChoice.Back())
                    .UseConverter(FormatTunRouteExclusionChoice)
                    .AddChoices(choices));

            if (selected.Kind == TunRouteExclusionChoiceKind.Back)
            {
                return;
            }

            if (selected.Kind == TunRouteExclusionChoiceKind.Separator)
            {
                continue;
            }

            if (selected.Kind == TunRouteExclusionChoiceKind.Add)
            {
                await AddTunRouteExcludeAsync(cancellationToken);
                continue;
            }

            await EditOrDeleteTunRouteExcludeAsync(selected.Value, cancellationToken);
        }
    }

    private async Task AddTunRouteExcludeAsync(CancellationToken cancellationToken)
    {
        var route = NormalizeTunRouteExclude(AskText("请输入要新增的排除网段，例如 192.168.50.0/24"));
        await UpdateTunRouteExcludesAsync(routes =>
        {
            if (routes.Contains(route, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("该排除网段已经存在。");
            }

            routes.Add(route);
        }, cancellationToken);

        AnsiConsole.MarkupLine($"[green]已新增排除网段：[/]{Markup.Escape(route)}");
    }

    private async Task EditOrDeleteTunRouteExcludeAsync(
        string route,
        CancellationToken cancellationToken)
    {
        var back = MenuItem.Back("↩️ 返回列表");
        var selected = PromptMenu(
            $"处理 {route}",
            back,
            new MenuItem("✏️ 编辑", ct => EditTunRouteExcludeAsync(route, ct)),
            new MenuItem("🗑️ 删除", ct => DeleteTunRouteExcludeAsync(route, ct)),
            back);

        if (selected.ShouldReturn)
        {
            return;
        }

        await RunMenuActionAsync(selected, cancellationToken);
    }

    private async Task EditTunRouteExcludeAsync(
        string oldRoute,
        CancellationToken cancellationToken)
    {
        var newRoute = NormalizeTunRouteExclude(AskText("请输入新的排除网段", oldRoute));
        await UpdateTunRouteExcludesAsync(routes =>
        {
            var index = routes.FindIndex(value => string.Equals(value, oldRoute, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException("原排除网段不存在，可能已经被修改。");
            }

            if (!string.Equals(oldRoute, newRoute, StringComparison.OrdinalIgnoreCase) &&
                routes.Contains(newRoute, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("该排除网段已经存在。");
            }

            routes[index] = newRoute;
        }, cancellationToken);

        AnsiConsole.MarkupLine($"[green]已更新排除网段：[/]{Markup.Escape(oldRoute)} -> {Markup.Escape(newRoute)}");
    }

    private async Task DeleteTunRouteExcludeAsync(
        string route,
        CancellationToken cancellationToken)
    {
        if (!AskYesNo($"确定删除排除网段「{route}」？", defaultValue: false))
        {
            return;
        }

        await UpdateTunRouteExcludesAsync(routes =>
        {
            routes.RemoveAll(value => string.Equals(value, route, StringComparison.OrdinalIgnoreCase));
        }, cancellationToken);

        AnsiConsole.MarkupLine($"[green]已删除排除网段：[/]{Markup.Escape(route)}");
    }

    private async Task UpdateTunRouteExcludesAsync(
        Action<List<string>> update,
        CancellationToken cancellationToken)
    {
        await _services.SettingsService.UpdateAsync(settings =>
        {
            var routes = settings.Tun.RouteExcludeAddress
                .Select(NormalizeTunRouteExclude)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            update(routes);
            settings.Tun.RouteExcludeAddress = routes;
            settings.Tun.Enabled = string.Equals(settings.ProxyMode, "tun", StringComparison.OrdinalIgnoreCase);
        }, cancellationToken);

        await _services.AppLog.InfoAsync("更新 TUN 排除网段。", cancellationToken);
        await OfferRestartForTunSettingsChangeAsync(cancellationToken);
    }

    private static IReadOnlyList<TunRouteExclusionChoice> BuildTunRouteExclusionChoices(
        IReadOnlyList<string> routes)
    {
        var choices = routes
            .Select(TunRouteExclusionChoice.Route)
            .ToList();

        choices.Add(TunRouteExclusionChoice.Separator());
        choices.Add(TunRouteExclusionChoice.Add());
        choices.Add(TunRouteExclusionChoice.Back());
        return choices;
    }

    private static string FormatTunRouteExclusionChoice(TunRouteExclusionChoice choice)
    {
        return choice.Kind switch
        {
            TunRouteExclusionChoiceKind.Route => Markup.Escape(choice.Value),
            TunRouteExclusionChoiceKind.Separator => "[grey]────────────────────────[/]",
            TunRouteExclusionChoiceKind.Add => "➕ 新增网段",
            _ => "↩️ 返回 TUN 设置"
        };
    }

    private static string NormalizeTunRouteExclude(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("排除网段不能为空。");
        }

        if (trimmed.Any(char.IsWhiteSpace) || trimmed.Contains(',') || trimmed.Contains(';'))
        {
            throw new InvalidOperationException("排除网段不能包含空格、逗号或分号。");
        }

        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex <= 0 || slashIndex == trimmed.Length - 1)
        {
            throw new InvalidOperationException("排除网段必须使用 CIDR 格式，例如 192.168.0.0/16 或 fc00::/7。");
        }

        var addressText = trimmed[..slashIndex];
        var prefixText = trimmed[(slashIndex + 1)..];
        if (!IPAddress.TryParse(addressText, out var address))
        {
            throw new InvalidOperationException("排除网段的 IP 地址无效。");
        }

        if (!int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefixLength))
        {
            throw new InvalidOperationException("排除网段的前缀长度无效。");
        }

        var maxPrefixLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
        {
            throw new InvalidOperationException($"排除网段的前缀长度必须在 0-{maxPrefixLength} 之间。");
        }

        return $"{address}/{prefixLength.ToString(CultureInfo.InvariantCulture)}";
    }

    private async Task ExternalResourcesMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var resources = await _services.ExternalResourceService.ListAsync(cancellationToken);
            RenderExternalResources(resources);

            var back = MenuItem.Back("↩️ 返回应用设置");
            var selected = PromptMenu(
                "选择操作",
                back,
                new MenuItem("🔄 同步资源", ct => SyncExternalResourceAsync(resources, ct)),
                new MenuItem("✏️ 编辑资源地址", ct => EditExternalResourceUrlAsync(resources, ct)),
                back);

            if (selected.ShouldReturn)
            {
                return;
            }

            await RunMenuActionAsync(selected, cancellationToken);
        }
    }

    private void RenderExternalResources(IReadOnlyList<ExternalResourceInfo> resources)
    {
        foreach (var resource in resources)
        {
            var sizeText = resource.SizeBytes is null ? "未下载" : FormatBytes(resource.SizeBytes.Value);
            var updatedText = resource.UpdatedAt is null ? "未下载" : FormatRelativeTime(resource.UpdatedAt);

            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(resource.Name)}[/]");
            AnsiConsole.MarkupLine($"{Markup.Escape(sizeText)} · {Markup.Escape(updatedText)}");
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(resource.Url)}[/]");
            AnsiConsole.WriteLine();
        }
    }

    private async Task SyncExternalResourceAsync(
        IReadOnlyList<ExternalResourceInfo> resources,
        CancellationToken cancellationToken)
    {
        var selected = SelectExternalResource(resources, "选择要同步的资源", includeAll: true);
        if (selected.Id == "__paprika_cancel__")
        {
            throw new MenuBackException();
        }

        var targets = selected.Id == "__paprika_all__"
            ? resources
            : resources.Where(resource => resource.Id == selected.Id).ToArray();

        if (!AskYesNo($"是否同步 {FormatExternalResourceTargetName(selected)}？", defaultValue: false))
        {
            return;
        }

        var failures = new List<string>();
        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async context =>
            {
                foreach (var resource in targets)
                {
                    var task = context.AddTask($"同步 {resource.Name}", maxValue: 100);
                    var progress = new Progress<double>(value =>
                    {
                        task.Value = Math.Clamp(value, 0, 100);
                    });

                    try
                    {
                        // 外部资源直接同步到 mihomo 的 -d 数据目录，运行时可立即读取。
                        await _services.ExternalResourceService.DownloadAsync(
                            resource.Id,
                            progress,
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures.Add($"{resource.Name}：{ex.Message}");
                    }
                    finally
                    {
                        task.Value = 100;
                        task.StopTask();
                    }
                }
            });

        if (failures.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]外部资源已同步。[/]");
            return;
        }

        AnsiConsole.MarkupLine("[yellow]部分外部资源同步失败：[/]");
        foreach (var failure in failures)
        {
            AnsiConsole.MarkupLine($"[yellow]-[/] {Markup.Escape(failure)}");
        }
    }

    private async Task EditExternalResourceUrlAsync(
        IReadOnlyList<ExternalResourceInfo> resources,
        CancellationToken cancellationToken)
    {
        var selected = SelectExternalResource(resources, "选择要编辑的资源", includeAll: false);
        if (selected.Id == "__paprika_cancel__")
        {
            throw new MenuBackException();
        }

        AnsiConsole.MarkupLine($"当前地址：{Markup.Escape(selected.Url)}");
        var newUrl = AskText($"请输入 {selected.Name} 新地址", selected.Url).Trim();

        await _services.ExternalResourceService.UpdateUrlAsync(
            selected.Id,
            newUrl,
            cancellationToken);

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(selected.Name)} 地址已更新。[/]");
    }

    private static ExternalResourceInfo SelectExternalResource(
        IReadOnlyList<ExternalResourceInfo> resources,
        string title,
        bool includeAll)
    {
        var cancel = new ExternalResourceInfo(
            "__paprika_cancel__",
            "返回",
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            null);
        var choices = new List<ExternalResourceInfo>();

        if (includeAll)
        {
            choices.Add(new ExternalResourceInfo(
                "__paprika_all__",
                "全部资源",
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                null));
        }

        choices.AddRange(resources);

        return AnsiConsole.Prompt(
            new SelectionPrompt<ExternalResourceInfo>()
                .Title($"{title} [grey](Esc 返回上一层)[/]")
                .PageSize(8)
                .WrapAround()
                .AddCancelResult(cancel)
                .UseConverter(FormatExternalResourceChoice)
                .AddChoices(choices));
    }

    private static string FormatExternalResourceChoice(ExternalResourceInfo resource)
    {
        if (resource.Id is "__paprika_all__" or "__paprika_cancel__")
        {
            return Markup.Escape(resource.Name);
        }

        var sizeText = resource.SizeBytes is null ? "未下载" : FormatBytes(resource.SizeBytes.Value);
        var updatedText = resource.UpdatedAt is null ? "未下载" : FormatRelativeTime(resource.UpdatedAt);
        return $"{Markup.Escape(resource.Name)} [grey]({Markup.Escape(sizeText)}，{Markup.Escape(updatedText)})[/]";
    }

    private static string FormatExternalResourceTargetName(ExternalResourceInfo resource)
    {
        return resource.Id == "__paprika_all__"
            ? "全部外部资源"
            : $"资源「{resource.Name}」";
    }

    private async Task SystemProxyExcludedDomainsMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TryClearScreen();
            await RenderHeaderAsync(cancellationToken);

            var settings = await _services.SettingsService.LoadAsync(cancellationToken);
            var choices = BuildSystemProxyExclusionChoices(settings.SystemProxyExcludedDomains);
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<SystemProxyExclusionChoice>()
                    .Title("系统代理排除域名 [grey](↑/↓ 选择，Enter 确认，Esc 返回上一层)[/]")
                    .PageSize(16)
                    .WrapAround()
                    .AddCancelResult(SystemProxyExclusionChoice.Back())
                    .UseConverter(FormatSystemProxyExclusionChoice)
                    .AddChoices(choices));

            if (selected.Kind == SystemProxyExclusionChoiceKind.Back)
            {
                return;
            }

            if (selected.Kind == SystemProxyExclusionChoiceKind.Separator)
            {
                continue;
            }

            if (selected.Kind == SystemProxyExclusionChoiceKind.Add)
            {
                await AddSystemProxyExcludedDomainAsync(cancellationToken);
                continue;
            }

            await EditOrDeleteSystemProxyExcludedDomainAsync(selected.Value, cancellationToken);
        }
    }

    private async Task AddSystemProxyExcludedDomainAsync(CancellationToken cancellationToken)
    {
        var domain = NormalizeSystemProxyExcludedDomain(AskText("请输入要新增的排除域名"));
        await UpdateSystemProxyExcludedDomainsAsync(domains =>
        {
            if (domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("该排除域名已经存在。");
            }

            domains.Add(domain);
        }, cancellationToken);

        AnsiConsole.MarkupLine($"[green]已新增排除域名：[/]{Markup.Escape(domain)}");
    }

    private async Task EditOrDeleteSystemProxyExcludedDomainAsync(
        string domain,
        CancellationToken cancellationToken)
    {
        var back = MenuItem.Back("↩️ 返回列表");
        var selected = PromptMenu(
            $"处理 {domain}",
            back,
            new MenuItem("✏️ 编辑", ct => EditSystemProxyExcludedDomainAsync(domain, ct)),
            new MenuItem("🗑️ 删除", ct => DeleteSystemProxyExcludedDomainAsync(domain, ct)),
            back);

        if (selected.ShouldReturn)
        {
            return;
        }

        await RunMenuActionAsync(selected, cancellationToken);
    }

    private async Task EditSystemProxyExcludedDomainAsync(
        string oldDomain,
        CancellationToken cancellationToken)
    {
        var newDomain = NormalizeSystemProxyExcludedDomain(AskText("请输入新的排除域名", oldDomain));
        await UpdateSystemProxyExcludedDomainsAsync(domains =>
        {
            var index = domains.FindIndex(value => string.Equals(value, oldDomain, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException("原排除域名不存在，可能已经被修改。");
            }

            if (!string.Equals(oldDomain, newDomain, StringComparison.OrdinalIgnoreCase) &&
                domains.Contains(newDomain, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("该排除域名已经存在。");
            }

            domains[index] = newDomain;
        }, cancellationToken);

        AnsiConsole.MarkupLine($"[green]已更新排除域名：[/]{Markup.Escape(oldDomain)} -> {Markup.Escape(newDomain)}");
    }

    private async Task DeleteSystemProxyExcludedDomainAsync(
        string domain,
        CancellationToken cancellationToken)
    {
        if (!AskYesNo($"确定删除排除域名「{domain}」？", defaultValue: false))
        {
            return;
        }

        await UpdateSystemProxyExcludedDomainsAsync(domains =>
        {
            domains.RemoveAll(value => string.Equals(value, domain, StringComparison.OrdinalIgnoreCase));
        }, cancellationToken);

        AnsiConsole.MarkupLine($"[green]已删除排除域名：[/]{Markup.Escape(domain)}");
    }

    private async Task UpdateSystemProxyExcludedDomainsAsync(
        Action<List<string>> update,
        CancellationToken cancellationToken)
    {
        await _services.SettingsService.UpdateAsync(settings =>
        {
            var domains = settings.SystemProxyExcludedDomains
                .Select(NormalizeSystemProxyExcludedDomain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            update(domains);
            settings.SystemProxyExcludedDomains = domains;
        }, cancellationToken);

        await RefreshManagedSystemProxyAsync(cancellationToken);
    }

    private async Task RefreshManagedSystemProxyAsync(CancellationToken cancellationToken)
    {
        var status = await _services.SystemProxyService.GetStatusAsync(cancellationToken);
        if (!status.IsSupported || !status.IsManagedByPaprika)
        {
            return;
        }

        // 已接管时再次 Enable 只刷新 ProxyOverride，不覆盖最初备份的用户代理设置。
        await _services.SystemProxyService.EnableAsync(cancellationToken);
    }

    private static IReadOnlyList<SystemProxyExclusionChoice> BuildSystemProxyExclusionChoices(
        IReadOnlyList<string> domains)
    {
        var choices = domains
            .Select(SystemProxyExclusionChoice.Domain)
            .ToList();

        choices.Add(SystemProxyExclusionChoice.Separator());
        choices.Add(SystemProxyExclusionChoice.Add());
        choices.Add(SystemProxyExclusionChoice.Back());
        return choices;
    }

    private static string FormatSystemProxyExclusionChoice(SystemProxyExclusionChoice choice)
    {
        return choice.Kind switch
        {
            SystemProxyExclusionChoiceKind.Domain => Markup.Escape(choice.Value),
            SystemProxyExclusionChoiceKind.Separator => "[grey]────────────────────────[/]",
            SystemProxyExclusionChoiceKind.Add => "➕ 新增域名",
            _ => "↩️ 退出"
        };
    }

    private static string NormalizeSystemProxyExcludedDomain(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("排除域名不能为空。");
        }

        if (trimmed.Contains(';'))
        {
            throw new InvalidOperationException("排除域名不能包含分号。");
        }

        return trimmed;
    }

    private async Task ShowRecentLogsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AnsiConsole.Write(CreateCoreLogPanel("mihomo.log 最新 100 条"));
    }

    private async Task ShowRecentAppLogsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AnsiConsole.Write(CreatePlainLogPanel(_services.Paths.AppLogPath, "paprika.log 最新 100 条", "还没有 Paprika 应用日志。"));
    }

    private async Task TailLogsAsync(CancellationToken cancellationToken)
    {
        await AnsiConsole.Live(CreateCoreLogPanel("mihomo.log 实时刷新 (Esc 返回)"))
            .AutoClear(false)
            .StartAsync(async context =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (TryConsumeEscapeKey())
                    {
                        throw new MenuBackException();
                    }

                    // 轮询比文件监听更稳：日志轮转或进入页面后才创建文件也能正常刷新。
                    context.UpdateTarget(CreateCoreLogPanel("mihomo.log 实时刷新 (Esc 返回)"));
                    await Task.Delay(700, cancellationToken);
                }
            });
    }

    private async Task TailAppLogsAsync(CancellationToken cancellationToken)
    {
        await AnsiConsole.Live(CreatePlainLogPanel(_services.Paths.AppLogPath, "paprika.log 实时刷新 (Esc 返回)", "还没有 Paprika 应用日志。"))
            .AutoClear(false)
            .StartAsync(async context =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (TryConsumeEscapeKey())
                    {
                        throw new MenuBackException();
                    }

                    context.UpdateTarget(CreatePlainLogPanel(_services.Paths.AppLogPath, "paprika.log 实时刷新 (Esc 返回)", "还没有 Paprika 应用日志。"));
                    await Task.Delay(700, cancellationToken);
                }
            });
    }

    private Panel CreateCoreLogPanel(string header)
    {
        const int lineCount = 100;

        try
        {
            if (!File.Exists(_services.Paths.CoreLogPath))
            {
                return new Panel(new Markup("[yellow]还没有 mihomo 日志。启动核心后会写入这里。[/]"))
                    .Header(header)
                    .RoundedBorder()
                    .Expand();
            }

            var entries =
                CoreLogFile.ReadLinesShared(_services.Paths.CoreLogPath)
                    .TakeLast(lineCount)
                    .Select(ParseCoreLogEntry)
                    .ToArray();

            if (entries.Length == 0)
            {
                return new Panel(new Markup("[grey]日志文件为空。[/]"))
                    .Header(header)
                    .RoundedBorder()
                    .Expand();
            }

            var table = new Table()
                .Border(TableBorder.None)
                .Expand()
                .AddColumn(new TableColumn("[grey]时间[/]").NoWrap())
                .AddColumn(new TableColumn("[grey]级别[/]").NoWrap())
                .AddColumn("[grey]内容[/]");

            foreach (var entry in entries)
            {
                table.AddRow(
                    FormatLogTime(entry.Timestamp),
                    FormatLogLevel(entry.Level),
                    FormatLogMessage(entry.Message));
            }

            return new Panel(table)
                .Header(header)
                .RoundedBorder()
                .Expand();
        }
        catch (Exception ex)
        {
            return new Panel(new Markup($"[yellow]读取日志失败：[/]{Markup.Escape(ex.Message)}"))
                .Header(header)
                .RoundedBorder()
                .Expand();
        }
    }

    private static Panel CreatePlainLogPanel(string logPath, string header, string emptyMessage)
    {
        const int lineCount = 100;

        try
        {
            if (!File.Exists(logPath))
            {
                return new Panel(new Markup($"[yellow]{Markup.Escape(emptyMessage)}[/]"))
                    .Header(header)
                    .RoundedBorder()
                    .Expand();
            }

            var lines = CoreLogFile.ReadLinesShared(logPath).TakeLast(lineCount).ToArray();
            if (lines.Length == 0)
            {
                return new Panel(new Markup("[grey]日志文件为空。[/]"))
                    .Header(header)
                    .RoundedBorder()
                    .Expand();
            }

            var rows = lines.Select(line => new Markup(ColorizeAppLogLine(line))).Cast<IRenderable>().ToArray();
            return new Panel(new Rows(rows))
                .Header(header)
                .RoundedBorder()
                .Expand();
        }
        catch (Exception ex)
        {
            return new Panel(new Markup($"[yellow]读取日志失败：[/]{Markup.Escape(ex.Message)}"))
                .Header(header)
                .RoundedBorder()
                .Expand();
        }
    }

    private static string ColorizeAppLogLine(string line)
    {
        var escaped = Markup.Escape(line);
        return escaped
            .Replace("[[INFO]]", "[dodgerblue1][[INFO]][/]", StringComparison.Ordinal)
            .Replace("[[WARN]]", "[yellow][[WARN]][/]", StringComparison.Ordinal)
            .Replace("[[ERROR]]", "[bold red][[ERROR]][/]", StringComparison.Ordinal)
            .Replace("TUN", "[aqua]TUN[/]", StringComparison.Ordinal)
            .Replace("system-proxy", "[lime]system-proxy[/]", StringComparison.Ordinal)
            .Replace("mihomo", "[green]mihomo[/]", StringComparison.Ordinal)
            .Replace("失败", "[red]失败[/]", StringComparison.Ordinal);
    }

    private static CoreLogEntry ParseCoreLogEntry(string line)
    {
        var timestamp = TryReadOuterLogTimestamp(line, out var payload)
            ?? TryParseLogTimestamp(ReadLogField(payload, "time"));
        var level = ReadLogField(payload, "level") ?? "info";
        var message = ReadLogField(payload, "msg") ?? payload;

        return new CoreLogEntry(timestamp, level, message);
    }

    private static DateTimeOffset? TryReadOuterLogTimestamp(string line, out string payload)
    {
        payload = line;
        if (!line.StartsWith('['))
        {
            return null;
        }

        var end = line.IndexOf(']');
        if (end <= 1)
        {
            return null;
        }

        var candidate = line.Substring(1, end - 1);
        payload = line[(end + 1)..].TrimStart();
        return DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, out var timestamp)
            ? timestamp
            : null;
    }

    private static DateTimeOffset? TryParseLogTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // mihomo 可能输出纳秒时间；解析失败时保留外层或空时间，不影响日志展示。
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, out var timestamp)
            ? timestamp
            : null;
    }

    private static string? ReadLogField(string text, string fieldName)
    {
        var key = $"{fieldName}=";
        var start = text.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += key.Length;
        if (start >= text.Length)
        {
            return string.Empty;
        }

        if (text[start] != '"')
        {
            var end = text.IndexOf(' ', start);
            return end < 0 ? text[start..] : text[start..end];
        }

        var builder = new StringBuilder();
        for (var index = start + 1; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '"')
            {
                return builder.ToString();
            }

            if (current == '\\' && index + 1 < text.Length)
            {
                var next = text[++index];
                builder.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => next
                });
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static string FormatLogTime(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return "[grey]-[/]";
        }

        var local = timestamp.Value.ToLocalTime();
        var format = local.Date == DateTimeOffset.Now.Date ? "HH:mm:ss" : "MM-dd HH:mm:ss";
        return $"[grey]{Markup.Escape(local.ToString(format, CultureInfo.CurrentCulture))}[/]";
    }

    private static string FormatLogLevel(string level)
    {
        return level.Trim().ToLowerInvariant() switch
        {
            "error" => "[bold red]ERROR[/]",
            "warning" or "warn" => "[yellow]WARN[/]",
            "debug" => "[grey]DEBUG[/]",
            "info" => "[dodgerblue1]INFO[/]",
            var value => $"[grey]{Markup.Escape(value.ToUpperInvariant())}[/]"
        };
    }

    private static string FormatLogMessage(string message)
    {
        var text = EscapeDisplayText(message);

        // 保留原始日志内容，只给协议、路由结果和错误关键词做醒目着色。
        text = text
            .Replace("[[TCP]]", "[grey][[TCP]][/]", StringComparison.Ordinal)
            .Replace("[[UDP]]", "[grey][[UDP]][/]", StringComparison.Ordinal)
            .Replace("DIRECT", "[aqua]DIRECT[/]", StringComparison.Ordinal)
            .Replace("REJECT", "[red]REJECT[/]", StringComparison.Ordinal)
            .Replace("GLOBAL", "[yellow]GLOBAL[/]", StringComparison.Ordinal)
            .Replace("RuleSet", "[green]RuleSet[/]", StringComparison.Ordinal)
            .Replace(" match ", " [grey]match[/] ", StringComparison.Ordinal)
            .Replace(" using ", " [grey]using[/] ", StringComparison.Ordinal)
            .Replace(" error:", " [red]error:[/]", StringComparison.Ordinal)
            .Replace(" timeout", " [red]timeout[/]", StringComparison.Ordinal);

        return text;
    }

    private static Table BuildTunRuntimeConfigTable(YamlMappingNode root)
    {
        var table = new Table()
            .RoundedBorder()
            .Title("运行时关键配置")
            .AddColumn("配置项")
            .AddColumn("值");

        AddRuntimeScalarRow(table, root, "mixed-port", "mixed-port");
        AddRuntimeScalarRow(table, root, "external-controller", "external-controller");
        AddRuntimeScalarRow(table, root, "mode", "mode");
        AddRuntimeScalarRow(table, root, "tun.enable", "tun", "enable");
        AddRuntimeScalarRow(table, root, "tun.stack", "tun", "stack");
        AddRuntimeScalarRow(table, root, "tun.device", "tun", "device");
        AddRuntimeScalarRow(table, root, "tun.auto-route", "tun", "auto-route");
        AddRuntimeScalarRow(table, root, "tun.auto-detect-interface", "tun", "auto-detect-interface");
        AddRuntimeSequenceRow(table, root, "tun.dns-hijack", "tun", "dns-hijack");
        AddRuntimeScalarRow(table, root, "tun.strict-route", "tun", "strict-route");
        AddRuntimeScalarRow(table, root, "tun.mtu", "tun", "mtu");
        AddRuntimeSequenceRow(table, root, "tun.route-exclude-address", "tun", "route-exclude-address");
        AddRuntimeScalarRow(table, root, "dns.enable", "dns", "enable");
        AddRuntimeScalarRow(table, root, "dns.enhanced-mode", "dns", "enhanced-mode");
        AddRuntimeScalarRow(table, root, "dns.fake-ip-range", "dns", "fake-ip-range");
        AddRuntimeSequenceRow(table, root, "dns.nameserver", "dns", "nameserver");
        AddRuntimeSequenceRow(table, root, "dns.proxy-server-nameserver", "dns", "proxy-server-nameserver");
        AddRuntimeScalarRow(table, root, "sniffer.enable", "sniffer", "enable");
        AddRuntimeScalarRow(table, root, "sniffer.force-dns-mapping", "sniffer", "force-dns-mapping");
        AddRuntimeScalarRow(table, root, "sniffer.parse-pure-ip", "sniffer", "parse-pure-ip");
        AddRuntimeScalarRow(table, root, "sniffer.override-destination", "sniffer", "override-destination");
        AddRuntimeSequenceRow(table, root, "sniffer.sniff.HTTP.ports", "sniffer", "sniff", "HTTP", "ports");
        AddRuntimeSequenceRow(table, root, "sniffer.sniff.TLS.ports", "sniffer", "sniff", "TLS", "ports");
        AddRuntimeSequenceRow(table, root, "sniffer.sniff.QUIC.ports", "sniffer", "sniff", "QUIC", "ports");

        return table;
    }

    private static void AddRuntimeScalarRow(
        Table table,
        YamlMappingNode root,
        string label,
        params string[] path)
    {
        table.AddRow(
            Markup.Escape(label),
            FormatRuntimeConfigValue(ReadYamlScalar(root, path)));
    }

    private static void AddRuntimeSequenceRow(
        Table table,
        YamlMappingNode root,
        string label,
        params string[] path)
    {
        var values = ReadYamlSequence(root, path);
        var value = values.Count == 0 ? null : string.Join(", ", values);
        table.AddRow(
            Markup.Escape(label),
            FormatRuntimeConfigValue(value));
    }

    private static string FormatRuntimeConfigValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "[grey]未写入[/]";
        }

        var normalized = value.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "true" => "[green]true[/]",
            "false" => "[yellow]false[/]",
            "rule" => "[lime]rule[/]",
            "global" => "[yellow]global[/]",
            "direct" => "[red]direct[/]",
            _ => Markup.Escape(TruncateDisplayText(normalized, 120))
        };
    }

    private static bool TryConsumeEscapeKey()
    {
        if (!Console.KeyAvailable)
        {
            return false;
        }

        return Console.ReadKey(intercept: true).Key == ConsoleKey.Escape;
    }

    private Task ShowLogPathAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var table = new Table()
            .RoundedBorder()
            .AddColumn("日志")
            .AddColumn("路径");

        table.AddRow("mihomo 核心日志", Markup.Escape(_services.Paths.CoreLogPath));
        table.AddRow("Paprika 应用日志", Markup.Escape(_services.Paths.AppLogPath));

        AnsiConsole.Write(table);
        return Task.CompletedTask;
    }

    private async Task ShowSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);

        var table = new Table()
            .RoundedBorder()
            .AddColumn("项目")
            .AddColumn("值");

        table.AddRow("数据目录", Markup.Escape(_services.Paths.AppDataDirectory));
        table.AddRow("当前配置", Markup.Escape(settings.CurrentProfile ?? "-"));
        table.AddRow("内核路径", Markup.Escape(string.IsNullOrWhiteSpace(settings.CorePath) ? "-" : settings.CorePath));
        table.AddRow("mixed-port", settings.MixedPort.ToString());
        table.AddRow("external-controller", Markup.Escape($"{settings.ControllerHost}:{settings.ControllerPort}"));
        table.AddRow("接管方式", FormatProxyModeMarkup(settings.ProxyMode));
        table.AddRow("运行模式", Markup.Escape(FormatRunModeText(settings.RunMode)));
        table.AddRow("TUN 协议栈", FormatTunStackMarkup(settings.Tun.Stack));
        table.AddRow("TUN 网卡", Markup.Escape(settings.Tun.Device));
        table.AddRow("TUN 排除网段", $"{settings.Tun.RouteExcludeAddress.Count.ToString(CultureInfo.InvariantCulture)} 条");
        table.AddRow("系统代理", settings.SystemProxyEnabled ? "[green]Paprika 已开启[/]" : "[grey]Paprika 未开启[/]");
        table.AddRow("系统代理排除域名", $"{settings.SystemProxyExcludedDomains.Count} 条");
        table.AddRow("切换节点后自动关闭连接", settings.AutoCloseConnectionsOnNodeSwitch ? "[green]开启[/]" : "[grey]关闭[/]");
        table.AddRow("后台运行提示", settings.ShowRunInBackgroundPrompt ? "[green]开启[/]" : "[grey]关闭[/]");

        AnsiConsole.Write(table);
    }

    private async Task EnsureStartupRequirementsAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(settings.CorePath) || !File.Exists(settings.CorePath))
        {
            AnsiConsole.MarkupLine("[yellow]默认 mihomo 内核路径下还没有可用文件。[/]");
            AnsiConsole.MarkupLine($"默认路径：{Markup.Escape(_services.Paths.DefaultCorePath)}");
            throw new InvalidOperationException("启动前必须先进入「核心管理」>「下载/更新核心」，把 mihomo 安装到默认目录。");
        }

        if (string.IsNullOrWhiteSpace(settings.CurrentProfile))
        {
            AnsiConsole.MarkupLine("[yellow]尚未导入可用配置。[/]");
            if (AskYesNo("现在导入一个本地配置？", defaultValue: true))
            {
                await ImportProfileAsync(cancellationToken);
            }
        }

        settings = await _services.SettingsService.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.CurrentProfile))
        {
            throw new InvalidOperationException("启动前必须先导入并选择一个配置。");
        }
    }

    private void EnsureTunStartupRequirements(AppSettings settings)
    {
        if (!IsTunProxyMode(settings))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("当前第一版 TUN 模式只支持 Windows。");
        }

        if (!_services.WindowsPrivilegeService.IsAdministrator())
        {
            throw new InvalidOperationException("TUN 模式需要管理员权限。请右键 Paprika.exe，选择“以管理员身份运行”，然后重新启动代理。");
        }
    }

    private async Task RenderHeaderAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new FigletText("Paprika").Color(Color.IndianRed));

        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        var proxyStatus = await _services.SystemProxyService.GetStatusAsync(cancellationToken);

        AnsiConsole.Write(CreateStatusPanel(settings, status, proxyStatus));
    }

    private Panel CreateStatusPanel(
        AppSettings settings,
        CoreStatus status,
        SystemProxyStatus proxyStatus)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();

        var corePath = string.IsNullOrWhiteSpace(settings.CorePath)
            ? _services.Paths.DefaultCorePath
            : settings.CorePath;
        var coreExists = File.Exists(corePath);

        var isTunMode = IsTunProxyMode(settings);

        grid.AddRow("核心", FormatCoreHomeStatus(status));
        grid.AddRow("当前配置", Markup.Escape(settings.CurrentProfile ?? "-"));
        grid.AddRow("进程 ID", status.ProcessId?.ToString() ?? "-");
        grid.AddRow("核心文件", coreExists ? "[green]已找到[/]" : "[red]缺失[/]");
        grid.AddRow("接管方式", FormatProxyModeMarkup(settings.ProxyMode));
        grid.AddRow("TUN 状态", FormatTunHomeStatus(settings, status));
        if (isTunMode)
        {
            grid.AddRow("TUN 协议栈", FormatTunStackMarkup(settings.Tun.Stack));
            grid.AddRow("TUN 网卡", Markup.Escape(settings.Tun.Device));
        }

        grid.AddRow("系统代理", proxyStatus.IsEnabled ? "[green]开启[/]" : "[grey]关闭[/]");
        grid.AddRow("数据目录", Markup.Escape(_services.Paths.AppDataDirectory));

        var panelContent = new Rows(grid);
        if (!coreExists)
        {
            // 首页直接提示缺少核心，降低首次使用时的迷路感。
            panelContent = new Rows(
                grid,
                new Markup("[red]未找到 mihomo 核心。请进入「核心管理」>「下载/更新核心」。[/]"));
        }

        return new Panel(panelContent).Header("状态").RoundedBorder();
    }

    private static Panel CreateTrafficRatePanel(MihomoTrafficRate trafficRate)
    {
        const int minimumWidth = 28;

        if (!trafficRate.IsAvailable)
        {
            var stoppedGrid = new Grid().Width(minimumWidth);
            stoppedGrid.AddColumn();
            stoppedGrid.AddRow($"[yellow]{Markup.Escape(trafficRate.ErrorMessage ?? "网络速率不可用")}[/]");

            return new Panel(stoppedGrid)
                .Header("实时网络速率")
                .RoundedBorder();
        }

        var grid = new Grid().Width(minimumWidth);
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("上传", $"[aqua]↑ {Markup.Escape(FormatBytes(trafficRate.UploadBytesPerSecond))}/s[/]");
        grid.AddRow("下载", $"[lime]↓ {Markup.Escape(FormatBytes(trafficRate.DownloadBytesPerSecond))}/s[/]");

        return new Panel(grid)
            .Header("实时网络速率")
            .RoundedBorder();
    }

    private async Task ShowNonInteractiveSummaryAsync(CancellationToken cancellationToken)
    {
        var settings = await _services.SettingsService.LoadAsync(cancellationToken);
        var status = await _services.CoreManager.GetStatusAsync(cancellationToken);
        var proxyStatus = await _services.SystemProxyService.GetStatusAsync(cancellationToken);

        AnsiConsole.Write(CreateStatusPanel(settings, status, proxyStatus));
        AnsiConsole.MarkupLine("[yellow]当前宿主不是交互式控制台，无法显示方向键菜单。请双击 exe 或在终端中运行。[/]");
    }

    private async Task RunMenuActionAsync(MenuItem selected, CancellationToken cancellationToken)
    {
        if (selected.IsSubMenu)
        {
            // 子菜单自行处理 Esc/返回；这里不再追加暂停，避免用户感觉要按第二次键。
            await selected.RunAsync(cancellationToken);
            return;
        }

        TryClearScreen();
        await RenderHeaderAsync(cancellationToken);

        try
        {
            await selected.RunAsync(cancellationToken);
        }
        catch (MenuBackException)
        {
            // 三级操作里的 Esc 只关闭当前操作，外层循环会重绘原来的二级菜单。
            return;
        }
        catch (Exception ex)
        {
            await _services.AppLog.ErrorAsync($"菜单操作失败：{selected.Label}", ex, cancellationToken);
            AnsiConsole.MarkupLine($"[red]操作失败：[/]{Markup.Escape(ex.Message)}");
        }

        PauseForResult();
    }

    private static MenuItem PromptMenu(string title, MenuItem cancelResult, params MenuItem[] items)
    {
        // SelectionPrompt 支持方向键和回车；AddCancelResult 将 Esc 映射为调用方传入的返回动作。
        var pageSize = Math.Clamp(items.Length, 10, 18);
        return AnsiConsole.Prompt(
            new SelectionPrompt<MenuItem>()
                .Title($"{title} [grey](↑/↓ 选择，Enter 确认，Esc 返回)[/]")
                .PageSize(pageSize)
                .WrapAround()
                .AddCancelResult(cancelResult)
                .HighlightStyle(new Style(Color.Black, Color.IndianRed, Decoration.Bold))
                .UseConverter(item => item.MarkupLabel ?? Markup.Escape(item.Label))
                .AddChoices(items));
    }

    private static void AddTunSettingRepairActions(AppSettings settings, ICollection<string> actions)
    {
        if (!IsTunProxyMode(settings))
        {
            actions.Add("接管方式已切换为 TUN 模式。");
        }

        if (!settings.Tun.AutoRoute)
        {
            actions.Add("已开启自动路由。");
        }

        if (!settings.Tun.AutoDetectInterface)
        {
            actions.Add("已开启自动选择出口网卡。");
        }

        if (!settings.Tun.DnsHijack)
        {
            actions.Add("已开启 DNS 劫持。");
        }

        if (settings.Tun.StrictRoute)
        {
            actions.Add("已关闭严格路由。");
        }

        if (!settings.Tun.BypassLan || !ContainsDefaultTunRouteExcludes(settings.Tun.RouteExcludeAddress))
        {
            actions.Add("已开启局域网绕过，并补齐常见局域网网段。");
        }

        if (settings.Tun.Mtu is < 576 or > 9000)
        {
            actions.Add("MTU 已恢复为 1500。");
        }

        if (string.IsNullOrWhiteSpace(settings.Tun.Device))
        {
            actions.Add("TUN 网卡名称已恢复为 Paprika。");
        }
    }

    private static bool IsPaprikaLocalProxy(SystemProxyStatus proxyStatus, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(proxyStatus.ProxyServer))
        {
            return false;
        }

        var localPort = settings.MixedPort.ToString(CultureInfo.InvariantCulture);
        return proxyStatus.ProxyServer.Contains($"127.0.0.1:{localPort}", StringComparison.OrdinalIgnoreCase) ||
               proxyStatus.ProxyServer.Contains($"localhost:{localPort}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsDefaultTunRouteExcludes(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return false;
        }

        var existing = new HashSet<string>(
            values
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);

        return new TunSettings().RouteExcludeAddress.All(existing.Contains);
    }

    private static List<string> MergeDefaultTunRouteExcludes(IEnumerable<string>? values)
    {
        var merged = new List<string>();

        if (values is not null)
        {
            foreach (var value in values)
            {
                var trimmed = value.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) &&
                    !merged.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    merged.Add(trimmed);
                }
            }
        }

        foreach (var defaultValue in new TunSettings().RouteExcludeAddress)
        {
            if (!merged.Contains(defaultValue, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(defaultValue);
            }
        }

        return merged;
    }

    private static string? ReadYamlScalar(YamlMappingNode root, params string[] path)
    {
        var node = ReadYamlNode(root, path);
        return node is YamlScalarNode scalar ? scalar.Value : null;
    }

    private static bool? ReadYamlBool(YamlMappingNode root, params string[] path)
    {
        var value = ReadYamlScalar(root, path);
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static IReadOnlyList<string> ReadYamlSequence(YamlMappingNode root, params string[] path)
    {
        var node = ReadYamlNode(root, path);
        if (node is not YamlSequenceNode sequence)
        {
            return Array.Empty<string>();
        }

        return sequence.Children
            .OfType<YamlScalarNode>()
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static YamlNode? ReadYamlNode(YamlMappingNode root, params string[] path)
    {
        YamlNode current = root;
        foreach (var segment in path)
        {
            if (current is not YamlMappingNode mapping)
            {
                return null;
            }

            var key = mapping.Children.Keys
                .OfType<YamlScalarNode>()
                .FirstOrDefault(candidate => string.Equals(candidate.Value, segment, StringComparison.Ordinal));
            if (key is null)
            {
                return null;
            }

            current = mapping.Children[key];
        }

        return current;
    }

    private static string FormatTunHealthStatus(TunHealthStatus status)
    {
        return status switch
        {
            TunHealthStatus.Ok => "[green]正常[/]",
            TunHealthStatus.Warning => "[yellow]提醒[/]",
            TunHealthStatus.Error => "[red]错误[/]",
            _ => "[grey]信息[/]"
        };
    }

    private static string FormatRunModeChoice(RunModeOption option, string currentMode)
    {
        var current = string.Equals(option.Mode, currentMode, StringComparison.OrdinalIgnoreCase)
            ? "[green]*[/] "
            : "  ";
        return $"{current}{Markup.Escape(option.DisplayName)}";
    }

    private static string FormatProxyModeChoice(ProxyModeOption option, string currentMode)
    {
        var current = string.Equals(option.Mode, NormalizeProxyMode(currentMode), StringComparison.OrdinalIgnoreCase)
            ? "[green]*[/] "
            : "  ";
        return $"{current}{Markup.Escape(option.DisplayName)}";
    }

    private static string FormatTunStackChoice(TunStackOption option, string currentStack)
    {
        var current = string.Equals(option.Stack, NormalizeTunStack(currentStack), StringComparison.OrdinalIgnoreCase)
            ? "[green]*[/] "
            : "  ";
        return $"{current}{Markup.Escape(option.DisplayName)}";
    }

    private static string FormatProxyModeText(string? mode)
    {
        return NormalizeProxyMode(mode) == "tun" ? "TUN 模式" : "系统代理";
    }

    private static string FormatProxyModeMarkup(string? mode)
    {
        return NormalizeProxyMode(mode) == "tun" ? "[aqua]TUN 模式[/]" : "[lime]系统代理[/]";
    }

    private static string FormatCoreHomeStatus(CoreStatus status)
    {
        if (status.IsRunning)
        {
            return "[green]运行中[/]";
        }

        return status.IsProcessRunning ? "[yellow]未就绪[/]" : "[yellow]已停止[/]";
    }

    private static string FormatTunHomeStatus(AppSettings settings, CoreStatus status)
    {
        if (!IsTunProxyMode(settings))
        {
            return "[grey]未启用[/]";
        }

        if (status.IsRunning)
        {
            return "[green]运行中[/]";
        }

        return status.IsProcessRunning ? "[yellow]等待核心就绪[/]" : "[yellow]未运行[/]";
    }

    private static string FormatRunModeText(string? mode)
    {
        return NormalizeRunMode(mode) switch
        {
            "global" => "全局代理",
            "direct" => "全局直连",
            _ => "规则分流"
        };
    }

    private static string FormatRunModeMarkup(string? mode)
    {
        return NormalizeRunMode(mode) switch
        {
            "global" => "[yellow]全局代理[/]",
            "direct" => "[red]全局直连[/]",
            _ => "[lime]规则分流[/]"
        };
    }

    private static string FormatTunStackMarkup(string? stack)
    {
        return NormalizeTunStack(stack) switch
        {
            "system" => "[yellow]system[/]",
            "gvisor" => "[aqua]gvisor[/]",
            _ => "[lime]mixed[/]"
        };
    }

    private static string FormatToggleText(bool enabled)
    {
        return enabled ? "开启" : "关闭";
    }

    private static string FormatToggleMarkup(bool enabled)
    {
        return enabled ? "[lime]开启[/]" : "[red]关闭[/]";
    }

    private static bool IsTunProxyMode(AppSettings settings)
    {
        return string.Equals(NormalizeProxyMode(settings.ProxyMode), "tun", StringComparison.OrdinalIgnoreCase)
               && settings.Tun.Enabled;
    }

    private static string NormalizeProxyMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "tun" => "tun",
            _ => "system-proxy"
        };
    }

    private static string NormalizeRunMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
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

    private static string FormatRuleProviderChoice(MihomoRuleProviderInfo provider)
    {
        var ruleCount = provider.RuleCount is null
            ? "未知条目"
            : $"{provider.RuleCount.Value.ToString("N0", CultureInfo.CurrentCulture)} 个条目";
        var updatedAt = FormatRelativeTime(provider.UpdatedAt);

        return $"{EscapeDisplayText(provider.Name)} [grey]({Markup.Escape(provider.VehicleType)}，{Markup.Escape(provider.Behavior)}，{Markup.Escape(ruleCount)}，{Markup.Escape(updatedAt)})[/]";
    }

    private static string FormatProxyGroupChoice(MihomoProxyGroupInfo group)
    {
        return $"{EscapeDisplayText(group.Name)} [grey]({Markup.Escape(group.Type)}，当前：{EscapeDisplayText(group.Current)}，{group.Nodes.Count} 个)[/]";
    }

    private static string FormatProxyNodeChoice(MihomoProxyNodeInfo node)
    {
        var current = node.IsCurrent ? "[green]*[/] " : "  ";
        var details = new List<string> { Markup.Escape(node.Type) };

        if (node.DelayMs is not null)
        {
            details.Add($"延迟：{node.DelayMs}ms");
        }

        if (node.Alive == false)
        {
            details.Add("[red]不可用[/]");
        }
        else if (node.DelayMs is not null && node.Alive == true)
        {
            details.Add("[green]可用[/]");
        }

        return $"{current}{EscapeDisplayText(node.Name)} [grey]({string.Join("，", details)})[/]";
    }

    private static void RenderConnectionSnapshot(
        string title,
        MihomoConnectionSnapshot snapshot,
        IReadOnlyList<MihomoConnectionInfo> connections)
    {
        AnsiConsole.Write(BuildConnectionSnapshotRenderable(title, snapshot, connections));
    }

    private static Panel BuildConnectionSnapshotRenderable(
        string title,
        MihomoConnectionSnapshot snapshot,
        IReadOnlyList<MihomoConnectionInfo> connections)
    {
        var summary = new Markup(
            $"[grey]活动连接 {connections.Count} 个 · 总上传 {Markup.Escape(FormatBytes(snapshot.UploadBytes))} · 总下载 {Markup.Escape(FormatBytes(snapshot.DownloadBytes))}[/]");

        if (connections.Count == 0)
        {
            return new Panel(new Rows(
                    summary,
                    new Markup("[yellow]没有匹配的活动连接。[/]")))
                .Header(title)
                .RoundedBorder()
                .Expand();
        }

        const int maxRows = 80;
        var visibleConnections = connections
            .OrderByDescending(connection => connection.StartedAt ?? DateTimeOffset.MinValue)
            .Take(maxRows)
            .ToArray();

        var table = new Table()
            .RoundedBorder()
            .Expand()
            .AddColumn(new TableColumn("开始").NoWrap())
            .AddColumn(new TableColumn("进程").NoWrap())
            .AddColumn("目标")
            .AddColumn(new TableColumn("规则").NoWrap())
            .AddColumn("出口链")
            .AddColumn(new TableColumn("流量").NoWrap());

        foreach (var connection in visibleConnections)
        {
            table.AddRow(
                FormatConnectionStartedAt(connection.StartedAt),
                FormatConnectionProcess(connection),
                FormatConnectionTarget(connection),
                FormatConnectionRule(connection),
                FormatConnectionChains(connection),
                FormatConnectionTransfer(connection));
        }

        var rows = connections.Count > maxRows
            ? new Rows(
                summary,
                table,
                new Markup($"[grey]仅显示最新 {maxRows} 条，搜索可以缩小范围。[/]"))
            : new Rows(summary, table);

        return new Panel(rows)
            .Header(title)
            .RoundedBorder()
            .Expand();
    }

    private static Panel CreateMessagePanel(string message, string header, Color? color = null)
    {
        var style = color == Color.Yellow ? "yellow" : "grey";
        return new Panel(new Markup($"[{style}]{Markup.Escape(message)}[/]"))
            .Header(header)
            .RoundedBorder()
            .Expand();
    }

    private static bool ConnectionMatches(MihomoConnectionInfo connection, string keyword)
    {
        return EnumerateConnectionSearchFields(connection)
            .Any(value => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateConnectionSearchFields(MihomoConnectionInfo connection)
    {
        yield return connection.Id;
        yield return connection.Network;
        yield return connection.Type;
        yield return connection.Source;
        yield return connection.Destination;
        yield return connection.Host;
        yield return connection.Process;
        yield return connection.ProcessPath;
        yield return connection.Rule;
        yield return connection.RulePayload;

        foreach (var chain in connection.Chains)
        {
            yield return chain;
        }
    }

    private static string FormatConnectionChoice(MihomoConnectionInfo connection)
    {
        if (connection.Id == "__paprika_cancel__")
        {
            return "返回";
        }

        return $"{EscapeDisplayText(BuildConnectionTitle(connection))} [grey]({Markup.Escape(FormatConnectionPlainTransfer(connection))}，{EscapeDisplayText(FormatConnectionPlainChains(connection))})[/]";
    }

    private static string BuildConnectionTitle(MihomoConnectionInfo connection)
    {
        var process = GetConnectionProcessText(connection);
        var target = GetConnectionTargetText(connection);
        return $"{process} -> {target}";
    }

    private static string FormatConnectionStartedAt(DateTimeOffset? startedAt)
    {
        if (startedAt is null)
        {
            return "[grey]-[/]";
        }

        var local = startedAt.Value.ToLocalTime();
        var format = local.Date == DateTimeOffset.Now.Date ? "HH:mm:ss" : "MM-dd HH:mm";
        return $"[grey]{Markup.Escape(local.ToString(format, CultureInfo.CurrentCulture))}[/]";
    }

    private static string FormatConnectionProcess(MihomoConnectionInfo connection)
    {
        return EscapeDisplayText(GetConnectionProcessText(connection));
    }

    private static string FormatConnectionTarget(MihomoConnectionInfo connection)
    {
        var target = GetConnectionTargetText(connection);
        var prefix = connection.Network.Equals("udp", StringComparison.OrdinalIgnoreCase)
            ? "[yellow]UDP[/] "
            : "[grey]TCP[/] ";
        return $"{prefix}{EscapeDisplayText(target)}";
    }

    private static string FormatConnectionRule(MihomoConnectionInfo connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Rule) || connection.Rule == "-")
        {
            return "[grey]-[/]";
        }

        var payload = string.IsNullOrWhiteSpace(connection.RulePayload)
            ? string.Empty
            : $"/{connection.RulePayload}";
        return EscapeDisplayText($"{connection.Rule}{payload}");
    }

    private static string FormatConnectionChains(MihomoConnectionInfo connection)
    {
        var chainText = FormatConnectionPlainChains(connection);
        return chainText == "-" ? "[grey]-[/]" : EscapeDisplayText(chainText);
    }

    private static string FormatConnectionPlainChains(MihomoConnectionInfo connection)
    {
        if (connection.Chains.Count == 0)
        {
            return "-";
        }

        return string.Join(" > ", connection.Chains.TakeLast(3));
    }

    private static string FormatConnectionTransfer(MihomoConnectionInfo connection)
    {
        return Markup.Escape(FormatConnectionPlainTransfer(connection));
    }

    private static string FormatConnectionPlainTransfer(MihomoConnectionInfo connection)
    {
        return $"↑{FormatBytes(connection.UploadBytes)} ↓{FormatBytes(connection.DownloadBytes)}";
    }

    private static string GetConnectionProcessText(MihomoConnectionInfo connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Process) && connection.Process != "-")
        {
            return connection.Process;
        }

        if (!string.IsNullOrWhiteSpace(connection.ProcessPath))
        {
            return Path.GetFileName(connection.ProcessPath);
        }

        return "-";
    }

    private static string GetConnectionTargetText(MihomoConnectionInfo connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Destination) && connection.Destination != "-")
        {
            return connection.Destination;
        }

        return string.IsNullOrWhiteSpace(connection.Host) ? "-" : connection.Host;
    }

    private static string FormatProfileSourceText(ProfileInfo profile)
    {
        return profile.IsSubscription ? "订阅" : "本地";
    }

    private static string FormatProfileSourceMarkup(ProfileInfo profile)
    {
        return profile.IsSubscription ? "[green]订阅[/]" : "[grey]本地[/]";
    }

    private static string FormatProfileTrafficSummary(MihomoTrafficInfo? traffic)
    {
        if (traffic is null)
        {
            return "[grey]-[/]";
        }

        var total = traffic.TotalBytes is > 0 ? FormatBytes(traffic.TotalBytes.Value) : "未知";
        var used = traffic.UsedBytes is null ? "未知" : FormatBytes(traffic.UsedBytes.Value);
        var expire = traffic.ExpireAt is null
            ? string.Empty
            : $"，到期 {traffic.ExpireAt.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)}";

        return Markup.Escape($"{used} / {total}{expire}");
    }

    private static string BuildTrafficBarMarkup(MihomoTrafficInfo? traffic)
    {
        if (traffic is null)
        {
            return "[grey]订阅流量未提供[/]";
        }

        var expireText = traffic.ExpireAt is null
            ? string.Empty
            : $" · {Markup.Escape(traffic.ExpireAt.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture))}";

        if (traffic.TotalBytes is not > 0)
        {
            var usedText = traffic.UsedBytes is null
                ? "未知"
                : FormatBytes(traffic.UsedBytes.Value);
            return $"[grey]{Markup.Escape(usedText)} / 未知{expireText}[/]";
        }

        const int barWidth = 38;
        var total = traffic.TotalBytes.Value;
        var remaining = Math.Clamp(traffic.RemainingBytes ?? total, 0, total);
        var remainingWidth = (int)Math.Round((double)remaining / total * barWidth);
        remainingWidth = Math.Clamp(remainingWidth, 0, barWidth);
        var usedWidth = barWidth - remainingWidth;
        var remainingBar = new string('█', remainingWidth);
        var usedBar = new string('░', usedWidth);

        var usedQuotaText = traffic.UsedBytes is null ? "0B" : FormatBytes(traffic.UsedBytes.Value);
        return $"[green]{remainingBar}[/][grey]{usedBar}[/]{Environment.NewLine}{Markup.Escape(usedQuotaText)} / {Markup.Escape(FormatBytes(total))}{expireText}";
    }

    private static string FormatRelativeTime(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "未知";
        }

        var span = DateTimeOffset.Now - value.Value.ToLocalTime();
        if (span < TimeSpan.Zero || span.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{Math.Floor(span.TotalMinutes)} 分钟前";
        }

        if (span.TotalHours < 24)
        {
            return $"{Math.Floor(span.TotalHours)} 小时前";
        }

        if (span.TotalDays < 30)
        {
            return $"{Math.Floor(span.TotalDays)} 天前";
        }

        return value.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        var displayValue = (double)value;

        while (displayValue >= 1024 && unitIndex < units.Length - 1)
        {
            displayValue /= 1024;
            unitIndex++;
        }

        return $"{displayValue.ToString(displayValue >= 10 || unitIndex == 0 ? "0.#" : "0.##", CultureInfo.CurrentCulture)}{units[unitIndex]}";
    }

    private static string EscapeDisplayText(string value)
    {
        return Markup.Escape(ReplaceFlagEmojiWithRegionCodes(value));
    }

    private static string ReplaceFlagEmojiWithRegionCodes(string value)
    {
        const int regionalIndicatorA = 0x1F1E6;
        const int regionalIndicatorZ = 0x1F1FF;

        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var pendingRegionalIndicator = -1;

        foreach (var rune in value.EnumerateRunes())
        {
            var codePoint = rune.Value;
            var isRegionalIndicator = codePoint is >= regionalIndicatorA and <= regionalIndicatorZ;

            if (isRegionalIndicator)
            {
                if (pendingRegionalIndicator < 0)
                {
                    pendingRegionalIndicator = codePoint;
                    continue;
                }

                // 旗帜 emoji 在很多 Windows 控制台字体下无法合成为国旗，统一显示稳定的区域码。
                builder.Append('[');
                builder.Append(ToRegionLetter(pendingRegionalIndicator));
                builder.Append(ToRegionLetter(codePoint));
                builder.Append(']');
                pendingRegionalIndicator = -1;
                continue;
            }

            if (pendingRegionalIndicator >= 0)
            {
                builder.Append(char.ConvertFromUtf32(pendingRegionalIndicator));
                pendingRegionalIndicator = -1;
            }

            builder.Append(rune.ToString());
        }

        if (pendingRegionalIndicator >= 0)
        {
            builder.Append(char.ConvertFromUtf32(pendingRegionalIndicator));
        }

        return builder.ToString();

        static char ToRegionLetter(int regionalIndicator)
        {
            return (char)('A' + regionalIndicator - regionalIndicatorA);
        }
    }

    private static string AskPath(string title)
    {
        var path = AskText(title);
        return path.Trim().Trim('"');
    }

    private static int AskPort(string title, int defaultValue)
    {
        while (true)
        {
            var input = AskText(title, defaultValue.ToString());
            if (int.TryParse(input, out var port) && port is > 0 and <= 65535)
            {
                return port;
            }

            AnsiConsole.MarkupLine("[red]端口必须在 1-65535 之间。[/]");
        }
    }

    private static int AskNumber(string title, int defaultValue, int min, int max)
    {
        while (true)
        {
            var input = AskText(title, defaultValue.ToString(CultureInfo.InvariantCulture));
            if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value >= min &&
                value <= max)
            {
                return value;
            }

            AnsiConsole.MarkupLine($"[red]数值必须在 {min}-{max} 之间。[/]");
        }
    }

    private static string AskText(string title, string? defaultValue = null)
    {
        while (true)
        {
            var value = ReadInputLine(title, defaultValue);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            AnsiConsole.MarkupLine("[red]输入不能为空。[/]");
        }
    }

    private static bool AskYesNo(string title, bool defaultValue)
    {
        var suffix = defaultValue ? "Y/n" : "y/N";

        while (true)
        {
            var value = ReadInputLine($"{title} [{suffix}]", defaultValue ? "y" : "n");
            if (value.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("n", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            AnsiConsole.MarkupLine("[red]请输入 y 或 n。[/]");
        }
    }

    private static string ReadInputLine(string title, string? defaultValue)
    {
        var buffer = new List<char>();
        var escapedTitle = Markup.Escape(title);
        var prompt = defaultValue is null
            ? $"{escapedTitle} [grey](Esc 返回)[/]: "
            : $"{escapedTitle} [grey](默认：{Markup.Escape(defaultValue)}，Esc 返回)[/]: ";

        AnsiConsole.Markup(prompt);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    AnsiConsole.WriteLine();
                    throw new MenuBackException();

                case ConsoleKey.Enter:
                    AnsiConsole.WriteLine();
                    if (buffer.Count == 0 && defaultValue is not null)
                    {
                        return defaultValue;
                    }

                    return new string(buffer.ToArray());

                case ConsoleKey.Backspace:
                    if (buffer.Count > 0)
                    {
                        buffer.RemoveAt(buffer.Count - 1);
                        Console.Write("\b \b");
                    }

                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Add(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }

                    break;
            }
        }
    }

    private static bool IsInteractiveConsole()
    {
        return !Console.IsInputRedirected && !Console.IsOutputRedirected;
    }

    private static void TryClearScreen()
    {
        try
        {
            if (!Console.IsOutputRedirected)
            {
                AnsiConsole.Clear();
            }
        }
        catch (IOException)
        {
            // 清屏只是视觉优化，部分宿主没有可用缓冲区。
        }
    }

    private static void PauseForResult()
    {
        if (!IsInteractiveConsole())
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Markup("[grey]按任意键返回当前菜单...[/]");
        Console.ReadKey(intercept: true);
    }

    private static void PauseBeforeForegroundExit()
    {
        if (!IsInteractiveConsole())
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Markup("[grey]按任意键退出...[/]");
        Console.ReadKey(intercept: true);
    }

    private enum SystemProxyExclusionChoiceKind
    {
        Domain,
        Separator,
        Add,
        Back
    }

    private sealed record SystemProxyExclusionChoice(
        SystemProxyExclusionChoiceKind Kind,
        string Value)
    {
        public static SystemProxyExclusionChoice Domain(string value)
        {
            return new SystemProxyExclusionChoice(SystemProxyExclusionChoiceKind.Domain, value);
        }

        public static SystemProxyExclusionChoice Add()
        {
            return new SystemProxyExclusionChoice(SystemProxyExclusionChoiceKind.Add, string.Empty);
        }

        public static SystemProxyExclusionChoice Separator()
        {
            return new SystemProxyExclusionChoice(SystemProxyExclusionChoiceKind.Separator, string.Empty);
        }

        public static SystemProxyExclusionChoice Back()
        {
            return new SystemProxyExclusionChoice(SystemProxyExclusionChoiceKind.Back, string.Empty);
        }
    }

    private enum TunRouteExclusionChoiceKind
    {
        Route,
        Separator,
        Add,
        Back
    }

    private sealed record TunRouteExclusionChoice(
        TunRouteExclusionChoiceKind Kind,
        string Value)
    {
        public static TunRouteExclusionChoice Route(string value)
        {
            return new TunRouteExclusionChoice(TunRouteExclusionChoiceKind.Route, value);
        }

        public static TunRouteExclusionChoice Add()
        {
            return new TunRouteExclusionChoice(TunRouteExclusionChoiceKind.Add, string.Empty);
        }

        public static TunRouteExclusionChoice Separator()
        {
            return new TunRouteExclusionChoice(TunRouteExclusionChoiceKind.Separator, string.Empty);
        }

        public static TunRouteExclusionChoice Back()
        {
            return new TunRouteExclusionChoice(TunRouteExclusionChoiceKind.Back, string.Empty);
        }
    }

    private sealed record ProxyModeOption(string Mode, string DisplayName);

    private sealed record RunModeOption(string Mode, string DisplayName);

    private sealed record TunStackOption(string Stack, string DisplayName);

    private sealed record TunConnectivityTarget(
        string Name,
        string Host,
        string Url,
        string LogKeyword,
        bool IsForeign);

    private sealed record TunConnectivityResult(
        TunConnectivityTarget Target,
        TunDnsTestResult Dns,
        TunHttpTestResult Http);

    private sealed record TunDnsTestResult(
        bool IsSuccess,
        string Message,
        TimeSpan Duration);

    private sealed record TunHttpTestResult(
        bool IsSuccess,
        string Message,
        TimeSpan? Duration,
        int? StatusCode)
    {
        public static TunHttpTestResult Skipped(string message)
        {
            return new TunHttpTestResult(false, message, null, null);
        }
    }

    private enum TunHealthStatus
    {
        Info,
        Ok,
        Warning,
        Error
    }

    private sealed record TunHealthCheckItem(
        string Name,
        TunHealthStatus Status,
        string Message,
        bool CanRepair)
    {
        public static TunHealthCheckItem Info(string name, string message)
        {
            return new TunHealthCheckItem(name, TunHealthStatus.Info, message, CanRepair: false);
        }

        public static TunHealthCheckItem Ok(string name, string message)
        {
            return new TunHealthCheckItem(name, TunHealthStatus.Ok, message, CanRepair: false);
        }

        public static TunHealthCheckItem Warning(string name, string message, bool canRepair = true)
        {
            return new TunHealthCheckItem(name, TunHealthStatus.Warning, message, canRepair);
        }

        public static TunHealthCheckItem Error(string name, string message, bool canRepair)
        {
            return new TunHealthCheckItem(name, TunHealthStatus.Error, message, canRepair);
        }
    }

    private sealed record TunHealthReport(
        AppSettings Settings,
        CoreStatus CoreStatus,
        SystemProxyStatus ProxyStatus,
        IReadOnlyList<TunHealthCheckItem> Items)
    {
        public int RepairableIssueCount =>
            Items.Count(item => item.CanRepair && item.Status is TunHealthStatus.Warning or TunHealthStatus.Error);

        public bool HasRepairableIssues => RepairableIssueCount > 0;
    }

    private sealed record TunRepairResult(
        IReadOnlyList<string> Actions,
        IReadOnlyList<string> Warnings);

    private sealed record CoreLogEntry(
        DateTimeOffset? Timestamp,
        string Level,
        string Message);

    private sealed record MenuItem(
        string Label,
        Func<CancellationToken, Task> RunAsync,
        bool ShouldStay = false,
        bool ShouldReturn = false,
        bool ShouldExit = false,
        bool ShouldBackground = false,
        bool IsSubMenu = false,
        string? MarkupLabel = null)
    {
        public static MenuItem Stay()
        {
            return new MenuItem(string.Empty, _ => Task.CompletedTask, ShouldStay: true);
        }

        public static MenuItem SubMenu(string label, Func<CancellationToken, Task> runAsync)
        {
            return new MenuItem(label, runAsync, IsSubMenu: true);
        }

        public static MenuItem Back(string label)
        {
            return new MenuItem(label, _ => Task.CompletedTask, ShouldReturn: true);
        }

        public static MenuItem Exit(string label)
        {
            return new MenuItem(label, _ => Task.CompletedTask, ShouldExit: true);
        }
    }

    private sealed class MenuBackException : Exception
    {
    }
}
