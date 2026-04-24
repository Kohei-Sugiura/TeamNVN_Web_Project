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

async function adminFetch(path, opts = {}) {
    let creds = getAdminCreds();
    if (!creds) {
        creds = promptAdminCreds();
        if (!creds) throw new Error('認証情報が必要です');
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
        throw new Error('認証に失敗しました。ページをリロードして再ログインしてください。');
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

function statusLabel(status, withSongImplemented) {
    const map = { '-1': '却下', '0': '未承認', '1': '承認済み', '2': '実施済み' };
    const key = String(status);
    if (!withSongImplemented && key === '2') return '(2)';
    return map[key] ?? key;
}

function statusSelectOptions(currentValue, withSongImplemented) {
    const options = [
        { value: -1, label: '-1 却下' },
        { value: 0, label: '0 未承認' },
        { value: 1, label: '1 承認済み' },
    ];
    if (withSongImplemented) options.push({ value: 2, label: '2 実施済み' });
    return options.map((o) => `<option value="${o.value}"${Number(currentValue) === o.value ? ' selected' : ''}>${o.label}</option>`).join('');
}

const ADMIN_NAV = `
  <p class="admin-nav">
    <a href="/admin.html">未承認</a>
    <span class="sep">|</span>
    <a href="/admin-songs.html">楽曲</a>
    <span class="sep">|</span>
    <a href="/admin-events.html">イベント</a>
    <span class="sep">|</span>
    <a href="/admin-scores.html">スコア</a>
  </p>
`;

function renderAdminNav(containerSelector) {
    const el = document.querySelector(containerSelector);
    if (el) el.innerHTML = ADMIN_NAV;
}
