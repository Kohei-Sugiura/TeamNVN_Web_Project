ensureLoggedIn();
renderAdminNav('#admin-nav');

const loadBtn = document.getElementById('load-btn');
const resultEl = document.getElementById('admin-result');
const songsEl = document.getElementById('pending-songs');
const scoresEl = document.getElementById('pending-scores');

function formatDate(iso) {
    const d = new Date(iso);
    return d.toLocaleString('ja-JP', {
        year: 'numeric', month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit',
    });
}

const EFFECT_OPTIONS = ['NOV', 'ADV', 'EXH', 'MXM', 'INF', 'GRV', 'HVN', 'VVD', 'XCD', 'ULT'];

function effectSelectOptions(current) {
    return EFFECT_OPTIONS
        .map((e) => `<option value="${e}"${e === current ? ' selected' : ''}>${e}</option>`)
        .join('');
}

function songCard(item) {
    const reason = item.submitReason
        ? `<p>理由: ${escapeHtml(item.submitReason)}</p>`
        : '<p>理由: (未入力)</p>';

    return `
    <article class="admin-item" data-song-id="${item.weepaSongId}">
      <div class="admin-edit-fields">
        <label>曲名
          <input type="text" class="edit-song" value="${escapeHtml(item.song)}" maxlength="200">
        </label>
        <label>エフェクト
          <select class="edit-effect">${effectSelectOptions(item.effect)}</select>
        </label>
      </div>
      <p>提案者: ${escapeHtml(item.submitName)} (@${escapeHtml(item.xId)})</p>
      ${reason}
      <p class="period">投稿日時: ${escapeHtml(formatDate(item.submitDate))}</p>
      <div class="admin-actions">
        <button type="button" class="action-approve" data-song-id="${item.weepaSongId}">承認</button>
        <button type="button" class="action-reject" data-song-id="${item.weepaSongId}">却下</button>
      </div>
    </article>
  `;
}

function scoreCard(item) {
    const imageLink = item.imagePath
        ? `<p><a href="${encodeURI(item.imagePath)}" target="_blank" rel="noopener noreferrer">投稿画像を表示</a></p>`
        : '<p>投稿画像: (なし)</p>';

    const key = `${item.weepaEventId}:${item.xId}:${item.submitId}`;

    return `
    <article class="admin-item" data-key="${key}">
      <p><strong>${escapeHtml(item.song)}</strong> <span class="effect effect-${(item.effect || '').toLowerCase()}">${escapeHtml(item.effect)}</span></p>
      <p>投稿者: ${escapeHtml(item.submitName)} (@${escapeHtml(item.xId)})</p>
      <div class="admin-edit-fields">
        <label>スコア
          <input type="number" class="edit-score" value="${item.score}" min="0" max="10000000">
        </label>
        <label>EXスコア
          <input type="number" class="edit-ex-score" value="${item.exScore == null ? '' : item.exScore}" min="0" placeholder="(未入力可)">
        </label>
      </div>
      ${imageLink}
      <p class="period">投稿日時: ${escapeHtml(formatDate(item.submitDate))}</p>
      <div class="admin-actions"
           data-event-id="${item.weepaEventId}"
           data-x-id="${escapeHtml(item.xId)}"
           data-submit-id="${item.submitId}">
        <button type="button" class="action-approve">承認</button>
        <button type="button" class="action-reject">却下</button>
      </div>
    </article>
  `;
}

function setInfo(message, type = 'info') {
    resultEl.textContent = message;
    resultEl.className = `result ${type}`;
}

async function loadPending() {
    songsEl.innerHTML = '<p class="loading">読み込み中...</p>';
    scoresEl.innerHTML = '<p class="loading">読み込み中...</p>';

    try {
        const [songs, scores] = await Promise.all([
            adminFetch('/api/admin/songs/pending'),
            adminFetch('/api/admin/scores/pending'),
        ]);

        songsEl.innerHTML = songs.length
            ? songs.map(songCard).join('')
            : '<p class="empty">未承認の選曲提案はありません。</p>';

        scoresEl.innerHTML = scores.length
            ? scores.map(scoreCard).join('')
            : '<p class="empty">未承認のスコア投稿はありません。</p>';

        setInfo('未承認一覧を更新しました。', 'ok');
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        songsEl.innerHTML = '<p class="error">取得に失敗しました。</p>';
        scoresEl.innerHTML = '<p class="error">取得に失敗しました。</p>';
        setInfo(err.message, 'ng');
    }
}

function parseOptionalInt(text) {
    const trimmed = text.trim();
    if (trimmed === '') return { ok: true, value: null };
    if (!/^\d+$/.test(trimmed)) return { ok: false };
    return { ok: true, value: Number(trimmed) };
}

async function onSongAction(target) {
    const songId = target.dataset.songId;
    if (!songId) return;
    const card = target.closest('.admin-item');
    if (!card) return;

    const isApprove = target.classList.contains('action-approve');
    if (!isApprove && !confirm(`選曲提案 ${songId} を却下します。よろしいですか？`)) {
        return;
    }

    target.disabled = true;
    try {
        if (isApprove) {
            const songInput = card.querySelector('.edit-song');
            const effectInput = card.querySelector('.edit-effect');
            const payload = {
                song: songInput?.value.trim() || null,
                effect: effectInput?.value || null,
            };
            await adminFetch(`/api/admin/songs/${songId}/approve`, {
                method: 'POST',
                body: JSON.stringify(payload),
            });
            setInfo(`楽曲提案 ${songId} を承認しました。`, 'ok');
        } else {
            await adminFetch(`/api/admin/songs/${songId}/reject`, { method: 'POST', body: '{}' });
            setInfo(`楽曲提案 ${songId} を却下しました。`, 'info');
        }
        await loadPending();
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        setInfo(err.message, 'ng');
    } finally {
        target.disabled = false;
    }
}

function readScoreEditValues(card) {
    const scoreRaw = card.querySelector('.edit-score').value.trim();
    const exRaw = card.querySelector('.edit-ex-score').value.trim();
    const parsedScore = parseOptionalInt(scoreRaw);
    const parsedEx = parseOptionalInt(exRaw);
    if (!parsedScore.ok || !parsedEx.ok) {
        throw new Error('スコア/EXスコアは半角整数で入力してください');
    }
    const payload = {};
    if (parsedScore.value !== null) payload.score = parsedScore.value;
    if (parsedEx.value !== null) payload.exScore = parsedEx.value;
    return payload;
}

async function onScoreAction(target) {
    const card = target.closest('.admin-item');
    if (!card) return;
    const actions = card.querySelector('.admin-actions');
    if (!actions) return;
    const { eventId, xId, submitId } = actions.dataset;
    if (!eventId || !xId || !submitId) return;

    const scorePath = `/api/admin/scores/${eventId}/${encodeURIComponent(xId)}/${submitId}`;

    target.disabled = true;
    try {
        if (target.classList.contains('action-approve')) {
            const payload = readScoreEditValues(card);
            await adminFetch(`${scorePath}/approve`, {
                method: 'POST',
                body: JSON.stringify(payload),
            });
            setInfo(`スコア event=${eventId} submit=${submitId} を承認しました。`, 'ok');
        } else if (target.classList.contains('action-reject')) {
            if (!confirm(`スコア event=${eventId} submit=${submitId} を却下します。よろしいですか？`)) {
                return;
            }
            await adminFetch(`${scorePath}/reject`, { method: 'POST', body: '{}' });
            setInfo(`スコア event=${eventId} submit=${submitId} を却下しました。`, 'info');
        } else {
            return;
        }

        await loadPending();
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        setInfo(err.message, 'ng');
    } finally {
        target.disabled = false;
    }
}

// ============================================
// 開催スケジュールタブ
// ============================================

const scheduleCurrentEl = document.getElementById('schedule-current');
const scheduleQueueEl = document.getElementById('schedule-queue');
const publishListEl = document.getElementById('publish-list');
const scheduleConfigForm = document.getElementById('schedule-config-form');
const scheduleConfigTzEl = document.getElementById('schedule-config-tz');
const cfgDayEl = document.getElementById('cfg-day-of-week');
const cfgHourEl = document.getElementById('cfg-hour');
const cfgMinuteEl = document.getElementById('cfg-minute');
const cfgDurationEl = document.getElementById('cfg-duration');
const reorderSaveBtn = document.getElementById('reorder-save-btn');

const DAY_OF_WEEK_LABEL = {
    Sunday: '日曜', Monday: '月曜', Tuesday: '火曜', Wednesday: '水曜',
    Thursday: '木曜', Friday: '金曜', Saturday: '土曜',
};

function effectBadgeHtml(effect) {
    if (!effect) return '';
    return `<span class="effect effect-${String(effect).toLowerCase()}">${escapeHtml(effect)}</span>`;
}

function ymd(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    return `${d.getFullYear()}/${String(d.getMonth() + 1).padStart(2, '0')}/${String(d.getDate()).padStart(2, '0')}`;
}

function renderCurrentEvent(current) {
    if (!current) {
        scheduleCurrentEl.innerHTML = '<p class="empty">現在開催中のイベントはありません。</p>';
        return;
    }
    scheduleCurrentEl.innerHTML = `
      <div class="event-title">
        <span class="song">${escapeHtml(current.song)}</span>
        ${effectBadgeHtml(current.effect)}
      </div>
      <p>選曲者: ${escapeHtml(current.submitName)} (@${escapeHtml(current.xId)})</p>
      <div class="admin-edit-fields">
        <label>開始日時
          <input type="datetime-local" id="current-start" value="${isoToLocalInput(current.startDate)}">
        </label>
        <label>終了日時
          <input type="datetime-local" id="current-end" value="${isoToLocalInput(current.endDate)}">
        </label>
      </div>
      <div class="form-actions">
        <button type="button" id="current-save-btn" data-event-id="${current.weepaEventId}">期間を保存</button>
      </div>
    `;
    document.getElementById('current-save-btn').addEventListener('click', saveCurrentEvent);
}

async function saveCurrentEvent(ev) {
    const id = ev.currentTarget.dataset.eventId;
    const start = localInputToIso(document.getElementById('current-start').value);
    const end = localInputToIso(document.getElementById('current-end').value);
    if (!start || !end) {
        setInfo('開始日時と終了日時を入力してください。', 'ng');
        return;
    }
    ev.currentTarget.disabled = true;
    try {
        await adminFetch(`/api/admin/db/events/${id}`, {
            method: 'PUT',
            body: JSON.stringify({ startDate: start, endDate: end }),
        });
        setInfo(`現在開催中イベント (id=${id}) の期間を更新しました。`, 'ok');
        await loadSchedule();
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        setInfo(err.message, 'ng');
    }
}

function renderQueue(queue) {
    if (!queue.length) {
        scheduleQueueEl.innerHTML = '<p class="empty">未開催のイベントはありません。</p>';
        return;
    }
    const rows = queue.map((q, i) => `
      <tr data-event-id="${q.weepaEventId}">
        <td class="rank">${i + 1}</td>
        <td>${escapeHtml(q.song)}</td>
        <td>${effectBadgeHtml(q.effect)}</td>
        <td>${escapeHtml(q.submitName)} (@${escapeHtml(q.xId)})</td>
        <td>${escapeHtml(ymd(q.startDate))}~${escapeHtml(ymd(q.endDate))}</td>
        <td class="actions">
          <button type="button" class="queue-up" data-event-id="${q.weepaEventId}">↑</button>
          <button type="button" class="queue-down" data-event-id="${q.weepaEventId}">↓</button>
        </td>
      </tr>
    `).join('');
    scheduleQueueEl.innerHTML = `
      <div class="db-table-wrap">
        <table class="ranking-table">
          <thead>
            <tr>
              <th>#</th><th>楽曲</th><th>譜面</th><th>選曲者名</th>
              <th>開催期間</th><th>並替</th>
            </tr>
          </thead>
          <tbody>${rows}</tbody>
        </table>
      </div>
    `;
}

function renderPublishList(past) {
    if (!past.length) {
        publishListEl.innerHTML = '<p class="empty">過去のイベントはまだありません。</p>';
        return;
    }
    const rows = past.map((p) => {
        const published = !!p.talliedAt;
        const stateLabel = published
            ? `<span class="status-active">公開中</span>`
            : `<span class="status-tallying">非公開</span>`;
        return `
          <tr>
            <td>${escapeHtml(p.song)}</td>
            <td>${effectBadgeHtml(p.effect)}</td>
            <td>${escapeHtml(p.submitName)}</td>
            <td>${escapeHtml(ymd(p.startDate))}~${escapeHtml(ymd(p.endDate))}</td>
            <td class="status-cell">
              <label class="toggle-switch" title="${published ? '公開中 → クリックで非公開' : '非公開 → クリックで公開'}">
                <input type="checkbox" class="publish-toggle" data-event-id="${p.weepaEventId}"${published ? ' checked' : ''}>
                <span class="toggle-slider" aria-hidden="true"></span>
              </label>
              <span class="toggle-label">${stateLabel}</span>
            </td>
          </tr>
        `;
    }).join('');
    publishListEl.innerHTML = `
      <div class="db-table-wrap">
        <table class="ranking-table">
          <thead>
            <tr><th>楽曲</th><th>譜面</th><th>選曲者</th><th>開催期間</th><th class="status-cell">公開</th></tr>
          </thead>
          <tbody>${rows}</tbody>
        </table>
      </div>
    `;
}

function fillScheduleConfigForm(cfg) {
    cfgDayEl.value = String(
        Object.entries(DAY_OF_WEEK_LABEL).findIndex(([k]) => k === cfg.dayOfWeek)
    );
    // findIndex の代わりに英語名→数値マップで安定化
    const dowIndex = { Sunday: 0, Monday: 1, Tuesday: 2, Wednesday: 3, Thursday: 4, Friday: 5, Saturday: 6 };
    cfgDayEl.value = String(dowIndex[cfg.dayOfWeek] ?? 3);
    cfgHourEl.value = String(cfg.hour);
    cfgMinuteEl.value = String(cfg.minute);
    cfgDurationEl.value = String(cfg.durationHours);
    scheduleConfigTzEl.textContent = `タイムゾーン: ${cfg.timezone}（変更は .env で対応）`;
}

async function loadSchedule() {
    scheduleCurrentEl.innerHTML = '<p class="loading">読み込み中...</p>';
    scheduleQueueEl.innerHTML = '<p class="loading">読み込み中...</p>';
    publishListEl.innerHTML = '<p class="loading">読み込み中...</p>';
    try {
        const data = await adminFetch('/api/admin/schedule');
        renderCurrentEvent(data.current);
        renderQueue(data.queue || []);
        renderPublishList(data.past || []);
        fillScheduleConfigForm(data.config);
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        const msg = `<p class="error">取得に失敗しました: ${escapeHtml(err.message)}</p>`;
        scheduleCurrentEl.innerHTML = msg;
        scheduleQueueEl.innerHTML = '';
        publishListEl.innerHTML = '';
    }
}

scheduleConfigForm.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const payload = {
        dayOfWeek: Number(cfgDayEl.value),
        hour: Number(cfgHourEl.value),
        minute: Number(cfgMinuteEl.value),
        durationHours: Number(cfgDurationEl.value),
    };
    if (!confirm('スケジューリング設定を保存します。未来イベントの start/end が再計算されますが、よろしいですか？')) return;
    try {
        const result = await adminFetch('/api/admin/schedule/config', {
            method: 'PUT',
            body: JSON.stringify(payload),
        });
        setInfo(`スケジュール設定を保存しました (${result.recomputed} 件のイベントを再計算)。`, 'ok');
        await loadSchedule();
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        setInfo(err.message, 'ng');
    }
});

function moveQueueRow(eventId, dir) {
    const tr = scheduleQueueEl.querySelector(`tr[data-event-id="${eventId}"]`);
    if (!tr) return;
    const sibling = dir === -1 ? tr.previousElementSibling : tr.nextElementSibling;
    if (!sibling) return;
    if (dir === -1) tr.parentNode.insertBefore(tr, sibling);
    else tr.parentNode.insertBefore(sibling, tr);
    scheduleQueueEl.querySelectorAll('tbody tr').forEach((row, i) => {
        const rank = row.querySelector('td.rank');
        if (rank) rank.textContent = String(i + 1);
    });
}

scheduleQueueEl.addEventListener('click', (ev) => {
    const t = ev.target;
    if (!(t instanceof HTMLButtonElement)) return;
    const id = Number(t.dataset.eventId);
    if (!id) return;
    if (t.classList.contains('queue-up')) moveQueueRow(id, -1);
    else if (t.classList.contains('queue-down')) moveQueueRow(id, 1);
});

reorderSaveBtn.addEventListener('click', async () => {
    const order = Array.from(scheduleQueueEl.querySelectorAll('tbody tr'))
        .map((tr) => Number(tr.dataset.eventId));
    if (!order.length) return;
    reorderSaveBtn.disabled = true;
    try {
        await adminFetch('/api/admin/schedule/reorder', {
            method: 'PUT',
            body: JSON.stringify({ order }),
        });
        setInfo('開催キューの順番を保存しました。', 'ok');
        await loadSchedule();
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        setInfo(err.message, 'ng');
    } finally {
        reorderSaveBtn.disabled = false;
    }
});

publishListEl.addEventListener('change', async (ev) => {
    const t = ev.target;
    if (!(t instanceof HTMLInputElement) || !t.classList.contains('publish-toggle')) return;
    const id = t.dataset.eventId;
    if (!id) return;
    const wantPublish = t.checked;
    const verb = wantPublish ? '公開' : '非公開化';
    if (!confirm(`event_id=${id} を${verb}します。よろしいですか？`)) {
        t.checked = !wantPublish; // revert UI
        return;
    }
    t.disabled = true;
    try {
        await adminFetch(`/api/admin/events/${id}/${wantPublish ? 'publish' : 'unpublish'}`, {
            method: 'POST',
            body: '{}',
        });
        setInfo(`event_id=${id} を${verb}しました。`, 'ok');
        await loadSchedule();
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        t.checked = !wantPublish; // revert UI on failure
        setInfo(err.message, 'ng');
    } finally {
        t.disabled = false;
    }
});


// ============================================
// タブ切替
// ============================================

const tabsEl = document.getElementById('admin-tabs');
const panels = document.querySelectorAll('.admin-tab-panel');

function selectTab(tab) {
    tabsEl.querySelectorAll('button').forEach((b) => {
        b.classList.toggle('active', b.dataset.tab === tab);
    });
    panels.forEach((p) => {
        p.hidden = p.dataset.tab !== tab;
    });
    if (tab === 'schedule' || tab === 'publish') loadSchedule();
    if (history.replaceState) {
        const url = new URL(window.location.href);
        url.searchParams.set('tab', tab);
        history.replaceState(null, '', url);
    }
}

tabsEl.addEventListener('click', (ev) => {
    const t = ev.target;
    if (!(t instanceof HTMLButtonElement)) return;
    selectTab(t.dataset.tab);
});

// ============================================
// イベントリスナー / 初期化
// ============================================

loadBtn.addEventListener('click', () => {
    loadPending();
    const sched = document.querySelector('.admin-tab-panel[data-tab="schedule"]:not([hidden])');
    const pub = document.querySelector('.admin-tab-panel[data-tab="publish"]:not([hidden])');
    if (sched || pub) loadSchedule();
});

songsEl.addEventListener('click', async (ev) => {
    const target = ev.target;
    if (!(target instanceof HTMLButtonElement)) return;
    if (!target.classList.contains('action-approve') && !target.classList.contains('action-reject')) return;
    await onSongAction(target);
});

scoresEl.addEventListener('click', async (ev) => {
    const target = ev.target;
    if (!(target instanceof HTMLButtonElement)) return;
    if (!target.classList.contains('action-approve') && !target.classList.contains('action-reject')) return;
    await onScoreAction(target);
});

const initialTab = new URLSearchParams(window.location.search).get('tab') || 'songs';
selectTab(initialTab);
loadPending();
