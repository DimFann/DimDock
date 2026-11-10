using System.Text.Json;
using Website.Models;

namespace Website.Services;

public sealed class NavigationSettingsProvider(IConfiguration configuration, IFileStore fileStore)
{
    private const string FileName = "navigation.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<NavigationSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!await fileStore.FileExistsAsync(FileStoreArea.Admin, FileName, cancellationToken))
        {
            return new NavigationSettings
            {
                Gallery = configuration.GetValue<bool?>("Navigation:Gallery") ?? true,
                Search = configuration.GetValue<bool?>("Navigation:Search") ?? true,
                Blog = configuration.GetValue<bool?>("Navigation:Blog") ?? true,
                Commissions = configuration.GetValue<bool?>("Navigation:Commissions") ?? true
            };
        }

        await using var stream = await fileStore.OpenReadAsync(FileStoreArea.Admin, FileName, cancellationToken);
        return await JsonSerializer.DeserializeAsync<NavigationSettings>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Navigation settings must be a JSON object.");
    }

    public Task SaveAsync(NavigationSettings settings, CancellationToken cancellationToken = default) =>
        fileStore.WriteAllTextAsync(
            FileStoreArea.Admin, FileName, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
}
