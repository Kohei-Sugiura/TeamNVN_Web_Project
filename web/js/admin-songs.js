ensureLoggedIn();
renderAdminNav('#admin-nav');

const listEl = document.getElementById('list');
const statusEl = document.getElementById('status');
const createForm = document.getElementById('create-form');

function setStatus(message, type = 'info') {
    statusEl.textContent = message;
    statusEl.className = `result ${type}`;
}

function rowHtml(s) {
    return `
      <tr data-id="${s.weepaSongId}">
        <td>${s.weepaSongId}</td>
        <td>${escapeHtml(s.song)}</td>
        <td>${escapeHtml(s.effect)}</td>
        <td>${escapeHtml(s.xId)}</td>
        <td>${escapeHtml(s.submitName)}</td>
        <td>${escapeHtml(s.submitReason ?? '')}</td>
        <td>${escapeHtml(formatDateTime(s.submitDate))}</td>
        <td>${statusLabel(s.approvalStatus, true)} (${s.approvalStatus})</td>
        <td class="actions">
          <button class="action-edit" data-id="${s.weepaSongId}">編集</button>
          <button class="action-delete" data-id="${s.weepaSongId}">削除</button>
        </td>
      </tr>
    `;
}

function editRowHtml(s) {
    return `
      <tr data-id="${s.weepaSongId}" class="editing">
        <td>${s.weepaSongId}</td>
        <td><input name="song" value="${escapeHtml(s.song)}"></td>
        <td><input name="effect" value="${escapeHtml(s.effect)}" size="6"></td>
        <td><input name="xId" value="${escapeHtml(s.xId)}" size="12"></td>
        <td><input name="submitName" value="${escapeHtml(s.submitName)}" size="10"></td>
        <td><input name="submitReason" value="${escapeHtml(s.submitReason ?? '')}"></td>
        <td><input type="datetime-local" name="submitDate" value="${isoToLocalInput(s.submitDate)}"></td>
        <td><select name="approvalStatus">${statusSelectOptions(s.approvalStatus, true)}</select></td>
        <td class="actions">
          <button class="action-save" data-id="${s.weepaSongId}">保存</button>
          <button class="action-cancel" data-id="${s.weepaSongId}">キャンセル</button>
        </td>
      </tr>
    `;
}

function renderList(items) {
    if (!items.length) {
        listEl.innerHTML = '<p class="empty">データがありません。</p>';
        return;
    }
    listEl.innerHTML = `
      <table class="ranking-table db-table">
        <thead>
          <tr>
            <th>id</th><th>song</th><th>effect</th><th>x_id</th><th>submit_name</th>
            <th>submit_reason</th><th>submit_date</th><th>status</th><th>操作</th>
          </tr>
        </thead>
        <tbody>${items.map(rowHtml).join('')}</tbody>
      </table>
    `;
}

let items = [];

async function load() {
    listEl.innerHTML = '<p class="loading">読み込み中...</p>';
    try {
        items = await adminFetch('/api/admin/db/songs');
        renderList(items);
        setStatus(`${items.length} 件取得`, 'ok');
    } catch (err) {
        listEl.innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        setStatus(err.message, 'ng');
    }
}

function enterEditMode(id) {
    const s = items.find((x) => x.weepaSongId === id);
    if (!s) return;
    const tr = listEl.querySelector(`tr[data-id="${id}"]`);
    if (!tr) return;
    tr.outerHTML = editRowHtml(s);
}

function exitEditMode(id) {
    const s = items.find((x) => x.weepaSongId === id);
    if (!s) return;
    const tr = listEl.querySelector(`tr[data-id="${id}"]`);
    if (!tr) return;
    tr.outerHTML = rowHtml(s);
}

function readEditRow(id) {
    const tr = listEl.querySelector(`tr[data-id="${id}"]`);
    if (!tr) return null;
    const get = (name) => tr.querySelector(`[name="${name}"]`).value;
    return {
        song: get('song'),
        effect: get('effect'),
        xId: get('xId'),
        submitName: get('submitName'),
        submitReason: get('submitReason') || null,
        submitDate: localInputToIso(get('submitDate')),
        approvalStatus: Number(get('approvalStatus')),
    };
}

async function saveEdit(id) {
    const payload = readEditRow(id);
    if (!payload) return;
    try {
        await adminFetch(`/api/admin/db/songs/${id}`, {
            method: 'PUT',
            body: JSON.stringify(payload),
        });
        setStatus(`id=${id} 更新しました`, 'ok');
        await load();
    } catch (err) {
        setStatus(err.message, 'ng');
    }
}

async function deleteRow(id) {
    if (!confirm(`id=${id} を削除します。本当によろしいですか？`)) return;
    try {
        await adminFetch(`/api/admin/db/songs/${id}`, { method: 'DELETE' });
        setStatus(`id=${id} 削除しました`, 'ok');
        await load();
    } catch (err) {
        setStatus(err.message, 'ng');
    }
}

listEl.addEventListener('click', (ev) => {
    const t = ev.target;
    if (!(t instanceof HTMLButtonElement)) return;
    const id = Number(t.dataset.id);
    if (!id) return;
    if (t.classList.contains('action-edit')) enterEditMode(id);
    else if (t.classList.contains('action-cancel')) exitEditMode(id);
    else if (t.classList.contains('action-save')) saveEdit(id);
    else if (t.classList.contains('action-delete')) deleteRow(id);
});

createForm.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const fd = new FormData(createForm);
    const payload = {
        song: fd.get('song'),
        effect: fd.get('effect'),
        xId: fd.get('xId'),
        submitName: fd.get('submitName'),
        submitReason: fd.get('submitReason') || null,
        approvalStatus: Number(fd.get('approvalStatus')),
    };
    try {
        const result = await adminFetch('/api/admin/db/songs', {
            method: 'POST',
            body: JSON.stringify(payload),
        });
        setStatus(`id=${result.weepaSongId} を作成しました`, 'ok');
        createForm.reset();
        await load();
    } catch (err) {
        setStatus(err.message, 'ng');
    }
});

load();
