const cardsEl = document.getElementById('weepa-cards');

const TALLIED_AFTER_MS = 7 * 24 * 60 * 60 * 1000;

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function formatDateTime(iso) {
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

function eventState(ev, now = new Date()) {
  const start = new Date(ev.startDate);
  const end = new Date(ev.endDate);
  if (now < start) return 'upcoming';
  if (now < end) return 'active';
  if (now < new Date(end.getTime() + TALLIED_AFTER_MS)) return 'tallying';
  return 'finished';
}

function activeCard(ev) {
  const url = `/score.html?eventId=${encodeURIComponent(ev.weepaEventId)}`;
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">現在開催中</h3>
      <div class="weepa-status-body">
        <div class="event-title">
          <span class="song">${escapeHtml(ev.song)}</span>
          <span class="effect">${escapeHtml(ev.effect)}</span>
        </div>
        <p class="period">${escapeHtml(formatDateTime(ev.startDate))} ～ ${escapeHtml(formatDateTime(ev.endDate))}</p>
        <p class="event-link-wrap"><a class="event-link" href="${url}">スコアを投稿する</a></p>
      </div>
    </section>
  `;
}

function tallyingCard(ev) {
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">集計中</h3>
      <div class="weepa-status-body">
        <div class="event-title">
          <span class="song">${escapeHtml(ev.song)}</span>
          <span class="effect">${escapeHtml(ev.effect)}</span>
        </div>
        <p class="period">${escapeHtml(formatYmd(ev.startDate))}~${escapeHtml(formatYmd(ev.endDate))}</p>
        <p class="hint">集計が完了するまでしばらくお待ちください。</p>
      </div>
    </section>
  `;
}

function finishedCard(ev) {
  const url = `/ranking.html?eventId=${encodeURIComponent(ev.weepaEventId)}`;
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">集計結果</h3>
      <div class="weepa-status-body">
        <div class="event-title">
          <span class="song">${escapeHtml(ev.song)}</span>
          <span class="effect">${escapeHtml(ev.effect)}</span>
        </div>
        <p class="period">${escapeHtml(formatYmd(ev.startDate))}~${escapeHtml(formatYmd(ev.endDate))}</p>
        <p class="event-link-wrap"><a class="event-link" href="${url}">ランキング詳細を見る</a></p>
      </div>
    </section>
  `;
}

function adjustingCard() {
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">調整中</h3>
      <div class="weepa-status-body">
        <p>次回の開催準備中です。</p>
      </div>
    </section>
  `;
}

function renderCard(ev) {
  switch (eventState(ev)) {
    case 'active': return activeCard(ev);
    case 'tallying': return tallyingCard(ev);
    case 'finished': return finishedCard(ev);
    default: return '';
  }
}

(async () => {
  try {
    const res = await fetch('/api/events/recent?limit=3');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const events = await res.json();

    if (!events.length) {
      cardsEl.innerHTML = adjustingCard();
      return;
    }

    const hasActive = events.some((ev) => eventState(ev) === 'active');
    const cards = events.map(renderCard).filter(Boolean);
    if (!hasActive) cards.unshift(adjustingCard());

    cardsEl.innerHTML = cards.join('');
  } catch (err) {
    cardsEl.innerHTML = `<p class="error">取得に失敗しました: ${escapeHtml(err.message)}</p>`;
  }
})();
