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

function renderEventHeader(ev) {
  eventSection.hidden = false;
  eventDisplay.innerHTML = `
    <div class="event-title">
      <span class="song">${escapeHtml(ev.song)}</span>
      <span class="effect effect-${(ev.effect || '').toLowerCase()}">${escapeHtml(ev.effect)}</span>
    </div>
    <p class="info-row"><span class="label">開催期間</span>${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</p>
  `;
}

function renderRanking(entries) {
  rankingSection.hidden = false;
  renderRankingTable(rankingDisplay, entries);
}

function showEventError(message) {
  eventSection.hidden = false;
  eventDisplay.innerHTML = `<p class="error">${escapeHtml(message)}</p>`;
}

function renderPastList(events) {
  pastSection.hidden = false;
  if (!events || events.length === 0) {
    pastDisplay.innerHTML = '<p class="empty">集計済みのイベントはまだありません。</p>';
    return;
  }
  const rows = events.map((ev) => `
    <tr>
      <td class="name"><a href="/weepa/ranking/?eventId=${encodeURIComponent(ev.weepaEventId)}">${escapeHtml(ev.song)}</a></td>
      <td><span class="effect effect-${(ev.effect || '').toLowerCase()}">${escapeHtml(ev.effect)}</span></td>
      <td class="ex">${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</td>
    </tr>
  `).join('');
  pastDisplay.innerHTML = `
    <table class="ranking-table">
      <thead>
        <tr>
          <th>楽曲</th>
          <th>譜面種</th>
          <th>開催期間</th>
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
      showEventError('指定されたイベントは存在しません。');
      return;
    }
    if (!evRes.ok) throw new Error(`HTTP ${evRes.status}`);
    const ev = await evRes.json();
    renderEventHeader(ev);

    const rankRes = await fetch(`/api/events/${eventId}/ranking`);
    if (!rankRes.ok) throw new Error(`HTTP ${rankRes.status}`);
    const entries = await rankRes.json();
    renderRanking(entries);
  } catch (err) {
    showEventError(`情報の取得に失敗しました: ${err.message}`);
  }
}

async function showPastEventList() {
  try {
    const res = await fetch('/api/events/past');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const events = await res.json();
    renderPastList(events);
  } catch (err) {
    showPastError(`情報の取得に失敗しました: ${err.message}`);
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
        window.location.replace(`/weepa/ranking/?eventId=${encodeURIComponent(events[0].weepaEventId)}`);
        return;
      }
    }
  } catch { /* ignore — フォールバックの一覧表示に流す */ }
  await showPastEventList();
})();
