using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Drvrs.Web.Configuration;
using Drvrs.Web.Data;
using Microsoft.Extensions.Options;

namespace Drvrs.Web.Services;

public sealed record ResolvedMediaFile(string FullPath, string ContentType);

/// <summary>
/// Indexes multimedia attachments (case-insensitively and Unicode-normalized) and transparently transcodes
/// legacy Windows Media Video (.wmv) attachments from gov.pl into browser-native H.264 (.mp4) via ffmpeg.
/// </summary>
public sealed class MediaService
{
    private readonly DatabaseInitializer _db;
    private readonly DrvrsOptions _options;
    private readonly ILogger<MediaService> _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _transcodeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _ffmpegConcurrency = new(Math.Max(1, Math.Min(4, Environment.ProcessorCount)), Math.Max(1, Math.Min(4, Environment.ProcessorCount)));

    private volatile Dictionary<string, string> _fileIndex = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, string> _stemMp4Index = new(StringComparer.OrdinalIgnoreCase);
    private bool? _ffmpegAvailable;

    public MediaService(
        DatabaseInitializer db,
        IOptions<DrvrsOptions> options,
        ILogger<MediaService> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
        RebuildIndex();
    }

    public bool IsFfmpegAvailable
    {
        get
        {
            if (_ffmpegAvailable.HasValue)
            {
                return _ffmpegAvailable.Value;
            }

            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = _options.FfmpegExecutable,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(3000);
                _ffmpegAvailable = proc is { ExitCode: 0 };
            }
            catch
            {
                _ffmpegAvailable = false;
            }

            return _ffmpegAvailable.Value;
        }
    }

    public int AvailableLogicalMediaCount => _fileIndex.Count;

    public int CachedMp4Count
    {
        get
        {
            if (!Directory.Exists(_db.MediaCacheDirectory)) return 0;
            return Directory.EnumerateFiles(_db.MediaCacheDirectory, "*.mp4").Count();
        }
    }

    public void RebuildIndex()
    {
        var newIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newStemMp4 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(_db.MediaDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_db.MediaDirectory, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(path);
                if (fileName.StartsWith('.') || fileName.EndsWith(".tmp.mp4", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var normalizedName = fileName.Normalize(NormalizationForm.FormC);
                var ext = Path.GetExtension(normalizedName).ToLowerInvariant();
                var stem = Path.GetFileNameWithoutExtension(normalizedName);

                var isInCacheDir = path.StartsWith(_db.MediaCacheDirectory, StringComparison.Ordinal);
                if (ext == ".mp4")
                {
                    newStemMp4[stem] = path;
                    if (!isInCacheDir)
                    {
                        newIndex[normalizedName] = path;
                        // Also allow resolving original .wmv name directly to pre-converted .mp4
                        newIndex.TryAdd($"{stem}.wmv", path);
                    }
                    else
                    {
                        // If source .wmv was replaced/deleted after transcoding, keep logical .wmv entry alive
                        newIndex.TryAdd($"{stem}.wmv", path);
                    }
                }
                else
                {
                    newIndex[normalizedName] = path;
                }
            }
        }

        _fileIndex = newIndex;
        _stemMp4Index = newStemMp4;
    }

    public void RegisterExtractedFile(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath).Normalize(NormalizationForm.FormC);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(fileName);

        lock (_transcodeLocks)
        {
            _fileIndex[fileName] = fullPath;
            if (ext == ".mp4")
            {
                _stemMp4Index[stem] = fullPath;
                _fileIndex[$"{stem}.wmv"] = fullPath;
            }
        }
    }

    public bool HasMedia(string? requestedFileName)
    {
        if (string.IsNullOrWhiteSpace(requestedFileName))
        {
            return false;
        }

        var cleanName = SanitizeFileName(requestedFileName);
        if (_fileIndex.ContainsKey(cleanName))
        {
            return true;
        }

        var stem = Path.GetFileNameWithoutExtension(cleanName);
        if (_stemMp4Index.ContainsKey(stem))
        {
            return true;
        }

        // Fast direct-path check in case file was dropped into data/media after startup without index refresh
        var directPath = Path.Combine(_db.MediaDirectory, cleanName);
        if (File.Exists(directPath))
        {
            RegisterExtractedFile(directPath);
            return true;
        }

        var directMp4 = Path.Combine(_db.MediaDirectory, $"{stem}.mp4");
        if (File.Exists(directMp4))
        {
            RegisterExtractedFile(directMp4);
            return true;
        }

        return false;
    }

    public async Task<ResolvedMediaFile?> ResolveForBrowserAsync(string requestedFileName, CancellationToken ct = default)
    {
        var cleanName = SanitizeFileName(requestedFileName);
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            return null;
        }

        var ext = Path.GetExtension(cleanName).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(cleanName);

        // If video (.wmv), prefer already-available .mp4 so HTML5 <video> plays in every browser
        if (ext == ".wmv")
        {
            if (_stemMp4Index.TryGetValue(stem, out var existingMp4) && File.Exists(existingMp4))
            {
                return new ResolvedMediaFile(existingMp4, "video/mp4");
            }

            var cachedMp4Path = Path.Combine(_db.MediaCacheDirectory, $"{stem}.mp4");
            if (File.Exists(cachedMp4Path))
            {
                RegisterExtractedFile(cachedMp4Path);
                return new ResolvedMediaFile(cachedMp4Path, "video/mp4");
            }
        }

        if (!_fileIndex.TryGetValue(cleanName, out var sourcePath) || !File.Exists(sourcePath))
        {
            RebuildIndex();
            if (!_fileIndex.TryGetValue(cleanName, out sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }
        }

        var actualExt = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (actualExt == ".wmv")
        {
            if (!IsFfmpegAvailable)
            {
                _logger.LogWarning("Plik {File} wymaga transkodowania WMV->MP4, ale narzędzie ffmpeg nie jest zainstalowane.", cleanName);
                return new ResolvedMediaFile(sourcePath, "video/x-ms-wmv");
            }

            var mp4Path = await EnsureMp4TranscodedAsync(sourcePath, stem, ct);
            return new ResolvedMediaFile(mp4Path, "video/mp4");
        }

        return new ResolvedMediaFile(sourcePath, GetContentType(actualExt));
    }

    public void PrewarmExamVideosInBackground(IEnumerable<string> mediaFileNames)
    {
        if (!_options.PrewarmExamVideos || !IsFfmpegAvailable)
        {
            return;
        }

        var wmvFiles = mediaFileNames
            .Where(m => !string.IsNullOrWhiteSpace(m) && Path.GetExtension(m).Equals(".wmv", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (wmvFiles.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var file in wmvFiles)
            {
                try
                {
                    await ResolveForBrowserAsync(file, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Nie udało się wstępnie przekonwertować klipu {File}.", file);
                }
            }
        });
    }

    public async Task<string> TranscodeWmvFileAsync(string wmvPath, string targetMp4Path, bool deleteSourceWmv, CancellationToken ct = default)
    {
        var tmpOutput = $"{targetMp4Path}.{Guid.NewGuid():N}.tmp.mp4";
        Directory.CreateDirectory(Path.GetDirectoryName(targetMp4Path)!);

        await _ffmpegConcurrency.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _options.FfmpegExecutable,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(wmvPath);
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add("veryfast");
            psi.ArgumentList.Add("-crf");
            psi.ArgumentList.Add("24");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("aac");
            psi.ArgumentList.Add("-b:a");
            psi.ArgumentList.Add("96k");
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add(tmpOutput);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Nie udało się uruchomić procesu ffmpeg.");

            var stdErrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stdErr = await stdErrTask;

            if (proc.ExitCode != 0 || !File.Exists(tmpOutput))
            {
                throw new InvalidOperationException($"Błąd transkodowania WMV -> MP4 ({Path.GetFileName(wmvPath)}): {stdErr}");
            }

            File.Move(tmpOutput, targetMp4Path, overwrite: true);

            if (deleteSourceWmv && File.Exists(wmvPath))
            {
                try
                {
                    File.Delete(wmvPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Nie udało się usunąć źródłowego pliku WMV po konwersji: {Path}", wmvPath);
                }
            }

            RegisterExtractedFile(targetMp4Path);
            return targetMp4Path;
        }
        finally
        {
            _ffmpegConcurrency.Release();
            if (File.Exists(tmpOutput))
            {
                try { File.Delete(tmpOutput); } catch { /* ignore */ }
            }
        }
    }

    private async Task<string> EnsureMp4TranscodedAsync(string sourceWmvPath, string stem, CancellationToken ct)
    {
        var targetMp4Path = _options.ReplaceWmvAfterTranscode
            ? Path.Combine(_db.MediaDirectory, $"{stem}.mp4")
            : Path.Combine(_db.MediaCacheDirectory, $"{stem}.mp4");

        if (File.Exists(targetMp4Path))
        {
            RegisterExtractedFile(targetMp4Path);
            return targetMp4Path;
        }

        var fileLock = _transcodeLocks.GetOrAdd(stem, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(ct);
        try
        {
            if (File.Exists(targetMp4Path))
            {
                RegisterExtractedFile(targetMp4Path);
                return targetMp4Path;
            }

            _logger.LogInformation("Transkoduję klip egzaminacyjny {File}.wmv -> MP4 (H.264)...", stem);
            return await TranscodeWmvFileAsync(sourceWmvPath, targetMp4Path, _options.ReplaceWmvAfterTranscode, ct);
        }
        finally
        {
            fileLock.Release();
        }
    }

    private static string SanitizeFileName(string raw)
    {
        var decoded = Uri.UnescapeDataString(raw).Trim();
        var fileOnly = decoded.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        return fileOnly.Normalize(NormalizationForm.FormC);
    }

    private static string GetContentType(string ext) => ext switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".wmv" => "video/x-ms-wmv",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream"
    };
}
