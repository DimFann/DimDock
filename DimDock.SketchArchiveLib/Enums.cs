namespace SketchArchiveLib
{
    public enum Display
    {
        /// <summary>
        /// Default display mode, tiled grid of items.
        /// </summary>
        Gallery,

        /// <summary>
        /// Single panel that displays the current page of N pages, with begin/end/next/last buttons.
        /// Content is ordered by name as numbers ascending, then alphabetically ascending.
        /// </summary>
        Comic,

        /// <summary>
        /// Adds html content from info.html to the current page.
        /// </summary>
        Info,

        /// <summary>
        /// List of texts with Titles & Excerpts.
        /// </summary>
        TextLibrary,

        // GDrive-like list of files.
        Files,
    }
}
