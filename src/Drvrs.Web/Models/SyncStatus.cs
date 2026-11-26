namespace Drvrs.Web.Models;

public sealed record CategoryCountDto(
    string Category,
    int TotalQuestions,
    int BasicQuestions,
    int SpecialistQuestions,
    bool ReadyForOfficialExam);

public sealed record DatabaseStatusDto(
    bool HasQuestions,
    int TotalQuestions,
    int CategoryBQuestions,
    int TotalDistinctMediaReferenced,
    int AvailableMediaFilesCount,
    int TranscodedMp4Count,
    bool FfmpegAvailable,
    string? SourceExcelFile,
    DateTimeOffset? LastImportedAtUtc,
    string DataDirectoryPath,
    string MediaDirectoryPath,
    bool AdminSyncEnabled,
    bool AdminTokenRequired,
    IReadOnlyList<CategoryCountDto> Categories,
    SyncJobStatusDto SyncJob);

public sealed record SyncJobStatusDto(
    bool IsRunning,
    string Stage,
    string Message,
    long DownloadedBytes,
    long? TotalBytes,
    int ExtractedFiles,
    int? TotalArchiveEntries,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? LastError);

public sealed record StartSyncRequest(
    bool DownloadQuestions = true,
    bool DownloadMedia = false,
    bool ConvertWmvToMp4 = false);
