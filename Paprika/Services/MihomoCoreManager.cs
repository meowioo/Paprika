using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using Paprika.Models;

namespace Paprika.Services;

public sealed class MihomoCoreManager(
    AppPathService paths,
    AppSettingsService settingsService,
    AppStateService stateService,
    RuntimeConfigService runtimeConfigService,
    MihomoApiClient apiClient,
    AppLogService appLog)
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await appLog.InfoAsync("请求启动 mihomo。", cancellationToken);
        var currentStatus = await GetStatusAsync(cancellationToken);
        if (currentStatus.IsRunning)
        {
            await appLog.InfoAsync($"mihomo 已在运行，跳过启动。pid={currentStatus.ProcessId}", cancellationToken);
            return;
        }

        if (currentStatus.IsProcessRunning)
        {
            // 已记录但 API 不可用的进程视为异常，先停掉以免新旧进程抢占端口。
            await appLog.WarnAsync($"发现 mihomo 进程存在但 API 不可用，准备先停止。pid={currentStatus.ProcessId}", cancellationToken);
            await StopAsync(cancellationToken);
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        var corePath = ResolveCorePath();
        if (corePath is null)
        {
            await appLog.ErrorAsync($"未找到 mihomo 核心：{paths.DefaultCorePath}", cancellationToken: cancellationToken);
            throw new InvalidOperationException(
                $"未找到 mihomo 核心，请先下载到默认目录：{paths.DefaultCorePath}");
        }

        var runtimePath = await runtimeConfigService.GenerateAsync(settings, cancellationToken);
        await appLog.InfoAsync($"准备启动 mihomo：core={corePath}, runtime={runtimePath}", cancellationToken);

        paths.EnsureDirectories();

        var process = StartCoreProcess(corePath, runtimePath);

        await stateService.SaveAsync(new AppState
        {
            CoreProcessId = process.Id,
            CorePath = corePath,
            CoreStartedAt = DateTimeOffset.UtcNow,
            RuntimeConfigPath = runtimePath
        }, cancellationToken);
        await appLog.InfoAsync($"mihomo 进程已创建：pid={process.Id}", cancellationToken);

        try
        {
            await WaitForControllerAsync(process, cancellationToken);
            await appLog.InfoAsync($"mihomo external-controller 已就绪：pid={process.Id}", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CleanupStartedProcessAsync(process);
            await stateService.ClearCoreAsync(CancellationToken.None);
            await appLog.ErrorAsync("mihomo 启动失败，已清理刚启动的进程。", ex, CancellationToken.None);
            throw new InvalidOperationException(BuildStartupFailureMessage(ex), ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await appLog.InfoAsync("请求停止 mihomo。", cancellationToken);
        var state = await stateService.LoadAsync(cancellationToken);
        if (state.CoreProcessId is null)
        {
            await appLog.InfoAsync("没有记录中的 mihomo 进程，跳过停止。", cancellationToken);
            return;
        }

        try
        {
            var process = Process.GetProcessById(state.CoreProcessId.Value);
            if (!process.HasExited && IsTrackedCoreProcess(process, state))
            {
                // mihomo 作为子进程启动，停止时杀掉整棵进程树能避免残留 helper 进程。
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
                await appLog.InfoAsync($"mihomo 已停止：pid={state.CoreProcessId.Value}", cancellationToken);
            }
        }
        catch (ArgumentException)
        {
            // 保存的 PID 已过期，清掉状态避免后续误判。
            await appLog.WarnAsync($"记录中的 mihomo PID 已不存在：pid={state.CoreProcessId.Value}", cancellationToken);
        }
        finally
        {
            await stateService.ClearCoreAsync(cancellationToken);
            await appLog.InfoAsync("mihomo 状态已清理。", cancellationToken);
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public async Task<CoreStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var state = await stateService.LoadAsync(cancellationToken);
        var processRunning = false;
        var hadTrackedProcess = state.CoreProcessId is not null;

        if (state.CoreProcessId is not null)
        {
            try
            {
                var process = Process.GetProcessById(state.CoreProcessId.Value);
                processRunning = !process.HasExited && IsTrackedCoreProcess(process, state);
            }
            catch (ArgumentException)
            {
                processRunning = false;
            }
        }

        if (hadTrackedProcess && !processRunning)
        {
            await stateService.ClearCoreAsync(cancellationToken);
            state.CoreProcessId = null;
            state.CorePath = null;
            state.RuntimeConfigPath = null;
            state.CoreStartedAt = null;
        }

        // 仅有进程还不够，external-controller 能响应 /version 才算核心可用。
        var version = processRunning
            ? await apiClient.TryGetVersionAsync(cancellationToken)
            : null;

        return new CoreStatus(
            state.CoreProcessId,
            processRunning,
            !string.IsNullOrWhiteSpace(version),
            version,
            processRunning ? null : "No tracked mihomo process is running.");
    }

    private async Task WaitForControllerAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException($"mihomo 启动过程中已退出，退出码：{process.ExitCode}。");
            }

            var version = await apiClient.TryGetVersionAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(version))
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException("mihomo 已启动，但 /version 在 45 秒内没有响应。");
    }

    private static async Task CleanupStartedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch
        {
            // 启动失败后的清理不能掩盖原始错误。
        }
    }

    private string BuildStartupFailureMessage(Exception exception)
    {
        var message = new StringBuilder(exception.Message);
        var recentLogs = ReadRecentCoreLogs(20);

        if (!string.IsNullOrWhiteSpace(recentLogs))
        {
            message.AppendLine();
            message.AppendLine("最近的 mihomo 日志：");
            message.Append(recentLogs);
        }

        return message.ToString();
    }

    private string ReadRecentCoreLogs(int lineCount)
    {
        try
        {
            if (!File.Exists(paths.CoreLogPath))
            {
                return string.Empty;
            }

            return string.Join(Environment.NewLine, CoreLogFile.ReadLinesShared(paths.CoreLogPath).TakeLast(lineCount));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsTrackedCoreProcess(Process process, AppState state)
    {
        if (string.IsNullOrWhiteSpace(state.CorePath))
        {
            return true;
        }

        try
        {
            var expected = Path.GetFullPath(state.CorePath);
            var actual = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actual)
                   && string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 无法读取进程路径时宁愿判为不匹配，避免误杀复用 PID 的其他进程。
            return false;
        }
    }

    private string? ResolveCorePath()
    {
        var defaultPath = paths.DefaultCorePath;
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private Process StartCoreProcess(string corePath, string runtimePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return StartCoreProcessOnWindows(corePath, runtimePath);
        }

        // 非 Windows 保留托管重定向；Windows 走原生句柄，支持前台退出后核心继续写日志。
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = corePath,
                Arguments = $"-d \"{paths.AppDataDirectory}\" -f \"{runtimePath}\"",
                WorkingDirectory = paths.AppDataDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, args) => AppendCoreLog(args.Data);
        process.ErrorDataReceived += (_, args) => AppendCoreLog(args.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("启动 mihomo 进程失败。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private Process StartCoreProcessOnWindows(string corePath, string runtimePath)
    {
        paths.EnsureDirectories();

        var logHandle = OpenInheritableLogHandle(paths.CoreLogPath);
        try
        {
            var startupInfo = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles,
                StdOutput = logHandle.DangerousGetHandle(),
                StdError = logHandle.DangerousGetHandle()
            };

            var processInfo = new ProcessInformation();
            var commandLine = new StringBuilder(
                $"{QuoteCommandArgument(corePath)} -d {QuoteCommandArgument(paths.AppDataDirectory)} -f {QuoteCommandArgument(runtimePath)}");

            var created = CreateProcessW(
                corePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,
                creationFlags: CreateNoWindow,
                IntPtr.Zero,
                paths.AppDataDirectory,
                ref startupInfo,
                out processInfo);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "启动 mihomo 进程失败。");
            }

            try
            {
                return Process.GetProcessById(processInfo.ProcessId);
            }
            finally
            {
                CloseHandle(processInfo.ProcessHandle);
                CloseHandle(processInfo.ThreadHandle);
            }
        }
        finally
        {
            // 子进程已继承日志文件句柄；关闭 Paprika 自己的句柄后，
            // 前台进程退出也不会影响 mihomo 继续写 mihomo.log。
            logHandle.Dispose();
        }
    }

    private static SafeFileHandle OpenInheritableLogHandle(string logPath)
    {
        var securityAttributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };

        var handle = CreateFileW(
            logPath,
            FileAppendData,
            FileShareRead | FileShareWrite | FileShareDelete,
            ref securityAttributes,
            OpenAlways,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法打开日志文件：{logPath}");
        }

        return handle;
    }

    private static string QuoteCommandArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private void AppendCoreLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        paths.EnsureDirectories();
        File.AppendAllText(paths.CoreLogPath, $"[{DateTimeOffset.Now:O}] {line}{Environment.NewLine}");
    }

    private const int FileAppendData = 0x0004;
    private const int FileShareRead = 0x00000001;
    private const int FileShareWrite = 0x00000002;
    private const int FileShareDelete = 0x00000004;
    private const int OpenAlways = 4;
    private const int FileAttributeNormal = 0x00000080;
    private const int StartfUseStdHandles = 0x00000100;
    private const int CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        int desiredAccess,
        int shareMode,
        ref SecurityAttributes securityAttributes,
        int creationDisposition,
        int flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        int creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
