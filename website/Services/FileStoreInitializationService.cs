namespace Website.Services;

public sealed class FileStoreInitializationService(IFileStore fileStore) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var area in Enum.GetValues<FileStoreArea>())
        {
            if (!await fileStore.DirectoryExistsAsync(area, "", cancellationToken))
            {
                await fileStore.CreateDirectoryAsync(area, "", cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
