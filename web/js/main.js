const announcementList = document.getElementById('announcement-list');

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

function renderAnnouncements(items) {
  if (items.length === 0) {
    announcementList.innerHTML = '<p class="empty">現在、開催中のイベントはありません。</p>';
    return;
  }
  announcementList.innerHTML = items.join('');
}

function weepaAnnouncement(ev) {
  return `
    <article class="announcement">
      <header class="announcement-head">
        <span class="badge">WeePA</span>
        <span class="announcement-title">現在開催中</span>
      </header>
      <div class="announcement-body">
        <div class="event-title">
          <span class="song">${escapeHtml(ev.song)}</span>
          <span class="effect">${escapeHtml(ev.effect)}</span>
        </div>
        <p class="period">
          ${escapeHtml(formatDate(ev.startDate))} ～ ${escapeHtml(formatDate(ev.endDate))}
        </p>
      </div>
    </article>
  `;
}

(async () => {
  const items = [];
  try {
    const res = await fetch('/api/events/current');
    if (res.ok) {
      const { event: ev } = await res.json();
      if (ev) items.push(weepaAnnouncement(ev));
    }
  } catch (err) {
    announcementList.innerHTML =
      `<p class="error">お知らせの取得に失敗しました: ${escapeHtml(err.message)}</p>`;
    return;
  }

  renderAnnouncements(items);
})();
