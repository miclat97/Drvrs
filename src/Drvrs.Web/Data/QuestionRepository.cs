using Drvrs.Web.Models;
using Microsoft.Data.Sqlite;

namespace Drvrs.Web.Data;

public sealed record StudyQuestionDto(
    int Id,
    string QuestionNumber,
    string Scope,
    string Type,
    int Points,
    string Text,
    string AnswerA,
    string AnswerB,
    string AnswerC,
    string CorrectAnswer,
    string? MediaUrl,
    string MediaKind,
    bool MediaAvailable,
    IReadOnlyList<string> Categories);

public sealed record StudyQuestionsPageDto(
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<StudyQuestionDto> Items);

public sealed class QuestionRepository(DatabaseInitializer db)
{
    public async Task<int> GetTotalCountAsync(CancellationToken ct = default)
    {
        await using var conn = db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Questions;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<(string? SourceFile, DateTimeOffset? LastImportedAtUtc)> GetImportMetadataAsync(CancellationToken ct = default)
    {
        await using var conn = db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM Metadata WHERE Key IN ('SourceExcelFile', 'LastImportedAtUtc');";
        string? source = null;
        DateTimeOffset? importedAt = null;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var k = reader.GetString(0);
            var v = reader.GetString(1);
            if (k == "SourceExcelFile") source = v;
            else if (k == "LastImportedAtUtc" && DateTimeOffset.TryParse(v, out var dt)) importedAt = dt;
        }
        return (source, importedAt);
    }

    public async Task<HashSet<string>> GetAllDistinctMediaFileNamesAsync(CancellationToken ct = default)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT MediaFileName FROM Questions WHERE MediaFileName IS NOT NULL AND MediaFileName <> '';";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            set.Add(reader.GetString(0));
        }
        return set;
    }

    public async Task<IReadOnlyList<CategoryCountDto>> GetCategoryStatsAsync(CancellationToken ct = default)
    {
        await using var conn = db.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                Category,
                COUNT(*) AS TotalCount,
                SUM(CASE WHEN Scope = 0 AND Type = 0 THEN 1 ELSE 0 END) AS BasicCount,
                SUM(CASE WHEN Scope = 1 AND Type = 1 THEN 1 ELSE 0 END) AS SpecCount,
                SUM(CASE WHEN Scope = 0 AND Type = 0 AND Points = 3 THEN 1 ELSE 0 END) AS B3,
                SUM(CASE WHEN Scope = 0 AND Type = 0 AND Points = 2 THEN 1 ELSE 0 END) AS B2,
                SUM(CASE WHEN Scope = 0 AND Type = 0 AND Points = 1 THEN 1 ELSE 0 END) AS B1,
                SUM(CASE WHEN Scope = 1 AND Type = 1 AND Points = 3 THEN 1 ELSE 0 END) AS S3,
                SUM(CASE WHEN Scope = 1 AND Type = 1 AND Points = 2 THEN 1 ELSE 0 END) AS S2,
                SUM(CASE WHEN Scope = 1 AND Type = 1 AND Points = 1 THEN 1 ELSE 0 END) AS S1
            FROM QuestionCategories
            GROUP BY Category
            ORDER BY CASE WHEN Category = 'B' THEN 0 ELSE 1 END, Category;
            """;

        var list = new List<CategoryCountDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var cat = reader.GetString(0);
            var total = reader.GetInt32(1);
            var basic = reader.GetInt32(2);
            var spec = reader.GetInt32(3);
            var b3 = reader.GetInt32(4);
            var b2 = reader.GetInt32(5);
            var b1 = reader.GetInt32(6);
            var s3 = reader.GetInt32(7);
            var s2 = reader.GetInt32(8);
            var s1 = reader.GetInt32(9);

            var ready = b3 >= OfficialExamRules.Basic3PtCount &&
                        b2 >= OfficialExamRules.Basic2PtCount &&
                        b1 >= OfficialExamRules.Basic1PtCount &&
                        s3 >= OfficialExamRules.Specialist3PtCount &&
                        s2 >= OfficialExamRules.Specialist2PtCount &&
                        s1 >= OfficialExamRules.Specialist1PtCount;

            list.Add(new CategoryCountDto(cat, total, basic, spec, ready));
        }

        return list;
    }

    /// <summary>
    /// Draws an official 32-question exam strictly matching Polish state exam rules:
    /// Part 1 (Basic, Yes/No): 10x3pt + 6x2pt + 4x1pt (shuffled)
    /// Part 2 (Specialist, A/B/C): 6x3pt + 4x2pt + 2x1pt (shuffled)
    /// </summary>
    public async Task<List<Question>> DrawOfficialExamQuestionsAsync(
        string category,
        Func<string?, bool>? mediaFilter = null,
        CancellationToken ct = default)
    {
        var normalizedCategory = category.Trim().ToUpperInvariant();
        await using var conn = db.CreateConnection();

        var allCategoryQuestions = await LoadQuestionsForCategoryAsync(conn, normalizedCategory, ct);
        if (allCategoryQuestions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Brak pytań w bazie dla kategorii '{normalizedCategory}'. Zaimportuj najpierw oficjalny plik Excel z gov.pl.");
        }

        List<Question> TryDraw(IReadOnlyList<Question> pool)
        {
            var basicPool = pool.Where(q => q.Scope == ExamScope.Basic && q.Type == QuestionType.YesNo).ToList();
            var specPool = pool.Where(q => q.Scope == ExamScope.Specialist && q.Type == QuestionType.SingleChoice).ToList();

            var basicPart = new List<Question>(OfficialExamRules.BasicQuestionCount);
            basicPart.AddRange(PickRandom(basicPool.Where(q => q.Points == 3), OfficialExamRules.Basic3PtCount, "Podstawowe 3 pkt"));
            basicPart.AddRange(PickRandom(basicPool.Where(q => q.Points == 2), OfficialExamRules.Basic2PtCount, "Podstawowe 2 pkt"));
            basicPart.AddRange(PickRandom(basicPool.Where(q => q.Points == 1), OfficialExamRules.Basic1PtCount, "Podstawowe 1 pkt"));

            var specPart = new List<Question>(OfficialExamRules.SpecialistQuestionCount);
            specPart.AddRange(PickRandom(specPool.Where(q => q.Points == 3), OfficialExamRules.Specialist3PtCount, "Specjalistyczne 3 pkt"));
            specPart.AddRange(PickRandom(specPool.Where(q => q.Points == 2), OfficialExamRules.Specialist2PtCount, "Specjalistyczne 2 pkt"));
            specPart.AddRange(PickRandom(specPool.Where(q => q.Points == 1), OfficialExamRules.Specialist1PtCount, "Specjalistyczne 1 pkt"));

            var basicArr = basicPart.ToArray();
            var specArr = specPart.ToArray();
            Random.Shared.Shuffle(basicArr);
            Random.Shared.Shuffle(specArr);

            var combined = new List<Question>(OfficialExamRules.TotalQuestionCount);
            combined.AddRange(basicArr);
            combined.AddRange(specArr);
            return combined;
        }

        if (mediaFilter is not null)
        {
            var filtered = allCategoryQuestions
                .Where(q => string.IsNullOrWhiteSpace(q.MediaFileName) || mediaFilter(q.MediaFileName))
                .ToList();

            try
            {
                return TryDraw(filtered);
            }
            catch (InvalidOperationException)
            {
                // If partial media doesn't have enough questions in every point bucket (e.g. only 29 no-media basic questions),
                // gracefully fall back to full category pool so the user can always start an exam.
            }
        }

        return TryDraw(allCategoryQuestions);
    }

    public async Task<StudyQuestionsPageDto> SearchQuestionsAsync(
        string category,
        string language,
        string? scopeFilter,
        int? pointsFilter,
        string? searchQuery,
        int page,
        int pageSize,
        Func<string?, bool> isMediaAvailable,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        await using var conn = db.CreateConnection();
        var whereClauses = new List<string> { "qc.Category = $cat" };

        await using var countCmd = conn.CreateCommand();
        await using var dataCmd = conn.CreateCommand();

        var normalizedCategory = category.Trim().ToUpperInvariant();
        countCmd.Parameters.AddWithValue("$cat", normalizedCategory);
        dataCmd.Parameters.AddWithValue("$cat", normalizedCategory);

        if (!string.IsNullOrWhiteSpace(scopeFilter))
        {
            if (scopeFilter.Equals("BASIC", StringComparison.OrdinalIgnoreCase))
            {
                whereClauses.Add("q.Scope = 0");
            }
            else if (scopeFilter.Equals("SPECIALIST", StringComparison.OrdinalIgnoreCase))
            {
                whereClauses.Add("q.Scope = 1");
            }
        }

        if (pointsFilter is 1 or 2 or 3)
        {
            whereClauses.Add("q.Points = $pts");
            countCmd.Parameters.AddWithValue("$pts", pointsFilter.Value);
            dataCmd.Parameters.AddWithValue("$pts", pointsFilter.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            whereClauses.Add("(q.QuestionNumber LIKE $q OR q.TextPl LIKE $q OR q.AnswerAPl LIKE $q OR q.AnswerBPl LIKE $q OR q.AnswerCPl LIKE $q)");
            var pattern = $"%{searchQuery.Trim()}%";
            countCmd.Parameters.AddWithValue("$q", pattern);
            dataCmd.Parameters.AddWithValue("$q", pattern);
        }

        var whereSql = string.Join(" AND ", whereClauses);

        countCmd.CommandText = $"""
            SELECT COUNT(*)
            FROM Questions q
            INNER JOIN QuestionCategories qc ON qc.QuestionId = q.Id
            WHERE {whereSql};
            """;
        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        dataCmd.CommandText = $"""
            SELECT
                q.Id, q.QuestionNumber,
                q.TextPl, q.AnswerAPl, q.AnswerBPl, q.AnswerCPl,
                q.TextEn, q.AnswerAEn, q.AnswerBEn, q.AnswerCEn,
                q.TextDe, q.AnswerADe, q.AnswerBDe, q.AnswerCDe,
                q.TextUa, q.AnswerAUa, q.AnswerBUa, q.AnswerCUa,
                q.CorrectAnswer, q.Type, q.Scope, q.Points,
                q.MediaFileName, q.MediaKind, q.CategoriesCsv
            FROM Questions q
            INNER JOIN QuestionCategories qc ON qc.QuestionId = q.Id
            WHERE {whereSql}
            ORDER BY q.Id
            LIMIT $limit OFFSET $offset;
            """;
        dataCmd.Parameters.AddWithValue("$limit", pageSize);
        dataCmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);

        var items = new List<StudyQuestionDto>(pageSize);
        await using var reader = await dataCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var q = MapQuestion(reader);
            var (txt, a, b, c) = q.GetLocalizedText(language);
            var mediaAvail = !string.IsNullOrWhiteSpace(q.MediaFileName) && isMediaAvailable(q.MediaFileName);
            var mediaUrl = !string.IsNullOrWhiteSpace(q.MediaFileName)
                ? $"/api/media/{Uri.EscapeDataString(q.MediaFileName)}"
                : null;

            items.Add(new StudyQuestionDto(
                q.Id,
                q.QuestionNumber,
                q.Scope == ExamScope.Basic ? "PODSTAWOWY" : "SPECJALISTYCZNY",
                q.Type == QuestionType.YesNo ? "YES_NO" : "ABC",
                q.Points,
                txt, a, b, c,
                q.CorrectAnswer,
                mediaUrl,
                q.MediaKind.ToString().ToUpperInvariant(),
                mediaAvail,
                q.Categories));
        }

        return new StudyQuestionsPageDto(total, page, pageSize, items);
    }

    private static List<Question> PickRandom(IEnumerable<Question> source, int count, string bucketName)
    {
        var arr = source.ToArray();
        if (arr.Length < count)
        {
            throw new InvalidOperationException(
                $"Zbyt mało pytań w puli '{bucketName}': wymagane {count}, dostępne {arr.Length}.");
        }

        Random.Shared.Shuffle(arr);
        return arr[..count].ToList();
    }

    private static async Task<List<Question>> LoadQuestionsForCategoryAsync(
        SqliteConnection conn,
        string category,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                q.Id, q.QuestionNumber,
                q.TextPl, q.AnswerAPl, q.AnswerBPl, q.AnswerCPl,
                q.TextEn, q.AnswerAEn, q.AnswerBEn, q.AnswerCEn,
                q.TextDe, q.AnswerADe, q.AnswerBDe, q.AnswerCDe,
                q.TextUa, q.AnswerAUa, q.AnswerBUa, q.AnswerCUa,
                q.CorrectAnswer, q.Type, q.Scope, q.Points,
                q.MediaFileName, q.MediaKind, q.CategoriesCsv
            FROM Questions q
            INNER JOIN QuestionCategories qc ON qc.QuestionId = q.Id
            WHERE qc.Category = $cat;
            """;
        cmd.Parameters.AddWithValue("$cat", category);

        var list = new List<Question>(2200);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapQuestion(reader));
        }
        return list;
    }

    private static Question MapQuestion(SqliteDataReader r)
    {
        return new Question
        {
            Id = r.GetInt32(0),
            QuestionNumber = r.GetString(1),
            TextPl = r.GetString(2),
            AnswerAPl = r.GetString(3),
            AnswerBPl = r.GetString(4),
            AnswerCPl = r.GetString(5),
            TextEn = r.GetString(6),
            AnswerAEn = r.GetString(7),
            AnswerBEn = r.GetString(8),
            AnswerCEn = r.GetString(9),
            TextDe = r.GetString(10),
            AnswerADe = r.GetString(11),
            AnswerBDe = r.GetString(12),
            AnswerCDe = r.GetString(13),
            TextUa = r.GetString(14),
            AnswerAUa = r.GetString(15),
            AnswerBUa = r.GetString(16),
            AnswerCUa = r.GetString(17),
            CorrectAnswer = r.GetString(18),
            Type = (QuestionType)r.GetInt32(19),
            Scope = (ExamScope)r.GetInt32(20),
            Points = r.GetInt32(21),
            MediaFileName = r.IsDBNull(22) ? null : r.GetString(22),
            MediaKind = (MediaKind)r.GetInt32(23),
            Categories = r.GetString(24).Split(',', StringSplitOptions.RemoveEmptyEntries)
        };
    }
}
