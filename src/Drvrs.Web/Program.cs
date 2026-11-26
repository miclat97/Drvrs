using Drvrs.Web.Configuration;
using Drvrs.Web.Data;
using Drvrs.Web.Models;
using Drvrs.Web.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DrvrsOptions>(builder.Configuration.GetSection(DrvrsOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<ExcelQuestionImporter>();
builder.Services.AddSingleton<QuestionRepository>();
builder.Services.AddSingleton<MediaService>();
builder.Services.AddSingleton<ExamSessionService>();
builder.Services.AddSingleton<GovDataSyncService>();

builder.Services.AddHttpClient("GovPl", client =>
{
    client.Timeout = TimeSpan.FromHours(6);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Drvrs-PrawoJazdy/1.0 (+https://gov.pl/web/infrastruktura/prawo-jazdy)");
});

var app = builder.Build();

// Initialize SQLite schema
var dbInit = app.Services.GetRequiredService<DatabaseInitializer>();
await dbInit.InitializeSchemaAsync();

// Support CLI maintenance modes for Linux sysadmins & Docker init scripts
if (args.Contains("--sync-questions", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--sync-all", StringComparer.OrdinalIgnoreCase))
{
    var syncService = app.Services.GetRequiredService<GovDataSyncService>();
    var includeMedia = args.Contains("--sync-all", StringComparer.OrdinalIgnoreCase);
    var convertWmv = args.Contains("--convert-wmv", StringComparer.OrdinalIgnoreCase) ||
                     app.Services.GetRequiredService<IOptions<DrvrsOptions>>().Value.ConvertWmvDuringSync;

    await syncService.RunSyncBlockingAsync(new StartSyncRequest(
        DownloadQuestions: true,
        DownloadMedia: includeMedia,
        ConvertWmvToMp4: convertWmv));
    return;
}

var importIdx = Array.FindIndex(args, a => a.Equals("--import-xlsx", StringComparison.OrdinalIgnoreCase));
if (importIdx >= 0 && importIdx + 1 < args.Length)
{
    var importer = app.Services.GetRequiredService<ExcelQuestionImporter>();
    await importer.ImportIfChangedAsync(args[importIdx + 1], force: true);
    return;
}

// Auto-import any local Excel file in data/ and optionally trigger startup gov.pl sync
var govSync = app.Services.GetRequiredService<GovDataSyncService>();
await govSync.InitializeLocalAndStartupDataAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok", runtime = Environment.Version.ToString() }));

api.MapGet("/status", async (GovDataSyncService sync, CancellationToken ct) =>
    Results.Ok(await sync.GetDatabaseStatusAsync(ct)));

api.MapPost("/exams", async (StartExamRequest? req, ExamSessionService exams, CancellationToken ct) =>
{
    try
    {
        var state = await exams.StartExamAsync(req ?? new StartExamRequest(), ct);
        return Results.Ok(state);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/exams/{examId:guid}", (Guid examId, ExamSessionService exams) =>
{
    var state = exams.GetExamState(examId);
    return state is not null
        ? Results.Ok(state)
        : Results.NotFound(new { error = "Nie znaleziono sesji egzaminacyjnej." });
});

api.MapPost("/exams/{examId:guid}/questions/{index:int}/video-started", (Guid examId, int index, ExamSessionService exams) =>
{
    try
    {
        return Results.Ok(exams.NotifyVideoStarted(examId, index));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

api.MapPost("/exams/{examId:guid}/questions/{index:int}/video-ended", (Guid examId, int index, ExamSessionService exams) =>
{
    try
    {
        return Results.Ok(exams.NotifyVideoEnded(examId, index));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

api.MapPost("/exams/{examId:guid}/questions/{index:int}/submit", (
    Guid examId,
    int index,
    SubmitAnswerRequest req,
    ExamSessionService exams) =>
{
    try
    {
        return Results.Ok(exams.SubmitAnswer(examId, index, req));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

api.MapPost("/exams/{examId:guid}/finish", (Guid examId, ExamSessionService exams) =>
{
    try
    {
        return Results.Ok(exams.FinishExamEarly(examId));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

api.MapGet("/questions", async (
    string? category,
    string? language,
    string? scope,
    int? points,
    string? search,
    int? page,
    int? pageSize,
    QuestionRepository repo,
    MediaService media,
    CancellationToken ct) =>
{
    var result = await repo.SearchQuestionsAsync(
        category ?? "B",
        language ?? "PL",
        scope,
        points,
        search,
        page ?? 1,
        pageSize ?? 20,
        media.HasMedia,
        ct);
    return Results.Ok(result);
});

api.MapMethods("/media/{*fileName}", ["GET", "HEAD"], async (
    string fileName,
    MediaService media,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    var resolved = await media.ResolveForBrowserAsync(fileName, ct);
    if (resolved is null)
    {
        return Results.NotFound(new { error = $"Nie znaleziono pliku multimedialnego: {fileName}" });
    }

    httpContext.Response.Headers.CacheControl = "public, max-age=86400";
    return (IResult)TypedResults.PhysicalFile(
        resolved.FullPath,
        contentType: resolved.ContentType,
        enableRangeProcessing: true);
});

api.MapPost("/admin/sync", (
    StartSyncRequest? req,
    HttpContext httpContext,
    IOptions<DrvrsOptions> opts,
    GovDataSyncService sync) =>
{
    if (!ValidateAdminAccess(httpContext, opts.Value, out var errorResult))
    {
        return errorResult!;
    }

    var started = sync.TryStartBackgroundSync(req ?? new StartSyncRequest());
    return started
        ? Results.Accepted("/api/status", sync.CurrentStatus)
        : Results.Conflict(new { error = "Synchronizacja jest już w toku.", status = sync.CurrentStatus });
});

api.MapPost("/admin/rescan", async (
    HttpContext httpContext,
    IOptions<DrvrsOptions> opts,
    GovDataSyncService sync,
    CancellationToken ct) =>
{
    if (!ValidateAdminAccess(httpContext, opts.Value, out var errorResult))
    {
        return errorResult!;
    }

    var importRes = await sync.RescanLocalFilesAsync(forceExcelReimport: true, ct);
    var status = await sync.GetDatabaseStatusAsync(ct);
    return Results.Ok(new { imported = importRes, status });
});

// Fallback SPA route to index.html for non-API paths
app.MapFallbackToFile("index.html");

app.Run();

static bool ValidateAdminAccess(HttpContext ctx, DrvrsOptions options, out IResult? errorResult)
{
    if (!options.EnableAdminEndpoints)
    {
        errorResult = Results.StatusCode(StatusCodes.Status403Forbidden);
        return false;
    }

    if (!string.IsNullOrWhiteSpace(options.AdminToken))
    {
        var provided = ctx.Request.Headers["X-Admin-Token"].FirstOrDefault();
        if (!string.Equals(provided, options.AdminToken, StringComparison.Ordinal))
        {
            errorResult = Results.Unauthorized();
            return false;
        }
    }

    errorResult = null;
    return true;
}

public partial class Program;
