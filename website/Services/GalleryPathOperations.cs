using System.Collections.Concurrent;

namespace Website.Services;

public sealed record GalleryOperationResult(string Path, string? Message = null);

/// <summary>
/// Runs long gallery path operations (rename, move, reload) one at a time on a server-owned task,
/// so proxy timeouts or client disconnects cannot abandon a half-finished operation.
/// </summary>
public sealed class GalleryPathOperations(IServiceScopeFactory scopeFactory, ILogger<GalleryPathOperations> logger)
{
    public static readonly TimeSpan InlineWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetainFinished = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Operation> _operations = new(StringComparer.Ordinal);

    public sealed class Operation
    {
        public required string Id { get; init; }
        public required Task<GalleryOperationResult> Task { get; init; }
        public DateTimeOffset? FinishedAt { get; set; }
    }

    /// <summary>Returns null when another gallery path operation is already running.</summary>
    public Operation? TryStart(Func<IServiceProvider, CancellationToken, Task<GalleryOperationResult>> run)
    {
        if (!_gate.Wait(0))
        {
            return null;
        }
        PruneFinished();
        var id = Guid.NewGuid().ToString("N");
        var task = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                return await run(scope.ServiceProvider, CancellationToken.None);
            }
            catch (Exception error) when (!IsUserError(error))
            {
                logger.LogError(error, "Gallery path operation {OperationId} failed.", id);
                throw;
            }
            finally
            {
                _gate.Release();
            }
        });
        var operation = new Operation { Id = id, Task = task };
        _operations[id] = operation;
        _ = task.ContinueWith(
            _ => operation.FinishedAt = DateTimeOffset.UtcNow,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return operation;
    }

    public Operation? Find(string? id) =>
        id is not null && _operations.TryGetValue(id, out var operation) ? operation : null;

    public static bool IsUserError(Exception error) =>
        error is GalleryPathRenameException or GalleryReloadException;

    public static string ErrorMessage(Exception error) => error switch
    {
        _ when IsUserError(error) => error.Message,
        DirectoryNotFoundException => "The gallery directory could not be found in storage.",
        _ => "The path operation failed. Refresh the page to check its current state, and see the server log for details."
    };

    private void PruneFinished()
    {
        var cutoff = DateTimeOffset.UtcNow - RetainFinished;
        foreach (var (id, operation) in _operations)
        {
            if (operation.FinishedAt < cutoff)
            {
                _operations.TryRemove(id, out _);
            }
        }
    }
}
