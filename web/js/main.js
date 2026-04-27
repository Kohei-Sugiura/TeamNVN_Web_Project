const statusEl = document.getElementById('weepa-status');
const bodyEl = document.getElementById('weepa-current');
const latestSection = document.getElementById('weepa-latest-section');
const latestEl = document.getElementById('weepa-latest');

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function formatDate(iso) {
  const d = new Date(iso);
  return d.toLocaleString('ja-JP', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit',
  });
}

function formatYmd(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return `${d.getFullYear()}/${String(d.getMonth() + 1).padStart(2, '0')}/${String(d.getDate()).padStart(2, '0')}`;
}

function renderActive(ev) {
  statusEl.textContent = '現在開催中';
  bodyEl.innerHTML = `
    <div class="event-title">
      <span class="song">${escapeHtml(ev.song)}</span>
      <span class="effect">${escapeHtml(ev.effect)}</span>
    </div>
    <p class="period">
      ${escapeHtml(formatDate(ev.startDate))} ～ ${escapeHtml(formatDate(ev.endDate))}
    </p>
    <p class="event-link-wrap">
      <a class="event-link" href="/score.html?eventId=${encodeURIComponent(ev.weepaEventId)}">
        スコアを投稿する
      </a>
    </p>
  `;
}

function renderInactive() {
  statusEl.textContent = '調整中';
  bodyEl.innerHTML = '<p>次回の開催準備中です。</p>';
}

function renderError(message) {
  statusEl.textContent = '取得失敗';
  bodyEl.innerHTML = `<p class="error">${escapeHtml(message)}</p>`;
}

function renderLatest(ev, ranking) {
  const top = ranking.slice(0, 3);
  const rows = top.map((e) => `
    <tr>
      <td>${e.rank}</td>
      <td class="name">${escapeHtml(e.submitName)}</td>
      <td class="score">${Number(e.score).toLocaleString('en-US')}</td>
      <td class="ex">${e.exScore != null ? Number(e.exScore).toLocaleString('en-US') : '-'}</td>
    </tr>
  `).join('');

  const detailUrl = `/ranking.html?eventId=${encodeURIComponent(ev.weepaEventId)}`;

  latestEl.innerHTML = `
    <div class="event-title">
      <span class="song">${escapeHtml(ev.song)}</span>
      <span class="effect">${escapeHtml(ev.effect)}</span>
    </div>
    <p class="period">${escapeHtml(formatYmd(ev.startDate))}~${escapeHtml(formatYmd(ev.endDate))}</p>
    <table class="ranking-table mini">
      <thead><tr><th>#</th><th>名前</th><th>スコア</th><th>EX</th></tr></thead>
      <tbody>${rows}</tbody>
    </table>
    <p class="event-link-wrap"><a class="event-link" href="${detailUrl}">ランキング詳細を見る</a></p>
  `;
  latestSection.hidden = false;
}

async function loadLatestFinished() {
  try {
    const pastRes = await fetch('/api/events/past');
    if (!pastRes.ok) return;
    const past = await pastRes.json();
    if (!past.length) return;
    const latest = past[0];

    const rankRes = await fetch(`/api/events/${encodeURIComponent(latest.weepaEventId)}/ranking`);
    if (!rankRes.ok) return;
    const ranking = await rankRes.json();
    if (!ranking.length) return;

    renderLatest(latest, ranking);
  } catch {
    // nice-to-have なので失敗時はサイレント
  }
}

(async () => {
  try {
    const res = await fetch('/api/events/current');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const { event: ev } = await res.json();
    if (ev) renderActive(ev);
    else renderInactive();
  } catch (err) {
    renderError(err.message);
  }

  loadLatestFinished();
})();
