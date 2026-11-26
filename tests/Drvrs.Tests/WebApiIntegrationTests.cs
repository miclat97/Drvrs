using System.Net;
using System.Net.Http.Json;
using Drvrs.Web.Data;
using Drvrs.Web.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Drvrs.Tests;

public sealed class WebApiIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WebApplicationFactory<Program> _defaultLockedFactory;

    public WebApiIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"drvrs_api_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "questions"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "media"));

        var xlsxPath = Path.Combine(_tempDir, "questions", "katalog_testowy.xlsx");
        SyntheticXlsxBuilder.WriteOfficialFormatXlsx(xlsxPath, SyntheticXlsxBuilder.CreateFullCategoryBPool());
        File.WriteAllBytes(Path.Combine(_tempDir, "media", "foto_test.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);

        _defaultLockedFactory = CreateFactory(adminToken: "");
    }

    private WebApplicationFactory<Program> CreateFactory(string adminToken) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Drvrs:DataDirectory"] = _tempDir,
                    ["Drvrs:PrewarmExamVideos"] = "false",
                    ["Drvrs:AdminToken"] = adminToken
                });
            });
        });

    [Fact]
    public async Task EndToEnd_PublicStatusNeverLeaksPaths_AdminIsLockedByDefault_AndExamWorks()
    {
        using var client = _defaultLockedFactory.CreateClient();

        // 1. Public /api/status returns question counts WITHOUT server filesystem paths
        var statusRaw = await client.GetStringAsync("/api/status");
        Assert.DoesNotContain("dataDirectoryPath", statusRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tempDir, statusRaw, StringComparison.OrdinalIgnoreCase);

        var status = await client.GetFromJsonAsync<PublicStatusDto>("/api/status");
        Assert.NotNull(status);
        Assert.True(status!.HasQuestions);
        Assert.Equal(44, status.CategoryBQuestions);

        // 2. Verify security headers (CSP, X-Frame-Options, nosniff) on HTML page
        var htmlResp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, htmlResp.StatusCode);
        Assert.Equal("DENY", htmlResp.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", htmlResp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(htmlResp.Headers.Contains("Content-Security-Policy"));

        // 3. With empty AdminToken (default), all /api/admin/* endpoints are completely locked (403 Forbidden)
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/login", new AdminLoginRequest("anything"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/sync", new StartSyncRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/admin/rescan", null)).StatusCode);

        // 4. Start an exam and verify CorrectAnswer is NOT exposed during active exam
        var startResp = await client.PostAsJsonAsync("/api/exams", new StartExamRequest("B", "PL"));
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        var rawJson = await startResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"correctAnswer\"", rawJson, StringComparison.OrdinalIgnoreCase);

        // 5. Study search and Range-enabled media serving work cleanly
        var studyPage = await client.GetFromJsonAsync<StudyQuestionsPageDto>("/api/questions?category=B&scope=SPECIALIST&points=2");
        Assert.Equal(6, studyPage!.TotalCount);

        var mediaResp = await client.GetAsync("/api/media/foto_test.jpg");
        Assert.Equal(HttpStatusCode.OK, mediaResp.StatusCode);
        Assert.Contains("bytes", mediaResp.Headers.AcceptRanges);
    }

    [Fact]
    public async Task AdminAuth_RequiresSecret_IssuesHttpOnlyCookie_AndRateLimitsBruteForceByCloudflareIp()
    {
        const string secret = "SuperTajneHasloDoTuneluCloudflare2026!";
        using var factory = CreateFactory(adminToken: secret);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        // Unauthenticated admin status is 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/status")).StatusCode);

        // Simulate 5 wrong login attempts from attacker IP 198.51.100.77 via Cloudflare Tunnel header
        for (var i = 0; i < 5; i++)
        {
            using var badReq = new HttpRequestMessage(HttpMethod.Post, "/api/admin/login")
            {
                Content = JsonContent.Create(new AdminLoginRequest($"zly_strzal_{i}"))
            };
            badReq.Headers.Add("CF-Connecting-IP", "198.51.100.77");
            var badResp = await client.SendAsync(badReq);
            Assert.Equal(HttpStatusCode.Unauthorized, badResp.StatusCode);
        }

        // 6th attempt from same CF-Connecting-IP is blocked with 429 Too Many Requests!
        using (var throttledReq = new HttpRequestMessage(HttpMethod.Post, "/api/admin/login")
        {
            Content = JsonContent.Create(new AdminLoginRequest(secret))
        })
        {
            throttledReq.Headers.Add("CF-Connecting-IP", "198.51.100.77");
            var throttledResp = await client.SendAsync(throttledReq);
            Assert.Equal(HttpStatusCode.TooManyRequests, throttledResp.StatusCode);
        }

        // Legitimate admin from different CF-Connecting-IP logs in, receives HttpOnly cookie, and accesses /api/admin/status
        using var goodLogin = new HttpRequestMessage(HttpMethod.Post, "/api/admin/login")
        {
            Content = JsonContent.Create(new AdminLoginRequest(secret))
        };
        goodLogin.Headers.Add("CF-Connecting-IP", "203.0.113.5");
        var loginResp = await client.SendAsync(goodLogin);
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);

        var setCookie = string.Join(";", loginResp.Headers.GetValues("Set-Cookie"));
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);

        var adminStatusResp = await client.GetAsync("/api/admin/status");
        Assert.Equal(HttpStatusCode.OK, adminStatusResp.StatusCode);
        var adminStatus = await adminStatusResp.Content.ReadFromJsonAsync<AdminStatusDto>();
        Assert.NotNull(adminStatus);
        Assert.Equal(_tempDir, adminStatus!.DataDirectoryPath);
    }

    public void Dispose()
    {
        _defaultLockedFactory.Dispose();
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
