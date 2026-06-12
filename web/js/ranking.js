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
  eventDisplay.innerHTML = `
    <div class="event-title">
      <span class="song">${escapeHtml(ev.song)}</span>
      <span class="effect effect-${(ev.effect || '').toLowerCase()}">${escapeHtml(ev.effect)}</span>
    </div>
    <p class="info-row"><span class="label">${tt('weepa.label.period')}</span>${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</p>
  `;
}

// ?tier=1|2|3 が付いていたらタブ無しでその部門のみ表示
// (Discord の部門別スクショと結果共有リンク用)
function tierViewFromQuery() {
  const v = Number(new URLSearchParams(window.location.search).get('tier'));
  return (v >= 1 && v <= 3) ? v : null;
}

function renderRanking(entries, ev) {
  rankingSection.hidden = false;
  renderRankingTable(rankingDisplay, entries, {
    exScoreMax: ev?.exScoreMax,
    tierView: tierViewFromQuery(),
  });
}

function showEventError(message) {
  eventSection.hidden = false;
  eventDisplay.innerHTML = `<p class="error">${escapeHtml(message)}</p>`;
}

function renderPastList(events) {
  pastSection.hidden = false;
  if (!events || events.length === 0) {
    pastDisplay.innerHTML = `<p class="empty">${tt('common.empty.events')}</p>`;
    return;
  }
  const rows = events.map((ev) => `
    <tr>
      <td class="name"><a href="${lurl(`/weepa/ranking/?eventId=${encodeURIComponent(ev.weepaEventId)}`)}">${escapeHtml(ev.song)}</a></td>
      <td><span class="effect effect-${(ev.effect || '').toLowerCase()}">${escapeHtml(ev.effect)}</span></td>
      <td class="ex">${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</td>
    </tr>
  `).join('');
  pastDisplay.innerHTML = `
    <table class="ranking-table">
      <thead>
        <tr>
          <th>${tt('ranking.col.song')}</th>
          <th>${tt('ranking.col.effect')}</th>
          <th>${tt('ranking.col.period')}</th>
        </tr>
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
    const evRes = await fetch(`/api/events/${eventId}`);
    if (evRes.status === 404) {
      showEventError(tt('weepa.score.event.not-found'));
      return;
    }
    if (!evRes.ok) throw new Error(`HTTP ${evRes.status}`);
    const ev = await evRes.json();
    renderEventHeader(ev);

    const rankRes = await fetch(`/api/events/${eventId}/ranking`);
    if (!rankRes.ok) throw new Error(`HTTP ${rankRes.status}`);
    const entries = await rankRes.json();
    renderRanking(entries, ev);
  } catch (err) {
    showEventError(`${tt('common.error.fetch')}: ${err.message}`);
  }
}

async function showPastEventList() {
  try {
    const res = await fetch('/api/events/past');
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
    const res = await fetch('/api/events/past');
    if (res.ok) {
      const events = await res.json();
      if (events.length > 0) {
        window.location.replace(lurl(`/weepa/ranking/?eventId=${encodeURIComponent(events[0].weepaEventId)}`));
        return;
      }
    }
  } catch { /* ignore — フォールバックの一覧表示に流す */ }
  await showPastEventList();
})();
