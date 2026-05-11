namespace Paprika.Services;

internal sealed class ShutdownCleanupService(PaprikaServices services)
{
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private bool _cleanupComplete;

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await _cleanupLock.WaitAsync(cancellationToken);

        try
        {
            if (_cleanupComplete)
            {
                await services.AppLog.InfoAsync("退出清理已完成过，跳过重复清理。", cancellationToken);
                return;
            }

            await services.AppLog.InfoAsync("开始退出清理：恢复系统代理并停止 mihomo。", cancellationToken);
            var errors = new List<string>();

            await TryCleanupStepAsync(
                "恢复系统代理失败",
                DisableManagedSystemProxyAsync,
                errors,
                cancellationToken);

            await TryCleanupStepAsync(
                "停止 mihomo 失败",
                StopCoreAsync,
                errors,
                cancellationToken);

            if (errors.Count > 0)
            {
                await services.AppLog.ErrorAsync($"退出清理存在错误：{string.Join(" | ", errors)}", cancellationToken: cancellationToken);
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            }

            _cleanupComplete = true;
            await services.AppLog.InfoAsync("退出清理完成。", cancellationToken);
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    private async Task DisableManagedSystemProxyAsync(CancellationToken cancellationToken)
    {
        var proxyStatus = await services.SystemProxyService.GetStatusAsync(cancellationToken);
        if (proxyStatus.IsManagedByPaprika)
        {
            // 只恢复 Paprika 接管过的代理；用户原本由其他工具设置的代理不主动改动。
            await services.AppLog.InfoAsync("退出清理：恢复 Paprika 接管的系统代理。", cancellationToken);
            await services.SystemProxyService.DisableAsync(cancellationToken);
            return;
        }

        await services.AppLog.InfoAsync("退出清理：系统代理未由 Paprika 接管，跳过恢复。", cancellationToken);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var coreStatus = await services.CoreManager.GetStatusAsync(cancellationToken);
        if (coreStatus.IsProcessRunning)
        {
            await services.AppLog.InfoAsync($"退出清理：停止 mihomo。pid={coreStatus.ProcessId}", cancellationToken);
            await services.CoreManager.StopAsync(cancellationToken);
            return;
        }

        await services.AppLog.InfoAsync("退出清理：mihomo 未运行，跳过停止。", cancellationToken);
    }

    private static async Task TryCleanupStepAsync(
        string label,
        Func<CancellationToken, Task> cleanupStep,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            await cleanupStep(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 某一步失败也继续执行后续清理，避免代理恢复失败导致核心无法停止。
            errors.Add($"{label}：{ex.Message}");
            await Task.CompletedTask;
        }
    }
}
