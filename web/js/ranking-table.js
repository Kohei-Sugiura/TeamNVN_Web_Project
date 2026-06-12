// 個別イベントのランキング共通レンダラ (部門タブ切替)。
// /weepa/ranking/ (公開) と /admin/preview/ (管理者) で同じ見た目を共有する。
// グローバル window.renderRankingTable(targetEl, entries, options) を提供。
// options.exScoreMax: 楽曲の EX SCORE MAX 値 (null なら従来表記)。
//
// 部門 (entries[].tier, 最後の投稿の部門):
//   1=ストイック部門 : EXスコア降順。通常スコア列は表示しない
//   2=チャレンジ部門 : スコア降順・提出日昇順。EXスコア列は表示しない
//   3=カジュアル部門 : 順位なし。提出順 + 自己べ更新。EXスコア列は表示しない

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

  const tt = (k) => (window.i18n && window.i18n.t) ? window.i18n.t(k) : k;

  // 列定義: ラベル / ソート可否 / 値抽出 / セル描画
  const COLUMN_DEFS = {
    score: {
      labelKey: 'ranking.col.score',
      sortable: true,
      defaultDir: 'desc',
      getValue: (e) => e.score,
      cell: (e) => `<td class="${e.score === 10000000 ? 'score score-puc' : 'score'}">${formatScore(e.score)}</td>`,
    },
    exScore: {
      labelKey: 'ranking.col.exscore',
      sortable: true,
      defaultDir: 'desc',
      getValue: (e) => (e.exScore == null ? -Infinity : e.exScore),
      cell: (e, exScoreMax) => `<td class="ex">${formatExScoreCell(e.exScore, exScoreMax)}</td>`,
    },
    improve: {
      labelKey: 'ranking.col.improve',
      sortable: false,
      cell: (e) => {
        const improve = e.score - (e.firstScore ?? e.score);
        return improve > 0
          ? `<td class="improve">+${formatScore(improve)}</td>`
          : '<td class="improve zero">-</td>';
      },
    },
    lastSubmitDate: {
      labelKey: 'ranking.col.last-submit',
      sortable: true,
      defaultDir: 'desc',
      getValue: (e) => new Date(e.lastSubmitDate).getTime(),
      cell: (e) => `<td class="last-submit">${escapeHtml(formatDate(e.lastSubmitDate))}</td>`,
    },
    updateCount: {
      labelKey: 'ranking.col.update-count',
      sortable: true,
      defaultDir: 'desc',
      getValue: (e) => e.updateCount,
      cell: (e) => `<td class="update-count">${e.updateCount}</td>`,
    },
  };

  function rowsHtml(entries, cols, exScoreMax) {
    return entries.map((e) => `
      <tr>
        <td class="name">${escapeHtml(e.submitName)}</td>
        ${cols.map((k) => COLUMN_DEFS[k].cell(e, exScoreMax)).join('')}
      </tr>
    `).join('');
  }

  // ソート可能テーブル (ストイック / チャレンジ用)。
  // opts.cols: 表示列, opts.defaultKey: 初期ソート列, opts.tieBreak: 同値時の比較関数
  function buildSortableTable(container, entries, opts) {
    const { cols, exScoreMax, tieBreak } = opts;
    const state = { sortKey: opts.defaultKey, sortDir: 'desc' };

    function compareEntries(a, b, key, dir) {
      const col = COLUMN_DEFS[key];
      const av = col.getValue(a);
      const bv = col.getValue(b);
      let cmp = 0;
      if (av < bv) cmp = -1;
      else if (av > bv) cmp = 1;
      if (cmp === 0) return tieBreak(a, b);
      return dir === 'asc' ? cmp : -cmp;
    }

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
      const ths = cols.map((k) => {
        const def = COLUMN_DEFS[k];
        return def.sortable
          ? `<th class="sortable" data-sort="${k}">${tt(def.labelKey)}${indicator(k)}</th>`
          : `<th>${tt(def.labelKey)}</th>`;
      }).join('');
      container.innerHTML = `
        <table class="ranking-table ranking-detail-table sortable-table">
          <thead>
            <tr>
              <th>${tt('ranking.col.name')}</th>
              ${ths}
            </tr>
          </thead>
          <tbody>${rowsHtml(sorted, cols, exScoreMax)}</tbody>
        </table>
      `;
      container.querySelectorAll('th.sortable').forEach((th) => {
        th.addEventListener('click', () => {
          const k = th.dataset.sort;
          if (!COLUMN_DEFS[k] || !COLUMN_DEFS[k].sortable) return;
          if (state.sortKey === k) {
            state.sortDir = state.sortDir === 'asc' ? 'desc' : 'asc';
          } else {
            state.sortKey = k;
            state.sortDir = COLUMN_DEFS[k].defaultDir;
          }
          draw();
        });
      });
    }
    draw();
  }

  // カジュアル用テーブル: 順位なし・提出順 (最初の投稿日時昇順) 固定
  function buildCasualTable(container, entries, cols, exScoreMax) {
    const sorted = [...entries].sort((a, b) =>
      new Date(a.firstSubmitDate) - new Date(b.firstSubmitDate));
    const ths = cols.map((k) => `<th>${tt(COLUMN_DEFS[k].labelKey)}</th>`).join('');
    container.innerHTML = `
      <table class="ranking-table ranking-detail-table">
        <thead>
          <tr>
            <th>${tt('ranking.col.name')}</th>
            ${ths}
          </tr>
        </thead>
        <tbody>${rowsHtml(sorted, cols, exScoreMax)}</tbody>
      </table>
    `;
  }

  const TIERS = [
    {
      tier: 1,
      nameKey: 'weepa.tier.1.name',
      // EXスコア降順。同値は スコア降順 → ベスト到達日昇順
      build: (body, subset, exScoreMax) => buildSortableTable(body, subset, {
        cols: ['exScore', 'lastSubmitDate', 'updateCount'],
        defaultKey: 'exScore',
        exScoreMax,
        tieBreak: (a, b) => (b.score - a.score) || (new Date(a.submitDate) - new Date(b.submitDate)),
      }),
    },
    {
      tier: 2,
      nameKey: 'weepa.tier.2.name',
      // スコア降順。同値は提出日 (ベスト到達日) 昇順
      build: (body, subset, exScoreMax) => buildSortableTable(body, subset, {
        cols: ['score', 'lastSubmitDate', 'updateCount'],
        defaultKey: 'score',
        exScoreMax,
        tieBreak: (a, b) => new Date(a.submitDate) - new Date(b.submitDate),
      }),
    },
    {
      tier: 3,
      nameKey: 'weepa.tier.3.name',
      noteKey: 'ranking.tier3.note',
      build: (body, subset, exScoreMax) =>
        buildCasualTable(body, subset, ['score', 'improve', 'lastSubmitDate', 'updateCount'], exScoreMax),
    },
  ];

  function renderTierInto(panel, cfg, entries, exScoreMax) {
    const body = document.createElement('div');
    panel.appendChild(body);
    const subset = entries.filter((e) => (e.tier ?? 1) === cfg.tier);
    if (!subset.length) {
      body.innerHTML = `<p class="empty">${tt('ranking.tier.empty')}</p>`;
    } else {
      cfg.build(body, subset, exScoreMax);
    }
    if (cfg.noteKey) {
      const note = document.createElement('p');
      note.className = 'tier-note';
      note.textContent = tt(cfg.noteKey);
      panel.appendChild(note);
    }
  }

  // 部門タブ + 選択中部門のテーブル 1 つを表示。
  // options.tierView (1-3) 指定時はタブ無しでその部門だけを見出し付きで表示
  // (Discord スクショ・部門別発表用。/weepa/ranking/?tier=N から渡る)。
  window.renderRankingTable = function (targetEl, entries, options) {
    if (!entries || entries.length === 0) {
      targetEl.innerHTML = `<p class="empty">${tt('common.empty.ranking')}</p>`;
      return;
    }
    const exScoreMax = options && Number.isFinite(options.exScoreMax) ? options.exScoreMax : null;
    targetEl.innerHTML = '';

    const tierView = options && options.tierView;
    if (tierView >= 1 && tierView <= 3) {
      const cfg = TIERS.find((t) => t.tier === tierView);
      const heading = document.createElement('h3');
      heading.className = 'tier-heading';
      heading.textContent = tt(cfg.nameKey);
      targetEl.appendChild(heading);
      const panel = document.createElement('div');
      panel.className = 'tier-panel';
      targetEl.appendChild(panel);
      renderTierInto(panel, cfg, entries, exScoreMax);
      return;
    }

    const tabbar = document.createElement('div');
    tabbar.className = 'inner-tabs tier-tabs';
    const panel = document.createElement('div');
    panel.className = 'tier-panel';

    const buttons = TIERS.map((cfg, idx) => {
      const b = document.createElement('button');
      b.type = 'button';
      b.textContent = tt(cfg.nameKey);
      b.addEventListener('click', () => select(idx));
      tabbar.appendChild(b);
      return b;
    });

    function select(idx) {
      buttons.forEach((b, i) => b.classList.toggle('active', i === idx));
      panel.innerHTML = '';
      renderTierInto(panel, TIERS[idx], entries, exScoreMax);
    }

    targetEl.appendChild(tabbar);
    targetEl.appendChild(panel);
    select(0);
  };
})();
