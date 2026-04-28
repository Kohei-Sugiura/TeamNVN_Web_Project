const params = new URLSearchParams(window.location.search);
const returnUrl = params.get('return') || '/admin/';

const form = document.getElementById('login-form');
const userInput = document.getElementById('user');
const passwordInput = document.getElementById('password');
const btn = document.getElementById('login-btn');
const msg = document.getElementById('msg');

function setMsg(text, type) {
    msg.textContent = text;
    msg.className = `result ${type}`;
}

async function tryAuth(user, password) {
    const res = await fetch('/api/admin/songs/pending', {
        headers: { 'Authorization': `Basic ${encodeBasicAuth(user, password)}` },
    });
    return res.status;
}

(async () => {
    const existing = getAdminCreds();
    if (!existing) return;
    try {
        const status = await tryAuth(existing.user, existing.password);
        if (status === 200) {
            window.location.replace(returnUrl);
        } else {
            clearAdminCreds();
        }
    } catch {
        // 通信エラー時は何もせずログイン画面に留まる
    }
})();

form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const user = userInput.value.trim();
    const password = passwordInput.value;
    if (!user || !password) {
        setMsg('ユーザー名とパスワードを入力してください。', 'ng');
        return;
    }
    btn.disabled = true;
    setMsg('認証中...', 'info');
    try {
        const status = await tryAuth(user, password);
        if (status === 200) {
            setAdminCreds(user, password);
            setMsg('ログイン成功。リダイレクトします...', 'ok');
            window.location.replace(returnUrl);
        } else if (status === 401) {
            setMsg('ユーザー名またはパスワードが間違っています。', 'ng');
        } else {
            setMsg(`認証に失敗しました (HTTP ${status})`, 'ng');
        }
    } catch (err) {
        setMsg(`通信エラー: ${err.message}`, 'ng');
    } finally {
        btn.disabled = false;
    }
});
