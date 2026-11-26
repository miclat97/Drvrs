namespace Drvrs.Web.Models;

public enum QuestionType
{
    YesNo = 0,
    SingleChoice = 1
}

public enum ExamScope
{
    Basic = 0,
    Specialist = 1
}

public enum MediaKind
{
    None = 0,
    Image = 1,
    Video = 2
}

public sealed class Question
{
    public int Id { get; init; }
    public required string QuestionNumber { get; init; }
    public required string TextPl { get; init; }
    public string AnswerAPl { get; init; } = string.Empty;
    public string AnswerBPl { get; init; } = string.Empty;
    public string AnswerCPl { get; init; } = string.Empty;

    public string TextEn { get; init; } = string.Empty;
    public string AnswerAEn { get; init; } = string.Empty;
    public string AnswerBEn { get; init; } = string.Empty;
    public string AnswerCEn { get; init; } = string.Empty;

    public string TextDe { get; init; } = string.Empty;
    public string AnswerADe { get; init; } = string.Empty;
    public string AnswerBDe { get; init; } = string.Empty;
    public string AnswerCDe { get; init; } = string.Empty;

    public string TextUa { get; init; } = string.Empty;
    public string AnswerAUa { get; init; } = string.Empty;
    public string AnswerBUa { get; init; } = string.Empty;
    public string AnswerCUa { get; init; } = string.Empty;

    /// <summary>
    /// Single uppercase character: 'T', 'N', 'A', 'B', or 'C'.
    /// </summary>
    public required string CorrectAnswer { get; init; }

    public QuestionType Type { get; init; }
    public ExamScope Scope { get; init; }
    public int Points { get; init; }
    public string? MediaFileName { get; init; }
    public MediaKind MediaKind { get; init; }
    public required IReadOnlyList<string> Categories { get; init; }

    public (string Text, string AnswerA, string AnswerB, string AnswerC) GetLocalizedText(string? language)
    {
        var lang = (language ?? "PL").Trim().ToUpperInvariant();
        return lang switch
        {
            "EN" when !string.IsNullOrWhiteSpace(TextEn) => (
                TextEn,
                string.IsNullOrWhiteSpace(AnswerAEn) ? AnswerAPl : AnswerAEn,
                string.IsNullOrWhiteSpace(AnswerBEn) ? AnswerBPl : AnswerBEn,
                string.IsNullOrWhiteSpace(AnswerCEn) ? AnswerCPl : AnswerCEn),
            "DE" when !string.IsNullOrWhiteSpace(TextDe) => (
                TextDe,
                string.IsNullOrWhiteSpace(AnswerADe) ? AnswerAPl : AnswerADe,
                string.IsNullOrWhiteSpace(AnswerBDe) ? AnswerBPl : AnswerBDe,
                string.IsNullOrWhiteSpace(AnswerCDe) ? AnswerCPl : AnswerCDe),
            "UA" when !string.IsNullOrWhiteSpace(TextUa) => (
                TextUa,
                string.IsNullOrWhiteSpace(AnswerAUa) ? AnswerAPl : AnswerAUa,
                string.IsNullOrWhiteSpace(AnswerBUa) ? AnswerBPl : AnswerBUa,
                string.IsNullOrWhiteSpace(AnswerCUa) ? AnswerCPl : AnswerCUa),
            _ => (TextPl, AnswerAPl, AnswerBPl, AnswerCPl)
        };
    }

    public static MediaKind DetectMediaKind(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return MediaKind.None;
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".wmv" or ".mp4" or ".webm" or ".avi" or ".mov" or ".m4v" => MediaKind.Video,
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" => MediaKind.Image,
            _ => MediaKind.None
        };
    }
}
