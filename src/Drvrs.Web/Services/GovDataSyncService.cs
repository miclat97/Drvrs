using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Drvrs.Web.Configuration;
using Drvrs.Web.Data;
using Drvrs.Web.Models;
using Microsoft.Extensions.Options;

namespace Drvrs.Web.Services;

/// <summary>
/// Handles local file auto-import as well as automated downloading from gov.pl.
/// Archives are processed sequentially one-by-one and permanently deleted immediately after unpacking to minimize peak disk space.
/// </summary>
public sealed partial class GovDataSyncService
{
    private readonly DatabaseInitializer _db;
    private readonly ExcelQuestionImporter _importer;
    private readonly QuestionRepository _repository;
    private readonly MediaService _mediaService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DrvrsOptions _options;
    private readonly ILogger<GovDataSyncService> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private volatile SyncJobStatusDto _currentStatus = new(
        IsRunning: false,
        Stage: "IDLE",
        Message: "Gotowy",
        DownloadedBytes: 0,
        TotalBytes: null,
        ExtractedFiles: 0,
        TotalArchiveEntries: null,
        StartedAtUtc: null,
        FinishedAtUtc: null,
        LastError: null);

    static GovDataSyncService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public GovDataSyncService(
        DatabaseInitializer db,
        ExcelQuestionImporter importer,
        QuestionRepository repository,
        MediaService mediaService,
        IHttpClientFactory httpClientFactory,
        IOptions<DrvrsOptions> options,
        ILogger<GovDataSyncService> logger)
    {
        _db = db;
        _importer = importer;
        _repository = repository;
        _mediaService = mediaService;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public SyncJobStatusDto CurrentStatus => _currentStatus;

    public async Task<PublicStatusDto> GetPublicStatusAsync(CancellationToken ct = default)
    {
        var totalQuestions = await _repository.GetTotalCountAsync(ct);
        var categories = await _repository.GetCategoryStatsAsync(ct);
        var catB = categories.FirstOrDefault(c => c.Category == "B")?.TotalQuestions ?? 0;
        var distinctMedia = await _repository.GetAllDistinctMediaFileNamesAsync(ct);
        var availableReferenced = distinctMedia.Count(m => _mediaService.HasMedia(m));
        var hasPartialMediaOnly = distinctMedia.Count > 0 && availableReferenced > 0 && availableReferenced < distinctMedia.Count * 0.95;

        return new PublicStatusDto(
            HasQuestions: totalQuestions > 0,
            TotalQuestions: totalQuestions,
            CategoryBQuestions: catB,
            HasPartialMediaOnly: hasPartialMediaOnly,
            Categories: categories);
    }

    public async Task<AdminStatusDto> GetAdminStatusAsync(CancellationToken ct = default)
    {
        var totalQuestions = await _repository.GetTotalCountAsync(ct);
        var categories = await _repository.GetCategoryStatsAsync(ct);
        var catB = categories.FirstOrDefault(c => c.Category == "B")?.TotalQuestions ?? 0;
        var distinctMedia = await _repository.GetAllDistinctMediaFileNamesAsync(ct);
        var availableReferenced = distinctMedia.Count(m => _mediaService.HasMedia(m));
        var (sourceExcel, importedAt) = await _repository.GetImportMetadataAsync(ct);

        return new AdminStatusDto(
            HasQuestions: totalQuestions > 0,
            TotalQuestions: totalQuestions,
            CategoryBQuestions: catB,
            TotalDistinctMediaReferenced: distinctMedia.Count,
            AvailableMediaFilesCount: availableReferenced,
            TranscodedMp4Count: _mediaService.CachedMp4Count,
            FfmpegAvailable: _mediaService.IsFfmpegAvailable,
            SourceExcelFile: sourceExcel,
            LastImportedAtUtc: importedAt,
            DataDirectoryPath: _db.DataDirectory,
            MediaDirectoryPath: _db.MediaDirectory,
            Categories: categories,
            SyncJob: _currentStatus);
    }

    /// <summary>
    /// Scans data/ and data/questions/ for any manually placed .xlsx file and imports it if SQLite hasn't imported it yet.
    /// Also triggers optional startup gov.pl sync when configured via environment variables (e.g. Docker).
    /// </summary>
    public async Task InitializeLocalAndStartupDataAsync(CancellationToken ct = default)
    {
        var localExcel = FindLatestLocalExcelFile();
        if (localExcel is not null)
        {
            try
            {
                await _importer.ImportIfChangedAsync(localExcel, force: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Błąd podczas importu lokalnego pliku Excel: {Path}", localExcel);
            }
        }

        _mediaService.RebuildIndex();

        var totalQuestions = await _repository.GetTotalCountAsync(ct);
        var needQuestions = _options.AutoSyncQuestionsOnStartup && totalQuestions == 0;
        var needMedia = _options.AutoSyncMediaOnStartup && _mediaService.AvailableLogicalMediaCount == 0;

        if (needQuestions || needMedia)
        {
            _logger.LogInformation(
                "Automatyczna synchronizacja przy starcie (Pytania={NeedQuestions}, Multimedia={NeedMedia})...",
                needQuestions, needMedia);

            _ = TryStartBackgroundSync(new StartSyncRequest(
                DownloadQuestions: needQuestions || totalQuestions == 0,
                DownloadMedia: needMedia,
                ConvertWmvToMp4: _options.ConvertWmvDuringSync));
        }
    }

    public async Task<ExcelImportResult?> RescanLocalFilesAsync(bool forceExcelReimport = true, CancellationToken ct = default)
    {
        ExcelImportResult? result = null;
        var localExcel = FindLatestLocalExcelFile();
        if (localExcel is not null)
        {
            result = await _importer.ImportIfChangedAsync(localExcel, force: forceExcelReimport, ct);
        }

        _mediaService.RebuildIndex();
        return result;
    }

    public bool TryStartBackgroundSync(StartSyncRequest request)
    {
        if (!_syncLock.Wait(0))
        {
            return false;
        }

        var startedAt = DateTimeOffset.UtcNow;
        _currentStatus = new SyncJobStatusDto(
            IsRunning: true,
            Stage: "STARTING",
            Message: "Inicjalizacja pobierania z gov.pl...",
            DownloadedBytes: 0,
            TotalBytes: null,
            ExtractedFiles: 0,
            TotalArchiveEntries: null,
            StartedAtUtc: startedAt,
            FinishedAtUtc: null,
            LastError: null);

        _ = Task.Run(async () =>
        {
            try
            {
                await RunSyncCoreAsync(request, startedAt, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Synchronizacja z gov.pl zakończyła się błędem.");
                _currentStatus = _currentStatus with
                {
                    IsRunning = false,
                    Stage = "ERROR",
                    Message = $"Błąd synchronizacji: {ex.Message}",
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    LastError = ex.Message
                };
            }
            finally
            {
                _syncLock.Release();
            }
        });

        return true;
    }

    public async Task RunSyncBlockingAsync(StartSyncRequest request, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await RunSyncCoreAsync(request, startedAt, ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task RunSyncCoreAsync(StartSyncRequest request, DateTimeOffset startedAt, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient("GovPl");

        if (request.DownloadQuestions)
        {
            UpdateStatus(startedAt, "DOWNLOADING_EXCEL", "Pobieranie oficjalnego pliku Excel z pytaniami z gov.pl...", 0, null, 0, null);
            var excelUrl = await ResolveWorkingExcelUrlAsync(client, ct);
            var targetXlsx = Path.Combine(_db.QuestionsDirectory, "baza_pytan_gov_pl.xlsx");
            var tempXlsx = Path.Combine(_db.TempDirectory, $"pytania_{Guid.NewGuid():N}.tmp");

            try
            {
                await DownloadFileWithProgressAsync(client, excelUrl, tempXlsx, startedAt, "DOWNLOADING_EXCEL", "Pobieranie bazy pytań (.xlsx)...", ct);

                // Check if downloaded file is a .zip wrapper or direct .xlsx (note: .xlsx itself is a zip containing [Content_Types].xml)
                EnsureValidXlsxOrUnwrap(tempXlsx, targetXlsx);
            }
            finally
            {
                SafeDeleteFile(tempXlsx);
            }

            UpdateStatus(startedAt, "IMPORTING_SQLITE", "Importowanie pytań z pliku Excel do bazy SQLite...", 0, null, 0, null);
            var importRes = await _importer.ImportIfChangedAsync(targetXlsx, force: true, ct);
            _logger.LogInformation("Pobrano i zaimportowano {Count} pytań z gov.pl.", importRes.ImportedCount);
        }

        if (request.DownloadMedia)
        {
            var archiveUrls = await ResolveMediaArchiveUrlsAsync(client, ct);
            var ibm852 = Encoding.GetEncoding(852);
            var convertWmv = request.ConvertWmvToMp4 && _mediaService.IsFfmpegAvailable;

            for (var i = 0; i < archiveUrls.Count; i++)
            {
                var url = archiveUrls[i];
                var archiveLabel = $"Archiwum multimediów {i + 1}/{archiveUrls.Count}";
                var tempZipPath = Path.Combine(_db.TempDirectory, $"media_part_{i + 1}_{Guid.NewGuid():N}.zip");

                try
                {
                    await DownloadFileWithProgressAsync(
                        client,
                        url,
                        tempZipPath,
                        startedAt,
                        "DOWNLOADING_MEDIA",
                        $"{archiveLabel}: pobieranie z gov.pl...",
                        ct);

                    await ExtractAndCleanZipAsync(tempZipPath, archiveLabel, ibm852, convertWmv, startedAt, ct);
                }
                finally
                {
                    // CRITICAL: Immediately delete the multi-gigabyte .zip file right after unpacking to reclaim disk space!
                    SafeDeleteFile(tempZipPath);
                    _logger.LogInformation("{Label}: trwale usunięto tymczasowe archiwum źródłowe .zip w celu zwolnienia miejsca na dysku.", archiveLabel);
                }
            }

            _mediaService.RebuildIndex();
        }

        _currentStatus = new SyncJobStatusDto(
            IsRunning: false,
            Stage: "COMPLETED",
            Message: "Synchronizacja z bazą Ministerstwa Infrastruktury zakończona pomyślnie.",
            DownloadedBytes: _currentStatus.DownloadedBytes,
            TotalBytes: _currentStatus.TotalBytes,
            ExtractedFiles: _currentStatus.ExtractedFiles,
            TotalArchiveEntries: _currentStatus.TotalArchiveEntries,
            StartedAtUtc: startedAt,
            FinishedAtUtc: DateTimeOffset.UtcNow,
            LastError: null);
    }

    private async Task ExtractAndCleanZipAsync(
        string zipPath,
        string archiveLabel,
        Encoding fallbackEncoding,
        bool convertWmvToMp4,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        int totalEntries;
        int extracted = 0;

        using (var fs = File.OpenRead(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: fallbackEncoding))
        {
            var fileEntries = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && !e.FullName.EndsWith('/'))
                .ToList();

            totalEntries = fileEntries.Count;
            UpdateStatus(startedAt, "EXTRACTING_MEDIA", $"{archiveLabel}: rozpakowywanie {totalEntries} plików...", 0, null, 0, totalEntries);

            foreach (var entry in fileEntries)
            {
                ct.ThrowIfCancellationRequested();

                // Flatten any subfolder (e.g. "multimedia do pytań/..." or "cz. 2/...") directly into data/media/
                var flatFileName = Path.GetFileName(entry.FullName.Replace('\\', '/')).Normalize(NormalizationForm.FormC);
                if (string.IsNullOrWhiteSpace(flatFileName))
                {
                    continue;
                }

                var ext = Path.GetExtension(flatFileName).ToLowerInvariant();
                var stem = Path.GetFileNameWithoutExtension(flatFileName);
                var destPath = Path.Combine(_db.MediaDirectory, flatFileName);
                var destMp4Path = Path.Combine(_db.MediaDirectory, $"{stem}.mp4");

                // Skip if already converted to .mp4 or already extracted with matching byte size
                if (ext == ".wmv" && File.Exists(destMp4Path))
                {
                    extracted++;
                    continue;
                }

                if (File.Exists(destPath) && new FileInfo(destPath).Length == entry.Length)
                {
                    extracted++;
                    continue;
                }

                await using (var entryStream = entry.Open())
                await using (var outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await entryStream.CopyToAsync(outStream, ct);
                }

                if (convertWmvToMp4 && ext == ".wmv")
                {
                    await _mediaService.TranscodeWmvFileAsync(destPath, destMp4Path, deleteSourceWmv: true, ct);
                }
                else
                {
                    _mediaService.RegisterExtractedFile(destPath);
                }

                extracted++;
                if (extracted % 25 == 0 || extracted == totalEntries)
                {
                    UpdateStatus(
                        startedAt,
                        "EXTRACTING_MEDIA",
                        $"{archiveLabel}: rozpakowano {extracted} / {totalEntries} plików...",
                        0,
                        null,
                        extracted,
                        totalEntries);
                }
            }
        }
    }

    private async Task DownloadFileWithProgressAsync(
        HttpClient client,
        string url,
        string destinationPath,
        DateTimeOffset startedAt,
        string stage,
        string messagePrefix,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var netStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true);

        var buffer = new byte[131072];
        long downloaded = 0;
        var lastReport = StopwatchTimestamp();

        while (true)
        {
            var read = await netStream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            var nowTicks = StopwatchTimestamp();
            if (nowTicks - lastReport > TimeSpan.TicksPerMillisecond * 250)
            {
                lastReport = nowTicks;
                UpdateStatus(startedAt, stage, messagePrefix, downloaded, totalBytes, _currentStatus.ExtractedFiles, _currentStatus.TotalArchiveEntries);
            }
        }

        UpdateStatus(startedAt, stage, messagePrefix, downloaded, totalBytes, _currentStatus.ExtractedFiles, _currentStatus.TotalArchiveEntries);
    }

    private static void EnsureValidXlsxOrUnwrap(string downloadedPath, string targetXlsxPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetXlsxPath)!);

        using (var fs = File.OpenRead(downloadedPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            // Direct .xlsx contains xl/workbook.xml or xl/worksheets/sheet1.xml
            var isDirectXlsx = zip.Entries.Any(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase));
            if (isDirectXlsx)
            {
                fs.Close();
                File.Move(downloadedPath, targetXlsxPath, overwrite: true);
                return;
            }

            // If wrapped inside an outer .zip containing an .xlsx file, extract the .xlsx directly
            var nestedXlsx = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Pobrany plik z gov.pl nie jest arkuszem .xlsx ani archiwum zawierającym plik .xlsx.");

            nestedXlsx.ExtractToFile(targetXlsxPath, overwrite: true);
        }
    }

    private async Task<string> ResolveWorkingExcelUrlAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, _options.ExcelUrl);
            using var resp = await client.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode)
            {
                return _options.ExcelUrl;
            }
        }
        catch
        {
            // Fallback to scraping the official gov.pl page below
        }

        _logger.LogInformation("Wyszukiwanie aktualnego linku do pliku .xlsx na stronie {PageUrl}...", _options.GovPlPageUrl);
        var html = await client.GetStringAsync(_options.GovPlPageUrl, ct);
        var match = XlsxLinkRegex().Match(html);
        if (match.Success)
        {
            var href = match.Groups[1].Value;
            return href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : $"https://www.gov.pl{(href.StartsWith('/') ? "" : "/")}{href}";
        }

        return _options.ExcelUrl;
    }

    private async Task<List<string>> ResolveMediaArchiveUrlsAsync(HttpClient client, CancellationToken ct)
    {
        if (_options.MediaArchiveUrls.Count > 0)
        {
            return _options.MediaArchiveUrls;
        }

        var discovered = new List<string>();
        var html = await client.GetStringAsync(_options.GovPlPageUrl, ct);
        foreach (Match m in MediaLinkRegex().Matches(html))
        {
            var href = m.Groups[1].Value;
            var full = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : $"https://www.gov.pl{(href.StartsWith('/') ? "" : "/")}{href}";
            if (!discovered.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                discovered.Add(full);
            }
        }

        return discovered;
    }

    private string? FindLatestLocalExcelFile()
    {
        var candidates = new List<string>();
        if (Directory.Exists(_db.QuestionsDirectory))
        {
            candidates.AddRange(Directory.EnumerateFiles(_db.QuestionsDirectory, "*.xlsx"));
        }
        if (Directory.Exists(_db.DataDirectory))
        {
            candidates.AddRange(Directory.EnumerateFiles(_db.DataDirectory, "*.xlsx"));
        }

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private void UpdateStatus(
        DateTimeOffset startedAt,
        string stage,
        string message,
        long downloadedBytes,
        long? totalBytes,
        int extractedFiles,
        int? totalEntries)
    {
        _currentStatus = new SyncJobStatusDto(
            IsRunning: true,
            Stage: stage,
            Message: message,
            DownloadedBytes: downloadedBytes,
            TotalBytes: totalBytes,
            ExtractedFiles: extractedFiles,
            TotalArchiveEntries: totalEntries,
            StartedAtUtc: startedAt,
            FinishedAtUtc: null,
            LastError: null);
    }

    private static long StopwatchTimestamp() => DateTime.UtcNow.Ticks;

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [GeneratedRegex(@"<a[^>]+href=""([^""]*(?:attachment|pliki)[^""]*)""[^>]*>(?:(?!</a).)*?\.xlsx", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex XlsxLinkRegex();

    [GeneratedRegex(@"<a[^>]+href=""([^""]*(?:attachment|pliki)[^""]*)""[^>]*>(?:(?!</a).)*?Multimedia\s+do\s+pyta", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MediaLinkRegex();
}
