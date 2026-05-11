ensureLoggedIn();
renderAdminNav('#admin-nav');

const eventSection = document.getElementById('event-section');
const eventDisplay = document.getElementById('event-display');
const publishStatusEl = document.getElementById('publish-status');
const rankingSection = document.getElementById('ranking-section');
const rankingDisplay = document.getElementById('ranking-display');

function formatYmd(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    return `${d.getFullYear()}/${String(d.getMonth() + 1).padStart(2, '0')}/${String(d.getDate()).padStart(2, '0')}`;
}

function renderEventHeader(ev) {
    eventSection.hidden = false;
    const songLines = (ev.songs || [])
        .map((s) => `${s.songOrder}. ${escapeHtml(s.song)} <span class="effect effect-${(s.effect || '').toLowerCase()}">${escapeHtml(s.effect)}</span>`)
        .join('<br>');
    eventDisplay.innerHTML = `
      <h2 class="msc-event-title">${escapeHtml(ev.title)}</h2>
      <p class="info-row"><span class="label">開催期間</span>${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}</p>
      <p class="info-row"><span class="label">課題曲</span>${songLines}</p>
      ${ev.description ? `<p class="info-row"><span class="label">説明</span>${escapeHtml(ev.description)}</p>` : ''}
    `;
    const published = !!ev.talliedAt;
    publishStatusEl.textContent = published
        ? `公開中 (tallied_at: ${ev.talliedAt})`
        : '※ このイベントは未公開です。一般ユーザーには表示されません。';
    publishStatusEl.className = `result ${published ? 'ok' : 'info'}`;
}

function showError(message) {
    eventSection.hidden = false;
    eventDisplay.innerHTML = `<p class="error">${escapeHtml(message)}</p>`;
}

(async () => {
    const params = new URLSearchParams(window.location.search);
    const eventId = params.get('eventId');
    if (!eventId || !/^\d+$/.test(eventId)) {
        showError('eventId が指定されていません。');
        return;
    }
    try {
        const [ev, ranking] = await Promise.all([
            adminFetch(`/api/msc/events/${eventId}`),
            adminFetch(`/api/msc/events/${eventId}/ranking`),
        ]);
        if (!ev) {
            showError('指定されたイベントが見つかりません。');
            return;
        }
        renderEventHeader(ev);
        rankingSection.hidden = false;
        renderMscRankingTable(rankingDisplay, ranking, ev.songs || []);
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        showError(`取得に失敗しました: ${err.message}`);
    }
})();
