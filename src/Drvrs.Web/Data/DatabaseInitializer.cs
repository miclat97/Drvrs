using Drvrs.Web.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Drvrs.Web.Data;

public sealed class DatabaseInitializer
{
    private readonly string _dataDir;
    private readonly string _connectionString;

    public DatabaseInitializer(IOptions<DrvrsOptions> options, IHostEnvironment env)
    {
        _dataDir = options.Value.ResolveDataDirectory(env.ContentRootPath);
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(QuestionsDirectory);
        Directory.CreateDirectory(MediaDirectory);
        Directory.CreateDirectory(MediaCacheDirectory);
        Directory.CreateDirectory(TempDirectory);

        // Clean up only non-resumable *.tmp scratch files; keep resumable media_archive_*.zip files across restarts
        foreach (var tmpFile in Directory.EnumerateFiles(TempDirectory, "*.tmp"))
        {
            try { File.Delete(tmpFile); } catch { /* best-effort */ }
        }

        DatabasePath = Path.Combine(_dataDir, "drvrs.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    public async Task<string?> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Metadata WHERE Key = $k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetMetadataAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Metadata (Key, Value) VALUES ($k, $v)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public string DataDirectory => _dataDir;
    public string QuestionsDirectory => Path.Combine(_dataDir, "questions");
    public string MediaDirectory => Path.Combine(_dataDir, "media");
    public string MediaCacheDirectory => Path.Combine(_dataDir, "media", ".mp4-cache");
    public string TempDirectory => Path.Combine(_dataDir, "tmp");
    public string DatabasePath { get; }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task InitializeSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS Metadata (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Questions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                QuestionNumber TEXT NOT NULL,
                TextPl TEXT NOT NULL,
                AnswerAPl TEXT NOT NULL DEFAULT '',
                AnswerBPl TEXT NOT NULL DEFAULT '',
                AnswerCPl TEXT NOT NULL DEFAULT '',
                TextEn TEXT NOT NULL DEFAULT '',
                AnswerAEn TEXT NOT NULL DEFAULT '',
                AnswerBEn TEXT NOT NULL DEFAULT '',
                AnswerCEn TEXT NOT NULL DEFAULT '',
                TextDe TEXT NOT NULL DEFAULT '',
                AnswerADe TEXT NOT NULL DEFAULT '',
                AnswerBDe TEXT NOT NULL DEFAULT '',
                AnswerCDe TEXT NOT NULL DEFAULT '',
                TextUa TEXT NOT NULL DEFAULT '',
                AnswerAUa TEXT NOT NULL DEFAULT '',
                AnswerBUa TEXT NOT NULL DEFAULT '',
                AnswerCUa TEXT NOT NULL DEFAULT '',
                CorrectAnswer TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Scope INTEGER NOT NULL,
                Points INTEGER NOT NULL,
                MediaFileName TEXT NULL,
                MediaKind INTEGER NOT NULL,
                CategoriesCsv TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS QuestionCategories (
                Category TEXT NOT NULL,
                QuestionId INTEGER NOT NULL,
                Scope INTEGER NOT NULL,
                Type INTEGER NOT NULL,
                Points INTEGER NOT NULL,
                PRIMARY KEY (Category, QuestionId),
                FOREIGN KEY (QuestionId) REFERENCES Questions(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_QuestionCategories_ExamPool
                ON QuestionCategories (Category, Scope, Type, Points);

            CREATE INDEX IF NOT EXISTS IX_Questions_QuestionNumber
                ON Questions (QuestionNumber);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
