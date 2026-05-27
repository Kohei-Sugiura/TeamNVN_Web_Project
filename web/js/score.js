const form = document.getElementById('score-form');
const btn = document.getElementById('submit-btn');
const resultEl = document.getElementById('result');
const eventDisplay = document.getElementById('event-display');
const dialog = document.getElementById('confirm-dialog');
const confirmBody = document.getElementById('confirm-body');
const confirmOkBtn = document.getElementById('confirm-ok');
const confirmCancelBtn = document.getElementById('confirm-cancel');

const fieldKeys = ['xId', 'submitName', 'score', 'exScore', 'image'];

// X ID + 投稿者名をブラウザに永続化 (localStorage)。submit.js と共通キー。
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

function tt(key) {
  return (window.i18n && window.i18n.t) ? window.i18n.t(key) : key;
}
function lurl(href) {
  return (window.i18n && window.i18n.localLink) ? window.i18n.localLink(href) : href;
}

function formatDate(iso) {
  const d = new Date(iso);
  return d.toLocaleString('ja-JP', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit',
  });
}

function formatNum(n) {
  if (n === '' || n == null) return '';
  return Number(n).toLocaleString('en-US');
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

function renderEventHeader(ev) {
  eventDisplay.innerHTML = `
    <div class="event-title">
      <span class="song">${escapeHtml(ev.song)}</span>
      <span class="effect effect-${(ev.effect || '').toLowerCase()}">${escapeHtml(ev.effect)}</span>
    </div>
    <p class="info-row"><span class="label">${tt('weepa.label.period')}</span>${escapeHtml(formatDate(ev.startDate))} ～ ${escapeHtml(formatDate(ev.endDate))}</p>
  `;
}

function showEventError(message) {
  eventDisplay.innerHTML = `<p class="error">${escapeHtml(message)}</p>`;
}

let currentEventId = null;

(async () => {
  const params = new URLSearchParams(window.location.search);
  const eventId = params.get('eventId');

  try {
    let ev;
    if (eventId && /^\d+$/.test(eventId)) {
      const res = await fetch(`/api/events/${eventId}`);
      if (res.status === 404) { showEventError(tt('weepa.score.event.not-found')); return; }
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      ev = await res.json();
    } else {
      // eventId 未指定 → 現在開催中のイベントを取得し、URL を eventId 付きに置換 (シェア可能化)
      const res = await fetch('/api/events/current');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const body = await res.json();
      ev = body.event;
      if (ev) {
        window.location.replace(lurl(`/weepa/score/?eventId=${encodeURIComponent(ev.weepaEventId)}`));
        return;
      }
    }

    if (!ev) {
      showEventError(tt('weepa.score.event.no-current'));
      return;
    }

    const now = Date.now();
    const start = new Date(ev.startDate).getTime();
    const end = new Date(ev.endDate).getTime();
    if (now < start) {
      showEventError(tt('weepa.score.event.not-yet'));
      return;
    }
    if (now > end) {
      showEventError(tt('weepa.score.event.ended'));
      return;
    }

    currentEventId = ev.weepaEventId;
    renderEventHeader(ev);
    form.hidden = false;
  } catch (err) {
    showEventError(`${tt('common.error.fetch')}: ${err.message}`);
  }
})();

function getFormSnapshot() {
  return {
    xId: form.xId.value.trim(),
    submitName: form.submitName.value.trim(),
    score: form.score.value,
    exScore: form.exScore.value,
    imageFile: form.image.files[0] || null,
  };
}

function renderConfirm(snap) {
  const empty = `<span class="muted">${tt('common.muted.empty')}</span>`;
  const none = `<span class="muted">${tt('common.muted.none')}</span>`;
  const unsel = `<span class="muted">${tt('common.muted.unselected')}</span>`;
  const loading = `<span class="muted">${tt('common.muted.preview-loading')}</span>`;
  confirmBody.innerHTML = `
    <dl>
      <dt>${tt('weepa.score.field.xid')}</dt>
      <dd>${snap.xId ? escapeHtml(snap.xId) : empty}</dd>
      <dt>${tt('weepa.score.field.name')}</dt>
      <dd>${snap.submitName ? escapeHtml(snap.submitName) : empty}</dd>
      <dt>${tt('weepa.score.field.score')}</dt>
      <dd>${snap.score !== '' ? escapeHtml(formatNum(snap.score)) : empty}</dd>
      <dt>${tt('weepa.score.field.exscore')}</dt>
      <dd>${snap.exScore !== '' ? escapeHtml(formatNum(snap.exScore)) : none}</dd>
      <dt>${tt('weepa.score.field.image')}</dt>
      <dd id="confirm-image-slot">${snap.imageFile ? loading : unsel}</dd>
    </dl>
  `;

  if (snap.imageFile) {
    const reader = new FileReader();
    reader.onload = () => {
      const slot = document.getElementById('confirm-image-slot');
      if (!slot) return;
      slot.innerHTML = `<img class="preview" alt="${tt('weepa.score.preview.alt')}" src="${reader.result}">`;
    };
    reader.onerror = () => {
      const slot = document.getElementById('confirm-image-slot');
      if (slot) slot.innerHTML = `<span class="muted">${tt('common.muted.preview-fail')}</span>`;
    };
    reader.readAsDataURL(snap.imageFile);
  }
}

async function actuallySubmit(snap) {
  if (currentEventId == null) return;

  const fd = new FormData();
  fd.append('xId', snap.xId);
  fd.append('submitName', snap.submitName);
  fd.append('score', snap.score);
  if (snap.exScore !== '') {
    fd.append('exScore', snap.exScore);
  }
  if (snap.imageFile) {
    fd.append('image', snap.imageFile);
  }

  btn.disabled = true;
  resultEl.textContent = tt('common.error.sending');
  resultEl.className = 'result info';

  try {
    const res = await fetch(`/api/events/${currentEventId}/scores`, {
      method: 'POST',
      body: fd,
    });

    if (res.ok) {
      resultEl.textContent = tt('weepa.score.submit.ok');
      resultEl.className = 'result ok';
      saveUserProfile({ xId: snap.xId, submitName: snap.submitName });
      form.reset();
      prefillUserFields();
      setPreview(null);
      setRecognizeStatus('', '');
    } else if (res.status === 400) {
      const data = await res.json().catch(() => ({}));
      mapServerErrors(data.errors);
      resultEl.textContent = data.message ?? tt('common.error.validation');
      resultEl.className = 'result ng';
      scrollToFirstError();
    } else if (res.status === 413) {
      resultEl.textContent = tt('weepa.score.submit.413');
      resultEl.className = 'result ng';
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

// 画像選択 → Claude API で SCORE / EX SCORE を自動読み取りして入力欄にセット。
// 失敗は手入力にフォールバックさせるだけで、フォーム送信は阻害しない。
const recognizeStatusEl = document.getElementById('recognize-status');
function setRecognizeStatus(text, kind) {
  if (!recognizeStatusEl) return;
  recognizeStatusEl.textContent = text;
  recognizeStatusEl.className = `result ${kind || ''}`.trim();
}

// Anthropic vision API は base64 で 5MB 制限。スマホ撮影 (10MB 超) はそのままだと
// 拒否されるので、長辺 1600px / JPEG quality 0.85 に縮小して送る (認識精度は十分)。
// 元画像はそのまま投稿用に保持 → スコア投稿側はオリジナルが送られる。
async function shrinkImageForRecognize(file) {
  const MAX_DIM = 1600;
  const QUALITY = 0.85;
  const url = URL.createObjectURL(file);
  try {
    const img = await new Promise((resolve, reject) => {
      const im = new Image();
      im.onload = () => resolve(im);
      im.onerror = () => reject(new Error(tt('weepa.score.recognize.image-load-fail')));
      im.src = url;
    });
    const ratio = Math.min(1, MAX_DIM / Math.max(img.naturalWidth, img.naturalHeight));
    if (ratio === 1 && file.size <= 4 * 1024 * 1024) {
      // 元画像が 4MB 以下かつ縮小不要 → そのまま送る (再エンコードコストを避ける)
      return file;
    }
    const w = Math.round(img.naturalWidth * ratio);
    const h = Math.round(img.naturalHeight * ratio);
    const canvas = document.createElement('canvas');
    canvas.width = w; canvas.height = h;
    canvas.getContext('2d').drawImage(img, 0, 0, w, h);
    const blob = await new Promise((resolve) =>
      canvas.toBlob(resolve, 'image/jpeg', QUALITY));
    if (!blob) return file;
    return new File([blob], 'recognize.jpg', { type: 'image/jpeg' });
  } finally {
    URL.revokeObjectURL(url);
  }
}

const recognizeDialog = document.getElementById('recognize-dialog');

async function recognizeFromImage(file) {
  setRecognizeStatus('', '');
  if (recognizeDialog && typeof recognizeDialog.showModal === 'function') {
    try { recognizeDialog.showModal(); } catch { /* ignore */ }
  }
  let toSend;
  try {
    toSend = await shrinkImageForRecognize(file);
  } catch (err) {
    recognizeDialog?.close();
    setRecognizeStatus(`${tt('weepa.score.recognize.preprocess-fail')}: ${err.message}`, 'ng');
    return;
  }
  const fd = new FormData();
  fd.append('image', toSend);
  try {
    const res = await fetch('/api/score/recognize', { method: 'POST', body: fd });
    if (res.status === 429) {
      setRecognizeStatus(tt('weepa.score.recognize.too-many'), 'ng');
      return;
    }
    if (res.status === 503) {
      // API キー未設定 / 機能無効 → そっと黙る (手入力のみで運用)
      setRecognizeStatus('', '');
      return;
    }
    const data = await res.json().catch(() => ({}));
    if (!res.ok || !data.success) {
      setRecognizeStatus(
        `${tt('weepa.score.recognize.fail-prefix')}: ${data.error || `HTTP ${res.status}`}。${tt('weepa.score.recognize.fail-suffix')}`,
        'ng');
      return;
    }
    let filled = [];
    if (data.score != null) {
      form.score.value = data.score;
      filled.push(`SCORE = ${Number(data.score).toLocaleString('en-US')}`);
    }
    if (data.exScore != null) {
      form.exScore.value = data.exScore;
      filled.push(`EX = ${Number(data.exScore).toLocaleString('en-US')}`);
    }
    if (filled.length) {
      setRecognizeStatus(`${tt('weepa.score.recognize.filled-prefix')} (${filled.join(' / ')})。${tt('weepa.score.recognize.filled-suffix')}`, 'ok');
    } else {
      setRecognizeStatus(tt('weepa.score.recognize.no-read'), 'ng');
    }
  } catch (err) {
    setRecognizeStatus(`${tt('common.error.network')}: ${err.message}`, 'ng');
  } finally {
    recognizeDialog?.close();
  }
}

const previewEl = document.getElementById('image-preview');
let previewObjectUrl = null;
function setPreview(file) {
  if (!previewEl) return;
  if (previewObjectUrl) {
    URL.revokeObjectURL(previewObjectUrl);
    previewObjectUrl = null;
  }
  if (!file) {
    previewEl.removeAttribute('src');
    previewEl.hidden = true;
    return;
  }
  previewObjectUrl = URL.createObjectURL(file);
  previewEl.src = previewObjectUrl;
  previewEl.hidden = false;
}

form.image.addEventListener('change', () => {
  const f = form.image.files[0];
  if (!f) {
    setPreview(null);
    setRecognizeStatus('', '');
    return;
  }
  setPreview(f);
  recognizeFromImage(f);
});

form.addEventListener('submit', (ev) => {
  ev.preventDefault();
  clearErrors();
  if (currentEventId == null) return;
  const snap = getFormSnapshot();
  renderConfirm(snap);
  dialog.showModal();
});

confirmCancelBtn.addEventListener('click', () => dialog.close());

confirmOkBtn.addEventListener('click', async () => {
  dialog.close();
  await actuallySubmit(getFormSnapshot());
});
