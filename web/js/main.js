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

function renderLatest(ev) {
  const detailUrl = `/ranking.html?eventId=${encodeURIComponent(ev.weepaEventId)}`;
  latestEl.innerHTML = `
    <div class="event-title">
      <span class="song">${escapeHtml(ev.song)}</span>
      <span class="effect">${escapeHtml(ev.effect)}</span>
    </div>
    <p class="period">${escapeHtml(formatYmd(ev.startDate))}~${escapeHtml(formatYmd(ev.endDate))}</p>
    <p class="event-link-wrap"><a class="event-link" href="${detailUrl}">ランキング詳細を見る</a></p>
  `;
  latestSection.hidden = false;
}

(async () => {
  // current と past を並列でキック
  const currentPromise = fetch('/api/events/current')
    .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))));
  const pastPromise = fetch('/api/events/past')
    .then((r) => (r.ok ? r.json() : []))
    .catch(() => []);

  try {
    const { event: ev } = await currentPromise;
    if (ev) renderActive(ev);
    else renderInactive();
  } catch (err) {
    renderError(err.message);
  }

  const past = await pastPromise;
  if (past.length) renderLatest(past[0]);
})();
