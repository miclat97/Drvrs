namespace Drvrs.Web.Models;

public sealed record CategoryCountDto(
    string Category,
    int TotalQuestions,
    int BasicQuestions,
    int SpecialistQuestions,
    bool ReadyForOfficialExam);

/// <summary>
/// Safe public metadata exposed to anonymous exam takers — never leaks server paths or internal sync state.
/// </summary>
public sealed record PublicStatusDto(
    bool HasQuestions,
    int TotalQuestions,
    int CategoryBQuestions,
    bool HasPartialMediaOnly,
    IReadOnlyList<CategoryCountDto> Categories);

/// <summary>
/// Full diagnostic status available exclusively to authenticated administrators via /api/admin/status.
/// </summary>
public sealed record AdminStatusDto(
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

public sealed record AdminLoginRequest(string Password);
