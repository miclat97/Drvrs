using System.IO.Compression;
using System.Text;

namespace Drvrs.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
{
    private DateTimeOffset _utcNow = initialUtc;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
}

internal static class SyntheticXlsxBuilder
{
    public sealed record RawQuestionRow(
        string QuestionNumber,
        string Pytanie,
        string OdpA,
        string OdpB,
        string OdpC,
        string PoprawnaOdp,
        string Media,
        string ZakresStruktury,
        int LiczbaPunktow,
        string Kategorie,
        string PytanieEn = "");

    public static void WriteOfficialFormatXlsx(string targetPath, IReadOnlyList<RawQuestionRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (File.Exists(targetPath)) File.Delete(targetPath);

        var sharedStrings = new List<string>();
        var sharedMap = new Dictionary<string, int>(StringComparer.Ordinal);

        int AddShared(string val)
        {
            if (sharedMap.TryGetValue(val, out var idx)) return idx;
            idx = sharedStrings.Count;
            sharedStrings.Add(val);
            sharedMap[val] = idx;
            return idx;
        }

        string EscapeXml(string s) => s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

        var headers = new[]
        {
            "Lp", "Numer pytania", "Pytanie", "Odpowiedź A", "Odpowiedź B", "Odpowiedź C",
            "Poprawna odp", "Media", "Zakres struktury", "Liczba punktów", "Kategorie",
            "PJM pyt", "PJM A", "PJM B", "PJM C", "Pytanie [EN]"
        };

        var sheetSb = new StringBuilder();
        sheetSb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sheetSb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");

        // Header row 1
        sheetSb.Append("""<row r="1">""");
        for (var c = 0; c < headers.Length; c++)
        {
            var colRef = $"{ColName(c)}1";
            var sIdx = AddShared(headers[c]);
            sheetSb.Append($"""<c r="{colRef}" t="s"><v>{sIdx}</v></c>""");
        }
        sheetSb.Append("</row>");

        // Data rows
        for (var i = 0; i < rows.Count; i++)
        {
            var rNum = i + 2;
            var r = rows[i];
            sheetSb.Append($"""<row r="{rNum}">""");

            AppendStringCell(sheetSb, 0, rNum, (i + 1).ToString(), AddShared);
            AppendStringCell(sheetSb, 1, rNum, r.QuestionNumber, AddShared);
            AppendStringCell(sheetSb, 2, rNum, r.Pytanie, AddShared);
            if (!string.IsNullOrEmpty(r.OdpA)) AppendStringCell(sheetSb, 3, rNum, r.OdpA, AddShared);
            if (!string.IsNullOrEmpty(r.OdpB)) AppendStringCell(sheetSb, 4, rNum, r.OdpB, AddShared);
            if (!string.IsNullOrEmpty(r.OdpC)) AppendStringCell(sheetSb, 5, rNum, r.OdpC, AddShared);
            AppendStringCell(sheetSb, 6, rNum, r.PoprawnaOdp, AddShared);
            if (!string.IsNullOrEmpty(r.Media)) AppendStringCell(sheetSb, 7, rNum, r.Media, AddShared);
            AppendStringCell(sheetSb, 8, rNum, r.ZakresStruktury, AddShared);
            sheetSb.Append($"""<c r="{ColName(9)}{rNum}"><v>{r.LiczbaPunktow}</v></c>""");
            AppendStringCell(sheetSb, 10, rNum, r.Kategorie, AddShared);
            if (!string.IsNullOrEmpty(r.PytanieEn)) AppendStringCell(sheetSb, 15, rNum, r.PytanieEn, AddShared);

            sheetSb.Append("</row>");
        }

        sheetSb.Append("</sheetData></worksheet>");

        var sstSb = new StringBuilder();
        sstSb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sstSb.Append($"""<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{sharedStrings.Count}" uniqueCount="{sharedStrings.Count}">""");
        foreach (var s in sharedStrings)
        {
            sstSb.Append($"<si><t>{EscapeXml(s)}</t></si>");
        }
        sstSb.Append("</sst>");

        using var fs = File.Create(targetPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        WriteEntry(zip, "xl/sharedStrings.xml", sstSb.ToString());
        WriteEntry(zip, "xl/worksheets/sheet1.xml", sheetSb.ToString());
    }

    public static List<RawQuestionRow> CreateFullCategoryBPool(string videoFileName = "klip_testowy.wmv")
    {
        var list = new List<RawQuestionRow>();
        var id = 100;

        // Basic (TAK/NIE): 12x3pt, 8x2pt, 6x1pt (more than required 10/6/4)
        for (var i = 0; i < 12; i++)
            list.Add(new($"{id++}", $"Pytanie podstawowe 3pkt nr {i}", "", "", "", i % 2 == 0 ? "T" : "N", videoFileName, "PODSTAWOWY", 3, "A,B,C", $"Basic 3pt #{i}"));
        for (var i = 0; i < 8; i++)
            list.Add(new($"{id++}", $"Pytanie podstawowe 2pkt nr {i}", "", "", "", "T", "foto_test.jpg", "PODSTAWOWY", 2, "B,T"));
        for (var i = 0; i < 6; i++)
            list.Add(new($"{id++}", $"Pytanie podstawowe 1pkt nr {i}", "", "", "", "N", "", "Podstawowy", 1, "B"));

        // Specialist (A/B/C): 8x3pt, 6x2pt (including Ministry typo "Specajlistyczny"!), 4x1pt (more than required 6/4/2)
        for (var i = 0; i < 8; i++)
            list.Add(new($"{id++}", $"Pytanie specjalistyczne 3pkt nr {i}", "Wariant A", "Wariant B", "Wariant C", "B", "foto_test.jpg", "SPECJALISTYCZNY", 3, "B"));
        for (var i = 0; i < 6; i++)
            list.Add(new($"{id++}", $"Pytanie specjalistyczne 2pkt nr {i}", "Opcja A", "Opcja B", "Opcja C", "A", "", i == 0 ? "Specajlistyczny" : "Specjalistyczny", 2, "B"));
        for (var i = 0; i < 4; i++)
            list.Add(new($"{id++}", $"Pytanie specjalistyczne 1pkt nr {i}", "Odp A", "Odp B", "Odp C", "C", "", "SPECJALISTYCZNY", 1, "B"));

        // Add B1-only question to ensure "B" never matches "B1"
        list.Add(new("99999", "Pytanie wyłącznie dla B1", "", "", "", "T", "", "PODSTAWOWY", 3, "B1"));

        return list;
    }

    private static void AppendStringCell(StringBuilder sb, int colIdx, int rowNum, string value, Func<string, int> addShared)
    {
        var sIdx = addShared(value);
        sb.Append($"""<c r="{ColName(colIdx)}{rowNum}" t="s"><v>{sIdx}</v></c>""");
    }

    private static string ColName(int index)
    {
        var dividend = index + 1;
        var name = string.Empty;
        while (dividend > 0)
        {
            var mod = (dividend - 1) % 26;
            name = (char)('A' + mod) + name;
            dividend = (dividend - mod) / 26;
        }
        return name;
    }

    private static void WriteEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
