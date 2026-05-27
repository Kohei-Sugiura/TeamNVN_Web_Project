const eventSection = document.getElementById('event-section');
const eventDisplay = document.getElementById('event-display');
const rankingSection = document.getElementById('ranking-section');
const rankingDisplay = document.getElementById('ranking-display');
const pastSection = document.getElementById('past-section');
const pastDisplay = document.getElementById('past-display');

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function formatYmd(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return `${d.getFullYear()}/${String(d.getMonth() + 1).padStart(2, '0')}/${String(d.getDate()).padStart(2, '0')}`;
}

const tt = (k) => (window.i18n && window.i18n.t) ? window.i18n.t(k) : k;
const lurl = (h) => (window.i18n && window.i18n.localLink) ? window.i18n.localLink(h) : h;

function renderEventHeader(ev) {
  eventSection.hidden = false;
  const songLines = (ev.songs || [])
    .map((s) => `${s.songOrder}. ${escapeHtml(s.song)} <span class="effect effect-${(s.effect || '').toLowerCase()}">${escapeHtml(s.effect)}</span>`)
    .join('<br>');
  eventDisplay.innerHTML = `
    <h2 class="msc-event-title">${escapeHtml(ev.title)}</h2>
    <p class="info-row"><span class="label">${tt('msc.score.label.period')}</span>${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</p>
    <p class="info-row"><span class="label">${tt('msc.label.tracks')}</span>${songLines}</p>
    ${ev.description ? `<p class="info-row"><span class="label">${tt('msc.label.description')}</span>${escapeHtml(ev.description)}</p>` : ''}
  `;
}

function showEventError(message) {
  eventSection.hidden = false;
  eventDisplay.innerHTML = `<p class="error">${escapeHtml(message)}</p>`;
}

function renderRanking(entries, songs) {
  rankingSection.hidden = false;
  renderMscRankingTable(rankingDisplay, entries, songs);
}

function renderPastList(events) {
  pastSection.hidden = false;
  if (!events || events.length === 0) {
    pastDisplay.innerHTML = `<p class="empty">${tt('common.empty.events')}</p>`;
    return;
  }
  const rows = events.map((ev) => `
    <tr>
      <td class="name"><a href="${lurl(`/msc/ranking/?eventId=${encodeURIComponent(ev.mscEventId)}`)}">${escapeHtml(ev.title)}</a></td>
      <td>${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</td>
    </tr>
  `).join('');
  pastDisplay.innerHTML = `
    <table class="ranking-table">
      <thead>
        <tr><th>${tt('history.col.title')}</th><th>${tt('msc.score.label.period')}</th></tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function showPastError(message) {
  pastSection.hidden = false;
  pastDisplay.innerHTML = `<p class="error">${escapeHtml(message)}</p>`;
}

async function showEventRanking(eventId) {
  try {
    const evRes = await fetch(`/api/msc/events/${eventId}`);
    if (evRes.status === 404) {
      showEventError(tt('msc.score.event.not-found'));
      return;
    }
    if (!evRes.ok) throw new Error(`HTTP ${evRes.status}`);
    const ev = await evRes.json();
    renderEventHeader(ev);

    const rankRes = await fetch(`/api/msc/events/${eventId}/ranking`);
    if (rankRes.status === 404) {
      rankingSection.hidden = false;
      rankingDisplay.innerHTML = `<p class="empty">${tt('msc.ranking.not-published')}</p>`;
      return;
    }
    if (!rankRes.ok) throw new Error(`HTTP ${rankRes.status}`);
    const entries = await rankRes.json();
    renderRanking(entries, ev.songs || []);
  } catch (err) {
    showEventError(`${tt('common.error.fetch')}: ${err.message}`);
  }
}

async function showPastEventList() {
  try {
    const res = await fetch('/api/msc/events/past');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const events = await res.json();
    renderPastList(events);
  } catch (err) {
    showPastError(`${tt('common.error.fetch')}: ${err.message}`);
  }
}

(async () => {
  const params = new URLSearchParams(window.location.search);
  const eventId = params.get('eventId');
  if (eventId && /^\d+$/.test(eventId)) {
    await showEventRanking(eventId);
    return;
  }
  // eventId 未指定 → 直近の集計済みイベントへリダイレクト
  try {
    const res = await fetch('/api/msc/events/past');
    if (res.ok) {
      const events = await res.json();
      if (events.length > 0) {
        window.location.replace(lurl(`/msc/ranking/?eventId=${encodeURIComponent(events[0].mscEventId)}`));
        return;
      }
    }
  } catch { /* fallback */ }
  await showPastEventList();
})();
