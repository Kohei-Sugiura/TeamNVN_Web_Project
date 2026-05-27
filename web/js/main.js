const cardsEl = document.getElementById('weepa-cards');

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

function formatYmdHm(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  const ymd = `${d.getFullYear()}/${String(d.getMonth() + 1).padStart(2, '0')}/${String(d.getDate()).padStart(2, '0')}`;
  const hm = `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  return `${ymd} ${hm}`;
}

function isActive(ev, now) {
  return new Date(ev.startDate) <= now && now < new Date(ev.endDate);
}

function isPast(ev, now) {
  return new Date(ev.endDate) <= now;
}

function isTallied(ev) {
  return !!ev.talliedAt;
}

function tt(key) {
  return (window.i18n && window.i18n.t) ? window.i18n.t(key) : key;
}
function lurl(href) {
  return (window.i18n && window.i18n.localLink) ? window.i18n.localLink(href) : href;
}

function activeCard(ev) {
  const url = lurl(`/weepa/score/?eventId=${encodeURIComponent(ev.weepaEventId)}`);
  const submitter = ev.submitName
    ? `<p class="info-row"><span class="label">${tt('weepa.label.submitter')}</span>${escapeHtml(ev.submitName)}</p>`
    : '';
  const reason = ev.submitReason
    ? `<p class="info-row"><span class="label">${tt('weepa.label.reason')}</span>${escapeHtml(ev.submitReason)}</p>`
    : '';
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">${tt('weepa.current-event')}</h3>
      <div class="weepa-status-body">
        <div class="event-title">
          <span class="song">${escapeHtml(ev.song)}</span>
          <span class="effect effect-${(ev.effect || '').toLowerCase()}">${escapeHtml(ev.effect)}</span>
        </div>
        <p class="info-row"><span class="label">${tt('weepa.label.period')}</span>${escapeHtml(formatYmdHm(ev.startDate))} ~ ${escapeHtml(formatYmdHm(ev.endDate))}</p>
        ${submitter}
        ${reason}
        <p class="event-link-wrap"><a class="event-link" href="${url}">${tt('nav.score')}</a></p>
      </div>
    </section>
  `;
}

function recentEntry(ev, now) {
  const tallied = isTallied(ev);
  const action = tallied
    ? `<a class="event-link" href="${lurl(`/weepa/ranking/?eventId=${encodeURIComponent(ev.weepaEventId)}`)}">${tt('nav.ranking-detail')}</a>`
    : `<span class="weepa-pending">${tt('nav.pending')}</span>`;
  return `
    <div class="recent-event">
      <div class="event-title">
        <span class="song">${escapeHtml(ev.song)}</span>
        <span class="effect effect-${(ev.effect || '').toLowerCase()}">${escapeHtml(ev.effect)}</span>
      </div>
      <p class="info-row"><span class="label">${tt('weepa.label.period')}</span>${escapeHtml(formatYmdHm(ev.startDate))} ~ ${escapeHtml(formatYmdHm(ev.endDate))}</p>
      <p class="event-link-wrap">${action}</p>
    </div>
  `;
}

function recentCard(events, now) {
  if (!events.length) return '';
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">${tt('weepa.recent-events')}</h3>
      <div class="weepa-status-body recent-events">
        ${events.map((ev) => recentEntry(ev, now)).join('')}
      </div>
    </section>
  `;
}

function adjustingCard() {
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">${tt('weepa.adjusting')}</h3>
      <div class="weepa-status-body">
        <p>${tt('weepa.adjusting.body')}</p>
      </div>
    </section>
  `;
}

(async () => {
  try {
    const res = await fetch('/api/events/recent?limit=20');
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
    cardsEl.innerHTML = `<p class="error">${tt('common.error.fetch')}: ${escapeHtml(err.message)}</p>`;
  }
})();

// ============================================
// MoonShotChallenge (MSC) セクション
// ============================================
const mscCardsEl = document.getElementById('msc-cards');

function mscSongsHtml(songs) {
  if (!songs || !songs.length) return '';
  const lines = [...songs]
    .sort((a, b) => a.songOrder - b.songOrder)
    .map((s) => `${s.songOrder}. ${escapeHtml(s.song)} <span class="effect effect-${(s.effect || '').toLowerCase()}">${escapeHtml(s.effect)}</span>`)
    .join('<br>');
  return `<p class="info-row"><span class="label">${tt('msc.label.tracks')}</span>${lines}</p>`;
}

function mscActiveCard(ev) {
  const url = lurl(`/msc/score/?eventId=${encodeURIComponent(ev.mscEventId)}`);
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">${tt('weepa.current-event')}</h3>
      <div class="weepa-status-body">
        <h2 class="msc-event-title">${escapeHtml(ev.title)}</h2>
        <p class="info-row"><span class="label">${tt('weepa.label.period')}</span>${escapeHtml(formatYmdHm(ev.startDate))} ~ ${escapeHtml(formatYmdHm(ev.endDate))}</p>
        ${mscSongsHtml(ev.songs)}
        ${ev.description ? `<p class="info-row"><span class="label">${tt('weepa.label.reason')}</span>${escapeHtml(ev.description)}</p>` : ''}
        <p class="event-link-wrap"><a class="event-link" href="${url}">${tt('nav.score')}</a></p>
      </div>
    </section>
  `;
}

function mscRecentEntry(ev) {
  const tallied = isTallied(ev);
  const action = tallied
    ? `<a class="event-link" href="${lurl(`/msc/ranking/?eventId=${encodeURIComponent(ev.mscEventId)}`)}">${tt('nav.ranking-detail')}</a>`
    : `<span class="weepa-pending">${tt('nav.pending')}</span>`;
  return `
    <div class="recent-event">
      <h2 class="msc-event-title">${escapeHtml(ev.title)}</h2>
      <p class="info-row"><span class="label">${tt('weepa.label.period')}</span>${escapeHtml(formatYmdHm(ev.startDate))} ~ ${escapeHtml(formatYmdHm(ev.endDate))}</p>
      ${mscSongsHtml(ev.songs)}
      <p class="event-link-wrap">${action}</p>
    </div>
  `;
}

function mscRecentCard(events) {
  if (!events.length) return '';
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">${tt('weepa.recent-events')}</h3>
      <div class="weepa-status-body recent-events">
        ${events.map((ev) => mscRecentEntry(ev)).join('')}
      </div>
    </section>
  `;
}

function mscAdjustingCard() {
  return `
    <section class="weepa-status-card">
      <h3 class="weepa-status-title">${tt('weepa.adjusting')}</h3>
      <div class="weepa-status-body">
        <p>${tt('weepa.adjusting.body')}</p>
      </div>
    </section>
  `;
}

(async () => {
  if (!mscCardsEl) return;
  try {
    const res = await fetch('/api/msc/events/recent?limit=20');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const events = await res.json();
    const now = new Date();

    const active = events.find((ev) => isActive(ev, now));
    const recent = events
      .filter((ev) => isPast(ev, now))
      .sort((a, b) => new Date(b.endDate) - new Date(a.endDate))
      .slice(0, 3);

    const cards = [];
    cards.push(active ? mscActiveCard(active) : mscAdjustingCard());
    if (recent.length) cards.push(mscRecentCard(recent));

    mscCardsEl.innerHTML = cards.join('');
  } catch (err) {
    mscCardsEl.innerHTML = `<p class="error">${tt('common.error.fetch')}: ${escapeHtml(err.message)}</p>`;
  }
})();
