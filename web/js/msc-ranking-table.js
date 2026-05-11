// MSC ランキングテーブル共通レンダラ。
// /msc/ranking/ (公開) と /admin/msc/preview/ (管理者プレビュー) で同じ見た目を共有する。
// グローバル window.renderMscRankingTable(targetEl, entries, songs) を提供。
// songs は [{songOrder, song, effect}] の配列 (列ヘッダに使う)。

(function () {
  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
  }
  function formatScore(n) {
    return Number(n).toLocaleString('en-US');
  }
  function formatDateTime(iso) {
    const d = new Date(iso);
    return d.toLocaleString('ja-JP', {
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit',
    });
  }

  // tie-break は API 側 rank (合計 DESC, submit_date ASC で算出済) を使う
  function compareEntries(a, b, getValue, dir, defaultCmp) {
    const av = getValue(a);
    const bv = getValue(b);
    let cmp = 0;
    if (av < bv) cmp = -1;
    else if (av > bv) cmp = 1;
    if (cmp === 0) return defaultCmp(a, b);
    return dir === 'asc' ? cmp : -cmp;
  }

  window.renderMscRankingTable = function (targetEl, entries, songs) {
    if (!entries || entries.length === 0) {
      targetEl.innerHTML = '<p class="empty">まだランキングがありません。</p>';
      return;
    }
    const songList = [...(songs || [])].sort((a, b) => a.songOrder - b.songOrder);

    // sortKey は 'name' | 'total' | 'lastSubmitDate' | 'submissionCount' | `song${songOrder}`
    const sortableColumns = {
      name:            { defaultDir: 'asc',  getValue: (e) => e.submitName },
      total:           { defaultDir: 'desc', getValue: (e) => e.totalScore },
      lastSubmitDate:  { defaultDir: 'desc', getValue: (e) => new Date(e.lastSubmitDate).getTime() },
      submissionCount: { defaultDir: 'desc', getValue: (e) => e.submissionCount },
    };
    songList.forEach((s) => {
      sortableColumns[`song${s.songOrder}`] = {
        defaultDir: 'desc',
        getValue: (e) => {
          const found = (e.perSong || []).find((p) => p.songOrder === s.songOrder);
          return found ? found.score : -Infinity;
        },
      };
    });

    const state = { sortKey: 'total', sortDir: 'desc' };
    const defaultCmp = (a, b) => (a.rank ?? 0) - (b.rank ?? 0);

    function indicator(k) {
      let arrow = '';
      if (state.sortKey === k) {
        arrow = state.sortDir === 'asc' ? '▲' : '▼';
      }
      return `<span class="sort-arrow">${arrow}</span>`;
    }

    function draw() {
      const col = sortableColumns[state.sortKey];
      const sorted = [...entries].sort((a, b) =>
        compareEntries(a, b, col.getValue, state.sortDir, defaultCmp));

      const songHeaders = songList.map((s) =>
        `<th class="sortable" data-sort="song${s.songOrder}">${escapeHtml(s.song)} [${escapeHtml(s.effect)}]${indicator(`song${s.songOrder}`)}</th>`
      ).join('');

      const rows = sorted.map((e) => {
        const perSongMap = new Map((e.perSong || []).map((p) => [p.songOrder, p.score]));
        const perSongCells = songList.map((s) => {
          const val = perSongMap.get(s.songOrder);
          return `<td class="score">${val != null ? formatScore(val) : '-'}</td>`;
        }).join('');
        return `
          <tr>
            <td class="name">${escapeHtml(e.submitName)}</td>
            ${perSongCells}
            <td class="score">${formatScore(e.totalScore)}</td>
            <td class="last-submit">${escapeHtml(formatDateTime(e.lastSubmitDate))}</td>
            <td class="update-count">${e.submissionCount}</td>
          </tr>
        `;
      }).join('');

      targetEl.innerHTML = `
        <div class="db-table-wrap">
          <table class="ranking-table ranking-detail-table sortable-table">
            <thead>
              <tr>
                <th class="sortable" data-sort="name">投稿者名${indicator('name')}</th>
                ${songHeaders}
                <th class="sortable" data-sort="total">合計スコア${indicator('total')}</th>
                <th class="sortable" data-sort="lastSubmitDate">最終投稿日時${indicator('lastSubmitDate')}</th>
                <th class="sortable" data-sort="submissionCount">投稿回数${indicator('submissionCount')}</th>
              </tr>
            </thead>
            <tbody>${rows}</tbody>
          </table>
        </div>
      `;

      targetEl.querySelectorAll('th.sortable').forEach((th) => {
        th.addEventListener('click', () => {
          const k = th.dataset.sort;
          if (!sortableColumns[k]) return;
          if (state.sortKey === k) {
            state.sortDir = state.sortDir === 'asc' ? 'desc' : 'asc';
          } else {
            state.sortKey = k;
            state.sortDir = sortableColumns[k].defaultDir;
          }
          draw();
        });
      });
    }

    draw();
  };
})();
