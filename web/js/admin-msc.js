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

function renderMscEventsList(events) {
    if (!events.length) {
        mscEventsListEl.innerHTML = '<p class="empty">MSC イベントはまだありません。</p>';
        return;
    }
    const rows = events.map((e) => {
        const songLines = (e.songs ?? [])
            .map((s) => `${s.songOrder}. ${escapeHtml(s.song)} (${escapeHtml(s.effect)})`)
            .join('<br>');
        const published = e.talliedAt
            ? `<span class="status-active">公開中</span>`
            : `<span class="status-tallying">非公開</span>`;
        return `
          <tr>
            <td>${e.mscEventId}</td>
            <td>${escapeHtml(e.title)}</td>
            <td>${escapeHtml(mscYmdHm(e.startDate))} ~ ${escapeHtml(mscYmdHm(e.endDate))}</td>
            <td>${songLines || '(曲未設定)'}</td>
            <td>${published}</td>
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
