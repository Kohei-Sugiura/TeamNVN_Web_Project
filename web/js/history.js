const heldEl = document.getElementById('held');
const unheldEl = document.getElementById('unheld');

const tt = (k) => (window.i18n && window.i18n.t) ? window.i18n.t(k) : k;
const lurl = (h) => (window.i18n && window.i18n.localLink) ? window.i18n.localLink(h) : h;

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
    if (state === 'active') return `<span class="status-active">${tt('status.active')}</span>`;
    if (state === 'tallying') return `<span class="status-tallying">${tt('status.tallying')}</span>`;
    if (state === 'finished' && s.weepaEventId) {
        return `<a class="status-link" href="${lurl(`/weepa/ranking/?eventId=${encodeURIComponent(s.weepaEventId)}`)}">${tt('status.result-link')}</a>`;
    }
    return `<span class="status-tallying">${tt('status.dash')}</span>`;
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
      </tr>
    `;
}

function renderHeld(items, now) {
    if (!items.length) {
        heldEl.innerHTML = `<p class="empty">${tt('common.empty.history')}</p>`;
        return;
    }
    const rows = items.map((s, i) => heldRow(s, i, now)).join('');
    heldEl.innerHTML = `
      <table class="ranking-table history-table">
        <thead>
          <tr><th>${tt('history.col.num')}</th><th>${tt('history.col.song')}</th><th>${tt('history.col.effect')}</th><th>${tt('history.col.submitter')}</th><th>${tt('history.col.period')}</th><th class="status-cell">${tt('history.col.status')}</th></tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;
}

function renderUnheld(items) {
    if (!items.length) {
        unheldEl.innerHTML = `<p class="empty">${tt('common.empty.history.upcoming')}</p>`;
        return;
    }
    const rows = items.map(unheldRow).join('');
    unheldEl.innerHTML = `
      <table class="ranking-table history-table">
        <thead>
          <tr><th>${tt('history.col.song')}</th><th>${tt('history.col.effect')}</th><th>${tt('history.col.submitter')}</th></tr>
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

        // 「開催済み」= 紐付き event が既に開始している (start_date <= NOW)。
        // 未来日のイベント枠 (承認時に自動作成された次回以降) はまだ開催されていないので
        // 「未開催」セクションに入れる。
        const isHeld = (s) => s.eventStartDate && new Date(s.eventStartDate) <= now;

        const held = items
            .filter(isHeld)
            .sort((a, b) => new Date(b.eventStartDate) - new Date(a.eventStartDate));

        // 未開催は単純に投稿順 (承認時にランダム位置に挿入されるので
        // 開催順での並び替えは意味を持たない)。
        const unheld = items
            .filter((s) => !isHeld(s))
            .sort((a, b) => new Date(a.submitDate) - new Date(b.submitDate));

        renderHeld(held, now);
        renderUnheld(unheld);
    } catch (err) {
        heldEl.innerHTML = `<p class="error">${tt('common.error.fetch')}: ${escapeHtml(err.message)}</p>`;
        unheldEl.innerHTML = '';
    }
})();
