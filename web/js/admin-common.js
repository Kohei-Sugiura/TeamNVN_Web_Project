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
