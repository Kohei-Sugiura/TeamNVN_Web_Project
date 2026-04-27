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

function isActive(ev, now) {
  return new Date(ev.startDate) <= now && now < new Date(ev.endDate);
}

function isPast(ev, now) {
  return new Date(ev.endDate) <= now;
}

function isTallied(ev, now) {
  return now >= new Date(new Date(ev.endDate).getTime() + TALLIED_AFTER_MS);
}

function activeCard(ev) {
  const url = `/score.html?eventId=${encodeURIComponent(ev.weepaEventId)}`;
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">現在開催中</h3>
      <div class="weepa-status-body">
        <div class="event-title">
          <span class="song">${escapeHtml(ev.song)}</span>
          <span class="effect effect-${(ev.effect || '').toLowerCase()}">${escapeHtml(ev.effect)}</span>
        </div>
        <p class="period">開催期間: ${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</p>
        <p class="event-link-wrap"><a class="event-link" href="${url}">スコアを投稿する</a></p>
      </div>
    </section>
  `;
}

function recentEntry(ev, now) {
  const tallied = isTallied(ev, now);
  const action = tallied
    ? `<a class="event-link" href="/ranking.html?eventId=${encodeURIComponent(ev.weepaEventId)}">ランキング詳細を見る</a>`
    : `<span class="weepa-pending">集計が完了するまでお待ちください</span>`;
  return `
    <div class="recent-event">
      <div class="event-title">
        <span class="song">${escapeHtml(ev.song)}</span>
        <span class="effect effect-${(ev.effect || '').toLowerCase()}">${escapeHtml(ev.effect)}</span>
      </div>
      <p class="period">開催期間: ${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</p>
      <p class="event-link-wrap">${action}</p>
    </div>
  `;
}

function recentCard(events, now) {
  if (!events.length) return '';
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">直近の開催</h3>
      <div class="weepa-status-body recent-events">
        ${events.map((ev) => recentEntry(ev, now)).join('')}
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

(async () => {
  try {
    const res = await fetch('/api/events/recent?limit=6');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const events = await res.json();
    const now = new Date();

    const active = events.find((ev) => isActive(ev, now));
    const recent = events
      .filter((ev) => isPast(ev, now))
      .sort((a, b) => new Date(b.endDate) - new Date(a.endDate))
      .slice(0, 3);

    const cards = [];
    cards.push(active ? activeCard(active) : adjustingCard());
    if (recent.length) cards.push(recentCard(recent, now));

    cardsEl.innerHTML = cards.join('');
  } catch (err) {
    cardsEl.innerHTML = `<p class="error">取得に失敗しました: ${escapeHtml(err.message)}</p>`;
  }
})();
