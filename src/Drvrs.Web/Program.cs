using System.Threading.RateLimiting;
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
builder.Services.AddSingleton<AdminAuthService>();

builder.Services.AddHttpClient("GovPl", client =>
{
    client.Timeout = TimeSpan.FromHours(6);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Drvrs-PrawoJazdy/1.0 (+https://gov.pl/web/infrastruktura/prawo-jazdy)");
});

// Cloudflare Tunnel / public internet rate limiting (keyed by CF-Connecting-IP when behind local tunnel)
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.AddPolicy("AdminAuth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"admin:{AdminAuthService.ResolveClientIpKey(httpContext)}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    limiter.AddPolicy("ExamStart", httpContext =>
    {
        var opts = httpContext.RequestServices.GetRequiredService<IOptions<DrvrsOptions>>().Value;
        var limit = Math.Max(3, opts.ExamStartsPerMinutePerIp);
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"exam:{AdminAuthService.ResolveClientIpKey(httpContext)}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });

    limiter.AddPolicy("GeneralApi", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"api:{AdminAuthService.ResolveClientIpKey(httpContext)}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
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

// Security headers for public internet / Cloudflare Tunnel exposure
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    headers.ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' https://static.cloudflareinsights.com https://ajax.cloudflare.com; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data:; " +
        "media-src 'self'; " +
        "connect-src 'self' https://cloudflareinsights.com; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var name = ctx.File.Name;
        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    }
});
app.UseRateLimiter();

var api = app.MapGroup("/api").RequireRateLimiting("GeneralApi");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Public status endpoint — never exposes server filesystem paths or admin sync details
api.MapGet("/status", async (GovDataSyncService sync, CancellationToken ct) =>
    Results.Ok(await sync.GetPublicStatusAsync(ct)));

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
}).RequireRateLimiting("ExamStart");

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
        return Results.NotFound(new { error = "Nie znaleziono pliku multimedialnego." });
    }

    httpContext.Response.Headers.CacheControl = "public, max-age=86400";
    return (IResult)TypedResults.PhysicalFile(
        resolved.FullPath,
        contentType: resolved.ContentType,
        enableRangeProcessing: true);
});

// =============================================================================
// Hardened Admin Endpoints (/api/admin/*) — Disabled unless AdminToken is set!
// =============================================================================
var adminApi = api.MapGroup("/admin");

adminApi.MapGet("/session", (HttpContext ctx, AdminAuthService auth) =>
    Results.Ok(new
    {
        enabled = auth.IsAdminConfiguredAndEnabled,
        authenticated = auth.IsRequestAuthorized(ctx)
    }));

adminApi.MapPost("/login", (AdminLoginRequest req, HttpContext ctx, AdminAuthService auth) =>
{
    if (!auth.IsAdminConfiguredAndEnabled)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (!auth.VerifyPasswordConstantTime(req.Password))
    {
        return Results.Unauthorized();
    }

    auth.IssueAdminCookie(ctx);
    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("AdminAuth");

adminApi.MapPost("/logout", (HttpContext ctx, AdminAuthService auth) =>
{
    auth.ClearAdminCookie(ctx);
    return Results.Ok(new { authenticated = false });
});

adminApi.MapGet("/status", async (
    HttpContext ctx,
    AdminAuthService auth,
    GovDataSyncService sync,
    CancellationToken ct) =>
{
    if (!EnsureAdminAuthorized(ctx, auth, out var denied)) return denied!;
    return Results.Ok(await sync.GetAdminStatusAsync(ct));
});

adminApi.MapPost("/sync", (
    StartSyncRequest? req,
    HttpContext ctx,
    AdminAuthService auth,
    GovDataSyncService sync) =>
{
    if (!EnsureAdminAuthorized(ctx, auth, out var denied)) return denied!;

    var started = sync.TryStartBackgroundSync(req ?? new StartSyncRequest());
    return started
        ? Results.Accepted("/api/admin/status", sync.CurrentStatus)
        : Results.Conflict(new { error = "Synchronizacja jest już w toku.", status = sync.CurrentStatus });
}).RequireRateLimiting("AdminAuth");

adminApi.MapPost("/rescan", async (
    HttpContext ctx,
    AdminAuthService auth,
    GovDataSyncService sync,
    CancellationToken ct) =>
{
    if (!EnsureAdminAuthorized(ctx, auth, out var denied)) return denied!;

    var importRes = await sync.RescanLocalFilesAsync(forceExcelReimport: true, ct);
    var status = await sync.GetAdminStatusAsync(ct);
    return Results.Ok(new { imported = importRes, status });
}).RequireRateLimiting("AdminAuth");

// Fallback SPA route (including /admin) to index.html
app.MapFallbackToFile("index.html");

app.Run();

static bool EnsureAdminAuthorized(HttpContext ctx, AdminAuthService auth, out IResult? denied)
{
    if (!auth.IsAdminConfiguredAndEnabled)
    {
        denied = Results.StatusCode(StatusCodes.Status403Forbidden);
        return false;
    }

    if (!auth.IsRequestAuthorized(ctx))
    {
        denied = Results.Unauthorized();
        return false;
    }

    denied = null;
    return true;
}

public partial class Program;
