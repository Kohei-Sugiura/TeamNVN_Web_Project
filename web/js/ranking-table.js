// 個別イベントのランキングテーブル共通レンダラ。
// /weepa/ranking/ (公開) と /admin/preview/ (管理者) で同じ見た目を共有する。
// グローバル window.renderRankingTable(targetEl, entries, options) を提供。
// options.exScoreMax: 楽曲の EX SCORE MAX 値 (null なら従来表記)。

(function () {
  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
  }
  function formatScore(n) {
    return Number(n).toLocaleString('en-US');
  }
  // EX SCORE 表示:
  // - max が無い: 数値のみ (従来通り)
  // - 98% 以上: "5950 (MAX -50)"
  // - 98% 未満: "5857 (97.6%)" (小数点 1 桁まで切り捨て)
  function formatExScoreCell(ex, max) {
    if (ex == null) return '-';
    if (max == null || max <= 0) return formatScore(ex);
    const ratio = ex / max;
    if (ratio >= 0.98) {
      const diff = max - ex;
      return `${formatScore(ex)} <span class="ex-max-tag">(MAX -${diff})</span>`;
    }
    const pct = Math.floor(ratio * 1000) / 10; // 小数 1 桁切り捨て
    return `${formatScore(ex)} <span class="ex-max-tag">(${pct.toFixed(1)}%)</span>`;
  }
  // 閲覧者のタイムゾーンや実行環境 (Discord スクショは UTC コンテナ内で描画) に
  // 依存しないよう、表示は JST 固定。
  function formatDate(iso) {
    const d = new Date(iso);
    return d.toLocaleString('ja-JP', {
      timeZone: 'Asia/Tokyo',
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

  const tt = (k) => (window.i18n && window.i18n.t) ? window.i18n.t(k) : k;

  window.renderRankingTable = function (targetEl, entries, options) {
    if (!entries || entries.length === 0) {
      targetEl.innerHTML = `<p class="empty">${tt('common.empty.ranking')}</p>`;
      return;
    }
    const exScoreMax = options && Number.isFinite(options.exScoreMax) ? options.exScoreMax : null;
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
          <td class="ex">${formatExScoreCell(e.exScore, exScoreMax)}</td>
          <td class="last-submit">${escapeHtml(formatDate(e.lastSubmitDate))}</td>
          <td class="update-count">${e.updateCount}</td>
        </tr>
      `;
      }).join('');
      targetEl.innerHTML = `
        <table class="ranking-table ranking-detail-table sortable-table">
          <thead>
            <tr>
              <th>${tt('ranking.col.name')}</th>
              <th class="sortable" data-sort="score">${tt('ranking.col.score')}${indicator('score')}</th>
              <th class="sortable" data-sort="exScore">${tt('ranking.col.exscore')}${indicator('exScore')}</th>
              <th class="sortable" data-sort="lastSubmitDate">${tt('ranking.col.last-submit')}${indicator('lastSubmitDate')}</th>
              <th class="sortable" data-sort="updateCount">${tt('ranking.col.update-count')}${indicator('updateCount')}</th>
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
