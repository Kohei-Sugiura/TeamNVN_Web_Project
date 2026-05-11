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
  const songLines = (ev.songs || [])
    .map((s) => `${s.songOrder}. ${escapeHtml(s.song)} <span class="effect effect-${(s.effect || '').toLowerCase()}">${escapeHtml(s.effect)}</span>`)
    .join('<br>');
  eventDisplay.innerHTML = `
    <h2 class="msc-event-title">${escapeHtml(ev.title)}</h2>
    <p class="info-row"><span class="label">開催期間</span>${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</p>
    <p class="info-row"><span class="label">課題曲</span>${songLines}</p>
    ${ev.description ? `<p class="info-row"><span class="label">説明</span>${escapeHtml(ev.description)}</p>` : ''}
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
    pastDisplay.innerHTML = '<p class="empty">集計済みのイベントはまだありません。</p>';
    return;
  }
  const rows = events.map((ev) => `
    <tr>
      <td class="name"><a href="/msc/ranking/?eventId=${encodeURIComponent(ev.mscEventId)}">${escapeHtml(ev.title)}</a></td>
      <td>${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</td>
    </tr>
  `).join('');
  pastDisplay.innerHTML = `
    <table class="ranking-table">
      <thead>
        <tr><th>タイトル</th><th>開催期間</th></tr>
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
      showEventError('指定されたイベントは存在しません。');
      return;
    }
    if (!evRes.ok) throw new Error(`HTTP ${evRes.status}`);
    const ev = await evRes.json();
    renderEventHeader(ev);

    const rankRes = await fetch(`/api/msc/events/${eventId}/ranking`);
    if (rankRes.status === 404) {
      rankingSection.hidden = false;
      rankingDisplay.innerHTML = '<p class="empty">このイベントの集計結果はまだ公開されていません。</p>';
      return;
    }
    if (!rankRes.ok) throw new Error(`HTTP ${rankRes.status}`);
    const entries = await rankRes.json();
    renderRanking(entries, ev.songs || []);
  } catch (err) {
    showEventError(`情報の取得に失敗しました: ${err.message}`);
  }
}

async function showPastEventList() {
  try {
    const res = await fetch('/api/msc/events/past');
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
    const res = await fetch('/api/msc/events/past');
    if (res.ok) {
      const events = await res.json();
      if (events.length > 0) {
        window.location.replace(`/msc/ranking/?eventId=${encodeURIComponent(events[0].mscEventId)}`);
        return;
      }
    }
  } catch { /* fallback */ }
  await showPastEventList();
})();
