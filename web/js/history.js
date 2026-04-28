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
    const s = formatYmd(startIso);
    const e = formatYmd(endIso);
    if (!s && !e) return '';
    return `${s}~${e}`;
}

function eventState(s, now) {
    if (!s.eventStartDate || !s.eventEndDate) return null;
    const start = new Date(s.eventStartDate);
    const end = new Date(s.eventEndDate);
    if (now < start) return 'upcoming';
    if (now < end) return 'active';
    return s.eventTalliedAt ? 'finished' : 'tallying';
}

function statusCell(s, now) {
    const state = eventState(s, now);
    if (state === 'active') return '<span class="status-active">開催中</span>';
    if (state === 'tallying') return '<span class="status-tallying">集計中</span>';
    if (state === 'finished' && s.weepaEventId) {
        return `<a class="status-link" href="/weepa/ranking/?eventId=${encodeURIComponent(s.weepaEventId)}">結果を表示</a>`;
    }
    return '<span class="status-tallying">-</span>';
}

function heldRow(s, idx, now) {
    const period = formatPeriod(s.eventStartDate, s.eventEndDate);
    return `
      <tr>
        <td>${idx + 1}</td>
        <td>${escapeHtml(s.song)}</td>
        <td><span class="effect effect-${(s.effect || '').toLowerCase()}">${escapeHtml(s.effect)}</span></td>
        <td>${escapeHtml(s.submitName)}</td>
        <td>${escapeHtml(period)}</td>
        <td class="status-cell">${statusCell(s, now)}</td>
      </tr>
    `;
}

function unheldRow(s) {
    return `
      <tr>
        <td>${escapeHtml(s.song)}</td>
        <td><span class="effect effect-${(s.effect || '').toLowerCase()}">${escapeHtml(s.effect)}</span></td>
        <td>${escapeHtml(s.submitName)}</td>
        <td>${escapeHtml(formatYmd(s.submitDate))}</td>
      </tr>
    `;
}

function renderHeld(items, now) {
    if (!items.length) {
        heldEl.innerHTML = '<p class="empty">まだ開催履歴はありません。</p>';
        return;
    }
    const rows = items.map((s, i) => heldRow(s, i, now)).join('');
    heldEl.innerHTML = `
      <table class="ranking-table history-table">
        <thead>
          <tr><th>#</th><th>楽曲</th><th>譜面種</th><th>提案者</th><th>開催期間</th><th class="status-cell">ステータス</th></tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;
}

function renderUnheld(items) {
    if (!items.length) {
        unheldEl.innerHTML = '<p class="empty">未開催の楽曲はありません。</p>';
        return;
    }
    const rows = items.map(unheldRow).join('');
    unheldEl.innerHTML = `
      <table class="ranking-table history-table">
        <thead>
          <tr><th>楽曲</th><th>譜面種</th><th>提案者</th><th>提案日</th></tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;
}

(async () => {
    try {
        const res = await fetch('/api/songs');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const items = await res.json();
        const now = new Date();

        // 「実施済み」は events 紐付きの有無で判定する。
        const held = items
            .filter((s) => s.eventStartDate)
            .sort((a, b) => new Date(a.eventStartDate) - new Date(b.eventStartDate));

        const unheld = items
            .filter((s) => !s.eventStartDate)
            .sort((a, b) => new Date(a.submitDate) - new Date(b.submitDate));

        renderHeld(held, now);
        renderUnheld(unheld);
    } catch (err) {
        heldEl.innerHTML = `<p class="error">取得に失敗しました: ${escapeHtml(err.message)}</p>`;
        unheldEl.innerHTML = '';
    }
})();
