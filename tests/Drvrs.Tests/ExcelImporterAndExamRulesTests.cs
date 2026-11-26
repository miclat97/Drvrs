using Drvrs.Web.Configuration;
using Drvrs.Web.Data;
using Drvrs.Web.Models;
using Drvrs.Web.Services;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drvrs.Tests;

public sealed class ExcelImporterAndExamRulesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DatabaseInitializer _db;
    private readonly ExcelQuestionImporter _importer;
    private readonly QuestionRepository _repo;
    private readonly MediaService _media;
    private readonly ManualTimeProvider _clock;
    private readonly ExamSessionService _examService;

    public ExcelImporterAndExamRulesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"drvrs_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var opts = Options.Create(new DrvrsOptions
        {
            DataDirectory = _tempDir,
            PrewarmExamVideos = false
        });
        var env = new HostingEnvironment { ContentRootPath = _tempDir };

        _db = new DatabaseInitializer(opts, env);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();

        // Create dummy media files so staged video timer activates
        File.WriteAllBytes(Path.Combine(_db.MediaDirectory, "klip_testowy.wmv"), [0x30, 0x26, 0xB2, 0x75]);
        File.WriteAllBytes(Path.Combine(_db.MediaDirectory, "foto_test.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);

        _importer = new ExcelQuestionImporter(_db, NullLogger<ExcelQuestionImporter>.Instance);
        _repo = new QuestionRepository(_db);
        _media = new MediaService(_db, opts, NullLogger<MediaService>.Instance);
        _clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        _examService = new ExamSessionService(_repo, _media, _clock, opts);
    }

    [Fact]
    public async Task Importer_ImportsOfficialXlsx_SeparatesCategoryBFromB1_AndSkipsUnchangedSha256()
    {
        var xlsxPath = Path.Combine(_tempDir, "pytania.xlsx");
        var rows = SyntheticXlsxBuilder.CreateFullCategoryBPool();
        SyntheticXlsxBuilder.WriteOfficialFormatXlsx(xlsxPath, rows);

        var firstImport = await _importer.ImportIfChangedAsync(xlsxPath);
        Assert.False(firstImport.SkippedBecauseUnchanged);
        Assert.Equal(rows.Count, firstImport.ImportedCount);

        var secondImport = await _importer.ImportIfChangedAsync(xlsxPath);
        Assert.True(secondImport.SkippedBecauseUnchanged);

        var stats = await _repo.GetCategoryStatsAsync();
        var catB = Assert.Single(stats, c => c.Category == "B");
        // B1-only question #99999 must NOT leak into Category B!
        Assert.Equal(rows.Count - 1, catB.TotalQuestions);
        Assert.True(catB.ReadyForOfficialExam);
    }

    [Fact]
    public async Task OfficialExam_HasExact20BasicAnd12Specialist_74MaxPoints_And68PassThreshold()
    {
        var xlsxPath = Path.Combine(_tempDir, "pytania.xlsx");
        SyntheticXlsxBuilder.WriteOfficialFormatXlsx(xlsxPath, SyntheticXlsxBuilder.CreateFullCategoryBPool());
        await _importer.ImportIfChangedAsync(xlsxPath);

        var drawn = await _repo.DrawOfficialExamQuestionsAsync("B");
        Assert.Equal(32, drawn.Count);

        var basic = drawn.Take(20).ToList();
        var spec = drawn.Skip(20).ToList();

        Assert.All(basic, q =>
        {
            Assert.Equal(ExamScope.Basic, q.Scope);
            Assert.Equal(QuestionType.YesNo, q.Type);
        });
        Assert.Equal(10, basic.Count(q => q.Points == 3));
        Assert.Equal(6, basic.Count(q => q.Points == 2));
        Assert.Equal(4, basic.Count(q => q.Points == 1));

        Assert.All(spec, q =>
        {
            Assert.Equal(ExamScope.Specialist, q.Scope);
            Assert.Equal(QuestionType.SingleChoice, q.Type);
        });
        Assert.Equal(6, spec.Count(q => q.Points == 3));
        Assert.Equal(4, spec.Count(q => q.Points == 2));
        Assert.Equal(2, spec.Count(q => q.Points == 1));

        Assert.Equal(74, drawn.Sum(q => q.Points));
    }

    [Fact]
    public async Task VideoQuestion_DoesNotCountVideoDuration_Allows20sRead_FullVideo_Then15sAnswer()
    {
        // Build pool where ALL 3-pt, 2-pt, 1-pt basic questions use video so Question #0 is guaranteed to be a video question
        var rows = SyntheticXlsxBuilder.CreateFullCategoryBPool()
            .Select(r => r.ZakresStruktury.StartsWith("PODST", StringComparison.OrdinalIgnoreCase)
                ? r with { Media = "klip_testowy.wmv", PoprawnaOdp = "T" }
                : r)
            .ToList();

        var xlsxPath = Path.Combine(_tempDir, "video_pool.xlsx");
        SyntheticXlsxBuilder.WriteOfficialFormatXlsx(xlsxPath, rows);
        await _importer.ImportIfChangedAsync(xlsxPath, force: true);

        var state = await _examService.StartExamAsync(new StartExamRequest("B", "PL"));
        Assert.NotNull(state.CurrentQuestion);
        Assert.True(state.CurrentQuestion!.UsesStagedVideoTimer);
        Assert.Equal("READING_BEFORE_VIDEO", state.CurrentQuestion.Phase);
        Assert.Equal(20, state.CurrentQuestion.PhaseRemainingSeconds);

        // User reads question for 12 seconds, then clicks video
        _clock.Advance(TimeSpan.FromSeconds(12));
        state = _examService.NotifyVideoStarted(state.ExamId, 0);
        Assert.Equal("PLAYING_VIDEO", state.CurrentQuestion!.Phase);

        // Video plays for 38 seconds — must NOT consume the 15s answer time!
        _clock.Advance(TimeSpan.FromSeconds(38));
        state = _examService.GetExamState(state.ExamId)!;
        Assert.Equal(0, state.CurrentQuestion!.Index);
        Assert.Equal("PLAYING_VIDEO", state.CurrentQuestion.Phase);

        // Video finishes -> 15s answer countdown starts
        state = _examService.NotifyVideoEnded(state.ExamId, 0);
        Assert.Equal("ANSWERING_AFTER_VIDEO", state.CurrentQuestion!.Phase);
        Assert.Equal(15, state.CurrentQuestion.PhaseRemainingSeconds);

        // User answers after 14 seconds -> accepted and earns full points!
        _clock.Advance(TimeSpan.FromSeconds(14));
        state = _examService.SubmitAnswer(state.ExamId, 0, new SubmitAnswerRequest("T"));
        Assert.Equal(1, state.CurrentQuestion!.Index);

        // Attempting to go back to Question 0 is strictly forbidden
        Assert.Throws<InvalidOperationException>(() =>
            _examService.SubmitAnswer(state.ExamId, 0, new SubmitAnswerRequest("N")));

        var finished = _examService.FinishExamEarly(state.ExamId);
        Assert.True(finished.Summary!.Questions[0].IsCorrect);
    }

    [Fact]
    public async Task Global25MinuteCutoff_LocksExamImmediately_AndEvaluates68PointThreshold()
    {
        // All Basic = "T", all Specialist = "A" so we can deterministically score exact points
        var rows = SyntheticXlsxBuilder.CreateFullCategoryBPool()
            .Select(r => r.ZakresStruktury.StartsWith("PODST", StringComparison.OrdinalIgnoreCase)
                ? r with { Media = "", PoprawnaOdp = "T" }
                : r with { Media = "", PoprawnaOdp = "A" })
            .ToList();

        var xlsxPath = Path.Combine(_tempDir, "scoring_pool.xlsx");
        SyntheticXlsxBuilder.WriteOfficialFormatXlsx(xlsxPath, rows);
        await _importer.ImportIfChangedAsync(xlsxPath, force: true);

        // Exam 1: Answer all 32 right EXCEPT 2x3pt questions (= 68/74 -> PASSED!)
        var exam1 = await _examService.StartExamAsync(new StartExamRequest("B", "PL"));
        var intentionallyWrong3Pt = 0;
        while (!exam1.IsCompleted)
        {
            var q = exam1.CurrentQuestion!;
            var correctAns = q.Type == "YES_NO" ? "T" : "A";
            var wrongAns = q.Type == "YES_NO" ? "N" : "B";

            string chosen;
            if (q.Points == 3 && intentionallyWrong3Pt < 2)
            {
                chosen = wrongAns;
                intentionallyWrong3Pt++;
            }
            else
            {
                chosen = correctAns;
            }

            exam1 = _examService.SubmitAnswer(exam1.ExamId, q.Index, new SubmitAnswerRequest(chosen));
        }

        Assert.Equal(68, exam1.Summary!.TotalScore);
        Assert.True(exam1.Summary.Passed);

        // Exam 2: Advance 25 minutes + 1 second -> Global timeout locks exam immediately!
        var exam2 = await _examService.StartExamAsync(new StartExamRequest("B", "PL"));
        _clock.Advance(TimeSpan.FromMinutes(25).Add(TimeSpan.FromSeconds(1)));
        var lateSubmit = _examService.SubmitAnswer(exam2.ExamId, 0, new SubmitAnswerRequest("T"));
        Assert.True(lateSubmit.IsCompleted);
        Assert.Equal("UPŁYNĄŁ_CZAS_EGZAMINU", lateSubmit.CompletionReason);
        Assert.Equal(0, lateSubmit.Summary!.TotalScore);
        Assert.False(lateSubmit.Summary.Passed);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
