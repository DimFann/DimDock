namespace Website.Models;

public sealed record GalleryDirectoryLink(
    string Name, string Path, bool IsAdminOnly, bool IsShortcut, ulong? ShortcutId = null);
