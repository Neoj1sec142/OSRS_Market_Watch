
namespace MarketAPI.Services;

public sealed class DatabaseWriteCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        try
        {
            await operation(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        try
        {
            return await operation(ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
