const eventDisplay = document.getElementById('event-display');
const rankingDisplay = document.getElementById('ranking-display');

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

function renderEventHeader(ev) {
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
  eventDisplay.innerHTML = `<p class="error">${escapeHtml(message)}</p>`;
  rankingDisplay.innerHTML = '';
}

(async () => {
  const params = new URLSearchParams(window.location.search);
  const eventId = params.get('eventId');
  if (!eventId || !/^\d+$/.test(eventId)) {
    showEventError('イベントが指定されていません。');
    return;
  }

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
})();
