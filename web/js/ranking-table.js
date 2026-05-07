// 個別イベントのランキングテーブル共通レンダラ。
// /weepa/ranking/ (公開) と /admin/preview/ (管理者) で同じ見た目を共有する。
// グローバル window.renderRankingTable(targetEl, entries) を提供。

(function () {
  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
  }
  function formatScore(n) {
    return Number(n).toLocaleString('en-US');
  }
  function formatDate(iso) {
    const d = new Date(iso);
    return d.toLocaleString('ja-JP', {
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit',
    });
  }

  // 各列の値抽出と既定ソート方向。同値時の tie-break は entries の rank 値を使う
  // (rank は API 側で score DESC, ex_score DESC, submit_date ASC で計算済)。
  const SORTABLE_COLUMNS = {
    score:          { defaultDir: 'desc', getValue: (e) => e.score },
    exScore:        { defaultDir: 'desc', getValue: (e) => (e.exScore == null ? -Infinity : e.exScore) },
    lastSubmitDate: { defaultDir: 'desc', getValue: (e) => new Date(e.lastSubmitDate).getTime() },
    updateCount:    { defaultDir: 'desc', getValue: (e) => e.updateCount },
  };

  function compareEntries(a, b, key, dir) {
    const col = SORTABLE_COLUMNS[key];
    const av = col.getValue(a);
    const bv = col.getValue(b);
    let cmp = 0;
    if (av < bv) cmp = -1;
    else if (av > bv) cmp = 1;
    if (cmp === 0) cmp = a.rank - b.rank;
    return dir === 'asc' ? cmp : -cmp;
  }

  window.renderRankingTable = function (targetEl, entries) {
    if (!entries || entries.length === 0) {
      targetEl.innerHTML = '<p class="empty">まだランキングがありません。</p>';
      return;
    }
    // デフォルト: EXスコア → スコア → 投稿日 (rank tie-break が後段を担保)
    const state = { sortKey: 'exScore', sortDir: 'desc' };

    function indicator(k) {
      let arrow = '';
      if (state.sortKey === k) {
        arrow = state.sortDir === 'asc' ? '▲' : '▼';
      }
      return `<span class="sort-arrow">${arrow}</span>`;
    }

    function draw() {
      const sorted = [...entries].sort((a, b) =>
        compareEntries(a, b, state.sortKey, state.sortDir));
      const rows = sorted.map((e) => {
        const scoreClass = e.score === 10000000 ? 'score score-puc' : 'score';
        return `
        <tr>
          <td class="name">${escapeHtml(e.submitName)}</td>
          <td class="${scoreClass}">${formatScore(e.score)}</td>
          <td class="ex">${e.exScore != null ? formatScore(e.exScore) : '-'}</td>
          <td class="last-submit">${escapeHtml(formatDate(e.lastSubmitDate))}</td>
          <td class="update-count">${e.updateCount}</td>
        </tr>
      `;
      }).join('');
      targetEl.innerHTML = `
        <table class="ranking-table ranking-detail-table sortable-table">
          <thead>
            <tr>
              <th>投稿者名</th>
              <th class="sortable" data-sort="score">スコア${indicator('score')}</th>
              <th class="sortable" data-sort="exScore">EXスコア${indicator('exScore')}</th>
              <th class="sortable" data-sort="lastSubmitDate">最終投稿日時${indicator('lastSubmitDate')}</th>
              <th class="sortable" data-sort="updateCount">スコア更新回数${indicator('updateCount')}</th>
            </tr>
          </thead>
          <tbody>${rows}</tbody>
        </table>
      `;
      targetEl.querySelectorAll('th.sortable').forEach((th) => {
        th.addEventListener('click', () => {
          const k = th.dataset.sort;
          if (!SORTABLE_COLUMNS[k]) return;
          if (state.sortKey === k) {
            state.sortDir = state.sortDir === 'asc' ? 'desc' : 'asc';
          } else {
            state.sortKey = k;
            state.sortDir = SORTABLE_COLUMNS[k].defaultDir;
          }
          draw();
        });
      });
    }
    draw();
  };
})();
