using Paprika.Services.SystemProxy;

namespace Paprika.Services;

internal sealed class PaprikaServices
{
    private PaprikaServices(
        AppPathService paths,
        AppLogService appLog,
        AppSettingsService settingsService,
        AppStateService stateService,
        ProfileService profileService,
        RuntimeConfigService runtimeConfigService,
        MihomoCoreDownloadService coreDownloadService,
        ConfigResourceService configResourceService,
        ExternalResourceService externalResourceService,
        NodeSelectionService nodeSelectionService,
        ConnectionDiagnosticsService connectionDiagnosticsService,
        WindowsPrivilegeService windowsPrivilegeService,
        ISystemProxyService systemProxyService,
        MihomoApiClient apiClient,
        MihomoCoreManager coreManager)
    {
        Paths = paths;
        AppLog = appLog;
        SettingsService = settingsService;
        StateService = stateService;
        ProfileService = profileService;
        RuntimeConfigService = runtimeConfigService;
        CoreDownloadService = coreDownloadService;
        ConfigResourceService = configResourceService;
        ExternalResourceService = externalResourceService;
        NodeSelectionService = nodeSelectionService;
        ConnectionDiagnosticsService = connectionDiagnosticsService;
        WindowsPrivilegeService = windowsPrivilegeService;
        SystemProxyService = systemProxyService;
        ApiClient = apiClient;
        CoreManager = coreManager;
    }

    public AppPathService Paths { get; }

    public AppLogService AppLog { get; }

    public AppSettingsService SettingsService { get; }

    public AppStateService StateService { get; }

    public ProfileService ProfileService { get; }

    public RuntimeConfigService RuntimeConfigService { get; }

    public MihomoCoreDownloadService CoreDownloadService { get; }

    public ConfigResourceService ConfigResourceService { get; }

    public ExternalResourceService ExternalResourceService { get; }

    public NodeSelectionService NodeSelectionService { get; }

    public ConnectionDiagnosticsService ConnectionDiagnosticsService { get; }

    public WindowsPrivilegeService WindowsPrivilegeService { get; }

    public ISystemProxyService SystemProxyService { get; }

    public MihomoApiClient ApiClient { get; }

    public MihomoCoreManager CoreManager { get; }

    public static PaprikaServices Create()
    {
        // 创建顺序按依赖展开：路径 -> 持久化服务 -> 配置/API -> 核心编排。
        var paths = new AppPathService();
        var appLog = new AppLogService(paths);
        var settings = new AppSettingsService(paths);
        var state = new AppStateService(paths);
        var profile = new ProfileService(paths, settings);
        var runtime = new RuntimeConfigService(paths, profile, appLog);
        var coreDownload = new MihomoCoreDownloadService(paths, settings);
        ISystemProxyService systemProxy = OperatingSystem.IsWindows()
            ? new WindowsSystemProxyService(settings, state)
            : new NoopSystemProxyService();
        var api = new MihomoApiClient(settings);
        var core = new MihomoCoreManager(paths, settings, state, runtime, api, appLog);
        var configResources = new ConfigResourceService(core, api);
        var externalResources = new ExternalResourceService(paths, settings);
        var nodeSelection = new NodeSelectionService(core, api);
        var connectionDiagnostics = new ConnectionDiagnosticsService(core, api);
        var windowsPrivilege = new WindowsPrivilegeService();

        return new PaprikaServices(paths, appLog, settings, state, profile, runtime, coreDownload, configResources, externalResources, nodeSelection, connectionDiagnostics, windowsPrivilege, systemProxy, api, core);
    }
}
