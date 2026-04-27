const heldEl = document.getElementById('held');
const unheldEl = document.getElementById('unheld');

function escapeHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s).replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
}

function formatYmd(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${y}/${m}/${day}`;
}

function formatPeriod(startIso, endIso) {
    const s = formatYmd(startIso);
    const e = formatYmd(endIso);
    if (!s && !e) return '';
    return `${s}~${e}`;
}

function heldRow(s, idx) {
    const period = formatPeriod(s.eventStartDate, s.eventEndDate);
    const isFinished = s.eventEndDate && new Date(s.eventEndDate) < new Date();
    const songCell = isFinished && s.weepaEventId
        ? `<a href="/ranking.html?eventId=${encodeURIComponent(s.weepaEventId)}">${escapeHtml(s.song)}</a>`
        : escapeHtml(s.song);
    return `
      <tr>
        <td>${idx + 1}</td>
        <td>${songCell}</td>
        <td>${escapeHtml(s.effect)}</td>
        <td>${escapeHtml(s.submitName)} (@${escapeHtml(s.xId)})</td>
        <td>${escapeHtml(period)}</td>
      </tr>
    `;
}

function unheldRow(s) {
    return `
      <tr>
        <td>${escapeHtml(s.song)}</td>
        <td>${escapeHtml(s.effect)}</td>
        <td>${escapeHtml(s.submitName)} (@${escapeHtml(s.xId)})</td>
        <td>${escapeHtml(formatYmd(s.submitDate))}</td>
      </tr>
    `;
}

function renderHeld(items) {
    if (!items.length) {
        heldEl.innerHTML = '<p class="empty">まだ開催履歴はありません。</p>';
        return;
    }
    const rows = items.map(heldRow).join('');
    heldEl.innerHTML = `
      <table class="ranking-table history-table">
        <thead>
          <tr><th>#</th><th>楽曲</th><th>譜面種</th><th>提案者</th><th>開催日</th></tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;
}

function renderUnheld(items) {
    if (!items.length) {
        unheldEl.innerHTML = '<p class="empty">未開催の楽曲はありません。</p>';
        return;
    }
    const rows = items.map(unheldRow).join('');
    unheldEl.innerHTML = `
      <table class="ranking-table history-table">
        <thead>
          <tr><th>楽曲</th><th>譜面種</th><th>提案者</th><th>提案日</th></tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;
}

(async () => {
    try {
        const res = await fetch('/api/songs');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const items = await res.json();

        const held = items
            .filter((s) => s.approvalStatus === 2 && s.eventStartDate)
            .sort((a, b) => new Date(a.eventStartDate) - new Date(b.eventStartDate));

        const unheld = items
            .filter((s) => s.approvalStatus === 0 || s.approvalStatus === 1)
            .sort((a, b) => new Date(a.submitDate) - new Date(b.submitDate));

        renderHeld(held);
        renderUnheld(unheld);
    } catch (err) {
        heldEl.innerHTML = `<p class="error">取得に失敗しました: ${escapeHtml(err.message)}</p>`;
        unheldEl.innerHTML = '';
    }
})();
