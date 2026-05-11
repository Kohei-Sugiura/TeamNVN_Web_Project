ensureLoggedIn();
renderAdminNav('#admin-nav');

const MSC_SONG_MIN = 2;
const MSC_SONG_MAX = 4;
const MSC_EFFECTS = ['NOV', 'ADV', 'EXH', 'MXM', 'INF', 'GRV', 'HVN', 'VVD', 'XCD', 'NBL', 'ULT'];

const mscEventCreateForm = document.getElementById('msc-event-create-form');
const mscSongsListEl = document.getElementById('msc-songs-list');
const mscAddSongBtn = document.getElementById('msc-add-song-btn');
const mscCreateResult = document.getElementById('msc-create-result');
const mscEventsListEl = document.getElementById('msc-events-list');
const mscTabsEl = document.getElementById('msc-tabs');
const mscPanels = document.querySelectorAll('.msc-tab-panel');
const mscPendingScoresEl = document.getElementById('msc-pending-scores');
const mscPendingReloadBtn = document.getElementById('msc-pending-reload-btn');
const mscScoresResultEl = document.getElementById('msc-scores-result');
const mscPublishListEl = document.getElementById('msc-publish-list');
const mscPublishResultEl = document.getElementById('msc-publish-result');

function mscSetResult(text, kind) {
    if (!mscCreateResult) return;
    mscCreateResult.textContent = text;
    mscCreateResult.className = `result ${kind}`;
}

function mscYmdHm(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    const h = String(d.getHours()).padStart(2, '0');
    const mm = String(d.getMinutes()).padStart(2, '0');
    return `${y}/${m}/${day} ${h}:${mm}`;
}

function mscEffectSelectHtml(current) {
    return `<select name="effect" required>${MSC_EFFECTS
        .map((e) => `<option value="${e}"${e === current ? ' selected' : ''}>${e}</option>`)
        .join('')}</select>`;
}

function mscSongRowHtml(order, song = '', effect = '') {
    return `
      <div class="msc-song-row" data-order="${order}">
        <span class="msc-song-order-label">${order} 曲目</span>
        <input type="text" name="song" placeholder="楽曲名" maxlength="200" required value="${escapeHtml(song)}">
        ${mscEffectSelectHtml(effect)}
        <button type="button" class="msc-remove-song" aria-label="この曲を削除">×</button>
      </div>
    `;
}

function mscCurrentSongCount() {
    return mscSongsListEl.querySelectorAll('.msc-song-row').length;
}

function mscUpdateButtons() {
    const n = mscCurrentSongCount();
    if (mscAddSongBtn) mscAddSongBtn.disabled = n >= MSC_SONG_MAX;
    mscSongsListEl.querySelectorAll('.msc-remove-song').forEach((b) => {
        b.disabled = n <= MSC_SONG_MIN;
    });
}

function mscRenumberRows() {
    mscSongsListEl.querySelectorAll('.msc-song-row').forEach((row, idx) => {
        const order = idx + 1;
        row.dataset.order = String(order);
        const label = row.querySelector('.msc-song-order-label');
        if (label) label.textContent = `${order} 曲目`;
    });
}

function ensureMscSongsRows() {
    if (!mscSongsListEl) return;
    if (mscSongsListEl.children.length === 0) {
        mscSongsListEl.innerHTML = mscSongRowHtml(1) + mscSongRowHtml(2);
    }
    mscUpdateButtons();
}

mscAddSongBtn.addEventListener('click', () => {
    if (mscCurrentSongCount() >= MSC_SONG_MAX) return;
    const order = mscCurrentSongCount() + 1;
    mscSongsListEl.insertAdjacentHTML('beforeend', mscSongRowHtml(order));
    mscUpdateButtons();
});

mscSongsListEl.addEventListener('click', (ev) => {
    const t = ev.target;
    if (!(t instanceof HTMLButtonElement)) return;
    if (!t.classList.contains('msc-remove-song')) return;
    if (mscCurrentSongCount() <= MSC_SONG_MIN) return;
    const row = t.closest('.msc-song-row');
    if (row) row.remove();
    mscRenumberRows();
    mscUpdateButtons();
});

function mscCollectSongs() {
    return Array.from(mscSongsListEl.querySelectorAll('.msc-song-row')).map((row, idx) => ({
        songOrder: idx + 1,
        song: row.querySelector('input[name="song"]').value.trim(),
        effect: row.querySelector('select[name="effect"]').value,
    }));
}

mscEventCreateForm.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    mscSetResult('', '');

    const title = mscEventCreateForm.title.value.trim();
    const description = mscEventCreateForm.description.value.trim() || null;
    const startDate = localInputToIso(mscEventCreateForm.startDate.value);
    const endDate = localInputToIso(mscEventCreateForm.endDate.value);
    const songs = mscCollectSongs();

    if (!title) { mscSetResult('タイトルを入力してください', 'ng'); return; }
    if (!startDate || !endDate) { mscSetResult('開催期間を入力してください', 'ng'); return; }
    if (new Date(endDate) <= new Date(startDate)) {
        mscSetResult('開催終了は開始より後の日時にしてください', 'ng');
        return;
    }
    if (songs.length < MSC_SONG_MIN || songs.length > MSC_SONG_MAX) {
        mscSetResult(`曲は ${MSC_SONG_MIN}〜${MSC_SONG_MAX} 曲です`, 'ng');
        return;
    }
    if (songs.some((s) => !s.song)) {
        mscSetResult('楽曲名が未入力の行があります', 'ng');
        return;
    }

    const payload = { title, description, startDate, endDate, songs };
    try {
        const result = await adminFetch('/api/admin/msc/events', {
            method: 'POST',
            body: JSON.stringify(payload),
        });
        mscSetResult(`作成しました: msc_event_id=${result?.mscEventId ?? '?'}`, 'ok');
        mscEventCreateForm.reset();
        mscSongsListEl.innerHTML = '';
        ensureMscSongsRows();
        await loadMscEvents();
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        mscSetResult(err.message, 'ng');
    }
});

async function loadMscEvents() {
    if (!mscEventsListEl) return;
    mscEventsListEl.innerHTML = '<p class="loading">読み込み中...</p>';
    try {
        const events = await adminFetch('/api/admin/msc/events');
        renderMscEventsList(events ?? []);
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        // API 未実装中はエラーで OK。UI 雛形だけ表示する
        mscEventsListEl.innerHTML = `<p class="empty">取得に失敗しました (${escapeHtml(err.message)})。API 実装後に表示されます。</p>`;
    }
}

function mscEventStateBadge(ev) {
    const now = Date.now();
    const start = new Date(ev.startDate).getTime();
    const end = new Date(ev.endDate).getTime();
    if (now < start) return `<span class="status-tallying">開催前</span>`;
    if (now < end) return `<span class="status-active">開催中</span>`;
    if (ev.talliedAt) return `<span class="status-active">公開済</span>`;
    return `<span class="status-tallying">集計中 (未公開)</span>`;
}

function renderMscEventsList(events) {
    if (!events.length) {
        mscEventsListEl.innerHTML = '<p class="empty">MSC イベントはまだありません。</p>';
        return;
    }
    // 開始日 ASC (近いもの順)
    events = [...events].sort((a, b) => new Date(a.startDate) - new Date(b.startDate));
    const rows = events.map((e) => {
        const songLines = (e.songs ?? [])
            .map((s) => `${s.songOrder}. ${escapeHtml(s.song)} (${escapeHtml(s.effect)})`)
            .join('<br>');
        return `
          <tr>
            <td>${e.mscEventId}</td>
            <td>${escapeHtml(e.title)}</td>
            <td>${escapeHtml(mscYmdHm(e.startDate))} ~ ${escapeHtml(mscYmdHm(e.endDate))}</td>
            <td>${songLines || '(曲未設定)'}</td>
            <td>${mscEventStateBadge(e)}</td>
          </tr>
        `;
    }).join('');
    mscEventsListEl.innerHTML = `
      <div class="db-table-wrap">
        <table class="ranking-table">
          <thead><tr><th>id</th><th>タイトル</th><th>開催期間</th><th>課題曲</th><th>状態</th></tr></thead>
          <tbody>${rows}</tbody>
        </table>
      </div>
    `;
}

// ============================================
// スコア承認サブタブ
// ============================================

function trackLabel(playOrder) {
    if (playOrder === 4) return 'EX Track';
    const suf = { 1: 'st', 2: 'nd', 3: 'rd' };
    return `${playOrder}${suf[playOrder]} Track`;
}

function setScoresResult(text, kind) {
    if (!mscScoresResultEl) return;
    mscScoresResultEl.textContent = text;
    mscScoresResultEl.className = `result ${kind}`;
}

function renderPendingSubmissionCard(sub) {
    const playsByOrder = new Map(sub.plays.map((p) => [p.playOrder, p]));
    const imagesByOrder = new Map(sub.images.map((i) => [i.playOrder, i.imagePath]));
    const key = `${sub.mscEventId}:${encodeURIComponent(sub.xId)}:${sub.submitId}`;

    // 各 Track を info-row パターンで縦並び (ラベル "1st Track" シアン → 楽曲名 → スコア入力 → 画像リンク)
    const scoreInputs = [];
    for (let i = 1; i <= 4; i++) {
        const p = playsByOrder.get(i);
        if (!p) continue;
        const effectBadge = `<span class="effect effect-${(p.effect || '').toLowerCase()}">${escapeHtml(p.effect)}</span>`;
        const img = imagesByOrder.get(i);
        const imgLink = img
            ? `<p><a href="${encodeURI(img)}" target="_blank" rel="noopener noreferrer">画像を開く</a></p>`
            : '';
        scoreInputs.push(`
          <div class="msc-pending-track">
            <p class="info-row"><span class="label">${trackLabel(i)}</span>${escapeHtml(p.song)} ${effectBadge}</p>
            <input type="number" class="msc-pending-score" data-play-order="${i}"
                   value="${p.score}" min="0" max="10000000" step="1" inputmode="numeric">
            ${imgLink}
          </div>
        `);
    }

    // summary 画像 (play_order=0)
    const summaryImg = imagesByOrder.get(0);
    const summaryHtml = summaryImg
        ? `<p class="info-row" style="margin-top:14px;"><span class="label">通しリザルト</span><a href="${encodeURI(summaryImg)}" target="_blank" rel="noopener noreferrer">画像を開く</a></p>`
        : '';

    // 合計スコア (各曲ベスト合算)
    const bestPerSong = new Map();
    for (const p of sub.plays) {
        const cur = bestPerSong.get(p.songOrder) ?? -1;
        if (p.score > cur) bestPerSong.set(p.songOrder, p.score);
    }
    const total = Array.from(bestPerSong.values()).reduce((a, b) => a + b, 0);

    return `
      <article class="admin-item" data-key="${key}">
        <p><strong>${escapeHtml(sub.eventTitle)}</strong></p>
        <p class="info-row"><span class="label">投稿者</span>${escapeHtml(sub.submitName)} (@${escapeHtml(sub.xId)})</p>
        <p class="info-row"><span class="label">プレー環境</span>${sub.platform === 'arcade' ? 'アーケード' : 'コナステ'}</p>
        <div class="msc-pending-tracks">
          ${scoreInputs.join('')}
        </div>
        ${summaryHtml}
        <p class="info-row" style="margin-top:14px;"><span class="label">投稿日時</span>${escapeHtml(mscYmdHm(sub.submitDate))}</p>
        <p class="info-row"><span class="label">合計スコア (各曲ベスト合算)</span>${total.toLocaleString('en-US')}</p>
        <div class="admin-actions"
             data-event-id="${sub.mscEventId}"
             data-x-id="${escapeHtml(sub.xId)}"
             data-submit-id="${sub.submitId}">
          <button type="button" class="action-approve">承認</button>
          <button type="button" class="action-reject">却下</button>
        </div>
      </article>
    `;
}

async function loadPendingSubmissions() {
    if (!mscPendingScoresEl) return;
    mscPendingScoresEl.innerHTML = '<p class="loading">読み込み中...</p>';
    setScoresResult('', '');
    try {
        const list = await adminFetch('/api/admin/msc/submissions/pending');
        if (!list || list.length === 0) {
            mscPendingScoresEl.innerHTML = '<p class="empty">未承認のスコア投稿はありません。</p>';
            return;
        }
        mscPendingScoresEl.innerHTML = list.map(renderPendingSubmissionCard).join('');
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        mscPendingScoresEl.innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
    }
}

if (mscPendingReloadBtn) {
    mscPendingReloadBtn.addEventListener('click', loadPendingSubmissions);
}

if (mscPendingScoresEl) {
    mscPendingScoresEl.addEventListener('click', async (ev) => {
        const target = ev.target;
        if (!(target instanceof HTMLButtonElement)) return;
        const actionsEl = target.closest('.admin-actions');
        if (!actionsEl) return;
        const eventId = actionsEl.dataset.eventId;
        const xId = actionsEl.dataset.xId;
        const submitId = actionsEl.dataset.submitId;
        if (!eventId || !xId || !submitId) return;
        const card = target.closest('.admin-item');

        if (target.classList.contains('action-approve')) {
            // 各 Track のスコア入力値を収集 (修正があれば反映)
            const plays = Array.from(card.querySelectorAll('.msc-pending-score')).map((el) => ({
                playOrder: Number(el.dataset.playOrder),
                score: Number(el.value),
            })).filter((p) => Number.isFinite(p.score));
            if (plays.some((p) => p.score < 0 || p.score > 10000000)) {
                setScoresResult('スコアは 0〜10,000,000 の範囲です', 'ng');
                return;
            }
            if (!(await adminConfirm(`event=${eventId} / @${xId} / submit_id=${submitId} を承認します。よろしいですか？`))) return;
            target.disabled = true;
            try {
                await adminFetch(`/api/admin/msc/submissions/${eventId}/${xId}/${submitId}/approve`, {
                    method: 'POST',
                    body: JSON.stringify({ plays }),
                });
                setScoresResult('承認しました', 'ok');
                await loadPendingSubmissions();
            } catch (err) {
                if (err.message === 'AUTH_REDIRECT') return;
                setScoresResult(err.message, 'ng');
                target.disabled = false;
            }
        } else if (target.classList.contains('action-reject')) {
            if (!(await adminConfirm(`event=${eventId} / @${xId} / submit_id=${submitId} を却下します。よろしいですか？`))) return;
            target.disabled = true;
            try {
                await adminFetch(`/api/admin/msc/submissions/${eventId}/${xId}/${submitId}/reject`, {
                    method: 'POST', body: '{}',
                });
                setScoresResult('却下しました', 'ok');
                await loadPendingSubmissions();
            } catch (err) {
                if (err.message === 'AUTH_REDIRECT') return;
                setScoresResult(err.message, 'ng');
                target.disabled = false;
            }
        }
    });
}

// ============================================
// 公開設定サブタブ
// ============================================

function setPublishResult(text, kind) {
    if (!mscPublishResultEl) return;
    mscPublishResultEl.textContent = text;
    mscPublishResultEl.className = `result ${kind}`;
}

async function loadPublishList() {
    if (!mscPublishListEl) return;
    mscPublishListEl.innerHTML = '<p class="loading">読み込み中...</p>';
    setPublishResult('', '');
    try {
        const events = await adminFetch('/api/admin/msc/events');
        const past = (events ?? []).filter((e) => new Date(e.endDate).getTime() <= Date.now());
        renderPublishList(past);
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        mscPublishListEl.innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
    }
}

function renderPublishList(events) {
    if (!events.length) {
        mscPublishListEl.innerHTML = '<p class="empty">終了済みの MSC イベントはまだありません。</p>';
        return;
    }
    // 終了日が新しい順
    events = [...events].sort((a, b) => new Date(b.endDate) - new Date(a.endDate));
    const rows = events.map((e) => {
        const published = !!e.talliedAt;
        const stateLabel = published
            ? '<span class="status-active">公開中</span>'
            : '<span class="status-tallying">非公開</span>';
        const songs = (e.songs ?? [])
            .map((s) => `${s.songOrder}. ${escapeHtml(s.song)} (${escapeHtml(s.effect)})`)
            .join('<br>');
        return `
          <tr>
            <td>${e.mscEventId}</td>
            <td>${escapeHtml(e.title)}</td>
            <td>${escapeHtml(mscYmdHm(e.startDate))} ~ ${escapeHtml(mscYmdHm(e.endDate))}</td>
            <td>${songs || '(曲未設定)'}</td>
            <td><a href="/admin/msc/preview/?eventId=${e.mscEventId}">プレビュー</a>${published ? ` / <a href="/msc/ranking/?eventId=${e.mscEventId}" target="_blank" rel="noopener">公開ページ</a>` : ''}</td>
            <td class="status-cell">
              <button type="button" class="toggle-switch publish-toggle"
                      data-event-id="${e.mscEventId}"
                      data-published="${published}"
                      aria-pressed="${published}"
                      title="${published ? '公開中 → クリックで非公開' : '非公開 → クリックで公開'}">
                <span class="toggle-slider" aria-hidden="true"></span>
              </button>
              <span class="toggle-label">${stateLabel}</span>
            </td>
          </tr>
        `;
    }).join('');
    mscPublishListEl.innerHTML = `
      <div class="db-table-wrap">
        <table class="ranking-table">
          <thead>
            <tr><th>id</th><th>タイトル</th><th>開催期間</th><th>課題曲</th><th>確認</th><th class="status-cell">公開</th></tr>
          </thead>
          <tbody>${rows}</tbody>
        </table>
      </div>
    `;
}

if (mscPublishListEl) {
    mscPublishListEl.addEventListener('click', async (ev) => {
        const btn = ev.target.closest('.publish-toggle');
        if (!btn) return;
        const id = btn.dataset.eventId;
        if (!id) return;
        const isPublished = btn.dataset.published === 'true';
        const wantPublish = !isPublished;
        const verb = wantPublish ? '公開' : '非公開化';
        if (!(await adminConfirm(`msc_event_id=${id} を${verb}します。よろしいですか？`))) return;
        btn.disabled = true;
        try {
            await adminFetch(`/api/admin/msc/events/${id}/${wantPublish ? 'publish' : 'unpublish'}`, {
                method: 'POST',
                body: '{}',
            });
            setPublishResult(`msc_event_id=${id} を${verb}しました。`, 'ok');
            await loadPublishList();
        } catch (err) {
            if (err.message === 'AUTH_REDIRECT') return;
            setPublishResult(err.message, 'ng');
            btn.disabled = false;
        }
    });
}

// ============================================
// タブ切替
// ============================================
function selectMscTab(tab) {
    mscTabsEl.querySelectorAll('button').forEach((b) => {
        b.classList.toggle('active', b.dataset.tab === tab);
    });
    mscPanels.forEach((p) => {
        p.hidden = p.dataset.tab !== tab;
    });
    if (tab === 'events') {
        ensureMscSongsRows();
        loadMscEvents();
    }
    if (tab === 'scores') {
        loadPendingSubmissions();
    }
    if (tab === 'publish') {
        loadPublishList();
    }
    if (history.replaceState) {
        const url = new URL(window.location.href);
        url.searchParams.set('tab', tab);
        history.replaceState(null, '', url);
    }
}

mscTabsEl.addEventListener('click', (ev) => {
    const t = ev.target;
    if (!(t instanceof HTMLButtonElement)) return;
    selectMscTab(t.dataset.tab);
});

const initialTab = new URLSearchParams(window.location.search).get('tab') || 'events';
selectMscTab(initialTab);
