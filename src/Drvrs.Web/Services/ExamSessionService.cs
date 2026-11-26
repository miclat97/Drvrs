using System.Collections.Concurrent;
using Drvrs.Web.Configuration;
using Drvrs.Web.Data;
using Drvrs.Web.Models;
using Microsoft.Extensions.Options;

namespace Drvrs.Web.Services;

public sealed class ExamSessionService(
    QuestionRepository repository,
    MediaService mediaService,
    TimeProvider timeProvider,
    IOptions<DrvrsOptions> options)
{
    private static readonly TimeSpan NetworkGraceWindow = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan MaxVideoPlaybackSafetyCap = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan ActiveSessionRetention = TimeSpan.FromMinutes(35);
    private static readonly TimeSpan CompletedSessionRetention = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, ExamSession> _sessions = new();

    public async Task<ExamStateResponse> StartExamAsync(StartExamRequest request, CancellationToken ct = default)
    {
        PruneOldSessions();

        var category = string.IsNullOrWhiteSpace(request.Category) ? "B" : request.Category.Trim().ToUpperInvariant();
        var language = NormalizeLanguage(request.Language);

        Func<string?, bool>? mediaFilter = request.OnlyAvailableMedia
            ? mediaService.HasMedia
            : null;

        var drawnQuestions = await repository.DrawOfficialExamQuestionsAsync(category, mediaFilter, ct);
        var now = timeProvider.GetUtcNow();

        var items = drawnQuestions
            .Select((q, idx) => new ExamSessionItem
            {
                Index = idx,
                Question = q,
                MediaAvailable = !string.IsNullOrWhiteSpace(q.MediaFileName) && mediaService.HasMedia(q.MediaFileName),
                OpenedAtUtc = idx == 0 ? now : null
            })
            .ToList();

        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            Category = category,
            Language = language,
            StartedAtUtc = now,
            GlobalExpiresAtUtc = now.AddSeconds(OfficialExamRules.TotalDurationSeconds),
            Items = items,
            CurrentIndex = 0
        };

        _sessions[session.Id] = session;

        // Proactively transcode WMV clips for this exam in background so videos play with zero wait
        mediaService.PrewarmExamVideosInBackground(
            items.Where(i => i.MediaAvailable && i.Question.MediaFileName is not null)
                 .Select(i => i.Question.MediaFileName!));

        lock (session.SyncRoot)
        {
            return BuildStateResponse(session, now);
        }
    }

    public ExamStateResponse? GetExamState(Guid examId)
    {
        if (!_sessions.TryGetValue(examId, out var session))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        lock (session.SyncRoot)
        {
            AdvanceTimersIfExpired(session, now);
            return BuildStateResponse(session, now);
        }
    }

    public ExamStateResponse NotifyVideoStarted(Guid examId, int questionIndex)
    {
        var session = RequireSession(examId);
        var now = timeProvider.GetUtcNow();

        lock (session.SyncRoot)
        {
            AdvanceTimersIfExpired(session, now);
            if (session.IsCompleted || session.CurrentIndex != questionIndex)
            {
                return BuildStateResponse(session, now);
            }

            var item = session.Items[questionIndex];
            if (item.UsesStagedVideoTimer && item.VideoStartedAtUtc is null)
            {
                var openedAt = item.OpenedAtUtc ?? now;
                var elapsedRead = (now - openedAt).TotalSeconds;
                // Only enter non-counted video phase if clicked within the 20s reading window (plus tiny network grace)
                if (elapsedRead <= OfficialExamRules.BasicVideoReadSeconds + NetworkGraceWindow.TotalSeconds)
                {
                    item.VideoStartedAtUtc = now;
                }
            }

            return BuildStateResponse(session, now);
        }
    }

    public ExamStateResponse NotifyVideoEnded(Guid examId, int questionIndex)
    {
        var session = RequireSession(examId);
        var now = timeProvider.GetUtcNow();

        lock (session.SyncRoot)
        {
            AdvanceTimersIfExpired(session, now);
            if (session.IsCompleted || session.CurrentIndex != questionIndex)
            {
                return BuildStateResponse(session, now);
            }

            var item = session.Items[questionIndex];
            if (item.UsesStagedVideoTimer && item.VideoStartedAtUtc is not null && item.VideoEndedAtUtc is null)
            {
                item.VideoEndedAtUtc = now;
            }

            return BuildStateResponse(session, now);
        }
    }

    public ExamStateResponse SubmitAnswer(Guid examId, int questionIndex, SubmitAnswerRequest request)
    {
        var session = RequireSession(examId);
        var now = timeProvider.GetUtcNow();

        lock (session.SyncRoot)
        {
            // 1. Global 25-minute hard cutoff
            if (!session.IsCompleted && now >= session.GlobalExpiresAtUtc)
            {
                session.CompletedAtUtc = session.GlobalExpiresAtUtc;
                session.CompletionReason = "UPŁYNĄŁ_CZAS_EGZAMINU";
                return BuildStateResponse(session, now);
            }

            if (session.IsCompleted)
            {
                return BuildStateResponse(session, now);
            }

            // 2. Enforce strict linear progression: no returning to previous questions or jumping ahead
            if (questionIndex != session.CurrentIndex)
            {
                throw new InvalidOperationException(
                    "Nie można wrócić do poprzedniego pytania ani pominąć kolejności pytań egzaminacyjnych.");
            }

            var item = session.Items[questionIndex];
            var normalizedAnswer = NormalizeAnswer(request.SelectedAnswer, item.Question.Type);

            // 3. Validate per-question deadline (accounting for 2.5s network RTT grace)
            var exceededDeadline = HasExceededQuestionDeadlineWithGrace(item, now);
            if (exceededDeadline)
            {
                // Time expired on the server beyond network grace -> answer is not counted
                item.SelectedAnswer = null;
                item.TimedOut = true;
            }
            else
            {
                item.SelectedAnswer = normalizedAnswer;
                item.TimedOut = request.TimedOut && normalizedAnswer is null;
            }

            item.IsAnswered = true;
            item.AnsweredAtUtc = now;

            MoveToNextQuestionOrComplete(session, now);
            return BuildStateResponse(session, now);
        }
    }

    public ExamStateResponse FinishExamEarly(Guid examId)
    {
        var session = RequireSession(examId);
        var now = timeProvider.GetUtcNow();

        lock (session.SyncRoot)
        {
            if (!session.IsCompleted)
            {
                session.CompletedAtUtc = now > session.GlobalExpiresAtUtc ? session.GlobalExpiresAtUtc : now;
                session.CompletionReason = now >= session.GlobalExpiresAtUtc
                    ? "UPŁYNĄŁ_CZAS_EGZAMINU"
                    : "ZAKOŃCZONO_PRZEZ_ZDAJĄCEGO";
            }

            return BuildStateResponse(session, now);
        }
    }

    private ExamSession RequireSession(Guid examId)
    {
        if (!_sessions.TryGetValue(examId, out var session))
        {
            throw new KeyNotFoundException($"Nie znaleziono sesji egzaminacyjnej o ID {examId}.");
        }
        return session;
    }

    private static void AdvanceTimersIfExpired(ExamSession session, DateTimeOffset now)
    {
        if (session.IsCompleted)
        {
            return;
        }

        if (now >= session.GlobalExpiresAtUtc)
        {
            session.CompletedAtUtc = session.GlobalExpiresAtUtc;
            session.CompletionReason = "UPŁYNĄŁ_CZAS_EGZAMINU";
            return;
        }

        while (!session.IsCompleted && session.CurrentIndex < session.Items.Count)
        {
            var current = session.Items[session.CurrentIndex];
            current.OpenedAtUtc ??= now;

            // Safety cap if video started long ago and client disconnected without sending video-ended
            if (current.UsesStagedVideoTimer &&
                current.VideoStartedAtUtc is not null &&
                current.VideoEndedAtUtc is null &&
                now - current.VideoStartedAtUtc.Value > MaxVideoPlaybackSafetyCap)
            {
                current.VideoEndedAtUtc = current.VideoStartedAtUtc.Value + MaxVideoPlaybackSafetyCap;
            }

            if (HasExceededQuestionDeadlineWithGrace(current, now))
            {
                current.SelectedAnswer = null;
                current.IsAnswered = true;
                current.TimedOut = true;
                current.AnsweredAtUtc = now;
                MoveToNextQuestionOrComplete(session, now);
            }
            else
            {
                break;
            }
        }
    }

    private static void MoveToNextQuestionOrComplete(ExamSession session, DateTimeOffset now)
    {
        session.CurrentIndex++;
        if (session.CurrentIndex >= session.Items.Count)
        {
            session.CompletedAtUtc = now;
            session.CompletionReason = "UKOŃCZONO_WSZYSTKIE_PYTANIA";
        }
        else
        {
            session.Items[session.CurrentIndex].OpenedAtUtc = now;
        }
    }

    private static (QuestionTimerPhase Phase, double RemainingSeconds) EvaluatePhase(ExamSessionItem item, DateTimeOffset now)
    {
        var openedAt = item.OpenedAtUtc ?? now;

        if (!item.UsesStagedVideoTimer)
        {
            var limit = item.Question.Scope == ExamScope.Basic
                ? OfficialExamRules.BasicStaticTotalSeconds
                : OfficialExamRules.SpecialistTotalSeconds;
            var rem = Math.Max(0, limit - (now - openedAt).TotalSeconds);
            return (QuestionTimerPhase.SingleCountdown, rem);
        }

        // Staged video timer: 20s read -> video playback (uncounted) -> 15s answer
        if (item.VideoStartedAtUtc is null)
        {
            var elapsedSinceOpen = (now - openedAt).TotalSeconds;
            if (elapsedSinceOpen < OfficialExamRules.BasicVideoReadSeconds)
            {
                return (QuestionTimerPhase.ReadingBeforeVideo, OfficialExamRules.BasicVideoReadSeconds - elapsedSinceOpen);
            }

            // User did not click video within 20s reading window -> 15s answer window counts down immediately
            var answerElapsed = elapsedSinceOpen - OfficialExamRules.BasicVideoReadSeconds;
            return (QuestionTimerPhase.AnsweringAfterVideo, Math.Max(0, OfficialExamRules.BasicVideoAnswerSeconds - answerElapsed));
        }

        if (item.VideoEndedAtUtc is null)
        {
            return (QuestionTimerPhase.PlayingVideo, OfficialExamRules.BasicVideoAnswerSeconds);
        }

        var elapsedAfterVideo = (now - item.VideoEndedAtUtc.Value).TotalSeconds;
        return (QuestionTimerPhase.AnsweringAfterVideo, Math.Max(0, OfficialExamRules.BasicVideoAnswerSeconds - elapsedAfterVideo));
    }

    private static bool HasExceededQuestionDeadlineWithGrace(ExamSessionItem item, DateTimeOffset now)
    {
        var openedAt = item.OpenedAtUtc ?? now;
        var grace = NetworkGraceWindow.TotalSeconds;

        if (!item.UsesStagedVideoTimer)
        {
            var limit = item.Question.Scope == ExamScope.Basic
                ? OfficialExamRules.BasicStaticTotalSeconds
                : OfficialExamRules.SpecialistTotalSeconds;
            return (now - openedAt).TotalSeconds > limit + grace;
        }

        if (item.VideoStartedAtUtc is null)
        {
            // 20s reading + 15s answer if video was never clicked
            var fallbackTotal = OfficialExamRules.BasicVideoReadSeconds + OfficialExamRules.BasicVideoAnswerSeconds;
            return (now - openedAt).TotalSeconds > fallbackTotal + grace;
        }

        if (item.VideoEndedAtUtc is null)
        {
            return false;
        }

        return (now - item.VideoEndedAtUtc.Value).TotalSeconds > OfficialExamRules.BasicVideoAnswerSeconds + grace;
    }

    private static ExamStateResponse BuildStateResponse(ExamSession session, DateTimeOffset now)
    {
        var globalRemaining = session.IsCompleted
            ? 0
            : Math.Max(0, (session.GlobalExpiresAtUtc - now).TotalSeconds);

        if (session.IsCompleted)
        {
            return new ExamStateResponse(
                ExamId: session.Id,
                Category: session.Category,
                Language: session.Language,
                IsCompleted: true,
                CompletionReason: session.CompletionReason,
                StartedAtUtc: session.StartedAtUtc,
                GlobalExpiresAtUtc: session.GlobalExpiresAtUtc,
                GlobalRemainingSeconds: 0,
                CurrentQuestion: null,
                Summary: BuildSummary(session));
        }

        var item = session.Items[session.CurrentIndex];
        var (phase, phaseRemaining) = EvaluatePhase(item, now);
        var (text, ansA, ansB, ansC) = item.Question.GetLocalizedText(session.Language);

        var basicDone = Math.Min(session.CurrentIndex, OfficialExamRules.BasicQuestionCount);
        var specDone = Math.Max(0, session.CurrentIndex - OfficialExamRules.BasicQuestionCount);

        var mediaUrl = item.MediaAvailable && !string.IsNullOrWhiteSpace(item.Question.MediaFileName)
            ? $"/api/media/{Uri.EscapeDataString(item.Question.MediaFileName)}"
            : null;

        var singleLimit = item.Question.Scope == ExamScope.Basic
            ? OfficialExamRules.BasicStaticTotalSeconds
            : OfficialExamRules.SpecialistTotalSeconds;

        var currentDto = new ExamQuestionViewDto(
            Index: item.Index,
            TotalQuestions: session.Items.Count,
            BasicQuestionsDone: basicDone,
            BasicQuestionsTotal: OfficialExamRules.BasicQuestionCount,
            SpecialistQuestionsDone: specDone,
            SpecialistQuestionsTotal: OfficialExamRules.SpecialistQuestionCount,
            QuestionNumber: item.Question.QuestionNumber,
            Scope: item.Question.Scope == ExamScope.Basic ? "PODSTAWOWY" : "SPECJALISTYCZNY",
            Type: item.Question.Type == QuestionType.YesNo ? "YES_NO" : "ABC",
            Points: item.Question.Points,
            Text: text,
            AnswerA: ansA,
            AnswerB: ansB,
            AnswerC: ansC,
            MediaUrl: mediaUrl,
            MediaKind: item.Question.MediaKind.ToString().ToUpperInvariant(),
            MediaAvailable: item.MediaAvailable,
            MissingMediaFileName: (!item.MediaAvailable && !string.IsNullOrWhiteSpace(item.Question.MediaFileName))
                ? item.Question.MediaFileName
                : null,
            UsesStagedVideoTimer: item.UsesStagedVideoTimer,
            Phase: phase switch
            {
                QuestionTimerPhase.ReadingBeforeVideo => "READING_BEFORE_VIDEO",
                QuestionTimerPhase.PlayingVideo => "PLAYING_VIDEO",
                QuestionTimerPhase.AnsweringAfterVideo => "ANSWERING_AFTER_VIDEO",
                _ => "SINGLE_COUNTDOWN"
            },
            PhaseRemainingSeconds: Math.Round(phaseRemaining, 2),
            ReadPhaseTotalSeconds: OfficialExamRules.BasicVideoReadSeconds,
            AnswerPhaseTotalSeconds: OfficialExamRules.BasicVideoAnswerSeconds,
            SingleCountdownTotalSeconds: singleLimit);

        return new ExamStateResponse(
            ExamId: session.Id,
            Category: session.Category,
            Language: session.Language,
            IsCompleted: false,
            CompletionReason: null,
            StartedAtUtc: session.StartedAtUtc,
            GlobalExpiresAtUtc: session.GlobalExpiresAtUtc,
            GlobalRemainingSeconds: Math.Round(globalRemaining, 2),
            CurrentQuestion: currentDto,
            Summary: null);
    }

    private static ExamSummaryDto BuildSummary(ExamSession session)
    {
        var basicItems = session.Items.Where(i => i.Question.Scope == ExamScope.Basic).ToList();
        var specItems = session.Items.Where(i => i.Question.Scope == ExamScope.Specialist).ToList();

        var basicScore = basicItems.Sum(i => i.EarnedPoints);
        var basicMax = basicItems.Sum(i => i.Question.Points);
        var specScore = specItems.Sum(i => i.EarnedPoints);
        var specMax = specItems.Sum(i => i.Question.Points);
        var totalScore = basicScore + specScore;

        var correctCount = session.Items.Count(i => i.IsCorrect);
        var unansweredCount = session.Items.Count(i => string.IsNullOrWhiteSpace(i.SelectedAnswer));
        var wrongCount = session.Items.Count - correctCount - unansweredCount;

        var completedAt = session.CompletedAtUtc ?? session.GlobalExpiresAtUtc;
        var durationSec = (int)Math.Clamp((completedAt - session.StartedAtUtc).TotalSeconds, 0, OfficialExamRules.TotalDurationSeconds);

        var reviews = session.Items.Select(i =>
        {
            var (text, a, b, c) = i.Question.GetLocalizedText(session.Language);
            var mediaUrl = i.MediaAvailable && !string.IsNullOrWhiteSpace(i.Question.MediaFileName)
                ? $"/api/media/{Uri.EscapeDataString(i.Question.MediaFileName)}"
                : null;

            return new ExamQuestionReviewDto(
                Index: i.Index,
                QuestionNumber: i.Question.QuestionNumber,
                Scope: i.Question.Scope == ExamScope.Basic ? "PODSTAWOWY" : "SPECJALISTYCZNY",
                Type: i.Question.Type == QuestionType.YesNo ? "YES_NO" : "ABC",
                Points: i.Question.Points,
                EarnedPoints: i.EarnedPoints,
                Text: text,
                AnswerA: a,
                AnswerB: b,
                AnswerC: c,
                CorrectAnswer: i.Question.CorrectAnswer,
                SelectedAnswer: i.SelectedAnswer,
                IsCorrect: i.IsCorrect,
                TimedOut: i.TimedOut,
                MediaUrl: mediaUrl,
                MediaKind: i.Question.MediaKind.ToString().ToUpperInvariant(),
                MediaAvailable: i.MediaAvailable);
        }).ToList();

        return new ExamSummaryDto(
            ExamId: session.Id,
            Category: session.Category,
            Language: session.Language,
            Passed: totalScore >= OfficialExamRules.PassingScore,
            TotalScore: totalScore,
            MaxScore: OfficialExamRules.MaxScore,
            PassingScore: OfficialExamRules.PassingScore,
            BasicScore: basicScore,
            BasicMaxScore: basicMax,
            SpecialistScore: specScore,
            SpecialistMaxScore: specMax,
            CorrectCount: correctCount,
            WrongCount: wrongCount,
            UnansweredCount: unansweredCount,
            DurationSeconds: durationSec,
            CompletionReason: session.CompletionReason ?? "UKOŃCZONO_WSZYSTKIE_PYTANIA",
            StartedAtUtc: session.StartedAtUtc,
            CompletedAtUtc: completedAt,
            Questions: reviews);
    }

    private static string? NormalizeAnswer(string? raw, QuestionType type)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var up = raw.Trim().ToUpperInvariant();
        return type switch
        {
            QuestionType.YesNo when up is "T" or "N" => up,
            QuestionType.SingleChoice when up is "A" or "B" or "C" => up,
            _ => null
        };
    }

    private static string NormalizeLanguage(string? lang)
    {
        var up = (lang ?? "PL").Trim().ToUpperInvariant();
        return up is "PL" or "EN" or "DE" or "UA" ? up : "PL";
    }

    private void PruneOldSessions()
    {
        var now = timeProvider.GetUtcNow();
        var activeCutoff = now - ActiveSessionRetention;
        var completedCutoff = now - CompletedSessionRetention;

        foreach (var (id, session) in _sessions)
        {
            var expired = session.CompletedAtUtc.HasValue
                ? session.CompletedAtUtc.Value < completedCutoff
                : session.StartedAtUtc < activeCutoff;

            if (expired)
            {
                _sessions.TryRemove(id, out _);
            }
        }

        var maxSessions = Math.Max(50, options.Value.MaxActiveExamSessions);
        if (_sessions.Count >= maxSessions)
        {
            var overflow = _sessions.Count - maxSessions + 1;
            var oldestIds = _sessions.Values
                .OrderBy(s => s.CompletedAtUtc.HasValue ? 0 : 1)
                .ThenBy(s => s.StartedAtUtc)
                .Take(overflow)
                .Select(s => s.Id)
                .ToList();

            foreach (var id in oldestIds)
            {
                _sessions.TryRemove(id, out _);
            }
        }
    }
}
