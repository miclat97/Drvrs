using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Drvrs.Web.Models;

namespace Drvrs.Web.Data;

public sealed record ExcelImportResult(
    int ImportedCount,
    int SkippedRows,
    string SourceFileName,
    string Sha256Hash,
    bool SkippedBecauseUnchanged);

/// <summary>
/// Ultra-lightweight streaming OpenXML (.xlsx) parser using built-in ZipArchive + XmlReader.
/// Imports all ~3,500 Ministry exam questions into SQLite in ~100ms with minimal RAM and zero external NuGet dependencies.
/// </summary>
public sealed class ExcelQuestionImporter(
    DatabaseInitializer db,
    ILogger<ExcelQuestionImporter> logger)
{
    private static readonly HashSet<string> ValidAnswers = new(StringComparer.OrdinalIgnoreCase)
    {
        "T", "N", "A", "B", "C"
    };

    public async Task<ExcelImportResult> ImportIfChangedAsync(
        string xlsxPath,
        bool force = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(xlsxPath))
        {
            throw new FileNotFoundException($"Nie znaleziono pliku Excel z bazą pytań: {xlsxPath}", xlsxPath);
        }

        var hash = await ComputeFileSha256Async(xlsxPath, ct);
        var fileName = Path.GetFileName(xlsxPath);

        await using var conn = db.CreateConnection();
        if (!force)
        {
            var existingHash = await GetMetadataAsync(conn, "SourceExcelSha256", ct);
            var existingCountStr = await GetMetadataAsync(conn, "ImportedQuestionsCount", ct);
            if (string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(existingCountStr, out var existingCount) &&
                existingCount > 0)
            {
                logger.LogInformation("Plik Excel ({FileName}) nie uległ zmianie (SHA-256 zgodny, {Count} pytań w SQLite). Pomijam ponowny import.", fileName, existingCount);
                return new ExcelImportResult(existingCount, 0, fileName, hash, SkippedBecauseUnchanged: true);
            }
        }

        logger.LogInformation("Rozpoczynam import pytań egzaminacyjnych z pliku {FileName} do bazy SQLite...", fileName);
        var (questions, skippedRows) = ParseQuestionsFromXlsx(xlsxPath);

        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var clearCmd = conn.CreateCommand())
        {
            clearCmd.Transaction = tx;
            clearCmd.CommandText = """
                DELETE FROM QuestionCategories;
                DELETE FROM Questions;
                DELETE FROM sqlite_sequence WHERE name = 'Questions';
                """;
            await clearCmd.ExecuteNonQueryAsync(ct);
        }

        await using var insertQuestionCmd = conn.CreateCommand();
        insertQuestionCmd.Transaction = tx;
        insertQuestionCmd.CommandText = """
            INSERT INTO Questions (
                QuestionNumber,
                TextPl, AnswerAPl, AnswerBPl, AnswerCPl,
                TextEn, AnswerAEn, AnswerBEn, AnswerCEn,
                TextDe, AnswerADe, AnswerBDe, AnswerCDe,
                TextUa, AnswerAUa, AnswerBUa, AnswerCUa,
                CorrectAnswer, Type, Scope, Points,
                MediaFileName, MediaKind, CategoriesCsv
            ) VALUES (
                $num,
                $tpl, $apl, $bpl, $cpl,
                $ten, $aen, $ben, $cen,
                $tde, $ade, $bde, $cde,
                $tua, $aua, $bua, $cua,
                $ans, $type, $scope, $pts,
                $media, $mkind, $cats
            );
            SELECT last_insert_rowid();
            """;

        var pNum = insertQuestionCmd.Parameters.Add("$num", Microsoft.Data.Sqlite.SqliteType.Text);
        var pTpl = insertQuestionCmd.Parameters.Add("$tpl", Microsoft.Data.Sqlite.SqliteType.Text);
        var pApl = insertQuestionCmd.Parameters.Add("$apl", Microsoft.Data.Sqlite.SqliteType.Text);
        var pBpl = insertQuestionCmd.Parameters.Add("$bpl", Microsoft.Data.Sqlite.SqliteType.Text);
        var pCpl = insertQuestionCmd.Parameters.Add("$cpl", Microsoft.Data.Sqlite.SqliteType.Text);
        var pTen = insertQuestionCmd.Parameters.Add("$ten", Microsoft.Data.Sqlite.SqliteType.Text);
        var pAen = insertQuestionCmd.Parameters.Add("$aen", Microsoft.Data.Sqlite.SqliteType.Text);
        var pBen = insertQuestionCmd.Parameters.Add("$ben", Microsoft.Data.Sqlite.SqliteType.Text);
        var pCen = insertQuestionCmd.Parameters.Add("$cen", Microsoft.Data.Sqlite.SqliteType.Text);
        var pTde = insertQuestionCmd.Parameters.Add("$tde", Microsoft.Data.Sqlite.SqliteType.Text);
        var pAde = insertQuestionCmd.Parameters.Add("$ade", Microsoft.Data.Sqlite.SqliteType.Text);
        var pBde = insertQuestionCmd.Parameters.Add("$bde", Microsoft.Data.Sqlite.SqliteType.Text);
        var pCde = insertQuestionCmd.Parameters.Add("$cde", Microsoft.Data.Sqlite.SqliteType.Text);
        var pTua = insertQuestionCmd.Parameters.Add("$tua", Microsoft.Data.Sqlite.SqliteType.Text);
        var pAua = insertQuestionCmd.Parameters.Add("$aua", Microsoft.Data.Sqlite.SqliteType.Text);
        var pBua = insertQuestionCmd.Parameters.Add("$bua", Microsoft.Data.Sqlite.SqliteType.Text);
        var pCua = insertQuestionCmd.Parameters.Add("$cua", Microsoft.Data.Sqlite.SqliteType.Text);
        var pAns = insertQuestionCmd.Parameters.Add("$ans", Microsoft.Data.Sqlite.SqliteType.Text);
        var pType = insertQuestionCmd.Parameters.Add("$type", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pScope = insertQuestionCmd.Parameters.Add("$scope", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pPts = insertQuestionCmd.Parameters.Add("$pts", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pMedia = insertQuestionCmd.Parameters.Add("$media", Microsoft.Data.Sqlite.SqliteType.Text);
        var pMkind = insertQuestionCmd.Parameters.Add("$mkind", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pCats = insertQuestionCmd.Parameters.Add("$cats", Microsoft.Data.Sqlite.SqliteType.Text);

        await using var insertCatCmd = conn.CreateCommand();
        insertCatCmd.Transaction = tx;
        insertCatCmd.CommandText = """
            INSERT OR IGNORE INTO QuestionCategories (Category, QuestionId, Scope, Type, Points)
            VALUES ($cat, $qid, $scope, $type, $pts);
            """;
        var pcCat = insertCatCmd.Parameters.Add("$cat", Microsoft.Data.Sqlite.SqliteType.Text);
        var pcQid = insertCatCmd.Parameters.Add("$qid", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pcScope = insertCatCmd.Parameters.Add("$scope", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pcType = insertCatCmd.Parameters.Add("$type", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pcPts = insertCatCmd.Parameters.Add("$pts", Microsoft.Data.Sqlite.SqliteType.Integer);

        foreach (var q in questions)
        {
            ct.ThrowIfCancellationRequested();

            pNum.Value = q.QuestionNumber;
            pTpl.Value = q.TextPl;
            pApl.Value = q.AnswerAPl;
            pBpl.Value = q.AnswerBPl;
            pCpl.Value = q.AnswerCPl;
            pTen.Value = q.TextEn;
            pAen.Value = q.AnswerAEn;
            pBen.Value = q.AnswerBEn;
            pCen.Value = q.AnswerCEn;
            pTde.Value = q.TextDe;
            pAde.Value = q.AnswerADe;
            pBde.Value = q.AnswerBDe;
            pCde.Value = q.AnswerCDe;
            pTua.Value = q.TextUa;
            pAua.Value = q.AnswerAUa;
            pBua.Value = q.AnswerBUa;
            pCua.Value = q.AnswerCUa;
            pAns.Value = q.CorrectAnswer;
            pType.Value = (int)q.Type;
            pScope.Value = (int)q.Scope;
            pPts.Value = q.Points;
            pMedia.Value = string.IsNullOrWhiteSpace(q.MediaFileName) ? DBNull.Value : q.MediaFileName;
            pMkind.Value = (int)q.MediaKind;
            pCats.Value = string.Join(',', q.Categories);

            var rowIdObj = await insertQuestionCmd.ExecuteScalarAsync(ct);
            var questionId = Convert.ToInt64(rowIdObj);

            pcQid.Value = questionId;
            pcScope.Value = (int)q.Scope;
            pcType.Value = (int)q.Type;
            pcPts.Value = q.Points;

            foreach (var cat in q.Categories)
            {
                pcCat.Value = cat;
                await insertCatCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await SetMetadataAsync(conn, tx, "SourceExcelFile", fileName, ct);
        await SetMetadataAsync(conn, tx, "SourceExcelSha256", hash, ct);
        await SetMetadataAsync(conn, tx, "ImportedQuestionsCount", questions.Count.ToString(), ct);
        await SetMetadataAsync(conn, tx, "LastImportedAtUtc", DateTimeOffset.UtcNow.ToString("O"), ct);

        await tx.CommitAsync(ct);

        logger.LogInformation("Zaimportowano {Count} pytań do bazy SQLite (pominięto {Skipped} pustych/nagłówkowych wierszy).", questions.Count, skippedRows);
        return new ExcelImportResult(questions.Count, skippedRows, fileName, hash, SkippedBecauseUnchanged: false);
    }

    public static (List<Question> Questions, int SkippedRows) ParseQuestionsFromXlsx(string xlsxPath)
    {
        using var fileStream = File.OpenRead(xlsxPath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);

        var sharedStrings = ReadSharedStrings(archive);
        var worksheetEntry = FindFirstWorksheetEntry(archive)
            ?? throw new InvalidDataException("Plik .xlsx nie zawiera arkusza z pytaniami (xl/worksheets/sheet1.xml).");

        using var sheetStream = worksheetEntry.Open();
        using var reader = XmlReader.Create(sheetStream, new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = false,
            DtdProcessing = DtdProcessing.Prohibit
        });

        // Default official column indices (0-based):
        // A(0)=Lp, B(1)=Numer pytania, C(2)=Pytanie, D(3)=Odp A, E(4)=Odp B, F(5)=Odp C,
        // G(6)=Poprawna odp, H(7)=Media, I(8)=Zakres struktury, J(9)=Liczba punktów, K(10)=Kategorie
        // P(15)..S(18)=EN, T(19)..W(22)=DE, X(23)..AA(26)=UA
        int colNum = 1, colQ = 2, colA = 3, colB = 4, colC = 5, colAns = 6, colMedia = 7, colScope = 8, colPts = 9, colCats = 10;
        int colQEn = 15, colAEn = 16, colBEn = 17, colCEn = 18;
        int colQDe = 19, colADe = 20, colBDe = 21, colCDe = 22;
        int colQUa = 23, colAUa = 24, colBUa = 25, colCUa = 26;

        var questions = new List<Question>(3600);
        var skippedRows = 0;
        var headerChecked = false;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
            {
                continue;
            }

            var cells = ReadRowCells(reader, sharedStrings);
            if (cells.Count == 0)
            {
                skippedRows++;
                continue;
            }

            if (!headerChecked)
            {
                headerChecked = true;
                if (IsHeaderRow(cells))
                {
                    MapHeaderColumns(
                        cells,
                        ref colNum, ref colQ, ref colA, ref colB, ref colC,
                        ref colAns, ref colMedia, ref colScope, ref colPts, ref colCats,
                        ref colQEn, ref colAEn, ref colBEn, ref colCEn,
                        ref colQDe, ref colADe, ref colBDe, ref colCDe,
                        ref colQUa, ref colAUa, ref colBUa, ref colCUa);
                    skippedRows++;
                    continue;
                }
            }

            var textPl = GetCell(cells, colQ).Trim();
            var rawAnswer = GetCell(cells, colAns).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(textPl) || !ValidAnswers.Contains(rawAnswer))
            {
                skippedRows++;
                continue;
            }

            var qNumber = GetCell(cells, colNum).Trim();
            if (string.IsNullOrWhiteSpace(qNumber))
            {
                qNumber = (questions.Count + 1).ToString();
            }

            var ansA = GetCell(cells, colA).Trim();
            var ansB = GetCell(cells, colB).Trim();
            var ansC = GetCell(cells, colC).Trim();

            var hasAbcVariants = !string.IsNullOrWhiteSpace(ansA) ||
                                 !string.IsNullOrWhiteSpace(ansB) ||
                                 !string.IsNullOrWhiteSpace(ansC);

            var questionType = (!hasAbcVariants && (rawAnswer == "T" || rawAnswer == "N"))
                ? QuestionType.YesNo
                : QuestionType.SingleChoice;

            var rawScope = GetCell(cells, colScope).Trim();
            var scope = ParseScope(rawScope, questionType);

            if (!int.TryParse(GetCell(cells, colPts).Trim(), out var points) || points is < 1 or > 3)
            {
                skippedRows++;
                continue;
            }

            var rawMedia = GetCell(cells, colMedia).Trim();
            string? mediaFile = null;
            if (!string.IsNullOrWhiteSpace(rawMedia))
            {
                // Normalize any Windows/Unix path separator if present, preserving exact filename
                mediaFile = rawMedia.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            }

            var categories = GetCell(cells, colCats)
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (categories.Length == 0)
            {
                skippedRows++;
                continue;
            }

            questions.Add(new Question
            {
                QuestionNumber = qNumber,
                TextPl = textPl,
                AnswerAPl = ansA,
                AnswerBPl = ansB,
                AnswerCPl = ansC,
                TextEn = GetCell(cells, colQEn).Trim(),
                AnswerAEn = GetCell(cells, colAEn).Trim(),
                AnswerBEn = GetCell(cells, colBEn).Trim(),
                AnswerCEn = GetCell(cells, colCEn).Trim(),
                TextDe = GetCell(cells, colQDe).Trim(),
                AnswerADe = GetCell(cells, colADe).Trim(),
                AnswerBDe = GetCell(cells, colBDe).Trim(),
                AnswerCDe = GetCell(cells, colCDe).Trim(),
                TextUa = GetCell(cells, colQUa).Trim(),
                AnswerAUa = GetCell(cells, colAUa).Trim(),
                AnswerBUa = GetCell(cells, colBUa).Trim(),
                AnswerCUa = GetCell(cells, colCUa).Trim(),
                CorrectAnswer = rawAnswer,
                Type = questionType,
                Scope = scope,
                Points = points,
                MediaFileName = mediaFile,
                MediaKind = Question.DetectMediaKind(mediaFile),
                Categories = categories
            });
        }

        return (questions, skippedRows);
    }

    private static ExamScope ParseScope(string rawScope, QuestionType fallbackType)
    {
        if (rawScope.StartsWith("PODST", StringComparison.OrdinalIgnoreCase))
        {
            return ExamScope.Basic;
        }

        // Matches "SPECJALISTYCZNY", "Specjalistyczny", and the official typo "Specajlistyczny"
        if (rawScope.StartsWith("SPEC", StringComparison.OrdinalIgnoreCase))
        {
            return ExamScope.Specialist;
        }

        return fallbackType == QuestionType.YesNo ? ExamScope.Basic : ExamScope.Specialist;
    }

    private static bool IsHeaderRow(Dictionary<int, string> cells)
    {
        foreach (var val in cells.Values)
        {
            if (val.Contains("Numer pytania", StringComparison.OrdinalIgnoreCase) ||
                val.Contains("Poprawna odp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(val.Trim(), "Pytanie", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void MapHeaderColumns(
        Dictionary<int, string> cells,
        ref int colNum, ref int colQ, ref int colA, ref int colB, ref int colC,
        ref int colAns, ref int colMedia, ref int colScope, ref int colPts, ref int colCats,
        ref int colQEn, ref int colAEn, ref int colBEn, ref int colCEn,
        ref int colQDe, ref int colADe, ref int colBDe, ref int colCDe,
        ref int colQUa, ref int colAUa, ref int colBUa, ref int colCUa)
    {
        foreach (var (idx, rawHeader) in cells)
        {
            var h = rawHeader.Trim();
            if (h.Equals("Numer pytania", StringComparison.OrdinalIgnoreCase)) colNum = idx;
            else if (h.Equals("Pytanie", StringComparison.OrdinalIgnoreCase)) colQ = idx;
            else if (h.Equals("Odpowiedź A", StringComparison.OrdinalIgnoreCase)) colA = idx;
            else if (h.Equals("Odpowiedź B", StringComparison.OrdinalIgnoreCase)) colB = idx;
            else if (h.Equals("Odpowiedź C", StringComparison.OrdinalIgnoreCase)) colC = idx;
            else if (h.StartsWith("Poprawna odp", StringComparison.OrdinalIgnoreCase)) colAns = idx;
            else if (h.Equals("Media", StringComparison.OrdinalIgnoreCase)) colMedia = idx;
            else if (h.StartsWith("Zakres struktury", StringComparison.OrdinalIgnoreCase)) colScope = idx;
            else if (h.StartsWith("Liczba punkt", StringComparison.OrdinalIgnoreCase)) colPts = idx;
            else if (h.StartsWith("Kategorie", StringComparison.OrdinalIgnoreCase)) colCats = idx;
            else if (h.Equals("Pytanie [EN]", StringComparison.OrdinalIgnoreCase)) colQEn = idx;
            else if (h.Equals("Odpowiedź A [EN]", StringComparison.OrdinalIgnoreCase)) colAEn = idx;
            else if (h.Equals("Odpowiedź B [EN]", StringComparison.OrdinalIgnoreCase)) colBEn = idx;
            else if (h.Equals("Odpowiedź C [EN]", StringComparison.OrdinalIgnoreCase)) colCEn = idx;
            else if (h.Equals("Pytanie [D]", StringComparison.OrdinalIgnoreCase)) colQDe = idx;
            else if (h.Equals("Odpowiedź A [D]", StringComparison.OrdinalIgnoreCase)) colADe = idx;
            else if (h.Equals("Odpowiedź B [D]", StringComparison.OrdinalIgnoreCase)) colBDe = idx;
            else if (h.Equals("Odpowiedź C [D]", StringComparison.OrdinalIgnoreCase)) colCDe = idx;
            else if (h.Equals("Pytanie [UA]", StringComparison.OrdinalIgnoreCase)) colQUa = idx;
            else if (h.Equals("Odpowiedź A [UA]", StringComparison.OrdinalIgnoreCase)) colAUa = idx;
            else if (h.Equals("Odpowiedź B [UA]", StringComparison.OrdinalIgnoreCase)) colBUa = idx;
            else if (h.Equals("Odpowiedź C [UA]", StringComparison.OrdinalIgnoreCase)) colCUa = idx;
        }
    }

    private static string GetCell(Dictionary<int, string> cells, int colIndex)
        => cells.TryGetValue(colIndex, out var v) ? v : string.Empty;

    private static Dictionary<int, string> ReadRowCells(XmlReader rowReader, List<string> sharedStrings)
    {
        var cells = new Dictionary<int, string>();
        if (rowReader.IsEmptyElement)
        {
            return cells;
        }

        using var subtree = rowReader.ReadSubtree();
        subtree.Read(); // Move to <row>
        var nextSequentialCol = 0;

        while (subtree.Read())
        {
            if (subtree.NodeType != XmlNodeType.Element || subtree.LocalName != "c")
            {
                continue;
            }

            var cellRef = subtree.GetAttribute("r");
            var cellType = subtree.GetAttribute("t");
            var colIndex = !string.IsNullOrEmpty(cellRef)
                ? ParseColumnReference(cellRef)
                : nextSequentialCol;
            nextSequentialCol = colIndex + 1;

            if (subtree.IsEmptyElement)
            {
                continue;
            }

            var value = ReadCellValue(subtree, cellType, sharedStrings);
            if (!string.IsNullOrEmpty(value))
            {
                cells[colIndex] = value;
            }
        }

        return cells;
    }

    private static string ReadCellValue(XmlReader cellReader, string? cellType, List<string> sharedStrings)
    {
        using var cellSub = cellReader.ReadSubtree();
        cellSub.Read(); // <c>

        if (cellType == "inlineStr")
        {
            var sb = new StringBuilder();
            while (cellSub.Read())
            {
                if (cellSub.NodeType == XmlNodeType.Element && cellSub.LocalName == "t")
                {
                    sb.Append(cellSub.ReadElementContentAsString());
                }
            }
            return sb.ToString();
        }

        while (cellSub.Read())
        {
            if (cellSub.NodeType == XmlNodeType.Element && cellSub.LocalName == "v")
            {
                var raw = cellSub.ReadElementContentAsString();
                if (cellType == "s" && int.TryParse(raw, out var sstIdx) && sstIdx >= 0 && sstIdx < sharedStrings.Count)
                {
                    return sharedStrings[sstIdx];
                }
                return raw;
            }
        }

        return string.Empty;
    }

    private static int ParseColumnReference(string cellRef)
    {
        var col = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                col = (col * 26) + (ch - 'A' + 1);
            }
            else if (ch is >= 'a' and <= 'z')
            {
                col = (col * 26) + (ch - 'a' + 1);
            }
            else
            {
                break;
            }
        }
        return Math.Max(0, col - 1);
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var list = new List<string>(8192);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return list;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = false,
            DtdProcessing = DtdProcessing.Prohibit
        });

        var sb = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                sb.Clear();
                if (!reader.IsEmptyElement)
                {
                    using var siSub = reader.ReadSubtree();
                    siSub.Read();
                    while (siSub.Read())
                    {
                        if (siSub.NodeType == XmlNodeType.Element && siSub.LocalName == "t")
                        {
                            sb.Append(siSub.ReadElementContentAsString());
                        }
                    }
                }
                list.Add(sb.ToString());
            }
        }

        return list;
    }

    private static ZipArchiveEntry? FindFirstWorksheetEntry(ZipArchive archive)
    {
        var direct = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "xl/worksheets/sheet1.xml", StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct;
        }

        return archive.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hashBytes = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hashBytes);
    }

    private static async Task<string?> GetMetadataAsync(Microsoft.Data.Sqlite.SqliteConnection conn, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Metadata WHERE Key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res as string;
    }

    private static async Task SetMetadataAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        string key,
        string value,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Metadata (Key, Value) VALUES ($key, $val)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$val", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
