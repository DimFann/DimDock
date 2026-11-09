namespace Website.Services;

public static class FileStoreKeys
{
    public static string GallerySourceUrl(string? parentPath, string originalName)
    {
        var directory = string.Equals(parentPath, "root", StringComparison.OrdinalIgnoreCase)
            ? ""
            : parentPath ?? "";
        var segments = Validate(directory, allowEmpty: true)
            .Append(originalName)
            .Select(Uri.EscapeDataString);
        return "/img_src/" + string.Join('/', segments);
    }

    public static string AreaName(FileStoreArea area) => area switch
    {
        FileStoreArea.Gallery => "img_src",
        FileStoreArea.Thumbnails => "img_db_thumbs",
        FileStoreArea.Blogs => "blogs",
        FileStoreArea.About => "about",
        FileStoreArea.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Unknown file storage area.")
    };

    public static string[] Validate(string key, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Contains('\\') || key.StartsWith('/') ||
            (!allowEmpty && string.IsNullOrWhiteSpace(key)))
        {
            throw new InvalidDataException($"Invalid relative file key: {key}");
        }

        if (key.EndsWith('/'))
        {
            if (!allowEmpty)
            {
                throw new InvalidDataException($"Invalid relative file key: {key}");
            }
            key = key[..^1];
        }

        var segments = key.Length == 0 ? [] : key.Split('/');
        if (segments.Any(segment =>
            string.IsNullOrEmpty(segment) ||
            segment is "." or ".." ||
            segment.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character))))
        {
            throw new InvalidDataException($"Invalid relative file key: {key}");
        }

        return segments;
    }

    public static string Normalize(string key, bool allowEmpty = true) =>
        string.Join('/', Validate(key, allowEmpty));
}
