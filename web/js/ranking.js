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

function formatScore(n) {
  return Number(n).toLocaleString('en-US');
}

function formatDate(iso) {
  const d = new Date(iso);
  return d.toLocaleString('ja-JP', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit',
  });
}

function formatDateOnly(iso) {
  const d = new Date(iso);
  return d.toLocaleDateString('ja-JP', {
    year: 'numeric', month: '2-digit', day: '2-digit',
  });
}

function renderEventHeader(ev) {
  eventSection.hidden = false;
  eventDisplay.innerHTML = `
    <div class="event-title">
      <span class="song">${escapeHtml(ev.song)}</span>
      <span class="effect">${escapeHtml(ev.effect)}</span>
    </div>
    <p class="period">
      ${escapeHtml(formatDate(ev.startDate))} ～ ${escapeHtml(formatDate(ev.endDate))}
    </p>
  `;
}

function renderRanking(entries) {
  rankingSection.hidden = false;
  if (!entries || entries.length === 0) {
    rankingDisplay.innerHTML = '<p class="empty">まだランキングがありません。</p>';
    return;
  }
  const rows = entries.map((e) => `
    <tr>
      <td class="name">${escapeHtml(e.submitName)}</td>
      <td class="score">${formatScore(e.score)}</td>
      <td class="ex">${e.exScore != null ? formatScore(e.exScore) : '-'}</td>
    </tr>
  `).join('');
  rankingDisplay.innerHTML = `
    <table class="ranking-table">
      <thead>
        <tr>
          <th>投稿者名</th>
          <th>スコア</th>
          <th>EXスコア</th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
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
      <td class="name"><a href="/ranking.html?eventId=${encodeURIComponent(ev.weepaEventId)}">${escapeHtml(ev.song)}</a></td>
      <td class="effect">${escapeHtml(ev.effect)}</td>
      <td class="ex">${escapeHtml(formatDateOnly(ev.endDate))}</td>
    </tr>
  `).join('');
  pastDisplay.innerHTML = `
    <table class="ranking-table">
      <thead>
        <tr>
          <th>楽曲</th>
          <th>譜面種</th>
          <th>集計日</th>
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
  } else {
    await showPastEventList();
  }
})();
