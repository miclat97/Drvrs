namespace Drvrs.Web.Models;

public static class OfficialExamRules
{
    public const int TotalDurationSeconds = 25 * 60; // 1500s (25 minutes)
    public const int BasicQuestionCount = 20;
    public const int SpecialistQuestionCount = 12;
    public const int TotalQuestionCount = BasicQuestionCount + SpecialistQuestionCount; // 32

    public const int Basic3PtCount = 10;
    public const int Basic2PtCount = 6;
    public const int Basic1PtCount = 4;

    public const int Specialist3PtCount = 6;
    public const int Specialist2PtCount = 4;
    public const int Specialist1PtCount = 2;

    public const int MaxScore = 74;
    public const int PassingScore = 68;

    public const int BasicVideoReadSeconds = 20;
    public const int BasicVideoAnswerSeconds = 15;
    public const int BasicStaticTotalSeconds = 35; // 20s + 15s
    public const int SpecialistTotalSeconds = 50;
}

public enum QuestionTimerPhase
{
    ReadingBeforeVideo = 0,
    PlayingVideo = 1,
    AnsweringAfterVideo = 2,
    SingleCountdown = 3
}

public sealed class ExamSessionItem
{
    public required int Index { get; init; } // 0..31
    public required Question Question { get; init; }
    public required bool MediaAvailable { get; init; }

    public DateTimeOffset? OpenedAtUtc { get; set; }
    public DateTimeOffset? VideoStartedAtUtc { get; set; }
    public DateTimeOffset? VideoEndedAtUtc { get; set; }
    public DateTimeOffset? AnsweredAtUtc { get; set; }

    public string? SelectedAnswer { get; set; }
    public bool IsAnswered { get; set; }
    public bool TimedOut { get; set; }

    public bool UsesStagedVideoTimer =>
        Question.Scope == ExamScope.Basic &&
        Question.MediaKind == MediaKind.Video &&
        MediaAvailable;

    public bool IsCorrect =>
        !string.IsNullOrWhiteSpace(SelectedAnswer) &&
        string.Equals(SelectedAnswer, Question.CorrectAnswer, StringComparison.OrdinalIgnoreCase);

    public int EarnedPoints => IsCorrect ? Question.Points : 0;
}

public sealed class ExamSession
{
    public required Guid Id { get; init; }
    public required string Category { get; init; }
    public required string Language { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset GlobalExpiresAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? CompletionReason { get; set; }

    public required List<ExamSessionItem> Items { get; init; }
    public int CurrentIndex { get; set; }
    public bool IsCompleted => CompletedAtUtc.HasValue;
    public Lock SyncRoot { get; } = new();
}

public sealed record StartExamRequest(
    string? Category = "B",
    string? Language = "PL",
    bool OnlyAvailableMedia = false);

public sealed record SubmitAnswerRequest(
    string? SelectedAnswer,
    bool TimedOut = false);

public sealed record ExamQuestionViewDto(
    int Index,
    int TotalQuestions,
    int BasicQuestionsDone,
    int BasicQuestionsTotal,
    int SpecialistQuestionsDone,
    int SpecialistQuestionsTotal,
    string QuestionNumber,
    string Scope,
    string Type,
    int Points,
    string Text,
    string AnswerA,
    string AnswerB,
    string AnswerC,
    string? MediaUrl,
    string MediaKind,
    bool MediaAvailable,
    string? MissingMediaFileName,
    bool UsesStagedVideoTimer,
    string Phase,
    double PhaseRemainingSeconds,
    int ReadPhaseTotalSeconds,
    int AnswerPhaseTotalSeconds,
    int SingleCountdownTotalSeconds);

public sealed record ExamStateResponse(
    Guid ExamId,
    string Category,
    string Language,
    bool IsCompleted,
    string? CompletionReason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset GlobalExpiresAtUtc,
    double GlobalRemainingSeconds,
    ExamQuestionViewDto? CurrentQuestion,
    ExamSummaryDto? Summary);

public sealed record ExamQuestionReviewDto(
    int Index,
    string QuestionNumber,
    string Scope,
    string Type,
    int Points,
    int EarnedPoints,
    string Text,
    string AnswerA,
    string AnswerB,
    string AnswerC,
    string CorrectAnswer,
    string? SelectedAnswer,
    bool IsCorrect,
    bool TimedOut,
    string? MediaUrl,
    string MediaKind,
    bool MediaAvailable);

public sealed record ExamSummaryDto(
    Guid ExamId,
    string Category,
    string Language,
    bool Passed,
    int TotalScore,
    int MaxScore,
    int PassingScore,
    int BasicScore,
    int BasicMaxScore,
    int SpecialistScore,
    int SpecialistMaxScore,
    int CorrectCount,
    int WrongCount,
    int UnansweredCount,
    int DurationSeconds,
    string CompletionReason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<ExamQuestionReviewDto> Questions);
