using System.Text.Json;
using Paprika.Models;

namespace Paprika.Services;

public sealed class AppStateService(AppPathService paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<AppState> LoadAsync(CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();

        if (!File.Exists(paths.StatePath))
        {
            // 没有状态文件通常表示 Paprika 还没有启动过核心。
            return new AppState();
        }

        await using var stream = File.OpenRead(paths.StatePath);
        return await JsonSerializer.DeserializeAsync<AppState>(stream, JsonOptions, cancellationToken)
               ?? new AppState();
    }

    public async Task SaveAsync(AppState state, CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();

        await using var stream = File.Create(paths.StatePath);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
    }

    public async Task UpdateAsync(Action<AppState> update, CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken);
        update(state);
        await SaveAsync(state, cancellationToken);
    }

    public async Task ClearCoreAsync(CancellationToken cancellationToken)
    {
        // 保留未来可能加入的非核心状态，只清掉进程跟踪字段。
        var state = await LoadAsync(cancellationToken);
        state.CoreProcessId = null;
        state.CorePath = null;
        state.CoreStartedAt = null;
        state.RuntimeConfigPath = null;
        await SaveAsync(state, cancellationToken);
    }
}
