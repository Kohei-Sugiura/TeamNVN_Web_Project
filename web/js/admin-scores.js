ensureLoggedIn();
renderAdminNav('#admin-nav');

const listEl = document.getElementById('list');
const statusEl = document.getElementById('status');
const createForm = document.getElementById('create-form');

function setStatus(message, type = 'info') {
    statusEl.textContent = message;
    statusEl.className = `result ${type}`;
}

function pkKey(s) {
    return `${s.weepaEventId}:${s.xId}:${s.submitId}`;
}

function pkPath(s) {
    return `/api/admin/db/scores/${s.weepaEventId}/${encodeURIComponent(s.xId)}/${s.submitId}`;
}

function imagePathCell(path) {
    if (!path) return '<em>(なし)</em>';
    return `<a href="${encodeURI(path)}" target="_blank" rel="noopener noreferrer">${escapeHtml(path)}</a>`;
}

function rowHtml(s) {
    const key = pkKey(s);
    const songLabel = s.song ? `${s.song} [${s.effect}]` : '(-)';
    return `
      <tr data-key="${escapeHtml(key)}">
        <td>${s.weepaEventId}<br><span class="dim">${escapeHtml(songLabel)}</span></td>
        <td>${escapeHtml(s.xId)}</td>
        <td>${s.submitId}</td>
        <td>${escapeHtml(s.submitName)}</td>
        <td class="score">${Number(s.score).toLocaleString('en-US')}</td>
        <td class="ex">${s.exScore == null ? '-' : Number(s.exScore).toLocaleString('en-US')}</td>
        <td>${imagePathCell(s.imagePath)}</td>
        <td>${escapeHtml(formatDateTime(s.submitDate))}</td>
        <td>${statusLabel(s.approvalStatus, false)} (${s.approvalStatus})</td>
        <td class="actions">
          <button class="action-edit" data-key="${escapeHtml(key)}">編集</button>
          <button class="action-delete" data-key="${escapeHtml(key)}">削除</button>
        </td>
      </tr>
    `;
}

function editRowHtml(s) {
    const key = pkKey(s);
    return `
      <tr data-key="${escapeHtml(key)}" class="editing">
        <td>${s.weepaEventId}</td>
        <td>${escapeHtml(s.xId)}</td>
        <td>${s.submitId}</td>
        <td><input name="submitName" value="${escapeHtml(s.submitName)}"></td>
        <td><input type="number" name="score" value="${s.score}" min="0" max="10000000"></td>
        <td><input type="number" name="exScore" value="${s.exScore ?? ''}" min="0" placeholder="未入力"></td>
        <td><input name="imagePath" value="${escapeHtml(s.imagePath ?? '')}"></td>
        <td><input type="datetime-local" name="submitDate" value="${isoToLocalInput(s.submitDate)}"></td>
        <td><select name="approvalStatus">${statusSelectOptions(s.approvalStatus, false)}</select></td>
        <td class="actions">
          <button class="action-save" data-key="${escapeHtml(key)}">保存</button>
          <button class="action-cancel" data-key="${escapeHtml(key)}">キャンセル</button>
        </td>
      </tr>
    `;
}

function renderList(list) {
    if (!list.length) {
        listEl.innerHTML = '<p class="empty">データがありません。</p>';
        return;
    }
    listEl.innerHTML = `
      <table class="ranking-table db-table">
        <thead>
          <tr>
            <th>event_id</th><th>x_id</th><th>submit_id</th><th>submit_name</th>
            <th>score</th><th>ex_score</th><th>image_path</th><th>submit_date</th><th>status</th><th>操作</th>
          </tr>
        </thead>
        <tbody>${list.map(rowHtml).join('')}</tbody>
      </table>
    `;
}

let items = [];

async function load() {
    listEl.innerHTML = '<p class="loading">読み込み中...</p>';
    try {
        items = await adminFetch('/api/admin/db/scores');
        renderList(items);
        setStatus(`${items.length} 件取得`, 'ok');
    } catch (err) {
        listEl.innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        setStatus(err.message, 'ng');
    }
}

function findByKey(key) {
    return items.find((s) => pkKey(s) === key);
}

function enterEditMode(key) {
    const s = findByKey(key);
    if (!s) return;
    const tr = listEl.querySelector(`tr[data-key="${CSS.escape(key)}"]`);
    if (tr) tr.outerHTML = editRowHtml(s);
}

function exitEditMode(key) {
    const s = findByKey(key);
    if (!s) return;
    const tr = listEl.querySelector(`tr[data-key="${CSS.escape(key)}"]`);
    if (tr) tr.outerHTML = rowHtml(s);
}

function readEditRow(key) {
    const tr = listEl.querySelector(`tr[data-key="${CSS.escape(key)}"]`);
    if (!tr) return null;
    const get = (name) => tr.querySelector(`[name="${name}"]`).value;
    const ex = get('exScore').trim();
    return {
        submitName: get('submitName'),
        score: Number(get('score')),
        exScore: ex === '' ? null : Number(ex),
        imagePath: get('imagePath'),
        submitDate: localInputToIso(get('submitDate')),
        approvalStatus: Number(get('approvalStatus')),
    };
}

async function saveEdit(key) {
    const s = findByKey(key);
    if (!s) return;
    const payload = readEditRow(key);
    if (!payload) return;
    try {
        await adminFetch(pkPath(s), {
            method: 'PUT',
            body: JSON.stringify(payload),
        });
        setStatus(`${key} 更新しました`, 'ok');
        await load();
    } catch (err) {
        setStatus(err.message, 'ng');
    }
}

async function deleteRow(key) {
    const s = findByKey(key);
    if (!s) return;
    if (!confirm(`${key} を削除します。よろしいですか？`)) return;
    try {
        await adminFetch(pkPath(s), { method: 'DELETE' });
        setStatus(`${key} 削除しました`, 'ok');
        await load();
    } catch (err) {
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

createForm.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const fd = new FormData(createForm);
    const submitIdRaw = (fd.get('submitId') || '').toString().trim();
    const exRaw = (fd.get('exScore') || '').toString().trim();
    const payload = {
        weepaEventId: Number(fd.get('weepaEventId')),
        xId: fd.get('xId'),
        submitId: submitIdRaw === '' ? null : Number(submitIdRaw),
        submitName: fd.get('submitName'),
        score: Number(fd.get('score')),
        exScore: exRaw === '' ? null : Number(exRaw),
        imagePath: fd.get('imagePath') || '',
        approvalStatus: Number(fd.get('approvalStatus')),
    };
    try {
        const result = await adminFetch('/api/admin/db/scores', {
            method: 'POST',
            body: JSON.stringify(payload),
        });
        setStatus(`event=${result.weepaEventId} x_id=${result.xId} submit_id=${result.submitId} を作成しました`, 'ok');
        createForm.reset();
        await load();
    } catch (err) {
        setStatus(err.message, 'ng');
    }
});

load();
