const form = document.getElementById('submit-form');
const btn = document.getElementById('submit-btn');
const resultEl = document.getElementById('result');
const dialog = document.getElementById('confirm-dialog');
const confirmBody = document.getElementById('confirm-body');
const confirmOkBtn = document.getElementById('confirm-ok');
const confirmCancelBtn = document.getElementById('confirm-cancel');

const fieldKeys = ['song', 'effect', 'xId', 'submitName', 'submitReason'];

// X ID + 投稿者名をブラウザに永続化 (localStorage)。
// 同一ブラウザ内で再訪・別フォームに遷移しても自動入力される。
const USER_PROFILE_KEY = 'weepa_user_profile';
function loadUserProfile() {
  try { return JSON.parse(localStorage.getItem(USER_PROFILE_KEY) || 'null') || {}; }
  catch { return {}; }
}
function saveUserProfile(profile) {
  try { localStorage.setItem(USER_PROFILE_KEY, JSON.stringify(profile)); }
  catch { /* quota / private mode 等は無視 */ }
}
function prefillUserFields() {
  const p = loadUserProfile();
  if (p.xId && !form.xId.value) form.xId.value = p.xId;
  if (p.submitName && !form.submitName.value) form.submitName.value = p.submitName;
}
prefillUserFields();

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function clearErrors() {
  for (const key of fieldKeys) {
    const el = document.querySelector(`.error[data-for="${key}"]`);
    if (el) el.textContent = '';
  }
  resultEl.textContent = '';
  resultEl.className = 'result';
}

function showFieldError(key, message) {
  const el = document.querySelector(`.error[data-for="${key}"]`);
  if (el) el.textContent = message;
}

function scrollToFirstError() {
  const firstError = Array.from(document.querySelectorAll('.error[data-for]'))
    .find((el) => el.textContent.trim() !== '');
  if (!firstError) return;
  const target = firstError.closest('.field') || firstError;
  target.scrollIntoView({ behavior: 'smooth', block: 'center' });
}

function mapServerErrors(errors) {
  if (!errors) return;
  const norm = {};
  for (const k of Object.keys(errors)) {
    norm[k.charAt(0).toLowerCase() + k.slice(1)] = errors[k];
  }
  for (const key of fieldKeys) {
    if (norm[key]?.length) {
      showFieldError(key, norm[key].join(' / '));
    }
  }
}

function effectLabel(value) {
  const opt = form.effect.querySelector(`option[value="${value}"]`);
  return opt ? opt.textContent : value;
}

function buildPayload() {
  return {
    song: form.song.value.trim(),
    effect: form.effect.value,
    xId: form.xId.value.trim(),
    submitName: form.submitName.value.trim(),
    submitReason: form.submitReason.value.trim() || null,
  };
}

function tt(key) {
  return (window.i18n && window.i18n.t) ? window.i18n.t(key) : key;
}

function renderConfirm(payload) {
  const empty = `<span class="muted">${tt('common.muted.empty')}</span>`;
  const unselected = `<span class="muted">${tt('common.muted.unselected')}</span>`;
  const none = `<span class="muted">${tt('common.muted.none')}</span>`;
  confirmBody.innerHTML = `
    <dl>
      <dt>${tt('weepa.submit.field.xid')}</dt>
      <dd>${payload.xId ? escapeHtml(payload.xId) : empty}</dd>
      <dt>${tt('weepa.submit.field.name')}</dt>
      <dd>${payload.submitName ? escapeHtml(payload.submitName) : empty}</dd>
      <dt>${tt('weepa.submit.field.song')}</dt>
      <dd>${payload.song ? escapeHtml(payload.song) : empty}</dd>
      <dt>${tt('weepa.submit.field.effect')}</dt>
      <dd>${payload.effect ? escapeHtml(effectLabel(payload.effect)) : unselected}</dd>
      <dt>${tt('weepa.submit.field.reason')}</dt>
      <dd>${payload.submitReason ? escapeHtml(payload.submitReason) : none}</dd>
    </dl>
  `;
}

async function actuallySubmit(payload) {
  btn.disabled = true;
  resultEl.textContent = tt('common.error.sending');
  resultEl.className = 'result info';

  try {
    const res = await fetch('/api/songs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });

    if (res.ok) {
      await res.json().catch(() => null);
      resultEl.textContent = tt('weepa.submit.ok');
      resultEl.className = 'result ok';
      saveUserProfile({ xId: payload.xId, submitName: payload.submitName });
      form.reset();
      prefillUserFields();
    } else if (res.status === 400) {
      const data = await res.json().catch(() => ({}));
      mapServerErrors(data.errors);
      resultEl.textContent = tt('common.error.validation');
      resultEl.className = 'result ng';
      scrollToFirstError();
    } else {
      resultEl.textContent = `${tt('common.error.send')} (HTTP ${res.status})`;
      resultEl.className = 'result ng';
    }
  } catch (err) {
    resultEl.textContent = `${tt('common.error.network')}: ${err.message}`;
    resultEl.className = 'result ng';
  } finally {
    btn.disabled = false;
    resultEl.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }
}

form.addEventListener('submit', (ev) => {
  ev.preventDefault();
  clearErrors();
  const payload = buildPayload();
  renderConfirm(payload);
  dialog.showModal();
});

confirmCancelBtn.addEventListener('click', () => dialog.close());

confirmOkBtn.addEventListener('click', async () => {
  dialog.close();
  await actuallySubmit(buildPayload());
});
