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

function songCard(item) {
    const reason = item.submitReason
        ? `<p>理由: ${escapeHtml(item.submitReason)}</p>`
        : '<p>理由: (未入力)</p>';

    return `
    <article class="admin-item">
      <p><strong>${escapeHtml(item.song)}</strong> <span class="effect effect-${(item.effect || '').toLowerCase()}">${escapeHtml(item.effect)}</span></p>
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
      <p>event=${item.weepaEventId} / submit=${item.submitId} / @${escapeHtml(item.xId)}</p>
      <p>投稿者: ${escapeHtml(item.submitName)}</p>
      <p>スコア: <strong>${Number(item.score).toLocaleString('ja-JP')}</strong> / EX: ${item.exScore == null ? '-' : Number(item.exScore).toLocaleString('ja-JP')}</p>
      <p class="period">投稿日時: ${escapeHtml(formatDate(item.submitDate))}</p>
      ${imageLink}
      <div class="admin-actions"
           data-event-id="${item.weepaEventId}"
           data-x-id="${escapeHtml(item.xId)}"
           data-submit-id="${item.submitId}"
           data-score="${item.score}"
           data-ex-score="${item.exScore == null ? '' : item.exScore}">
        <button type="button" class="action-approve">承認</button>
        <button type="button" class="action-approve-edit">修正して承認</button>
        <button type="button" class="action-reject">却下</button>
      </div>
      <div class="admin-edit-panel" hidden>
        <div class="admin-edit-fields">
          <label>スコア
            <input type="number" class="edit-score" value="${item.score}" min="0" max="10000000">
          </label>
          <label>EXスコア
            <input type="number" class="edit-ex-score" value="${item.exScore == null ? '' : item.exScore}" min="0" placeholder="(未入力可)">
          </label>
        </div>
        <p class="hint">いずれか / 両方を編集して「この値で承認」してください。空欄のまま承認すれば元の値のままです。</p>
        <div class="admin-actions">
          <button type="button" class="action-edit-confirm">この値で承認</button>
          <button type="button" class="action-edit-cancel">キャンセル</button>
        </div>
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

    const isApprove = target.classList.contains('action-approve');
    if (!isApprove && !confirm(`選曲提案 ${songId} を却下します。よろしいですか？`)) {
        return;
    }

    target.disabled = true;
    try {
        if (isApprove) {
            await adminFetch(`/api/admin/songs/${songId}/approve`, { method: 'POST', body: '{}' });
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

function toggleEditPanel(card, showEdit) {
    const actions = card.querySelector('.admin-actions');
    const panel = card.querySelector('.admin-edit-panel');
    if (!actions || !panel) return;
    actions.hidden = showEdit;
    panel.hidden = !showEdit;
}

function readEditValues(panel) {
    const scoreRaw = panel.querySelector('.edit-score').value.trim();
    const exRaw = panel.querySelector('.edit-ex-score').value.trim();
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

    if (target.classList.contains('action-approve-edit')) {
        toggleEditPanel(card, true);
        return;
    }
    if (target.classList.contains('action-edit-cancel')) {
        toggleEditPanel(card, false);
        return;
    }

    target.disabled = true;
    try {
        if (target.classList.contains('action-approve')) {
            await adminFetch(`${scorePath}/approve`, { method: 'POST', body: '{}' });
            setInfo(`スコア event=${eventId} submit=${submitId} を承認しました。`, 'ok');
        } else if (target.classList.contains('action-edit-confirm')) {
            const panel = card.querySelector('.admin-edit-panel');
            const payload = readEditValues(panel);
            await adminFetch(`${scorePath}/approve`, {
                method: 'POST',
                body: JSON.stringify(payload),
            });
            const summary = Object.keys(payload).length === 0
                ? '修正なしで承認'
                : `修正値 ${JSON.stringify(payload)} で承認`;
            setInfo(`スコア event=${eventId} submit=${submitId} を${summary}しました。`, 'ok');
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

loadBtn.addEventListener('click', loadPending);

songsEl.addEventListener('click', async (ev) => {
    const target = ev.target;
    if (!(target instanceof HTMLButtonElement)) return;
    if (!target.classList.contains('action-approve') && !target.classList.contains('action-reject')) return;
    await onSongAction(target);
});

scoresEl.addEventListener('click', async (ev) => {
    const target = ev.target;
    if (!(target instanceof HTMLButtonElement)) return;
    const classes = ['action-approve', 'action-approve-edit', 'action-reject', 'action-edit-confirm', 'action-edit-cancel'];
    if (!classes.some((c) => target.classList.contains(c))) return;
    await onScoreAction(target);
});

loadPending();
