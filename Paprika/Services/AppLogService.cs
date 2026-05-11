using System.Text;

namespace Paprika.Services;

public sealed class AppLogService(AppPathService paths)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public Task InfoAsync(string message, CancellationToken cancellationToken = default)
    {
        return WriteAsync("INFO", message, null, cancellationToken);
    }

    public Task WarnAsync(string message, CancellationToken cancellationToken = default)
    {
        return WriteAsync("WARN", message, null, cancellationToken);
    }

    public Task ErrorAsync(string message, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        return WriteAsync("ERROR", message, exception, cancellationToken);
    }

    private async Task WriteAsync(
        string level,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        try
        {
            paths.EnsureDirectories();
            var line = BuildLogLine(level, message, exception);

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await File.AppendAllTextAsync(paths.AppLogPath, line, Encoding.UTF8, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 应用日志只用于诊断，写入失败不能影响代理启动/关闭主流程。
        }
    }

    private static string BuildLogLine(string level, string message, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        builder.Append(DateTimeOffset.Now.ToString("O"));
        builder.Append("] [");
        builder.Append(level);
        builder.Append("] ");
        builder.Append(message);

        if (exception is not null)
        {
            builder.Append(" | ");
            builder.Append(exception.GetType().Name);
            builder.Append(": ");
            builder.Append(exception.Message);
        }

        builder.AppendLine();
        return builder.ToString();
    }
}
