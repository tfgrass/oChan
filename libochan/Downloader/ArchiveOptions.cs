namespace oChan.Downloader;

/// <summary>
/// Represents options for archiving content.
/// </summary>
public class ArchiveOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to include HTML content in the archive.
    /// </summary>
    public bool IncludeHtml { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to include media files in the archive.
    /// </summary>
    public bool IncludeMedia { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to rewrite URLs for offline viewing.
    /// </summary>
    public bool RewriteUrls { get; set; }

    /// <summary>
    /// Gets or sets the output directory where archived content will be saved.
    /// </summary>
    public string OutputDirectory { get; set; }

    /// <summary>
    /// Gets or sets the download queue used for managing downloads.
    /// </summary>
    public DownloadQueue DownloadQueue { get; set; }

    public ArchiveOptions()
    {
        // Assign default values or make them configurable
        OutputDirectory = "Downloads"; // Default output directory
        DownloadQueue = new DownloadQueue(5, 1024 * 1024 * 10); // Default settings for download queue, 10mb/s
    }
}

