using System.Net;
using System.Net.Http.Json;
using Drvrs.Web.Data;
using Drvrs.Web.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Drvrs.Tests;

public sealed class WebApiIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WebApplicationFactory<Program> _factory;

    public WebApiIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"drvrs_api_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "questions"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "media"));

        // Seed synthetic official-format Excel + image attachment BEFORE host boots so startup auto-import runs!
        var xlsxPath = Path.Combine(_tempDir, "questions", "katalog_testowy.xlsx");
        SyntheticXlsxBuilder.WriteOfficialFormatXlsx(xlsxPath, SyntheticXlsxBuilder.CreateFullCategoryBPool());
        File.WriteAllBytes(Path.Combine(_tempDir, "media", "foto_test.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Drvrs:DataDirectory"] = _tempDir,
                    ["Drvrs:PrewarmExamVideos"] = "false"
                });
            });
        });
    }

    [Fact]
    public async Task EndToEnd_AutoImportsExcelOnStartup_RunsExam_AndServesMediaWithRangeSupport()
    {
        using var client = _factory.CreateClient();

        // 1. Verify status shows auto-imported questions
        var status = await client.GetFromJsonAsync<DatabaseStatusDto>("/api/status");
        Assert.NotNull(status);
        Assert.True(status!.HasQuestions);
        Assert.Equal(44, status.CategoryBQuestions);

        // 2. Verify index.html SPA loads
        var htmlResp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, htmlResp.StatusCode);
        var html = await htmlResp.Content.ReadAsStringAsync();
        Assert.Contains("Egzamin teoretyczny na prawo jazdy kat. B", html);

        // 3. Start an exam and verify CorrectAnswer is NOT exposed during active exam
        var startResp = await client.PostAsJsonAsync("/api/exams", new StartExamRequest("B", "PL"));
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var rawJson = await startResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"correctAnswer\"", rawJson, StringComparison.OrdinalIgnoreCase);

        var examState = await startResp.Content.ReadFromJsonAsync<ExamStateResponse>();
        Assert.NotNull(examState);
        Assert.False(examState!.IsCompleted);
        Assert.Equal(0, examState.CurrentQuestion!.Index);

        // 4. Verify study questions search endpoint
        var studyPage = await client.GetFromJsonAsync<StudyQuestionsPageDto>("/api/questions?category=B&scope=SPECIALIST&points=2");
        Assert.NotNull(studyPage);
        Assert.Equal(6, studyPage!.TotalCount); // Including the "Specajlistyczny" row!

        // 5. Verify media endpoint serves image with Accept-Ranges: bytes
        var mediaResp = await client.GetAsync("/api/media/foto_test.jpg");
        Assert.Equal(HttpStatusCode.OK, mediaResp.StatusCode);
        Assert.Equal("image/jpeg", mediaResp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("bytes", mediaResp.Headers.AcceptRanges);
    }

    public void Dispose()
    {
        _factory.Dispose();
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
