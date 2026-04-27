const statusEl = document.getElementById('weepa-status');
const bodyEl = document.getElementById('weepa-current');

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
})();
