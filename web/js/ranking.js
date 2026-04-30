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
    <p class="period">
      開催期間: ${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}
    </p>
  `;
}

const rankingState = {
  entries: [],
  sortKey: 'rank',
  sortDir: 'asc',
};

const SORTABLE_COLUMNS = {
  rank: { label: '#', defaultDir: 'asc', getValue: (e) => e.rank },
  score: { label: 'スコア', defaultDir: 'desc', getValue: (e) => e.score },
  exScore: { label: 'EXスコア', defaultDir: 'desc', getValue: (e) => (e.exScore == null ? -Infinity : e.exScore) },
  lastSubmitDate: { label: '最終投稿日時', defaultDir: 'desc', getValue: (e) => new Date(e.lastSubmitDate).getTime() },
  updateCount: { label: 'スコア更新回数', defaultDir: 'desc', getValue: (e) => e.updateCount },
};

function compareEntries(a, b, key, dir) {
  const col = SORTABLE_COLUMNS[key];
  const av = col.getValue(a);
  const bv = col.getValue(b);
  let cmp = 0;
  if (av < bv) cmp = -1;
  else if (av > bv) cmp = 1;
  if (cmp === 0) cmp = a.rank - b.rank;
  return dir === 'asc' ? cmp : -cmp;
}

function sortIndicator(key) {
  if (rankingState.sortKey !== key) return '';
  return rankingState.sortDir === 'asc' ? ' ▲' : ' ▼';
}

function renderRanking(entries) {
  rankingSection.hidden = false;
  rankingState.entries = entries || [];
  if (!entries || entries.length === 0) {
    rankingDisplay.innerHTML = '<p class="empty">まだランキングがありません。</p>';
    return;
  }
  drawRankingTable();
}

function drawRankingTable() {
  const sorted = [...rankingState.entries].sort((a, b) =>
    compareEntries(a, b, rankingState.sortKey, rankingState.sortDir));
  const rows = sorted.map((e) => `
    <tr>
      <td class="rank">${e.rank}</td>
      <td class="name">${escapeHtml(e.submitName)}</td>
      <td class="score">${formatScore(e.score)}</td>
      <td class="ex">${e.exScore != null ? formatScore(e.exScore) : '-'}</td>
      <td class="last-submit">${escapeHtml(formatDate(e.lastSubmitDate))}</td>
      <td class="update-count">${e.updateCount}</td>
    </tr>
  `).join('');
  rankingDisplay.innerHTML = `
    <table class="ranking-table sortable-table">
      <thead>
        <tr>
          <th class="sortable" data-sort="rank">#${sortIndicator('rank')}</th>
          <th>投稿者名</th>
          <th class="sortable" data-sort="score">スコア${sortIndicator('score')}</th>
          <th class="sortable" data-sort="exScore">EXスコア${sortIndicator('exScore')}</th>
          <th class="sortable" data-sort="lastSubmitDate">最終投稿日時${sortIndicator('lastSubmitDate')}</th>
          <th class="sortable" data-sort="updateCount">スコア更新回数${sortIndicator('updateCount')}</th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
  rankingDisplay.querySelectorAll('th.sortable').forEach((th) => {
    th.addEventListener('click', () => {
      const key = th.dataset.sort;
      if (!SORTABLE_COLUMNS[key]) return;
      if (rankingState.sortKey === key) {
        rankingState.sortDir = rankingState.sortDir === 'asc' ? 'desc' : 'asc';
      } else {
        rankingState.sortKey = key;
        rankingState.sortDir = SORTABLE_COLUMNS[key].defaultDir;
      }
      drawRankingTable();
    });
  });
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
  } else {
    await showPastEventList();
  }
})();
