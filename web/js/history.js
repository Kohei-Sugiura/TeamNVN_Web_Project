const heldEl = document.getElementById('held');
const unheldEl = document.getElementById('unheld');

function escapeHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s).replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
}

function formatDateOnly(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    return d.toLocaleDateString('ja-JP', {
        year: 'numeric', month: '2-digit', day: '2-digit',
    });
}

function heldRow(s, idx) {
    return `
      <tr>
        <td class="ex">${idx + 1}</td>
        <td class="name">${escapeHtml(s.song)}</td>
        <td class="effect">${escapeHtml(s.effect)}</td>
        <td>${escapeHtml(s.submitName)} (@${escapeHtml(s.xId)})</td>
        <td class="ex">${escapeHtml(formatDateOnly(s.eventStartDate))}</td>
      </tr>
    `;
}

function unheldRow(s) {
    return `
      <tr>
        <td class="name">${escapeHtml(s.song)}</td>
        <td class="effect">${escapeHtml(s.effect)}</td>
        <td>${escapeHtml(s.submitName)} (@${escapeHtml(s.xId)})</td>
        <td class="ex">${escapeHtml(formatDateOnly(s.submitDate))}</td>
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
      <table class="ranking-table">
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
      <table class="ranking-table">
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
