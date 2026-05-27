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
    return `${formatYmd(startIso)}~${formatYmd(endIso)}`;
}

function songsCell(songs) {
    if (!songs || !songs.length) return tt('msc.songs.unset');
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
    if (state === 'active') return `<span class="status-active">${tt('status.active')}</span>`;
    if (state === 'tallying') return `<span class="status-tallying">${tt('status.tallying')}</span>`;
    if (state === 'finished') {
        return `<a class="status-link" href="${lurl(`/msc/ranking/?eventId=${encodeURIComponent(ev.mscEventId)}`)}">${tt('status.result-link')}</a>`;
    }
    return `<span class="status-tallying">${tt('status.dash')}</span>`;
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
        heldEl.innerHTML = `<p class="empty">${tt('common.empty.history')}</p>`;
        return;
    }
    const rows = items.map((ev, i) => heldRow(ev, i, now)).join('');
    heldEl.innerHTML = `
      <table class="ranking-table history-table">
        <thead>
          <tr><th>${tt('history.col.num')}</th><th>${tt('history.col.title')}</th><th>${tt('history.col.period')}</th><th>${tt('msc.label.tracks')}</th><th class="status-cell">${tt('history.col.status')}</th></tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;
}

function renderUnheld(items) {
    if (!items.length) {
        unheldEl.innerHTML = `<p class="empty">${tt('common.empty.history.upcoming.msc')}</p>`;
        return;
    }
    const rows = items.map(unheldRow).join('');
    unheldEl.innerHTML = `
      <table class="ranking-table history-table">
        <thead>
          <tr><th>${tt('history.col.title')}</th><th>${tt('history.col.upcoming-period')}</th><th>${tt('msc.label.tracks')}</th></tr>
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
        heldEl.innerHTML = `<p class="error">${tt('common.error.fetch')}: ${escapeHtml(err.message)}</p>`;
        unheldEl.innerHTML = '';
    }
})();
