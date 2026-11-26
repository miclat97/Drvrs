(() => {
  'use strict';

  const HISTORY_STORAGE_KEY = 'drvrs_exam_history_v1';

  // Application state
  const state = {
    currentView: 'home',
    dbStatus: null,
    syncPollTimer: null,

    // Active Exam
    exam: null,
    selectedAnswer: null,
    skipArmed: false,
    isSubmitting: false,
    globalDeadlinePerf: 0,
    phase: 'SINGLE_COUNTDOWN',
    phaseTotalSec: 35,
    phaseDeadlinePerf: 0,
    videoClickedInCurrentQuestion: false,
    videoFinishedInCurrentQuestion: false,
    rafId: null,

    // Summary Review
    lastSummary: null,
    reviewFilter: 'all',

    // Study Mode
    studyPage: 1,
    studyPageSize: 15,
    studyTotalCount: 0
  };

  // DOM helpers
  const $ = (id) => document.getElementById(id);

  function escapeHtml(str) {
    if (!str) return '';
    return String(str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function formatClock(totalSeconds) {
    const clamped = Math.max(0, Math.ceil(totalSeconds));
    const mins = Math.floor(clamped / 60);
    const secs = clamped % 60;
    return `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
  }

  function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return '0 MB';
    const mb = bytes / (1024 * 1024);
    if (mb < 1024) return `${mb.toFixed(1)} MB`;
    return `${(mb / 1024).toFixed(2)} GB`;
  }

  function pointsDescription(pts) {
    if (pts === 3) return 'Wartość: 3 pkt · wysokie znaczenie dla BRD';
    if (pts === 2) return 'Wartość: 2 pkt · średnie znaczenie dla BRD';
    return 'Wartość: 1 pkt · niskie znaczenie dla BRD';
  }

  function formatAnswerLabel(code, q) {
    if (!code) return 'Brak odpowiedzi (czas minął / pominięto)';
    const up = code.toUpperCase();
    if (up === 'T') return 'TAK';
    if (up === 'N') return 'NIE';
    if (up === 'A') return `A — ${q.answerA || ''}`;
    if (up === 'B') return `B — ${q.answerB || ''}`;
    if (up === 'C') return `C — ${q.answerC || ''}`;
    return up;
  }

  // =========================================================================
  // View Navigation
  // =========================================================================
  function switchView(viewName) {
    if (state.currentView === 'exam' && viewName !== 'exam' && viewName !== 'summary') {
      if (state.exam && !state.exam.isCompleted) {
        const leave = window.confirm('Trwa egzamin państwowy! Czy na pewno chcesz przerwać i zakończyć obecny egzamin?');
        if (!leave) return;
        finishExamEarlyAndNavigate(viewName);
        return;
      }
    }

    state.currentView = viewName;
    const views = ['home', 'exam', 'summary', 'study', 'history', 'data'];
    views.forEach((v) => {
      const el = $(`view-${v}`);
      if (el) el.classList.toggle('hidden', v !== viewName);
    });

    document.querySelectorAll('.nav-btn').forEach((btn) => {
      btn.classList.toggle('active', btn.dataset.view === viewName);
    });

    // Hide main navigation bar distractions while inside active state exam
    $('main-nav').classList.toggle('hidden', viewName === 'exam');

    if (viewName === 'home') {
      refreshStatus();
    } else if (viewName === 'study') {
      loadStudyQuestions();
    } else if (viewName === 'history') {
      renderHistoryView();
    } else if (viewName === 'data') {
      refreshStatus();
    }

    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  async function finishExamEarlyAndNavigate(targetView) {
    stopExamLoop();
    if (state.exam && state.exam.examId) {
      try {
        await fetch(`/api/exams/${state.exam.examId}/finish`, { method: 'POST' });
      } catch {
        // ignore
      }
    }
    state.exam = null;
    switchView(targetView);
  }

  // =========================================================================
  // Database & Media Status + Gov.pl Sync
  // =========================================================================
  async function refreshStatus() {
    try {
      const resp = await fetch('/api/status');
      if (!resp.ok) return;
      const status = await resp.json();
      state.dbStatus = status;
      renderStatusUI(status);

      if (status.syncJob && status.syncJob.isRunning) {
        ensureSyncPolling();
      } else {
        stopSyncPolling();
      }
    } catch (err) {
      console.error('Nie udało się pobrać statusu bazy:', err);
    }
  }

  function renderStatusUI(status) {
    const qSum = $('status-questions-summary');
    const mSum = $('status-media-summary');
    const partialWrap = $('partial-media-option-wrap');

    if (status.hasQuestions) {
      qSum.innerHTML = `<span class="status-dot ok"></span>Baza SQLite gotowa: <strong>${status.totalQuestions.toLocaleString('pl-PL')}</strong> pytań (kat. B: <strong>${status.categoryBQuestions.toLocaleString('pl-PL')}</strong>)`;
    } else {
      qSum.innerHTML = `<span class="status-dot warn"></span>Brak zaimportowanych pytań w bazie SQLite — pobierz bazę w zakładce „Baza i multimedia”`;
    }

    const totalRef = status.totalDistinctMediaReferenced || 0;
    const availRef = status.availableMediaFilesCount || 0;
    if (totalRef > 0) {
      mSum.textContent = `Multimedia: ${availRef.toLocaleString('pl-PL')} / ${totalRef.toLocaleString('pl-PL')}`;
      // Show convenience filter option when partial media is loaded on disk
      const isPartial = availRef > 0 && availRef < totalRef * 0.95;
      partialWrap.classList.toggle('hidden', !isPartial);
    } else {
      mSum.textContent = '';
      partialWrap.classList.add('hidden');
    }

    // Populate category dropdowns dynamically if DB has categories
    if (Array.isArray(status.categories) && status.categories.length > 0) {
      populateCategorySelect($('exam-category-select'), status.categories, true);
      populateCategorySelect($('study-category-select'), status.categories, false);
    }

    // Data management view metrics
    $('data-stat-questions').textContent = status.totalQuestions.toLocaleString('pl-PL');
    $('data-stat-catb').textContent = status.categoryBQuestions.toLocaleString('pl-PL');
    $('data-stat-media').textContent = totalRef > 0
      ? `${availRef.toLocaleString('pl-PL')} / ${totalRef.toLocaleString('pl-PL')}`
      : availRef.toLocaleString('pl-PL');
    $('data-stat-ffmpeg').textContent = status.ffmpegAvailable ? 'Aktywny (H.264)' : 'Brak w PATH';

    $('admin-token-box').classList.toggle('hidden', !status.adminTokenRequired);
    $('admin-sync-buttons').classList.toggle('hidden', !status.adminSyncEnabled);

    $('data-paths-code').textContent =
      `Katalog danych: ${status.dataDirectoryPath}\n` +
      `Arkusz pytań (.xlsx): ${status.dataDirectoryPath}/questions/\n` +
      `Pliki multimediów (.jpg, .wmv, .mp4): ${status.mediaDirectoryPath}/`;

    // Sync job box
    const job = status.syncJob;
    if (job) {
      $('sync-stage-label').textContent = job.isRunning
        ? `Trwa synchronizacja (${job.stage})`
        : (job.stage === 'ERROR' ? 'Błąd synchronizacji' : 'Status synchronizacji');
      $('sync-message-text').textContent = job.message || 'Gotowy.';

      if (job.totalBytes && job.totalBytes > 0) {
        const pct = Math.min(100, Math.round((job.downloadedBytes / job.totalBytes) * 100));
        $('sync-bytes-label').textContent = `${formatBytes(job.downloadedBytes)} / ${formatBytes(job.totalBytes)} (${pct}%)`;
      } else if (job.downloadedBytes > 0) {
        $('sync-bytes-label').textContent = formatBytes(job.downloadedBytes);
      } else if (job.totalArchiveEntries) {
        $('sync-bytes-label').textContent = `${job.extractedFiles} / ${job.totalArchiveEntries} plików`;
      } else {
        $('sync-bytes-label').textContent = '';
      }

      $('btn-sync-questions').disabled = job.isRunning;
      $('btn-sync-all-media').disabled = job.isRunning;
    }
  }

  function populateCategorySelect(selectEl, categories, onlyExamReady) {
    if (!selectEl) return;
    const prev = selectEl.value || 'B';
    const names = {
      B: 'Kategoria B (samochód osobowy)',
      A: 'Kategoria A (motocykl)',
      A1: 'Kategoria A1',
      A2: 'Kategoria A2',
      AM: 'Kategoria AM (motorower)',
      B1: 'Kategoria B1',
      C: 'Kategoria C (ciężarowy)',
      C1: 'Kategoria C1',
      D: 'Kategoria D (autobus)',
      D1: 'Kategoria D1',
      T: 'Kategoria T (ciągnik rolniczy)',
      PT: 'Pozwolenie tramwajowe (PT)'
    };

    const filtered = onlyExamReady ? categories.filter((c) => c.readyForOfficialExam) : categories;
    if (filtered.length === 0) return;

    selectEl.innerHTML = filtered
      .map((c) => {
        const label = onlyExamReady
          ? `${names[c.category] || `Kategoria ${c.category}`} · ${c.totalQuestions} pytań`
          : `Kat. ${c.category} (${c.totalQuestions})`;
        return `<option value="${escapeHtml(c.category)}">${escapeHtml(label)}</option>`;
      })
      .join('');

    if ([...selectEl.options].some((o) => o.value === prev)) {
      selectEl.value = prev;
    }
  }

  function ensureSyncPolling() {
    if (state.syncPollTimer) return;
    state.syncPollTimer = window.setInterval(refreshStatus, 1200);
  }

  function stopSyncPolling() {
    if (state.syncPollTimer) {
      window.clearInterval(state.syncPollTimer);
      state.syncPollTimer = null;
    }
  }

  function getAdminHeaders() {
    const headers = { 'Content-Type': 'application/json' };
    const tok = $('admin-token-input')?.value?.trim();
    if (tok) headers['X-Admin-Token'] = tok;
    return headers;
  }

  async function triggerGovSync(downloadMedia) {
    if (downloadMedia) {
      const ok = window.confirm(
        'Pobieranie pełnej paczki multimediów ze strony gov.pl obejmuje ok. 9.5 GB danych (archiwum zostanie automatycznie rozpakowane i natychmiast usunięte po wypakowaniu).\n\nCzy chcesz rozpocząć pobieranie w tle?'
      );
      if (!ok) return;
    }

    const convertWmv = Boolean($('sync-convert-wmv')?.checked);
    const resp = await fetch('/api/admin/sync', {
      method: 'POST',
      headers: getAdminHeaders(),
      body: JSON.stringify({
        downloadQuestions: true,
        downloadMedia: downloadMedia,
        convertWmvToMp4: convertWmv
      })
    });

    if (resp.status === 401) {
      window.alert('Wymagany prawidłowy token administratora (X-Admin-Token).');
      return;
    }

    await refreshStatus();
    ensureSyncPolling();
  }

  async function triggerLocalRescan() {
    const resp = await fetch('/api/admin/rescan', {
      method: 'POST',
      headers: getAdminHeaders()
    });
    if (resp.status === 401) {
      window.alert('Wymagany prawidłowy token administratora (X-Admin-Token).');
      return;
    }
    await refreshStatus();
  }

  // =========================================================================
  // Official State Exam Engine (Client + Server Synchronized)
  // =========================================================================
  async function startOfficialExam() {
    const errEl = $('home-error-msg');
    errEl.classList.add('hidden');
    $('btn-start-exam').disabled = true;

    const category = $('exam-category-select').value || 'B';
    const language = $('exam-language-select').value || 'PL';
    const onlyAvailableMedia = Boolean($('exam-only-available-media')?.checked);

    try {
      const resp = await fetch('/api/exams', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ category, language, onlyAvailableMedia })
      });

      const payload = await resp.json();
      if (!resp.ok) {
        errEl.textContent = payload.error || 'Nie udało się rozpocząć egzaminu.';
        errEl.classList.remove('hidden');
        return;
      }

      applyExamStateFromServer(payload, true);
      switchView('exam');
      startExamLoop();
    } catch (err) {
      errEl.textContent = 'Błąd połączenia z serwerem aplikacji.';
      errEl.classList.remove('hidden');
    } finally {
      $('btn-start-exam').disabled = false;
    }
  }

  function applyExamStateFromServer(examState, isNewQuestion) {
    state.exam = examState;
    state.isSubmitting = false;

    if (examState.isCompleted && examState.summary) {
      stopExamLoop();
      saveExamToLocalHistory(examState.summary);
      renderSummaryView(examState.summary);
      switchView('summary');
      return;
    }

    const nowPerf = performance.now();
    state.globalDeadlinePerf = nowPerf + examState.globalRemainingSeconds * 1000;

    const q = examState.currentQuestion;
    if (!q) return;

    if (isNewQuestion) {
      state.selectedAnswer = null;
      state.skipArmed = false;
      state.videoClickedInCurrentQuestion = false;
      state.videoFinishedInCurrentQuestion = false;
      $('exam-skip-confirm-msg').classList.add('hidden');
    }

    syncQuestionPhaseFromDto(q, nowPerf);

    if (isNewQuestion) {
      renderCurrentExamQuestion(examState.category, q);
    } else {
      updatePhaseHeaderUI();
    }
  }

  function syncQuestionPhaseFromDto(q, nowPerf) {
    state.phase = q.phase;
    if (q.phase === 'READING_BEFORE_VIDEO') {
      state.phaseTotalSec = q.readPhaseTotalSeconds; // 20s
      state.phaseDeadlinePerf = nowPerf + q.phaseRemainingSeconds * 1000;
    } else if (q.phase === 'PLAYING_VIDEO') {
      state.phaseTotalSec = q.answerPhaseTotalSeconds; // 15s waiting
      state.phaseDeadlinePerf = 0;
    } else if (q.phase === 'ANSWERING_AFTER_VIDEO') {
      state.phaseTotalSec = q.answerPhaseTotalSeconds; // 15s
      state.phaseDeadlinePerf = nowPerf + q.phaseRemainingSeconds * 1000;
    } else {
      state.phaseTotalSec = q.singleCountdownTotalSeconds; // 35s or 50s
      state.phaseDeadlinePerf = nowPerf + q.phaseRemainingSeconds * 1000;
    }
  }

  function renderCurrentExamQuestion(category, q) {
    $('exam-active-cat-badge').textContent = category;

    const isBasic = q.scope === 'PODSTAWOWY';
    $('counter-basic-wrap').classList.toggle('active-part', isBasic);
    $('counter-spec-wrap').classList.toggle('active-part', !isBasic);

    const basicDisplay = isBasic
      ? `${Math.min(q.basicQuestionsTotal, q.basicQuestionsDone + 1)} / ${q.basicQuestionsTotal}`
      : `${q.basicQuestionsTotal} / ${q.basicQuestionsTotal}`;
    const specDisplay = !isBasic
      ? `${Math.min(q.specialistQuestionsTotal, q.specialistQuestionsDone + 1)} / ${q.specialistQuestionsTotal}`
      : `0 / ${q.specialistQuestionsTotal}`;

    $('exam-basic-progress').textContent = basicDisplay;
    $('exam-spec-progress').textContent = specDisplay;

    $('exam-question-number').textContent = `Pytanie ${q.index + 1}/32 · Nr kat. ${q.questionNumber}`;

    const ptsEl = $('exam-question-points');
    ptsEl.textContent = pointsDescription(q.points);
    ptsEl.className = `points-badge pts-${q.points}`;

    $('exam-question-text').textContent = q.text;

    renderExamMediaViewport(q);
    renderExamAnswerButtons(q);
    updateNextButtonLabel();
    updatePhaseHeaderUI();
  }

  function renderExamMediaViewport(q) {
    const viewport = $('exam-media-viewport');
    viewport.innerHTML = '';

    if (q.mediaKind === 'VIDEO' && q.mediaAvailable && q.mediaUrl) {
      // Strictly NO autoplay! Video starts ONLY when user clicks on the video overlay.
      const video = document.createElement('video');
      video.id = 'exam-video-player';
      video.src = q.mediaUrl;
      video.preload = 'auto';
      video.playsInline = true;
      video.controls = false;
      video.disablePictureInPicture = true;

      const progressWrap = document.createElement('div');
      progressWrap.className = 'video-playback-bar hidden';
      progressWrap.id = 'exam-video-progress-wrap';
      const progressFill = document.createElement('div');
      progressFill.className = 'video-playback-fill';
      progressFill.id = 'exam-video-progress-fill';
      progressWrap.appendChild(progressFill);

      const statusTag = document.createElement('div');
      statusTag.className = 'video-status-tag hidden';
      statusTag.id = 'exam-video-status-tag';

      const overlay = document.createElement('button');
      overlay.type = 'button';
      overlay.className = 'video-click-overlay';
      overlay.id = 'btn-start-video-playback';
      overlay.innerHTML = `
        <span class="play-emblem" aria-hidden="true">
          <svg width="26" height="26" viewBox="0 0 24 24" fill="currentColor">
            <path d="M8 5.14v13.72a1 1 0 0 0 1.52.85l11-6.86a1 1 0 0 0 0-1.7l-11-6.86A1 1 0 0 0 8 5.14z"/>
          </svg>
        </span>
        <span class="play-overlay-title">Kliknij w film, aby rozpocząć odtwarzanie</span>
        <span class="play-overlay-sub">
          Zapoznaj się z treścią pytania (20 s) i kliknij tutaj, aby obejrzeć sytuację drogową.
          Czas trwania filmu nie wlicza się do 15 s na odpowiedź.
        </span>
      `;

      overlay.addEventListener('click', () => handleUserStartVideo(video, overlay, progressWrap, statusTag));

      video.addEventListener('timeupdate', () => {
        if (video.duration > 0) {
          const pct = Math.min(100, (video.currentTime / video.duration) * 100);
          progressFill.style.width = `${pct}%`;
        }
      });

      video.addEventListener('ended', () => {
        progressFill.style.width = '100%';
        statusTag.textContent = 'Materiał wideo zakończony';
        statusTag.classList.remove('hidden');
        handleVideoEnded();
      });

      video.addEventListener('error', () => {
        statusTag.textContent = 'Nie udało się odtworzyć pliku wideo';
        statusTag.classList.remove('hidden');
        if (state.phase === 'PLAYING_VIDEO') {
          handleVideoEnded();
        }
      });

      viewport.appendChild(video);
      viewport.appendChild(progressWrap);
      viewport.appendChild(statusTag);
      viewport.appendChild(overlay);
      return;
    }

    if (q.mediaKind === 'IMAGE' && q.mediaAvailable && q.mediaUrl) {
      const img = document.createElement('img');
      img.src = q.mediaUrl;
      img.alt = 'Sytuacja drogowa do pytania egzaminacyjnego';
      viewport.appendChild(img);
      return;
    }

    if (q.missingMediaFileName) {
      viewport.innerHTML = `
        <div class="no-media-placeholder">
          <div class="road-plate" aria-hidden="true">!</div>
          <div class="no-media-caption">Brak pliku multimedialnego na serwerze: <span class="mono-num">${escapeHtml(q.missingMediaFileName)}</span></div>
          <div style="font-size:0.78rem; opacity:0.8;">Wgraj archiwum multimediów z gov.pl w zakładce „Baza i multimedia”.</div>
        </div>
      `;
      return;
    }

    viewport.innerHTML = `
      <div class="no-media-placeholder">
        <div class="road-plate" aria-hidden="true">i</div>
        <div class="no-media-caption">Pytanie bez załącznika graficznego / wideo</div>
      </div>
    `;
  }

  async function handleUserStartVideo(videoEl, overlayEl, progressWrapEl, statusTagEl) {
    if (state.videoClickedInCurrentQuestion || !state.exam || !state.exam.currentQuestion) return;
    state.videoClickedInCurrentQuestion = true;

    overlayEl.classList.add('hidden');
    progressWrapEl.classList.remove('hidden');
    statusTagEl.textContent = 'Trwa odtwarzanie filmu...';
    statusTagEl.classList.remove('hidden');

    const wasInReadingWindow = state.phase === 'READING_BEFORE_VIDEO';
    if (wasInReadingWindow) {
      state.phase = 'PLAYING_VIDEO';
      state.phaseDeadlinePerf = 0;
      updatePhaseHeaderUI();
    }

    try {
      await videoEl.play();
    } catch (err) {
      console.warn('Odtwarzanie wideo napotkało błąd:', err);
    }

    if (wasInReadingWindow) {
      const examId = state.exam.examId;
      const qIdx = state.exam.currentQuestion.index;
      try {
        const resp = await fetch(`/api/exams/${examId}/questions/${qIdx}/video-started`, { method: 'POST' });
        if (resp.ok) {
          const updated = await resp.json();
          if (updated.isCompleted) {
            applyExamStateFromServer(updated, false);
          }
        }
      } catch {
        // local timer continues cleanly
      }
    }
  }

  async function handleVideoEnded() {
    if (state.videoFinishedInCurrentQuestion || !state.exam || !state.exam.currentQuestion) return;
    state.videoFinishedInCurrentQuestion = true;

    const q = state.exam.currentQuestion;
    const examId = state.exam.examId;

    // Immediately transition client timer to 15s answer window if we were in PLAYING_VIDEO
    if (state.phase === 'PLAYING_VIDEO') {
      const nowPerf = performance.now();
      state.phase = 'ANSWERING_AFTER_VIDEO';
      state.phaseTotalSec = q.answerPhaseTotalSeconds; // 15s
      state.phaseDeadlinePerf = nowPerf + q.answerPhaseTotalSeconds * 1000;
      updatePhaseHeaderUI();
    }

    try {
      const resp = await fetch(`/api/exams/${examId}/questions/${q.index}/video-ended`, { method: 'POST' });
      if (resp.ok) {
        const updated = await resp.json();
        if (updated.isCompleted) {
          applyExamStateFromServer(updated, false);
        } else if (updated.currentQuestion && updated.currentQuestion.index === q.index) {
          syncQuestionPhaseFromDto(updated.currentQuestion, performance.now());
        }
      }
    } catch {
      // local 15s timer already running
    }
  }

  function renderExamAnswerButtons(q) {
    const container = $('exam-answers-container');
    container.innerHTML = '';

    if (q.type === 'YES_NO') {
      const grid = document.createElement('div');
      grid.className = 'yesno-grid';
      grid.innerHTML = `
        <button type="button" class="answer-btn yesno-choice" id="btn-answer-T" data-answer="T">
          <span class="key-cap">T</span>
          <span>TAK</span>
        </button>
        <button type="button" class="answer-btn yesno-choice" id="btn-answer-N" data-answer="N">
          <span class="key-cap">N</span>
          <span>NIE</span>
        </button>
      `;
      grid.querySelectorAll('.answer-btn').forEach((btn) => {
        btn.addEventListener('click', () => selectAnswerOption(btn.dataset.answer));
      });
      container.appendChild(grid);
    } else {
      const stack = document.createElement('div');
      stack.className = 'abc-stack';
      const variants = [
        { code: 'A', text: q.answerA },
        { code: 'B', text: q.answerB },
        { code: 'C', text: q.answerC }
      ];
      stack.innerHTML = variants
        .map(
          (v) => `
          <button type="button" class="answer-btn" id="btn-answer-${v.code}" data-answer="${v.code}">
            <span class="key-cap">${v.code}</span>
            <span class="answer-text">${escapeHtml(v.text)}</span>
          </button>
        `
        )
        .join('');
      stack.querySelectorAll('.answer-btn').forEach((btn) => {
        btn.addEventListener('click', () => selectAnswerOption(btn.dataset.answer));
      });
      container.appendChild(stack);
    }
  }

  function selectAnswerOption(code) {
    state.selectedAnswer = code;
    state.skipArmed = false;
    $('exam-skip-confirm-msg').classList.add('hidden');

    document.querySelectorAll('#exam-answers-container .answer-btn').forEach((btn) => {
      btn.classList.toggle('selected', btn.dataset.answer === code);
    });

    updateNextButtonLabel();
  }

  function updateNextButtonLabel() {
    const btn = $('btn-next-question');
    const q = state.exam?.currentQuestion;
    const isLast = q && q.index === q.totalQuestions - 1;

    if (state.selectedAnswer) {
      btn.textContent = isLast
        ? `Zatwierdź odpowiedź (${state.selectedAnswer}) i zakończ egzamin →`
        : `Zatwierdź odpowiedź (${state.selectedAnswer}) i przejdź dalej →`;
    } else {
      btn.textContent = isLast ? 'Pomiń i zakończ egzamin →' : 'Następne pytanie →';
    }
  }

  function handleNextQuestionClick() {
    if (state.isSubmitting || !state.exam || !state.exam.currentQuestion) return;

    if (!state.selectedAnswer && !state.skipArmed) {
      state.skipArmed = true;
      $('exam-skip-confirm-msg').classList.remove('hidden');
      return;
    }

    submitCurrentQuestion(false);
  }

  async function submitCurrentQuestion(timedOut) {
    if (state.isSubmitting || !state.exam || !state.exam.currentQuestion) return;
    state.isSubmitting = true;

    const examId = state.exam.examId;
    const qIndex = state.exam.currentQuestion.index;
    const chosen = state.selectedAnswer;

    try {
      const resp = await fetch(`/api/exams/${examId}/questions/${qIndex}/submit`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          selectedAnswer: chosen,
          timedOut: Boolean(timedOut)
        })
      });

      if (resp.ok) {
        const nextState = await resp.json();
        applyExamStateFromServer(nextState, true);
      } else {
        // Re-sync state from server on conflict
        const syncResp = await fetch(`/api/exams/${examId}`);
        if (syncResp.ok) {
          applyExamStateFromServer(await syncResp.json(), true);
        }
      }
    } catch (err) {
      console.error('Błąd zapisu odpowiedzi:', err);
      state.isSubmitting = false;
    }
  }

  // =========================================================================
  // Smooth 60fps Monotonic Timer Loop
  // =========================================================================
  function startExamLoop() {
    stopExamLoop();
    const tick = () => {
      if (state.currentView !== 'exam' || !state.exam || state.exam.isCompleted) return;

      const nowPerf = performance.now();

      // 1. Global 25-minute countdown
      const globalRemSec = Math.max(0, (state.globalDeadlinePerf - nowPerf) / 1000);
      const globalEl = $('exam-global-timer');
      globalEl.textContent = formatClock(globalRemSec);
      globalEl.classList.toggle('warn', globalRemSec <= 180 && globalRemSec > 60);
      globalEl.classList.toggle('urgent', globalRemSec <= 60);

      if (globalRemSec <= 0 && !state.isSubmitting) {
        state.isSubmitting = true;
        fetch(`/api/exams/${state.exam.examId}/finish`, { method: 'POST' })
          .then((r) => r.json())
          .then((finalState) => applyExamStateFromServer(finalState, false))
          .catch(() => {});
        return;
      }

      // 2. Per-question phase countdown
      const q = state.exam.currentQuestion;
      if (q && !state.isSubmitting) {
        if (state.phase === 'PLAYING_VIDEO') {
          updatePhaseVisuals('PLAYING_VIDEO', q.answerPhaseTotalSeconds, q.answerPhaseTotalSeconds);
        } else {
          const phaseRem = Math.max(0, (state.phaseDeadlinePerf - nowPerf) / 1000);
          updatePhaseVisuals(state.phase, phaseRem, state.phaseTotalSec);

          if (phaseRem <= 0) {
            if (state.phase === 'READING_BEFORE_VIDEO') {
              // 20s reading window expired without user clicking the video:
              // Do NOT autoplay video — immediately start the 15s answer countdown!
              state.phase = 'ANSWERING_AFTER_VIDEO';
              state.phaseTotalSec = q.answerPhaseTotalSeconds;
              state.phaseDeadlinePerf = nowPerf + q.answerPhaseTotalSeconds * 1000;
              updatePhaseHeaderUI();
            } else {
              // Answer countdown reached 0 -> automatically advance (saving marked answer if any, or null if none)
              submitCurrentQuestion(true);
              return;
            }
          }
        }
      }

      state.rafId = window.requestAnimationFrame(tick);
    };

    state.rafId = window.requestAnimationFrame(tick);
  }

  function stopExamLoop() {
    if (state.rafId) {
      window.cancelAnimationFrame(state.rafId);
      state.rafId = null;
    }
  }

  function updatePhaseHeaderUI() {
    const q = state.exam?.currentQuestion;
    if (!q) return;

    const labelEl = $('exam-phase-label');
    const hintEl = $('exam-phase-hint');

    if (state.phase === 'READING_BEFORE_VIDEO') {
      labelEl.textContent = 'Czas na przeczytanie pytania';
      hintEl.textContent = 'Kliknij w film na ekranie obok (lub naciśnij Spację), aby rozpocząć odtwarzanie wideo.';
    } else if (state.phase === 'PLAYING_VIDEO') {
      labelEl.textContent = 'Odtwarzanie materiału wideo';
      hintEl.textContent = 'Obejrzyj film do końca — czas trwania filmu nie pomniejsza Twoich 15 sekund na odpowiedź.';
    } else if (state.phase === 'ANSWERING_AFTER_VIDEO') {
      labelEl.textContent = 'Czas na udzielenie odpowiedzi';
      hintEl.textContent = state.videoClickedInCurrentQuestion
        ? 'Wskaż TAK lub NIE przed upływem 15 sekund.'
        : 'Upłynęło 20 s na uruchomienie filmu — trwa odliczanie 15 s na odpowiedź!';
    } else if (q.scope === 'PODSTAWOWY') {
      labelEl.textContent = 'Czas na przeczytanie i odpowiedź';
      hintEl.textContent = 'Pytanie podstawowe bez filmu: łącznie 35 sekund (20 s + 15 s).';
    } else {
      labelEl.textContent = 'Czas na przeczytanie i odpowiedź';
      hintEl.textContent = 'Pytanie specjalistyczne (A/B/C): łącznie 50 sekund.';
    }
  }

  function updatePhaseVisuals(phase, remSec, totalSec) {
    const secEl = $('exam-phase-seconds');
    const barEl = $('exam-phase-bar');

    if (phase === 'PLAYING_VIDEO') {
      secEl.textContent = 'FILM';
      secEl.className = 'phase-seconds mono-num paused';
      barEl.style.width = '100%';
      barEl.className = 'timer-bar-fill paused';
      return;
    }

    const ceilSec = Math.ceil(remSec);
    secEl.textContent = `${ceilSec} s`;
    const isUrgent = phase !== 'READING_BEFORE_VIDEO' && remSec <= 5;
    secEl.className = `phase-seconds mono-num${isUrgent ? ' urgent' : ''}`;

    const pct = totalSec > 0 ? Math.max(0, Math.min(100, (remSec / totalSec) * 100)) : 0;
    barEl.style.width = `${pct}%`;
    barEl.className = `timer-bar-fill${isUrgent ? ' urgent' : ''}`;
  }

  // =========================================================================
  // Exam Summary & Detailed Review
  // =========================================================================
  function renderSummaryView(summary) {
    state.lastSummary = summary;
    state.reviewFilter = 'all';

    const badge = $('summary-verdict-badge');
    if (summary.passed) {
      badge.textContent = 'WYNIK POZYTYWNY — EGZAMIN ZDANY';
      badge.className = 'result-verdict-badge passed';
      $('summary-title').textContent = `Gratulacje! Zdałeś egzamin kat. ${summary.category}`;
    } else {
      badge.textContent = 'WYNIK NEGATYWNY — EGZAMIN NIEZDANY';
      badge.className = 'result-verdict-badge failed';
      $('summary-title').textContent = `Niestety, zabrakło ${summary.passingScore - summary.totalScore} pkt do zdania`;
    }

    const reasonMap = {
      'UKOŃCZONO_WSZYSTKIE_PYTANIA': 'Ukończono wszystkie 32 pytania egzaminacyjne.',
      'UPŁYNĄŁ_CZAS_EGZAMINU': 'Egzamin został zakończony automatycznie po upływie 25 minut.',
      'ZAKOŃCZONO_PRZEZ_ZDAJĄCEGO': 'Egzamin został zakończony wcześniej przez zdającego.'
    };
    $('summary-subtitle').textContent =
      `${reasonMap[summary.completionReason] || summary.completionReason} Wymagany próg zdawalności: ${summary.passingScore} / ${summary.maxScore} pkt.`;

    $('summary-score-giant').innerHTML = `${summary.totalScore} <span>/ ${summary.maxScore} pkt</span>`;
    $('summary-basic-score').textContent = `${summary.basicScore} / ${summary.basicMaxScore} pkt`;
    $('summary-spec-score').textContent = `${summary.specialistScore} / ${summary.specialistMaxScore} pkt`;
    $('summary-counts').textContent = `${summary.correctCount} / ${summary.wrongCount} / ${summary.unansweredCount}`;
    $('summary-duration').textContent = formatClock(summary.durationSeconds);

    const mistakesCount = summary.wrongCount + summary.unansweredCount;
    $('review-filter-all').textContent = `Wszystkie pytania (${summary.questions.length})`;
    $('review-filter-mistakes').textContent = `Błędne i pominięte (${mistakesCount})`;
    $('review-filter-correct').textContent = `Poprawne (${summary.correctCount})`;

    document.querySelectorAll('.filter-tab').forEach((t) => {
      t.classList.toggle('active', t.dataset.filter === 'all');
    });

    renderReviewQuestionsList();
  }

  function renderReviewQuestionsList() {
    const listEl = $('summary-review-list');
    if (!state.lastSummary) return;

    const filtered = state.lastSummary.questions.filter((q) => {
      if (state.reviewFilter === 'mistakes') return !q.isCorrect;
      if (state.reviewFilter === 'correct') return q.isCorrect;
      return true;
    });

    if (filtered.length === 0) {
      listEl.innerHTML = `<div class="surface-panel" style="text-align:center; color:var(--text-muted);">Brak pytań w wybranym filtrze.</div>`;
      return;
    }

    listEl.innerHTML = filtered
      .map((q) => {
        const statusClass = q.isCorrect ? 'ok' : 'err';
        const statusText = q.isCorrect
          ? `+${q.earnedPoints} pkt · Poprawna`
          : (q.selectedAnswer ? `0 / ${q.points} pkt · Błędna` : `0 / ${q.points} pkt · Brak odpowiedzi`);

        const mediaToggle = q.mediaAvailable && q.mediaUrl
          ? `<button type="button" class="btn btn-secondary btn-toggle-review-media" data-url="${escapeHtml(q.mediaUrl)}" data-kind="${escapeHtml(q.mediaKind)}" style="padding:0.3rem 0.65rem; font-size:0.78rem; margin-top:0.65rem;">Pokaż załącznik (${q.mediaKind === 'VIDEO' ? 'wideo' : 'zdjęcie'})</button>
             <div class="review-media-drawer hidden"></div>`
          : '';

        return `
          <article class="review-item">
            <div class="review-item-header">
              <span class="q-number-tag mono-num">Pytanie ${q.index + 1}/32 · ${q.scope} · Nr ${escapeHtml(q.questionNumber)}</span>
              <span class="review-status-pill ${statusClass} mono-num">${statusText}</span>
            </div>
            <h3 style="margin:0; font-size:1rem; font-weight:700;">${escapeHtml(q.text)}</h3>
            <div class="review-answers-compare">
              <span><strong>Twoja odpowiedź:</strong> ${escapeHtml(formatAnswerLabel(q.selectedAnswer, q))}</span>
              <span style="color:var(--emerald-600);"><strong>Prawidłowa odpowiedź:</strong> ${escapeHtml(formatAnswerLabel(q.correctAnswer, q))}</span>
            </div>
            ${mediaToggle}
          </article>
        `;
      })
      .join('');

    bindMediaDrawerButtons(listEl);
  }

  function bindMediaDrawerButtons(containerEl) {
    containerEl.querySelectorAll('.btn-toggle-review-media').forEach((btn) => {
      btn.addEventListener('click', () => {
        const drawer = btn.nextElementSibling;
        if (!drawer) return;
        const isHidden = drawer.classList.contains('hidden');
        if (isHidden) {
          const url = btn.dataset.url;
          const kind = btn.dataset.kind;
          drawer.innerHTML = kind === 'VIDEO'
            ? `<video src="${escapeHtml(url)}" controls playsinline preload="metadata"></video>`
            : `<img src="${escapeHtml(url)}" alt="Załącznik do pytania" loading="lazy" />`;
          drawer.classList.remove('hidden');
          btn.textContent = 'Ukryj załącznik';
        } else {
          drawer.innerHTML = '';
          drawer.classList.add('hidden');
          btn.textContent = `Pokaż załącznik (${btn.dataset.kind === 'VIDEO' ? 'wideo' : 'zdjęcie'})`;
        }
      });
    });
  }

  // =========================================================================
  // Study / Browse Questions Mode
  // =========================================================================
  async function loadStudyQuestions() {
    const listEl = $('study-questions-list');
    const category = $('study-category-select').value || 'B';
    const scope = $('study-scope-select').value || '';
    const points = $('study-points-select').value || '';
    const search = $('study-search-input').value.trim();

    const params = new URLSearchParams({
      category,
      page: String(state.studyPage),
      pageSize: String(state.studyPageSize)
    });
    if (scope) params.set('scope', scope);
    if (points) params.set('points', points);
    if (search) params.set('search', search);

    try {
      const resp = await fetch(`/api/questions?${params.toString()}`);
      if (!resp.ok) return;
      const data = await resp.json();
      state.studyTotalCount = data.totalCount;

      const totalPages = Math.max(1, Math.ceil(data.totalCount / data.pageSize));
      $('study-pagination-info').textContent =
        `Strona ${data.page} z ${totalPages} · Łącznie ${data.totalCount.toLocaleString('pl-PL')} pytań`;
      $('btn-study-prev').disabled = data.page <= 1;
      $('btn-study-next').disabled = data.page >= totalPages;

      if (data.items.length === 0) {
        listEl.innerHTML = `<div class="surface-panel" style="text-align:center; color:var(--text-muted);">Brak pytań spełniających kryteria wyszukiwania.</div>`;
        return;
      }

      listEl.innerHTML = data.items
        .map((q) => {
          const abcHtml = q.type === 'ABC'
            ? `<ul style="margin:0.55rem 0 0; padding-left:1.2rem; font-size:0.88rem; color:var(--text-secondary);">
                 <li><strong>A:</strong> ${escapeHtml(q.answerA)}</li>
                 <li><strong>B:</strong> ${escapeHtml(q.answerB)}</li>
                 <li><strong>C:</strong> ${escapeHtml(q.answerC)}</li>
               </ul>`
            : '';

          const mediaBtn = q.mediaAvailable && q.mediaUrl
            ? `<button type="button" class="btn btn-secondary btn-toggle-review-media" data-url="${escapeHtml(q.mediaUrl)}" data-kind="${escapeHtml(q.mediaKind)}" style="padding:0.3rem 0.65rem; font-size:0.78rem;">Pokaż ${q.mediaKind === 'VIDEO' ? 'film' : 'zdjęcie'}</button>`
            : '';

          return `
            <article class="review-item">
              <div class="review-item-header">
                <span class="q-number-tag mono-num">Nr ${escapeHtml(q.questionNumber)} · ${q.scope} · Kat. ${escapeHtml(q.categories.join(', '))}</span>
                <span class="points-badge pts-${q.points} mono-num">${q.points} pkt</span>
              </div>
              <h3 style="margin:0; font-size:1rem; font-weight:700;">${escapeHtml(q.text)}</h3>
              ${abcHtml}
              <div style="display:flex; align-items:center; gap:0.65rem; flex-wrap:wrap; margin-top:0.75rem;">
                <button type="button" class="btn btn-secondary btn-reveal-study-ans" data-ans="${escapeHtml(formatAnswerLabel(q.correctAnswer, q))}" style="padding:0.3rem 0.7rem; font-size:0.78rem;">
                  Pokaż poprawną odpowiedź
                </button>
                <span class="study-ans-revealed hidden" style="font-size:0.86rem; font-weight:700; color:var(--emerald-600);"></span>
                ${mediaBtn}
              </div>
              <div class="review-media-drawer hidden"></div>
            </article>
          `;
        })
        .join('');

      listEl.querySelectorAll('.btn-reveal-study-ans').forEach((btn) => {
        btn.addEventListener('click', () => {
          const span = btn.nextElementSibling;
          if (!span) return;
          span.textContent = `Prawidłowa odpowiedź: ${btn.dataset.ans}`;
          span.classList.remove('hidden');
          btn.classList.add('hidden');
        });
      });

      listEl.querySelectorAll('.btn-toggle-review-media').forEach((btn) => {
        btn.addEventListener('click', () => {
          const article = btn.closest('.review-item');
          const drawer = article?.querySelector('.review-media-drawer');
          if (!drawer) return;
          if (drawer.classList.contains('hidden')) {
            const url = btn.dataset.url;
            const kind = btn.dataset.kind;
            drawer.innerHTML = kind === 'VIDEO'
              ? `<video src="${escapeHtml(url)}" controls playsinline preload="metadata"></video>`
              : `<img src="${escapeHtml(url)}" alt="Załącznik" loading="lazy" />`;
            drawer.classList.remove('hidden');
            btn.textContent = 'Ukryj załącznik';
          } else {
            drawer.innerHTML = '';
            drawer.classList.add('hidden');
            btn.textContent = `Pokaż ${btn.dataset.kind === 'VIDEO' ? 'film' : 'zdjęcie'}`;
          }
        });
      });
    } catch (err) {
      console.error('Błąd pobierania pytań:', err);
    }
  }

  // =========================================================================
  // Local Exam History (Browser localStorage)
  // =========================================================================
  function loadLocalHistory() {
    try {
      const raw = window.localStorage.getItem(HISTORY_STORAGE_KEY);
      return raw ? JSON.parse(raw) : [];
    } catch {
      return [];
    }
  }

  function saveExamToLocalHistory(summary) {
    try {
      const list = loadLocalHistory();
      list.unshift({
        examId: summary.examId,
        category: summary.category,
        passed: summary.passed,
        totalScore: summary.totalScore,
        maxScore: summary.maxScore,
        basicScore: summary.basicScore,
        specialistScore: summary.specialistScore,
        durationSeconds: summary.durationSeconds,
        completedAtUtc: summary.completedAtUtc
      });
      window.localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(list.slice(0, 50)));
    } catch {
      // ignore storage quota errors
    }
  }

  function renderHistoryView() {
    const container = $('history-content');
    const history = loadLocalHistory();

    if (history.length === 0) {
      container.innerHTML = `
        <div class="surface-panel" style="text-align:center;">
          <p style="margin:0 0 1rem; color:var(--text-secondary);">Nie ukończyłeś jeszcze żadnego egzaminu na tym urządzeniu.</p>
          <button type="button" class="btn btn-primary" id="btn-history-start-first">Rozpocznij pierwszy egzamin</button>
        </div>
      `;
      $('btn-history-start-first')?.addEventListener('click', () => switchView('home'));
      return;
    }

    const passedCount = history.filter((h) => h.passed).length;
    const passRate = Math.round((passedCount / history.length) * 100);
    const avgScore = (history.reduce((acc, h) => acc + h.totalScore, 0) / history.length).toFixed(1);

    const rows = history
      .map((h) => {
        const dt = new Date(h.completedAtUtc).toLocaleString('pl-PL');
        const pillClass = h.passed ? 'ok' : 'err';
        const pillText = h.passed ? 'POZYTYWNY' : 'NEGATYWNY';
        return `
          <div class="review-item" style="display:flex; justify-content:space-between; align-items:center; gap:1rem; flex-wrap:wrap;">
            <div>
              <span class="review-status-pill ${pillClass}">${pillText}</span>
              <strong style="margin-left:0.5rem;">Kat. ${escapeHtml(h.category)}</strong>
              <span class="q-number-tag" style="margin-left:0.6rem;">${escapeHtml(dt)}</span>
            </div>
            <div class="mono-num" style="font-weight:800; font-size:1.05rem;">
              ${h.totalScore} / ${h.maxScore} pkt
              <span style="font-weight:500; font-size:0.82rem; color:var(--text-muted); margin-left:0.5rem;">(${formatClock(h.durationSeconds)})</span>
            </div>
          </div>
        `;
      })
      .join('');

    container.innerHTML = `
      <div class="summary-metrics-row">
        <div class="metric-tile">
          <div class="metric-tile-label">Ukończone egzaminy</div>
          <div class="metric-tile-val mono-num">${history.length}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-tile-label">Zdawalność</div>
          <div class="metric-tile-val mono-num">${passRate}% (${passedCount}/${history.length})</div>
        </div>
        <div class="metric-tile">
          <div class="metric-tile-label">Średni wynik</div>
          <div class="metric-tile-val mono-num">${avgScore} / 74 pkt</div>
        </div>
      </div>
      <div class="review-list">${rows}</div>
    `;
  }

  // =========================================================================
  // Keyboard Shortcuts inside Active Exam
  // =========================================================================
  function handleExamKeydown(e) {
    if (state.currentView !== 'exam' || !state.exam || state.exam.isCompleted) return;
    if (e.target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(e.target.tagName)) return;

    const q = state.exam.currentQuestion;
    if (!q) return;

    const key = e.key.toUpperCase();

    if (e.code === 'Space') {
      const overlayBtn = $('btn-start-video-playback');
      if (overlayBtn && !overlayBtn.classList.contains('hidden')) {
        e.preventDefault();
        overlayBtn.click();
      }
      return;
    }

    if (q.type === 'YES_NO') {
      if (key === 'T' || key === '1') {
        e.preventDefault();
        selectAnswerOption('T');
      } else if (key === 'N' || key === '2') {
        e.preventDefault();
        selectAnswerOption('N');
      }
    } else if (q.type === 'ABC') {
      if (key === 'A' || key === '1') {
        e.preventDefault();
        selectAnswerOption('A');
      } else if (key === 'B' || key === '2') {
        e.preventDefault();
        selectAnswerOption('B');
      } else if (key === 'C' || key === '3') {
        e.preventDefault();
        selectAnswerOption('C');
      }
    }

    if (e.key === 'Enter' && state.selectedAnswer) {
      e.preventDefault();
      submitCurrentQuestion(false);
    }
  }

  // =========================================================================
  // Event Listeners Initialization
  // =========================================================================
  function init() {
    $('nav-brand-home').addEventListener('click', () => switchView('home'));
    document.querySelectorAll('.nav-btn').forEach((btn) => {
      btn.addEventListener('click', () => switchView(btn.dataset.view));
    });

    $('btn-start-exam').addEventListener('click', startOfficialExam);
    $('btn-goto-study').addEventListener('click', () => switchView('study'));
    $('btn-open-data-panel').addEventListener('click', () => switchView('data'));

    $('btn-next-question').addEventListener('click', handleNextQuestionClick);
    $('btn-finish-exam-early').addEventListener('click', async () => {
      if (!state.exam) return;
      const confirmEnd = window.confirm(
        'Czy na pewno chcesz zakończyć egzamin przed czasem? Wszystkie pozostałe pytania zostaną ocenione na 0 punktów.'
      );
      if (!confirmEnd) return;
      stopExamLoop();
      const resp = await fetch(`/api/exams/${state.exam.examId}/finish`, { method: 'POST' });
      if (resp.ok) {
        applyExamStateFromServer(await resp.json(), false);
      }
    });

    $('btn-restart-exam').addEventListener('click', startOfficialExam);
    $('btn-back-home').addEventListener('click', () => switchView('home'));

    document.querySelectorAll('.filter-tab').forEach((tab) => {
      tab.addEventListener('click', () => {
        state.reviewFilter = tab.dataset.filter;
        document.querySelectorAll('.filter-tab').forEach((t) => t.classList.toggle('active', t === tab));
        renderReviewQuestionsList();
      });
    });

    // Study search events
    let searchDebounce = null;
    $('study-search-form').addEventListener('submit', (e) => {
      e.preventDefault();
      state.studyPage = 1;
      loadStudyQuestions();
    });
    $('study-search-input').addEventListener('input', () => {
      window.clearTimeout(searchDebounce);
      searchDebounce = window.setTimeout(() => {
        state.studyPage = 1;
        loadStudyQuestions();
      }, 280);
    });
    ['study-category-select', 'study-scope-select', 'study-points-select'].forEach((id) => {
      $(id).addEventListener('change', () => {
        state.studyPage = 1;
        loadStudyQuestions();
      });
    });
    $('btn-study-prev').addEventListener('click', () => {
      if (state.studyPage > 1) {
        state.studyPage--;
        loadStudyQuestions();
      }
    });
    $('btn-study-next').addEventListener('click', () => {
      state.studyPage++;
      loadStudyQuestions();
    });

    // History clear
    $('btn-clear-history').addEventListener('click', () => {
      if (window.confirm('Czy na pewno chcesz wyczyścić lokalną historię wyników?')) {
        window.localStorage.removeItem(HISTORY_STORAGE_KEY);
        renderHistoryView();
      }
    });

    // Data sync buttons
    $('btn-sync-questions').addEventListener('click', () => triggerGovSync(false));
    $('btn-sync-all-media').addEventListener('click', () => triggerGovSync(true));
    $('btn-rescan-local').addEventListener('click', triggerLocalRescan);

    window.addEventListener('keydown', handleExamKeydown);

    refreshStatus();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
