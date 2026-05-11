using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paprika.Services;

internal sealed class ConsoleShutdownHooks : IDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(8);

    private readonly Func<CancellationToken, Task> _cleanupAsync;
    private readonly ConsoleCancelEventHandler _cancelKeyPressHandler;
    private readonly EventHandler _processExitHandler;
    private readonly UnhandledExceptionEventHandler _unhandledExceptionHandler;
    private readonly WindowsConsoleCtrlHandler? _windowsConsoleCtrlHandler;

    private bool _disposed;

    public ConsoleShutdownHooks(Func<CancellationToken, Task> cleanupAsync)
    {
        _cleanupAsync = cleanupAsync;
        _cancelKeyPressHandler = OnCancelKeyPress;
        _processExitHandler = OnProcessExit;
        _unhandledExceptionHandler = OnUnhandledException;

        Console.CancelKeyPress += _cancelKeyPressHandler;
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException += _unhandledExceptionHandler;

        if (OperatingSystem.IsWindows())
        {
            _windowsConsoleCtrlHandler = OnWindowsConsoleControl;
            RegisterWindowsConsoleHandler(_windowsConsoleCtrlHandler);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Console.CancelKeyPress -= _cancelKeyPressHandler;
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException -= _unhandledExceptionHandler;

        if (OperatingSystem.IsWindows() && _windowsConsoleCtrlHandler is not null)
        {
            UnregisterWindowsConsoleHandler(_windowsConsoleCtrlHandler);
        }

        _disposed = true;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        // Ctrl+C/Ctrl+Break 仍然退出程序，但要先恢复代理并停止已跟踪的核心。
        args.Cancel = true;
        RunCleanupBlocking();
        Environment.Exit(0);
    }

    private void OnProcessExit(object? sender, EventArgs args)
    {
        RunCleanupBlocking();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        RunCleanupBlocking();
    }

    private bool OnWindowsConsoleControl(WindowsConsoleControl control)
    {
        if (control is WindowsConsoleControl.Close
            or WindowsConsoleControl.Logoff
            or WindowsConsoleControl.Shutdown)
        {
            // 点击窗口关闭按钮不会进入菜单循环，因此直接处理原生关闭事件。
            RunCleanupBlocking();
        }

        // 返回 false，让 Windows 继续执行正常的关闭流程。
        return false;
    }

    private void RunCleanupBlocking()
    {
        try
        {
            using var timeout = new CancellationTokenSource(CleanupTimeout);
            _cleanupAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // 退出钩子里不可靠显示 UI；菜单退出会由 InteractiveApp 报告清理错误。
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindowsConsoleHandler(WindowsConsoleCtrlHandler handler)
    {
        SetConsoleCtrlHandler(handler, add: true);
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterWindowsConsoleHandler(WindowsConsoleCtrlHandler handler)
    {
        SetConsoleCtrlHandler(handler, add: false);
    }

    private delegate bool WindowsConsoleCtrlHandler(WindowsConsoleControl control);

    private enum WindowsConsoleControl
    {
        CtrlC = 0,
        CtrlBreak = 1,
        Close = 2,
        Logoff = 5,
        Shutdown = 6
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(WindowsConsoleCtrlHandler handler, bool add);
}
