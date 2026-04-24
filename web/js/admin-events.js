renderAdminNav('#admin-nav');

const listEl = document.getElementById('list');
const statusEl = document.getElementById('status');
const createForm = document.getElementById('create-form');

function setStatus(message, type = 'info') {
    statusEl.textContent = message;
    statusEl.className = `result ${type}`;
}

function rowHtml(e) {
    const songLabel = e.song ? `${e.song} [${e.effect}]` : '(曲なし)';
    return `
      <tr data-id="${e.weepaEventId}">
        <td>${e.weepaEventId}</td>
        <td>${e.weepaSongId} ${escapeHtml(songLabel)}</td>
        <td>${escapeHtml(formatDateTime(e.startDate))}</td>
        <td>${escapeHtml(formatDateTime(e.endDate))}</td>
        <td class="actions">
          <button class="action-edit" data-id="${e.weepaEventId}">編集</button>
          <button class="action-delete" data-id="${e.weepaEventId}">削除</button>
        </td>
      </tr>
    `;
}

function editRowHtml(e) {
    return `
      <tr data-id="${e.weepaEventId}" class="editing">
        <td>${e.weepaEventId}</td>
        <td><input type="number" name="weepaSongId" value="${e.weepaSongId}" size="6"></td>
        <td><input type="datetime-local" name="startDate" value="${isoToLocalInput(e.startDate)}"></td>
        <td><input type="datetime-local" name="endDate" value="${isoToLocalInput(e.endDate)}"></td>
        <td class="actions">
          <button class="action-save" data-id="${e.weepaEventId}">保存</button>
          <button class="action-cancel" data-id="${e.weepaEventId}">キャンセル</button>
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
            <th>id</th><th>song_id (曲名)</th><th>start_date</th><th>end_date</th><th>操作</th>
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
        items = await adminFetch('/api/admin/db/events');
        renderList(items);
        setStatus(`${items.length} 件取得`, 'ok');
    } catch (err) {
        listEl.innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        setStatus(err.message, 'ng');
    }
}

function enterEditMode(id) {
    const e = items.find((x) => x.weepaEventId === id);
    if (!e) return;
    const tr = listEl.querySelector(`tr[data-id="${id}"]`);
    if (tr) tr.outerHTML = editRowHtml(e);
}

function exitEditMode(id) {
    const e = items.find((x) => x.weepaEventId === id);
    if (!e) return;
    const tr = listEl.querySelector(`tr[data-id="${id}"]`);
    if (tr) tr.outerHTML = rowHtml(e);
}

function readEditRow(id) {
    const tr = listEl.querySelector(`tr[data-id="${id}"]`);
    if (!tr) return null;
    const get = (name) => tr.querySelector(`[name="${name}"]`).value;
    return {
        weepaSongId: Number(get('weepaSongId')),
        startDate: localInputToIso(get('startDate')),
        endDate: localInputToIso(get('endDate')),
    };
}

async function saveEdit(id) {
    const payload = readEditRow(id);
    if (!payload) return;
    try {
        await adminFetch(`/api/admin/db/events/${id}`, {
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
    if (!confirm(`event_id=${id} を削除します。紐づくスコアがあるとFK違反で失敗します。よろしいですか？`)) return;
    try {
        await adminFetch(`/api/admin/db/events/${id}`, { method: 'DELETE' });
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
        weepaSongId: Number(fd.get('weepaSongId')),
        startDate: localInputToIso(fd.get('startDate')),
        endDate: localInputToIso(fd.get('endDate')),
    };
    try {
        const result = await adminFetch('/api/admin/db/events', {
            method: 'POST',
            body: JSON.stringify(payload),
        });
        setStatus(`event_id=${result.weepaEventId} を作成しました`, 'ok');
        createForm.reset();
        await load();
    } catch (err) {
        setStatus(err.message, 'ng');
    }
});

load();
