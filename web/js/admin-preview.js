ensureLoggedIn();
renderAdminNav('#admin-nav');

const eventSection = document.getElementById('event-section');
const eventDisplay = document.getElementById('event-display');
const publishStatusEl = document.getElementById('publish-status');
const rankingSection = document.getElementById('ranking-section');
const rankingDisplay = document.getElementById('ranking-display');

function formatScore(n) {
    return Number(n).toLocaleString('en-US');
}

function formatYmd(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    return `${d.getFullYear()}/${String(d.getMonth() + 1).padStart(2, '0')}/${String(d.getDate()).padStart(2, '0')}`;
}

function renderEventHeader(ev) {
    eventSection.hidden = false;
    const published = !!ev.talliedAt;
    eventDisplay.innerHTML = `
      <div class="event-title">
        <span class="song">${escapeHtml(ev.song)}</span>
        <span class="effect effect-${(ev.effect || '').toLowerCase()}">${escapeHtml(ev.effect)}</span>
      </div>
      <p class="period">
        開催期間: ${escapeHtml(formatYmd(ev.startDate))} ~ ${escapeHtml(formatYmd(ev.endDate))}
      </p>
    `;
    publishStatusEl.textContent = published
        ? `公開中 (tallied_at: ${ev.talliedAt})`
        : '※ このイベントは未公開です。一般ユーザーには表示されません。';
    publishStatusEl.className = `result ${published ? 'ok' : 'info'}`;
}

function renderRanking(entries) {
    rankingSection.hidden = false;
    if (!entries || entries.length === 0) {
        rankingDisplay.innerHTML = '<p class="empty">ランキング対象のスコアがありません。</p>';
        return;
    }
    const rows = entries.map((e) => `
      <tr>
        <td class="ex">${e.rank}</td>
        <td class="name">${escapeHtml(e.submitName)}</td>
        <td class="score">${formatScore(e.score)}</td>
        <td class="ex">${e.exScore != null ? formatScore(e.exScore) : '-'}</td>
      </tr>
    `).join('');
    rankingDisplay.innerHTML = `
      <table class="ranking-table">
        <thead>
          <tr><th>#</th><th>投稿者名</th><th>スコア</th><th>EXスコア</th></tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;
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
            adminFetch(`/api/events/${eventId}`),
            adminFetch(`/api/events/${eventId}/ranking`),
        ]);
        if (!ev) {
            showError('指定されたイベントが見つかりません。');
            return;
        }
        renderEventHeader(ev);
        renderRanking(ranking);
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        showError(`取得に失敗しました: ${err.message}`);
    }
})();
