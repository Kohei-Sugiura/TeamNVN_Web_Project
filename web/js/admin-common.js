const ADMIN_CREDS_KEY = 'admin-basic-auth-creds';

function getAdminCreds() {
    const raw = sessionStorage.getItem(ADMIN_CREDS_KEY);
    if (!raw) return null;
    try { return JSON.parse(raw); } catch { return null; }
}

function setAdminCreds(user, password) {
    sessionStorage.setItem(ADMIN_CREDS_KEY, JSON.stringify({ user, password }));
}

function clearAdminCreds() {
    sessionStorage.removeItem(ADMIN_CREDS_KEY);
}

function encodeBasicAuth(user, password) {
    return btoa(unescape(encodeURIComponent(`${user}:${password}`)));
}

function promptAdminCreds() {
    const user = window.prompt('管理者ユーザー名', '');
    if (user === null) return null;
    const password = window.prompt('管理者パスワード', '');
    if (password === null) return null;
    const creds = { user, password };
    setAdminCreds(user, password);
    return creds;
}

const ADMIN_LOGIN_PATH = '/admin/login/';

function redirectToLogin() {
    const ret = encodeURIComponent(window.location.pathname + window.location.search);
    window.location.replace(`${ADMIN_LOGIN_PATH}?return=${ret}`);
}

function ensureLoggedIn() {
    if (getAdminCreds()) return;
    redirectToLogin();
    // throw to halt any subsequent script execution while redirect is in flight
    throw new Error('AUTH_REDIRECT');
}

function logoutAdmin() {
    clearAdminCreds();
    window.location.replace(ADMIN_LOGIN_PATH);
}

async function adminFetch(path, opts = {}) {
    const creds = getAdminCreds();
    if (!creds) {
        redirectToLogin();
        throw new Error('AUTH_REDIRECT');
    }

    const res = await fetch(path, {
        ...opts,
        headers: {
            'Authorization': `Basic ${encodeBasicAuth(creds.user, creds.password)}`,
            'Content-Type': 'application/json',
            ...(opts.headers || {}),
        },
    });

    if (res.status === 401) {
        clearAdminCreds();
        redirectToLogin();
        throw new Error('AUTH_REDIRECT');
    }

    if (!res.ok) {
        let message = `HTTP ${res.status}`;
        try {
            const body = await res.json();
            if (body?.message) message = body.message;
        } catch { /* noop */ }
        throw new Error(message);
    }

    if (res.status === 204) return null;
    return res.json().catch(() => null);
}

function escapeHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s).replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
}

function formatDateTime(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    return d.toLocaleString('ja-JP', {
        year: 'numeric', month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit',
    });
}

// ISO 文字列を <input type="datetime-local"> が受け付けるローカル時刻文字列に変換
function isoToLocalInput(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    const pad = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

// datetime-local の値（ローカル）を ISO 文字列（UTC）に戻す
function localInputToIso(localStr) {
    if (!localStr) return null;
    return new Date(localStr).toISOString();
}

function statusLabel(status) {
    const map = { '-1': '却下', '0': '未承認', '1': '承認済み' };
    return map[String(status)] ?? String(status);
}

function statusSelectOptions(currentValue) {
    const options = [
        { value: -1, label: '-1 却下' },
        { value: 0, label: '0 未承認' },
        { value: 1, label: '1 承認済み' },
    ];
    return options.map((o) => `<option value="${o.value}"${Number(currentValue) === o.value ? ' selected' : ''}>${o.label}</option>`).join('');
}

const ADMIN_NAV = `
  <p class="admin-nav">
    <a href="/admin/">WeePA</a>
    <span class="sep">|</span>
    <a href="/admin/msc/">ムンチャレ</a>
    <span class="sep">|</span>
    <a href="/admin/db/">DB管理</a>
    <span class="sep">|</span>
    <a href="#" id="admin-logout-link">ログアウト</a>
  </p>
`;

function renderAdminNav(containerSelector) {
    const el = document.querySelector(containerSelector);
    if (!el) return;
    el.innerHTML = ADMIN_NAV;
    const logoutLink = document.getElementById('admin-logout-link');
    if (logoutLink) {
        logoutLink.addEventListener('click', (ev) => {
            ev.preventDefault();
            logoutAdmin();
        });
    }
}

// ============================================
// 共通の確認ダイアログ (native confirm() の代替。スマホで挙動が
// 不安定なケースがあるため、投稿フォームと同じ <dialog> 形式に統一)
// ============================================

function ensureCommonConfirmDialog() {
    let dialog = document.getElementById('common-confirm-dialog');
    if (dialog) return dialog;
    dialog = document.createElement('dialog');
    dialog.id = 'common-confirm-dialog';
    dialog.className = 'confirm-dialog';
    dialog.innerHTML = `
      <h3 id="common-confirm-title">確認</h3>
      <div class="dialog-body">
        <p id="common-confirm-message" style="margin:0; white-space:pre-wrap; word-break:break-word;"></p>
      </div>
      <div class="dialog-actions">
        <button type="button" class="cancel" data-result="false">戻る</button>
        <button type="button" data-result="true">OK</button>
      </div>
    `;
    document.body.appendChild(dialog);
    return dialog;
}

// datetime-local / date / time の input は、入力欄をクリックしただけでは
// ピッカーが開かない (右端の小さなアイコンクリックが必要)。
// showPicker() を呼んで「クリック = カレンダー展開」を実現する。
document.addEventListener('click', (ev) => {
    const t = ev.target;
    if (!(t instanceof HTMLInputElement)) return;
    if (!['datetime-local', 'date', 'time'].includes(t.type)) return;
    if (typeof t.showPicker !== 'function') return;
    try { t.showPicker(); } catch { /* 未対応ブラウザ / focus 不足は無視 */ }
});

/**
 * native confirm() の置き換え。
 * @param {string} message 表示メッセージ
 * @param {{ title?: string, okLabel?: string, cancelLabel?: string }} [opts]
 * @returns {Promise<boolean>} OK が押されたら true、戻る / Esc / バックドロップで false
 */
function adminConfirm(message, opts = {}) {
    const dialog = ensureCommonConfirmDialog();
    const titleEl = dialog.querySelector('#common-confirm-title');
    const msgEl = dialog.querySelector('#common-confirm-message');
    const okBtn = dialog.querySelector('button[data-result="true"]');
    const cancelBtn = dialog.querySelector('button[data-result="false"]');
    titleEl.textContent = opts.title ?? '確認';
    msgEl.textContent = message;
    okBtn.textContent = opts.okLabel ?? 'OK';
    cancelBtn.textContent = opts.cancelLabel ?? '戻る';

    return new Promise((resolve) => {
        let resolved = false;
        function done(result) {
            if (resolved) return;
            resolved = true;
            dialog.removeEventListener('click', onClick);
            dialog.removeEventListener('close', onClose);
            dialog.removeEventListener('cancel', onCancel);
            try { dialog.close(); } catch { /* already closed */ }
            resolve(result);
        }
        function onClick(ev) {
            const t = ev.target;
            if (!(t instanceof HTMLButtonElement)) return;
            done(t.dataset.result === 'true');
        }
        function onClose() { done(false); }
        function onCancel(ev) { ev.preventDefault(); done(false); }
        dialog.addEventListener('click', onClick);
        dialog.addEventListener('close', onClose);
        dialog.addEventListener('cancel', onCancel);
        dialog.showModal();
    });
}
