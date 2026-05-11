const heldEl = document.getElementById('held');
const unheldEl = document.getElementById('unheld');

function escapeHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s).replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
}

function formatYmd(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${y}/${m}/${day}`;
}

function formatPeriod(startIso, endIso) {
    return `${formatYmd(startIso)}~${formatYmd(endIso)}`;
}

function songsCell(songs) {
    if (!songs || !songs.length) return '(曲未設定)';
    return [...songs]
        .sort((a, b) => a.songOrder - b.songOrder)
        .map((s) => `${s.songOrder}. ${escapeHtml(s.song)} [${escapeHtml(s.effect)}]`)
        .join('<br>');
}

function eventState(ev, now) {
    const start = new Date(ev.startDate);
    const end = new Date(ev.endDate);
    if (now < start) return 'upcoming';
    if (now < end) return 'active';
    return ev.talliedAt ? 'finished' : 'tallying';
}

function statusCell(ev, now) {
    const state = eventState(ev, now);
    if (state === 'active') return '<span class="status-active">開催中</span>';
    if (state === 'tallying') return '<span class="status-tallying">集計中</span>';
    if (state === 'finished') {
        return `<a class="status-link" href="/msc/ranking/?eventId=${encodeURIComponent(ev.mscEventId)}">結果を表示</a>`;
    }
    return '<span class="status-tallying">-</span>';
}

function heldRow(ev, idx, now) {
    return `
      <tr>
        <td>${idx + 1}</td>
        <td>${escapeHtml(ev.title)}</td>
        <td>${escapeHtml(formatPeriod(ev.startDate, ev.endDate))}</td>
        <td>${songsCell(ev.songs)}</td>
        <td class="status-cell">${statusCell(ev, now)}</td>
      </tr>
    `;
}

function unheldRow(ev) {
    return `
      <tr>
        <td>${escapeHtml(ev.title)}</td>
        <td>${escapeHtml(formatPeriod(ev.startDate, ev.endDate))}</td>
        <td>${songsCell(ev.songs)}</td>
      </tr>
    `;
}

function renderHeld(items, now) {
    if (!items.length) {
        heldEl.innerHTML = '<p class="empty">まだ開催履歴はありません。</p>';
        return;
    }
    const rows = items.map((ev, i) => heldRow(ev, i, now)).join('');
    heldEl.innerHTML = `
      <table class="ranking-table history-table">
        <thead>
          <tr><th>#</th><th>タイトル</th><th>開催期間</th><th>課題曲</th><th class="status-cell">ステータス</th></tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;
}

function renderUnheld(items) {
    if (!items.length) {
        unheldEl.innerHTML = '<p class="empty">未開催のイベントはありません。</p>';
        return;
    }
    const rows = items.map(unheldRow).join('');
    unheldEl.innerHTML = `
      <table class="ranking-table history-table">
        <thead>
          <tr><th>タイトル</th><th>開催予定</th><th>課題曲</th></tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;
}

(async () => {
    try {
        const res = await fetch('/api/msc/events/recent?limit=200');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const events = await res.json();
        const now = new Date();

        const isHeld = (ev) => new Date(ev.startDate) <= now;
        const held = events
            .filter(isHeld)
            .sort((a, b) => new Date(b.startDate) - new Date(a.startDate));
        const unheld = events
            .filter((ev) => !isHeld(ev))
            .sort((a, b) => new Date(a.startDate) - new Date(b.startDate));

        renderHeld(held, now);
        renderUnheld(unheld);
    } catch (err) {
        heldEl.innerHTML = `<p class="error">取得に失敗しました: ${escapeHtml(err.message)}</p>`;
        unheldEl.innerHTML = '';
    }
})();
