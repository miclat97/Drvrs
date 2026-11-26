namespace Drvrs.Web.Configuration;

public sealed class DrvrsOptions
{
    public const string SectionName = "Drvrs";

    /// <summary>
    /// Root directory for SQLite database, Excel spreadsheets, and multimedia files.
    /// Relative paths are resolved against the application's ContentRootPath.
    /// </summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Official Ministry of Infrastructure page used for automatic link discovery fallback.
    /// </summary>
    public string GovPlPageUrl { get; set; } = "https://www.gov.pl/web/infrastruktura/prawo-jazdy";

    /// <summary>
    /// Direct URL to the official Ministry .xlsx catalog of exam questions.
    /// </summary>
    public string ExcelUrl { get; set; } = "https://www.gov.pl/attachment/a5c6c329-28a5-4274-a1a8-e2813f0a51bd";

    /// <summary>
    /// Direct URLs to the official Ministry .zip archives with multimedia attachments.
    /// Each archive is extracted flat into DataDirectory/media and immediately deleted to reclaim disk space.
    /// </summary>
    public List<string> MediaArchiveUrls { get; set; } =
    [
        "https://www.gov.pl/pliki/mi/multimedia_do_pytan.zip",
        "https://www.gov.pl/attachment/71d22a89-4c2d-4b08-9f47-99286bb0c964"
    ];

    /// <summary>
    /// Automatically download and import the official Excel question database on startup if SQLite DB is empty.
    /// </summary>
    public bool AutoSyncQuestionsOnStartup { get; set; } = false;

    /// <summary>
    /// Automatically download, unpack, and clean up the official multimedia ZIP archives on startup if media folder is empty.
    /// Ideal for turnkey Docker deployments.
    /// </summary>
    public bool AutoSyncMediaOnStartup { get; set; } = false;

    /// <summary>
    /// When true, .wmv files extracted during ZIP sync are immediately transcoded to H.264 .mp4 and the source .wmv is deleted,
    /// reducing total disk usage from ~9.5 GB to ~2.5 GB.
    /// </summary>
    public bool ConvertWmvDuringSync { get; set; } = false;

    /// <summary>
    /// When true, once on-demand/prewarm ffmpeg transcoding creates an .mp4 for a .wmv file, the original .wmv is deleted to save disk space.
    /// </summary>
    public bool ReplaceWmvAfterTranscode { get; set; } = false;

    /// <summary>
    /// Enables background pre-transcoding of .wmv clips when an exam session starts so videos play instantaneously.
    /// </summary>
    public bool PrewarmExamVideos { get; set; } = true;

    /// <summary>
    /// Path or binary name for ffmpeg used to transcode Windows Media Video (.wmv) to browser-native H.264 (.mp4).
    /// </summary>
    public string FfmpegExecutable { get; set; } = "ffmpeg";

    /// <summary>
    /// Enables /api/admin/* endpoints in the web UI for triggering gov.pl downloads or Excel re-imports.
    /// </summary>
    public bool EnableAdminEndpoints { get; set; } = true;

    /// <summary>
    /// Optional secret token required via X-Admin-Token header when triggering sync operations on a public server.
    /// Leave empty for open local/self-hosted management.
    /// </summary>
    public string? AdminToken { get; set; }

    public string ResolveDataDirectory(string contentRootPath)
    {
        return Path.IsPathRooted(DataDirectory)
            ? DataDirectory
            : Path.GetFullPath(Path.Combine(contentRootPath, DataDirectory));
    }
}
