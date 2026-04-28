ensureLoggedIn();
renderAdminNav('#admin-nav');

const EFFECT_OPTIONS = ['NOV', 'ADV', 'EXH', 'MXM', 'INF', 'GRV', 'HVN', 'VVD', 'XCD', 'ULT'];

function effectSelect(name, current) {
    const opts = EFFECT_OPTIONS
        .map((e) => `<option value="${e}"${e === current ? ' selected' : ''}>${e}</option>`)
        .join('');
    return `<select name="${name}">${opts}</select>`;
}

// テーブルディスクリプタ。新規テーブルはここに足すだけ。
// 各カラムのオプション:
//   pk: 'auto'(SERIAL) | 'required'(create時に必須入力) | 'optional'(create時任意・nullで自動採番)
//   computed: true → 結合等の参照専用カラム（編集不可・create時に出さない）
//   type: 'text' | 'number' | 'nullable-number' | 'datetime' | 'effect' | 'status'
//   max / min / required などは create フォーム入力の補助
const TABLES = {
    songs: {
        label: '楽曲 (weepa_songs)',
        endpoint: '/api/admin/db/songs',
        rowKey: (r) => `${r.weepaSongId}`,
        pkPath: (r) => `${r.weepaSongId}`,
        columns: [
            { key: 'weepaSongId', label: 'id', type: 'number', pk: 'auto' },
            { key: 'song', label: 'song', type: 'text', max: 200, required: true },
            { key: 'effect', label: 'effect', type: 'effect', required: true },
            { key: 'xId', label: 'x_id', type: 'text', required: true },
            { key: 'submitName', label: 'submit_name', type: 'text', required: true },
            { key: 'submitReason', label: 'submit_reason', type: 'text' },
            { key: 'submitDate', label: 'submit_date', type: 'datetime' },
            { key: 'approvalStatus', label: 'status', type: 'status' },
        ],
    },
    events: {
        label: 'イベント (weepa_events)',
        endpoint: '/api/admin/db/events',
        rowKey: (r) => `${r.weepaEventId}`,
        pkPath: (r) => `${r.weepaEventId}`,
        columns: [
            { key: 'weepaEventId', label: 'id', type: 'number', pk: 'auto' },
            { key: 'weepaSongId', label: 'song_id', type: 'number', required: true, min: 1 },
            { key: 'song', label: '(曲名)', type: 'text', computed: true },
            { key: 'effect', label: '(譜面)', type: 'effect', computed: true },
            { key: 'startDate', label: 'start_date', type: 'datetime', required: true },
            { key: 'endDate', label: 'end_date', type: 'datetime', required: true },
        ],
    },
    scores: {
        label: 'スコア (weepa_scores)',
        endpoint: '/api/admin/db/scores',
        rowKey: (r) => `${r.weepaEventId}:${r.xId}:${r.submitId}`,
        pkPath: (r) => `${r.weepaEventId}/${encodeURIComponent(r.xId)}/${r.submitId}`,
        columns: [
            { key: 'weepaEventId', label: 'event_id', type: 'number', pk: 'required', min: 1 },
            { key: 'xId', label: 'x_id', type: 'text', pk: 'required' },
            { key: 'submitId', label: 'submit_id', type: 'number', pk: 'optional', min: 1 },
            { key: 'song', label: '(曲名)', type: 'text', computed: true },
            { key: 'effect', label: '(譜面)', type: 'effect', computed: true },
            { key: 'submitName', label: 'submit_name', type: 'text', required: true },
            { key: 'score', label: 'score', type: 'number', required: true, min: 0, max: 10000000 },
            { key: 'exScore', label: 'ex_score', type: 'nullable-number' },
            { key: 'imagePath', label: 'image_path', type: 'text' },
            { key: 'submitDate', label: 'submit_date', type: 'datetime' },
            { key: 'approvalStatus', label: 'status', type: 'status' },
        ],
    },
};

const tableSelect = document.getElementById('table-select');
const listEl = document.getElementById('list');
const listTitleEl = document.getElementById('list-title');
const statusEl = document.getElementById('status');
const createForm = document.getElementById('create-form');

function setStatus(message, type = 'info') {
    statusEl.textContent = message;
    statusEl.className = `result ${type}`;
}

// ============================================
// 描画ユーティリティ
// ============================================

function isInputType(col) {
    return !col.computed;
}

function isReadonlyInEdit(col) {
    return col.computed || !!col.pk;
}

function inCreateForm(col) {
    if (col.computed) return false;
    if (col.pk === 'auto') return false;
    return true;
}

function inputHtml(col, value, opts = {}) {
    const v = value == null ? '' : String(value);
    const required = opts.required && col.required ? ' required' : '';
    const placeholder = opts.placeholder ? ` placeholder="${escapeHtml(opts.placeholder)}"` : '';
    switch (col.type) {
        case 'text':
            return `<input type="text" name="${col.key}" value="${escapeHtml(v)}"${col.max ? ` maxlength="${col.max}"` : ''}${required}${placeholder}>`;
        case 'number':
            return `<input type="number" name="${col.key}" value="${escapeHtml(v)}"${col.min !== undefined ? ` min="${col.min}"` : ''}${col.max !== undefined ? ` max="${col.max}"` : ''}${required}${placeholder}>`;
        case 'nullable-number':
            return `<input type="number" name="${col.key}" value="${escapeHtml(v)}"${col.min !== undefined ? ` min="${col.min}"` : ''} placeholder="${opts.placeholder ?? '(未入力可)'}">`;
        case 'datetime':
            return `<input type="datetime-local" name="${col.key}" value="${isoToLocalInput(value)}"${required}>`;
        case 'effect':
            return effectSelect(col.key, v || EFFECT_OPTIONS[0]);
        case 'status':
            return `<select name="${col.key}">${statusSelectOptions(value)}</select>`;
        default:
            return escapeHtml(v);
    }
}

function displayHtml(col, value) {
    if (col.type === 'effect') {
        return value
            ? `<span class="effect effect-${String(value).toLowerCase()}">${escapeHtml(value)}</span>`
            : '';
    }
    if (col.type === 'datetime') {
        return escapeHtml(formatDateTime(value));
    }
    if (col.type === 'status') {
        return value == null ? '' : `${statusLabel(value)} (${value})`;
    }
    if (col.type === 'nullable-number') {
        return value == null ? '-' : escapeHtml(String(value));
    }
    return escapeHtml(value == null ? '' : String(value));
}

function readInputValue(col, raw) {
    const trimmed = (raw ?? '').trim();
    switch (col.type) {
        case 'number':
        case 'nullable-number':
            return trimmed === '' ? null : Number(trimmed);
        case 'datetime':
            return localInputToIso(trimmed);
        case 'status':
            return trimmed === '' ? null : Number(trimmed);
        default:
            return trimmed === '' ? null : trimmed;
    }
}

// ============================================
// 一覧 / 編集
// ============================================

let currentTable = null;
let currentItems = [];

function rowHtml(table, row) {
    const cells = table.columns.map((col) => `<td>${displayHtml(col, row[col.key])}</td>`).join('');
    const key = table.rowKey(row);
    return `
      <tr data-key="${escapeHtml(key)}">
        ${cells}
        <td class="actions">
          <button type="button" class="action-edit" data-key="${escapeHtml(key)}">編集</button>
          <button type="button" class="action-delete" data-key="${escapeHtml(key)}">削除</button>
        </td>
      </tr>
    `;
}

function editRowHtml(table, row) {
    const cells = table.columns.map((col) => {
        if (isReadonlyInEdit(col)) {
            return `<td>${displayHtml(col, row[col.key])}</td>`;
        }
        return `<td>${inputHtml(col, row[col.key])}</td>`;
    }).join('');
    const key = table.rowKey(row);
    return `
      <tr data-key="${escapeHtml(key)}" class="editing">
        ${cells}
        <td class="actions">
          <button type="button" class="action-save" data-key="${escapeHtml(key)}">保存</button>
          <button type="button" class="action-cancel" data-key="${escapeHtml(key)}">キャンセル</button>
        </td>
      </tr>
    `;
}

function renderList(table, items) {
    if (!items.length) {
        listEl.innerHTML = '<p class="empty">データがありません。</p>';
        return;
    }
    const ths = table.columns.map((c) => `<th>${escapeHtml(c.label)}</th>`).join('');
    const rows = items.map((r) => rowHtml(table, r)).join('');
    listEl.innerHTML = `
      <div class="db-table-wrap">
        <table class="ranking-table db-table">
          <thead><tr>${ths}<th>操作</th></tr></thead>
          <tbody>${rows}</tbody>
        </table>
      </div>
    `;
}

async function loadList() {
    listEl.innerHTML = '<p class="loading">読み込み中...</p>';
    try {
        currentItems = await adminFetch(currentTable.endpoint);
        renderList(currentTable, currentItems);
        setStatus(`${currentItems.length} 件取得`, 'ok');
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        listEl.innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        setStatus(err.message, 'ng');
    }
}

function findByKey(key) {
    return currentItems.find((r) => currentTable.rowKey(r) === key);
}

function trByKey(key) {
    return listEl.querySelector(`tr[data-key="${CSS.escape(key)}"]`);
}

function enterEditMode(key) {
    const row = findByKey(key);
    if (!row) return;
    const tr = trByKey(key);
    if (tr) tr.outerHTML = editRowHtml(currentTable, row);
}

function exitEditMode(key) {
    const row = findByKey(key);
    if (!row) return;
    const tr = trByKey(key);
    if (tr) tr.outerHTML = rowHtml(currentTable, row);
}

function readEditPayload(key) {
    const tr = trByKey(key);
    if (!tr) return null;
    const payload = {};
    for (const col of currentTable.columns) {
        if (isReadonlyInEdit(col)) continue;
        const input = tr.querySelector(`[name="${col.key}"]`);
        if (!input) continue;
        payload[col.key] = readInputValue(col, input.value);
    }
    return payload;
}

async function saveEdit(key) {
    const row = findByKey(key);
    if (!row) return;
    const payload = readEditPayload(key);
    if (!payload) return;
    try {
        await adminFetch(`${currentTable.endpoint}/${currentTable.pkPath(row)}`, {
            method: 'PUT',
            body: JSON.stringify(payload),
        });
        setStatus(`${key} を更新しました`, 'ok');
        await loadList();
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        setStatus(err.message, 'ng');
    }
}

async function deleteRow(key) {
    const row = findByKey(key);
    if (!row) return;
    if (!confirm(`${key} を削除します。よろしいですか？`)) return;
    try {
        await adminFetch(`${currentTable.endpoint}/${currentTable.pkPath(row)}`, { method: 'DELETE' });
        setStatus(`${key} を削除しました`, 'ok');
        await loadList();
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        setStatus(err.message, 'ng');
    }
}

listEl.addEventListener('click', (ev) => {
    const t = ev.target;
    if (!(t instanceof HTMLButtonElement)) return;
    const key = t.dataset.key;
    if (!key) return;
    if (t.classList.contains('action-edit')) enterEditMode(key);
    else if (t.classList.contains('action-cancel')) exitEditMode(key);
    else if (t.classList.contains('action-save')) saveEdit(key);
    else if (t.classList.contains('action-delete')) deleteRow(key);
});

// ============================================
// 新規作成フォーム
// ============================================

function renderCreateForm(table) {
    const fields = table.columns
        .filter(inCreateForm)
        .map((col) => {
            const placeholderText = col.pk === 'optional' ? '(未入力で自動採番)' : undefined;
            const labelMark = col.required && col.pk !== 'optional' ? ' <span class="required">*</span>' : '';
            const note = col.pk === 'optional' ? '<p class="hint">空欄で自動採番</p>' : '';
            return `
              <div class="field">
                <label>${escapeHtml(col.label)}${labelMark}
                  ${inputHtml(col, undefined, { required: col.pk !== 'optional', placeholder: placeholderText })}
                </label>
                ${note}
              </div>
            `;
        })
        .join('');
    createForm.innerHTML = `
      ${fields}
      <div class="form-actions">
        <button type="submit">作成</button>
      </div>
    `;
}

createForm.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    if (!currentTable) return;
    const payload = {};
    for (const col of currentTable.columns) {
        if (!inCreateForm(col)) continue;
        const input = createForm.querySelector(`[name="${col.key}"]`);
        if (!input) continue;
        payload[col.key] = readInputValue(col, input.value);
    }
    try {
        const result = await adminFetch(currentTable.endpoint, {
            method: 'POST',
            body: JSON.stringify(payload),
        });
        setStatus(`作成しました: ${JSON.stringify(result)}`, 'ok');
        createForm.reset();
        await loadList();
    } catch (err) {
        if (err.message === 'AUTH_REDIRECT') return;
        setStatus(err.message, 'ng');
    }
});

// ============================================
// テーブル切替 + 初期化
// ============================================

function selectTable(key) {
    const table = TABLES[key];
    if (!table) return;
    currentTable = table;
    listTitleEl.textContent = `一覧: ${table.label}`;
    renderCreateForm(table);
    loadList();
    if (history.replaceState) {
        const url = new URL(window.location.href);
        url.searchParams.set('table', key);
        history.replaceState(null, '', url);
    }
}

(function initSelect() {
    const opts = Object.entries(TABLES)
        .map(([k, v]) => `<option value="${k}">${escapeHtml(v.label)}</option>`)
        .join('');
    tableSelect.innerHTML = opts;

    const params = new URLSearchParams(window.location.search);
    const initial = params.get('table');
    if (initial && TABLES[initial]) {
        tableSelect.value = initial;
    }

    tableSelect.addEventListener('change', () => selectTable(tableSelect.value));
    selectTable(tableSelect.value);
})();
