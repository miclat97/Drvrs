using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Drvrs.Web.Configuration;
using Drvrs.Web.Data;
using Drvrs.Web.Models;
using Microsoft.Extensions.Options;

namespace Drvrs.Web.Services;

/// <summary>
/// Handles local file auto-import as well as resumable automated downloading from gov.pl.
/// Archives are processed sequentially one-by-one and permanently deleted immediately after full unpacking to reclaim disk space,
/// while surviving container restarts cleanly via HTTP Range resume and per-archive completion tracking.
/// </summary>
public sealed partial class GovDataSyncService
{
    private readonly DatabaseInitializer _db;
    private readonly ExcelQuestionImporter _importer;
    private readonly QuestionRepository _repository;
    private readonly MediaService _mediaService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _appLifetime;
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
        IHostApplicationLifetime appLifetime,
        IOptions<DrvrsOptions> options,
        ILogger<GovDataSyncService> logger)
    {
        _db = db;
        _importer = importer;
        _repository = repository;
        _mediaService = mediaService;
        _httpClientFactory = httpClientFactory;
        _appLifetime = appLifetime;
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
    /// Also checks actual media coverage so interrupted Docker downloads/extractions automatically resume on restart!
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
        var distinctMedia = totalQuestions > 0
            ? await _repository.GetAllDistinctMediaFileNamesAsync(ct)
            : [];
        var availableMedia = distinctMedia.Count(m => _mediaService.HasMedia(m));

        _logger.LogInformation(
            "Stan bazy przy starcie: {Questions} pytań w SQLite, {AvailableMedia} / {TotalMedia} załączników multimedialnych dostępnych w {MediaDir}.",
            totalQuestions, availableMedia, distinctMedia.Count, _db.MediaDirectory);

        var needQuestions = _options.AutoSyncQuestionsOnStartup && totalQuestions == 0;
        // Resume media sync if fewer than 99% of referenced media files are on disk (handles interrupted restarts!)
        var isMediaIncomplete = distinctMedia.Count == 0
            ? _mediaService.AvailableLogicalMediaCount == 0
            : availableMedia < (int)(distinctMedia.Count * 0.99);
        var needMedia = _options.AutoSyncMediaOnStartup && isMediaIncomplete;

        if (needQuestions || needMedia)
        {
            _logger.LogInformation(
                "Uruchamiam automatyczną synchronizację w tle (PobierzPytania={NeedQuestions}, PobierzMultimedia={NeedMedia}, BrakujePlików={Missing})...",
                needQuestions, needMedia, Math.Max(0, distinctMedia.Count - availableMedia));

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
                await RunSyncCoreAsync(request, startedAt, _appLifetime.ApplicationStopping);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Synchronizacja z gov.pl została wstrzymana (zatrzymanie kontenera/aplikacji) — zostanie wznowiona przy kolejnym starcie.");
                _currentStatus = _currentStatus with
                {
                    IsRunning = false,
                    Stage = "INTERRUPTED",
                    Message = "Wstrzymano synchronizację (zostanie wznowiona po restarcie).",
                    FinishedAtUtc = DateTimeOffset.UtcNow
                };
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
                await DownloadFileResumableAsync(client, excelUrl, tempXlsx, startedAt, "DOWNLOADING_EXCEL", "Baza pytań (.xlsx)", allowResume: false, ct);
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

            // If media directory was wiped empty by user, reset per-archive completion markers
            if (_mediaService.AvailableLogicalMediaCount == 0)
            {
                foreach (var u in archiveUrls)
                {
                    await _db.SetMetadataAsync(GetArchiveDoneKey(u), "false", ct);
                }
            }

            for (var i = 0; i < archiveUrls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var url = archiveUrls[i];
                var archiveDoneKey = GetArchiveDoneKey(url);
                var archiveLabel = $"Archiwum multimediów {i + 1}/{archiveUrls.Count}";

                // Skip archive if it was already 100% unpacked in an earlier run and media folder wasn't cleared
                var alreadyDone = await _db.GetMetadataAsync(archiveDoneKey, ct);
                if (string.Equals(alreadyDone, "true", StringComparison.OrdinalIgnoreCase) &&
                    _mediaService.AvailableLogicalMediaCount > 2500)
                {
                    _logger.LogInformation("{Label}: archiwum zostało już wcześniej w pełni rozpakowane — pomijam.", archiveLabel);
                    continue;
                }

                var urlHash = ComputeShortHash(url);
                var tempZipPath = Path.Combine(_db.TempDirectory, $"media_archive_{i + 1}_{urlHash}.zip");

                try
                {
                    await DownloadFileResumableAsync(
                        client,
                        url,
                        tempZipPath,
                        startedAt,
                        "DOWNLOADING_MEDIA",
                        archiveLabel,
                        allowResume: true,
                        ct);

                    await ExtractAndCleanZipAsync(tempZipPath, archiveLabel, ibm852, convertWmv, startedAt, ct);

                    // Mark archive completed and IMMEDIATELY delete the multi-GB .zip file to reclaim disk space!
                    await _db.SetMetadataAsync(archiveDoneKey, "true", ct);
                    SafeDeleteFile(tempZipPath);
                    _logger.LogInformation("{Label}: rozpakowano w 100% i trwale usunięto plik źródłowy .zip z dysku.", archiveLabel);
                }
                catch (InvalidDataException ex)
                {
                    // Corrupted zip on disk -> delete it so next attempt downloads cleanly
                    _logger.LogError(ex, "{Label}: wykryto uszkodzone archiwum .zip — usuwam plik tymczasowy.", archiveLabel);
                    SafeDeleteFile(tempZipPath);
                    throw;
                }
            }

            _mediaService.RebuildIndex();
            var distinctMedia = await _repository.GetAllDistinctMediaFileNamesAsync(ct);
            var availableAfter = distinctMedia.Count(m => _mediaService.HasMedia(m));
            _logger.LogInformation(
                "Synchronizacja multimediów zakończona! Dostępne pliki: {Available} / {Total}.",
                availableAfter, distinctMedia.Count);
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
        int newlyWritten = 0;

        using (var fs = File.OpenRead(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: fallbackEncoding))
        {
            var fileEntries = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && !e.FullName.EndsWith('/'))
                .ToList();

            totalEntries = fileEntries.Count;
            _logger.LogInformation("{Label}: rozpoczynam rozpakowywanie {Total} plików do {MediaDir}...", archiveLabel, totalEntries, _db.MediaDirectory);
            UpdateStatus(startedAt, "EXTRACTING_MEDIA", $"{archiveLabel}: rozpakowywanie {totalEntries} plików...", 0, null, 0, totalEntries);

            foreach (var entry in fileEntries)
            {
                ct.ThrowIfCancellationRequested();

                var flatFileName = Path.GetFileName(entry.FullName.Replace('\\', '/')).Normalize(NormalizationForm.FormC);
                if (string.IsNullOrWhiteSpace(flatFileName))
                {
                    continue;
                }

                var ext = Path.GetExtension(flatFileName).ToLowerInvariant();
                var stem = Path.GetFileNameWithoutExtension(flatFileName);
                var destPath = Path.Combine(_db.MediaDirectory, flatFileName);
                var destMp4Path = Path.Combine(_db.MediaDirectory, $"{stem}.mp4");

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
                newlyWritten++;

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

                if (extracted % 500 == 0 || extracted == totalEntries)
                {
                    _logger.LogInformation("{Label}: postęp rozpakowywania {Extracted} / {Total} plików (nowo zapisane: {New})...", archiveLabel, extracted, totalEntries, newlyWritten);
                }
            }
        }
    }

    private async Task DownloadFileResumableAsync(
        HttpClient client,
        string url,
        string destinationPath,
        DateTimeOffset startedAt,
        string stage,
        string label,
        bool allowResume,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        long? remoteTotalBytes = null;
        var supportsRange = false;

        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResp = await client.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (headResp.IsSuccessStatusCode)
            {
                remoteTotalBytes = headResp.Content.Headers.ContentLength;
                supportsRange = headResp.Headers.AcceptRanges.Contains("bytes");
            }
        }
        catch
        {
            // Continue with GET if HEAD fails
        }

        long existingBytes = 0;
        if (allowResume && File.Exists(destinationPath))
        {
            existingBytes = new FileInfo(destinationPath).Length;
            if (remoteTotalBytes.HasValue && existingBytes == remoteTotalBytes.Value && existingBytes > 0)
            {
                _logger.LogInformation("{Label}: pełny plik ({SizeMB:F1} MB) znajduje się już na dysku — przechodzę od razu do rozpakowywania.", label, existingBytes / (1024.0 * 1024.0));
                UpdateStatus(startedAt, stage, $"{label}: pobrano 100%", existingBytes, remoteTotalBytes, 0, null);
                return;
            }

            if (remoteTotalBytes.HasValue && existingBytes > remoteTotalBytes.Value)
            {
                SafeDeleteFile(destinationPath);
                existingBytes = 0;
            }
        }

        using var getReq = new HttpRequestMessage(HttpMethod.Get, url);
        var canResume = allowResume && supportsRange && existingBytes > 0;
        if (canResume)
        {
            getReq.Headers.Range = new RangeHeaderValue(existingBytes, null);
            _logger.LogInformation("{Label}: wznawiam przerwane pobieranie od {DoneMB:F1} MB...", label, existingBytes / (1024.0 * 1024.0));
        }
        else
        {
            existingBytes = 0;
            _logger.LogInformation("{Label}: rozpoczynam pobieranie z {Url}...", label, url);
        }

        using var response = await client.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var isPartial = response.StatusCode == HttpStatusCode.PartialContent;
        if (!isPartial)
        {
            existingBytes = 0;
        }

        var contentLen = response.Content.Headers.ContentLength;
        var totalBytes = contentLen.HasValue ? existingBytes + contentLen.Value : remoteTotalBytes;

        await using var netStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(
            destinationPath,
            isPartial ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            131072,
            useAsync: true);

        var buffer = new byte[131072];
        long downloaded = existingBytes;
        var lastUiReport = DateTime.UtcNow.Ticks;
        var lastLoggedDecile = totalBytes is > 0 ? (int)((downloaded * 10) / totalBytes.Value) : -1;

        while (true)
        {
            var read = await netStream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - lastUiReport > TimeSpan.TicksPerMillisecond * 250)
            {
                lastUiReport = nowTicks;
                UpdateStatus(startedAt, stage, $"{label}: pobieranie z gov.pl...", downloaded, totalBytes, 0, null);
            }

            if (totalBytes is > 0)
            {
                var decile = (int)((downloaded * 10) / totalBytes.Value);
                if (decile > lastLoggedDecile)
                {
                    lastLoggedDecile = decile;
                    _logger.LogInformation(
                        "{Label}: pobrano {Pct}% ({DoneMB:F0} MB / {TotalMB:F0} MB)...",
                        label, decile * 10, downloaded / (1024.0 * 1024.0), totalBytes.Value / (1024.0 * 1024.0));
                }
            }
        }

        UpdateStatus(startedAt, stage, $"{label}: pobieranie zakończone.", downloaded, totalBytes, 0, null);
    }

    private static void EnsureValidXlsxOrUnwrap(string downloadedPath, string targetXlsxPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetXlsxPath)!);

        using (var fs = File.OpenRead(downloadedPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var isDirectXlsx = zip.Entries.Any(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase));
            if (isDirectXlsx)
            {
                fs.Close();
                File.Move(downloadedPath, targetXlsxPath, overwrite: true);
                return;
            }

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

    private static string GetArchiveDoneKey(string url) => $"MediaArchiveDone_{ComputeShortHash(url)}";

    private static string ComputeShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes)[..12];
    }

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
