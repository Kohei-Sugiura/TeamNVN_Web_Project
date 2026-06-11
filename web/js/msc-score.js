const eventDisplay = document.getElementById('event-display');
const form = document.getElementById('msc-score-form');
const playsList = document.getElementById('msc-plays-list');
const arcadeImages = document.getElementById('msc-images-arcade');
const konasteImages = document.getElementById('msc-images-konaste');
const resultEl = document.getElementById('result');
const dialog = document.getElementById('confirm-dialog');
const confirmBody = document.getElementById('confirm-body');
const confirmOkBtn = document.getElementById('confirm-ok');
const confirmCancelBtn = document.getElementById('confirm-cancel');
const btn = document.getElementById('submit-btn');

const fieldKeys = ['xId', 'submitName', 'platform', 'plays', 'image'];
const MAX_PLAYS = 4;

const tt = (k) => (window.i18n && window.i18n.t) ? window.i18n.t(k) : k;

// iPhone 撮影など大きい画像 (4MB 超) を長辺 1600px / JPEG 0.85 に縮小して
// 送信する。元のままだとモバイル回線で nginx の client_body_timeout (デフォ 60s)
// に引っかかる。WeePA score.js の同名関数と同じ実装。
async function shrinkImageForUpload(file) {
  const MAX_DIM = 1600;
  const QUALITY = 0.85;
  const url = URL.createObjectURL(file);
  try {
    const img = await new Promise((resolve, reject) => {
      const im = new Image();
      im.onload = () => resolve(im);
      im.onerror = () => reject(new Error('image load failed'));
      im.src = url;
    });
    const ratio = Math.min(1, MAX_DIM / Math.max(img.naturalWidth, img.naturalHeight));
    if (ratio === 1 && file.size <= 4 * 1024 * 1024) {
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
    return new File([blob], 'upload.jpg', { type: 'image/jpeg' });
  } finally {
    URL.revokeObjectURL(url);
  }
}

let currentEvent = null;
let songCount = 0;

// ===== Common utils =====
function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}
function formatYmdHm(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}/${p(d.getMonth() + 1)}/${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}
function trackLabel(playOrder) {
  if (playOrder === 4) return 'EX Track';
  const suf = { 1: 'st', 2: 'nd', 3: 'rd' };
  return `${playOrder}${suf[playOrder]} Track`;
}
function clearErrors() {
  // 静的フィールド + 動的に追加された score-play-N / image-play-N 全部クリア
  document.querySelectorAll('.error[data-for]').forEach((el) => {
    el.textContent = '';
  });
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

// ===== User profile =====
const USER_PROFILE_KEY = 'weepa_user_profile';
function loadUserProfile() {
  try { return JSON.parse(localStorage.getItem(USER_PROFILE_KEY) || 'null') || {}; }
  catch { return {}; }
}
function saveUserProfile(p) {
  try { localStorage.setItem(USER_PROFILE_KEY, JSON.stringify(p)); } catch { /* ignore */ }
}
function prefillUserFields() {
  const p = loadUserProfile();
  if (p.xId && !form.xId.value) form.xId.value = p.xId;
  if (p.submitName && !form.submitName.value) form.submitName.value = p.submitName;
}

// ===== Event header =====
function renderEventHeader(ev) {
  const songLines = ev.songs
    .map((s) => `${s.songOrder}. ${escapeHtml(s.song)} <span class="effect effect-${(s.effect || '').toLowerCase()}">${escapeHtml(s.effect)}</span>`)
    .join('<br>');
  eventDisplay.innerHTML = `
    <h2 class="msc-event-title">${escapeHtml(ev.title)}</h2>
    <p class="info-row"><span class="label">${tt('msc.score.label.period')}</span>${escapeHtml(formatYmdHm(ev.startDate))} ~ ${escapeHtml(formatYmdHm(ev.endDate))}</p>
    <p class="info-row"><span class="label">${tt('msc.score.label.tracks')}</span>${songLines}</p>
  `;
}
function showEventError(message) {
  eventDisplay.innerHTML = `<p class="error">${escapeHtml(message)}</p>`;
}

// ===== Plays rendering =====
function songBadgeHtml(song, effect) {
  return `<span class="msc-play-song-name">${escapeHtml(song)}</span> <span class="effect effect-${(effect || '').toLowerCase()}">${escapeHtml(effect)}</span>`;
}

function scoreFieldHtml(playOrder, disabled = false) {
  return `
    <div class="field msc-play-score-field">
      <label for="msc-score-${playOrder}">${tt('msc.score.label.score')} <span class="required">*</span></label>
      <input type="number" id="msc-score-${playOrder}" name="score"
             min="0" max="10000000" step="1" inputmode="numeric"
             placeholder="0 〜 10,000,000"${disabled ? ' disabled' : ''}>
      <p class="error" data-for="score-play-${playOrder}"></p>
    </div>
  `;
}

function imageFieldHtml(playOrder, required = false) {
  const label = required ? `${tt('msc.score.image.required')} <span class="required">*</span>` : tt('msc.score.image.optional');
  return `
    <div class="field msc-play-image-field">
      <label for="msc-image-${playOrder}">${label}</label>
      <input type="file" id="msc-image-${playOrder}" name="imagePlay${playOrder}"
             accept="image/jpeg,image/png,image/webp,image/*">
      <p class="hint">${tt('msc.score.image.hint')}</p>
      <p class="error" data-for="image-play-${playOrder}"></p>
    </div>
  `;
}

function renderMandatoryRow(playOrder) {
  const song = currentEvent.songs.find((s) => s.songOrder === playOrder);
  return `
    <div class="msc-play-row" data-play-order="${playOrder}" data-mandatory="true">
      <div class="msc-play-header">
        <span class="msc-play-track-name">${trackLabel(playOrder)}</span>
        <span class="msc-mandatory-tag">${tt('msc.score.label.mandatory')}</span>
      </div>
      <div class="msc-play-song-display">${songBadgeHtml(song.song, song.effect)}</div>
      ${scoreFieldHtml(playOrder, false)}
      ${imageFieldHtml(playOrder, true)}
    </div>
  `;
}

function renderRetryRow(playOrder) {
  const radioName = `retry${playOrder}`;
  const noneOption = `
    <label class="msc-retry-option msc-retry-none">
      <input type="radio" name="${radioName}" value="" checked>
      <span class="msc-retry-option-label">${tt('msc.score.retry.none')}</span>
    </label>
  `;
  const songOptions = currentEvent.songs.map((s) => `
    <label class="msc-retry-option">
      <input type="radio" name="${radioName}" value="${s.songOrder}">
      <span class="msc-retry-option-label">${songBadgeHtml(s.song, s.effect)}</span>
    </label>
  `).join('');
  return `
    <div class="msc-play-row" data-play-order="${playOrder}" data-mandatory="false">
      <div class="msc-play-header">
        <span class="msc-play-track-name">${trackLabel(playOrder)}</span>
        <span class="msc-retry-tag">${tt('msc.score.label.retry')}</span>
      </div>
      <fieldset class="msc-retry-options">
        ${noneOption}
        ${songOptions}
      </fieldset>
      ${scoreFieldHtml(playOrder, true)}
      ${imageFieldHtml(playOrder, false)}
    </div>
  `;
}

function initPlays() {
  playsList.innerHTML = '';
  for (let i = 1; i <= songCount; i++) {
    playsList.insertAdjacentHTML('beforeend', renderMandatoryRow(i));
  }
  for (let i = songCount + 1; i <= MAX_PLAYS; i++) {
    playsList.insertAdjacentHTML('beforeend', renderRetryRow(i));
  }
  // 全行に対し画像欄の表示状態を反映
  playsList.querySelectorAll('.msc-play-row').forEach(updateRowVisibility);
}

function updateRowVisibility(row) {
  const scoreField = row.querySelector('.msc-play-score-field');
  const imageField = row.querySelector('.msc-play-image-field');
  const p = getCurrentPlatform();
  const isMandatory = row.dataset.mandatory === 'true';

  // 必修枠は常に score + (konaste なら) image 両方表示
  if (isMandatory) {
    if (scoreField) scoreField.style.display = '';
    if (imageField) imageField.style.display = (p === 'konaste') ? '' : 'none';
    return;
  }
  // リトライ枠: 選択中の値で決まる
  const playOrder = row.dataset.playOrder;
  const r = row.querySelector(`input[name="retry${playOrder}"]:checked`);
  const active = !!r && r.value !== '';
  if (scoreField) scoreField.style.display = active ? '' : 'none';
  if (imageField) imageField.style.display = (active && p === 'konaste') ? '' : 'none';
}

// retry radio change → スコア input enable/clear + score/image 欄表示切替
playsList.addEventListener('change', (ev) => {
  const t = ev.target;
  if (!(t instanceof HTMLInputElement) || t.type !== 'radio') return;
  const row = t.closest('.msc-play-row');
  if (!row || row.dataset.mandatory === 'true') return;
  const scoreInput = row.querySelector('input[name="score"]');
  if (t.value === '') {
    scoreInput.disabled = true;
    scoreInput.value = '';
  } else {
    scoreInput.disabled = false;
  }
  updateRowVisibility(row);
});

// ===== Platform =====
function getCurrentPlatform() {
  return form.querySelector('input[name="platform"]:checked')?.value || 'arcade';
}
function updatePlatformView() {
  const p = getCurrentPlatform();
  arcadeImages.hidden = (p !== 'arcade');
  konasteImages.hidden = (p !== 'konaste');
  form.classList.remove('platform-arcade', 'platform-konaste');
  form.classList.add(`platform-${p}`);
  playsList.querySelectorAll('.msc-play-row').forEach(updateRowVisibility);
}
form.querySelectorAll('input[name="platform"]').forEach((r) => {
  r.addEventListener('change', updatePlatformView);
});

// ===== Initial load =====
(async function init() {
  try {
    const params = new URLSearchParams(window.location.search);
    const eventId = params.get('eventId');
    let ev;
    if (eventId && /^\d+$/.test(eventId)) {
      const res = await fetch(`/api/msc/events/${eventId}`);
      if (res.status === 404) { showEventError(tt('msc.score.event.not-found')); return; }
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      ev = await res.json();
    } else {
      const res = await fetch('/api/msc/events/current');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const body = await res.json();
      ev = body.event;
      if (ev) {
        const lurl = (h) => (window.i18n && window.i18n.localLink) ? window.i18n.localLink(h) : h;
        window.location.replace(lurl(`/msc/score/?eventId=${encodeURIComponent(ev.mscEventId)}`));
        return;
      }
    }
    if (!ev) { showEventError(tt('msc.score.event.no-current')); return; }
    const now = Date.now();
    if (now < new Date(ev.startDate).getTime()) { showEventError(tt('msc.score.event.not-yet')); return; }
    if (now > new Date(ev.endDate).getTime()) { showEventError(tt('msc.score.event.ended')); return; }
    if (!ev.songs || ev.songs.length < 2) { showEventError(tt('msc.score.event.no-songs')); return; }
    currentEvent = ev;
    songCount = ev.songs.length;
    renderEventHeader(ev);
    form.hidden = false;
    initPlays();
    updatePlatformView();
    prefillUserFields();
  } catch (err) {
    showEventError(`${tt('common.error.fetch')}: ${err.message}`);
  }
})();

// ===== Submission =====
function collectSnapshot() {
  const platform = getCurrentPlatform();
  const plays = [];
  const playImages = []; // [null, file_play1, file_play2, ...]; index 0 unused for clarity
  for (let i = 0; i <= MAX_PLAYS; i++) playImages.push(null);

  for (let i = 1; i <= MAX_PLAYS; i++) {
    const row = playsList.querySelector(`.msc-play-row[data-play-order="${i}"]`);
    if (!row) continue;
    const isMandatory = row.dataset.mandatory === 'true';
    let songOrder = null;
    if (isMandatory) {
      songOrder = i;
    } else {
      const r = row.querySelector(`input[name="retry${i}"]:checked`);
      if (!r || r.value === '') continue; // リトライなし
      songOrder = Number(r.value);
    }
    const scoreRaw = row.querySelector('input[name="score"]').value;
    const score = scoreRaw === '' ? NaN : Number(scoreRaw);
    plays.push({ playOrder: i, songOrder, score });

    // 画像 (konaste のみ採取)
    if (platform === 'konaste') {
      const el = row.querySelector(`input[name="imagePlay${i}"]`);
      playImages[i] = el?.files?.[0] || null;
    }
  }

  const imageSummary = platform === 'arcade'
    ? (form.querySelector('#imageSummary')?.files?.[0] || null)
    : (form.querySelector('#imageSummaryK')?.files?.[0] || null);

  return {
    xId: form.xId.value.trim(),
    submitName: form.submitName.value.trim(),
    platform,
    plays,
    imageSummary,
    playImages, // 1-indexed (index 0 unused)
  };
}

function validateSnapshot(snap) {
  let ok = true;
  if (!snap.xId) { showFieldError('xId', tt('msc.score.validation.xid')); ok = false; }
  if (!snap.submitName) { showFieldError('submitName', tt('msc.score.validation.name')); ok = false; }

  // 各 play のスコアを個別に検証 (必修枠 + リトライで曲が選択された枠)
  const playByOrder = new Map(snap.plays.map((p) => [p.playOrder, p]));
  for (let i = 1; i <= MAX_PLAYS; i++) {
    const isMandatory = i <= songCount;
    const play = playByOrder.get(i);
    if (isMandatory) {
      if (!play || !Number.isFinite(play.score)) {
        showFieldError(`score-play-${i}`, tt('msc.score.validation.score-required')); ok = false;
      } else if (play.score < 0 || play.score > 10000000) {
        showFieldError(`score-play-${i}`, tt('msc.score.validation.score-range')); ok = false;
      }
    } else if (play) {
      // リトライ枠で曲が選択されている
      if (!Number.isFinite(play.score)) {
        showFieldError(`score-play-${i}`, tt('msc.score.validation.score-required')); ok = false;
      } else if (play.score < 0 || play.score > 10000000) {
        showFieldError(`score-play-${i}`, tt('msc.score.validation.score-range')); ok = false;
      }
    }
  }

  // 画像
  const summaryKey = snap.platform === 'arcade' ? 'imageSummary' : 'imageSummaryK';
  if (!snap.imageSummary) {
    showFieldError(summaryKey, tt('msc.score.validation.summary-required')); ok = false;
  }
  if (snap.platform === 'konaste') {
    // 必修枠の画像を個別チェック (リトライ枠は任意)
    for (let i = 1; i <= songCount; i++) {
      if (!snap.playImages[i]) {
        showFieldError(`image-play-${i}`, tt('msc.score.validation.konaste-image')); ok = false;
      }
    }
  }
  return ok;
}

function renderConfirm(snap) {
  const songMap = new Map(currentEvent.songs.map((s) => [s.songOrder, s]));

  // 画像プレビュー slot を集めて、後段で非同期に埋める
  const imageSlots = [];
  let slotIdx = 0;
  function imageSlotHtml(file) {
    const slotId = `msc-confirm-img-${slotIdx++}`;
    imageSlots.push({ slotId, file });
    return `<div id="${slotId}"><span class="muted">${tt('common.muted.preview-loading')}</span></div>`;
  }

  // 各 Track を info-row 形式で表示 (ラベル / 楽曲名 / スコア が改行で並ぶ)。
  // 親 <dd> が white-space: pre-wrap のため、template literal の改行/インデントを
  // そのまま出さないよう一行にまとめる。
  const playBlocks = [];
  for (let i = 1; i <= MAX_PLAYS; i++) {
    const isMand = i <= songCount;
    const play = snap.plays.find((p) => p.playOrder === i);
    if (play) {
      const s = songMap.get(play.songOrder);
      const effectBadge = `<span class="effect effect-${(s?.effect || '').toLowerCase()}">${escapeHtml(s?.effect || '')}</span>`;
      playBlocks.push(`<p class="info-row"><span class="label">${trackLabel(i)}</span>${escapeHtml(s?.song || '?')} ${effectBadge}<br>${play.score.toLocaleString('en-US')}</p>`);
      if (snap.platform === 'konaste' && snap.playImages[i]) {
        playBlocks.push(`<div>${imageSlotHtml(snap.playImages[i])}</div>`);
      }
    } else if (!isMand) {
      playBlocks.push(`<p class="info-row"><span class="label">${trackLabel(i)}</span><span class="muted">${tt('msc.score.retry.none')}</span></p>`);
    }
  }
  const playsHtml = playBlocks.join('');

  const bestPerSong = new Map();
  for (const p of snap.plays) {
    const cur = bestPerSong.get(p.songOrder) ?? -1;
    if (p.score > cur) bestPerSong.set(p.songOrder, p.score);
  }
  const previewTotal = Array.from(bestPerSong.values()).reduce((a, b) => a + b, 0);

  // 通しリザルト → 合計スコア の順で末尾に
  const summaryHtml = snap.imageSummary
    ? `<dt>${tt('msc.score.confirm.summary')}</dt><dd>${imageSlotHtml(snap.imageSummary)}</dd>`
    : '';

  const platformLabel = snap.platform === 'arcade'
    ? tt('msc.score.platform.arcade')
    : tt('msc.score.platform.konaste');

  confirmBody.innerHTML = `
    <dl>
      <dt>${tt('weepa.score.field.xid')}</dt><dd>${escapeHtml(snap.xId)}</dd>
      <dt>${tt('weepa.score.field.name')}</dt><dd>${escapeHtml(snap.submitName)}</dd>
      <dt>${tt('msc.score.confirm.platform')}</dt><dd>${platformLabel}</dd>
      <dt>${tt('msc.score.confirm.score')}</dt>
      <dd>${playsHtml}</dd>
      ${summaryHtml}
      <dt>${tt('msc.score.confirm.total')}</dt><dd>${previewTotal.toLocaleString('en-US')}</dd>
    </dl>
  `;

  // 非同期で各画像を DataURL に変換して slot に埋め込む
  for (const s of imageSlots) {
    const reader = new FileReader();
    reader.onload = () => {
      const target = document.getElementById(s.slotId);
      if (target) target.innerHTML = `<img class="preview" alt="${tt('msc.score.confirm.image-alt')}" src="${reader.result}">`;
    };
    reader.onerror = () => {
      const target = document.getElementById(s.slotId);
      if (target) target.innerHTML = `<span class="muted">${tt('common.muted.preview-fail')}</span>`;
    };
    reader.readAsDataURL(s.file);
  }
}

// 確認ダイアログ内のステータス表示。投稿完了するまでダイアログを閉じない。
let _uploading = false;
function ensureDialogStatus() {
  let s = document.getElementById('confirm-status');
  if (s) return s;
  s = document.createElement('div');
  s.id = 'confirm-status';
  s.className = 'confirm-status';
  s.hidden = true;
  s.innerHTML = '<p class="confirm-status-text"></p><progress id="upload-progress" class="upload-progress" max="100" value="0"></progress>';
  const actions = dialog.querySelector('.dialog-actions');
  actions.parentNode.insertBefore(s, actions);
  return s;
}
function setDialogStatus(text, cls) {
  const s = ensureDialogStatus();
  s.hidden = false;
  s.querySelector('.confirm-status-text').textContent = text;
  s.className = 'confirm-status ' + (cls || '');
}
// pct=null で不定形 (アニメーション) 表示。アップロード完了後のサーバー処理待ちで使う。
function setDialogProgress(pct) {
  const p = document.getElementById('upload-progress');
  if (!p) return;
  if (pct == null) p.removeAttribute('value');
  else p.value = pct;
}
function hideDialogStatus() {
  const s = document.getElementById('confirm-status');
  if (s) s.hidden = true;
}

function xhrPost(url, fd, { onProgress, onSlow } = {}) {
  return new Promise((resolve) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    let slowTimer = null;
    if (onSlow) slowTimer = setTimeout(onSlow, 20000);
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && onProgress) {
        onProgress(Math.round((e.loaded / e.total) * 100));
      }
    };
    xhr.onload = () => {
      if (slowTimer) clearTimeout(slowTimer);
      resolve({ status: xhr.status, body: xhr.responseText });
    };
    xhr.onerror = () => {
      if (slowTimer) clearTimeout(slowTimer);
      resolve({ status: 0, body: '', isNetworkError: true });
    };
    xhr.ontimeout = () => {
      if (slowTimer) clearTimeout(slowTimer);
      resolve({ status: 0, body: '', isTimeout: true });
    };
    xhr.send(fd);
  });
}

async function actuallySubmit(snap) {
  if (!currentEvent) return;

  _uploading = true;
  confirmOkBtn.disabled = true;
  confirmCancelBtn.disabled = true;
  btn.disabled = true;

  // 全画像 (summary + 各 play) を 1600px / JPEG 0.85 に縮小してから送る。
  setDialogStatus(tt('weepa.score.upload.preparing'), 'info');
  setDialogProgress(0);

  let summaryToSend = snap.imageSummary;
  if (summaryToSend) {
    try { summaryToSend = await shrinkImageForUpload(summaryToSend); }
    catch { summaryToSend = snap.imageSummary; }
  }
  const playsToSend = [null];
  for (let i = 1; i <= MAX_PLAYS; i++) {
    let f = snap.playImages[i];
    if (f) {
      try { f = await shrinkImageForUpload(f); }
      catch { /* keep original */ }
    }
    playsToSend.push(f);
  }

  function buildFormData() {
    const fd = new FormData();
    fd.append('xId', snap.xId);
    fd.append('submitName', snap.submitName);
    fd.append('platform', snap.platform);
    fd.append('plays', JSON.stringify(snap.plays));
    if (summaryToSend) fd.append('imageSummary', summaryToSend);
    for (let i = 1; i <= MAX_PLAYS; i++) {
      if (playsToSend[i]) fd.append(`imagePlay${i}`, playsToSend[i]);
    }
    return fd;
  }

  let slowShown = false;
  let processing = false;
  async function trySend(label) {
    slowShown = false;
    processing = false;
    setDialogStatus(label, 'info');
    setDialogProgress(0);
    return xhrPost(`/api/msc/events/${currentEvent.mscEventId}/submissions`, buildFormData(), {
      onProgress: (pct) => {
        if (pct >= 100) {
          processing = true;
          setDialogProgress(null);
          setDialogStatus(tt('weepa.score.upload.processing'), 'info');
          return;
        }
        setDialogProgress(pct);
        if (!slowShown) setDialogStatus(`${label} ${pct}%`, 'info');
      },
      onSlow: () => {
        if (processing) return;
        slowShown = true;
        setDialogStatus(tt('weepa.score.upload.slow'), 'info');
      },
    });
  }

  let res;
  try {
    res = await trySend(`${tt('weepa.score.upload.progress')}...`);
    // 408 のみ自動リトライ (nginx 段階で弾かれており二重投稿リスクなし)。
    // ネットワークエラーはサーバーが処理した可能性が消せないので、自動リトライしない。
    if (res.status === 408) {
      setDialogStatus(tt('weepa.score.upload.retrying'), 'info');
      res = await trySend(tt('weepa.score.upload.retrying'));
    }
  } finally {
    _uploading = false;
    confirmCancelBtn.disabled = false;
    btn.disabled = false;
  }

  if (res.status >= 200 && res.status < 300) {
    hideDialogStatus();
    confirmOkBtn.disabled = false;
    dialog.close();
    resultEl.textContent = tt('msc.score.submit.ok');
    resultEl.className = 'result ok';
    saveUserProfile({ xId: snap.xId, submitName: snap.submitName });
    form.reset();
    initPlays();
    updatePlatformView();
    prefillUserFields();
    resultEl.scrollIntoView({ behavior: 'smooth', block: 'center' });
    return;
  }

  if (res.status === 400) {
    let data = {};
    try { data = JSON.parse(res.body); } catch { /* ignore */ }
    const msg = data?.message
      || (data?.errors ? Object.values(data.errors).flat().join(' / ') : '')
      || `${tt('msc.score.validation.input-error')} (HTTP 400)`;
    hideDialogStatus();
    confirmOkBtn.disabled = false;
    dialog.close();
    resultEl.textContent = msg;
    resultEl.className = 'result ng';
    return;
  }

  // ネットワーク・サーバー系: ダイアログは開いたまま再試行可能
  confirmOkBtn.disabled = false;
  if (res.status === 413) {
    setDialogStatus(tt('msc.score.submit.413'), 'ng');
  } else if (res.status === 408 || res.isTimeout) {
    setDialogStatus(tt('weepa.score.upload.timeout'), 'ng');
  } else if (res.isNetworkError || res.status === 0) {
    setDialogStatus(tt('common.error.network'), 'ng');
  } else {
    setDialogStatus(`${tt('msc.score.submit.fail')} (HTTP ${res.status})`, 'ng');
  }
}

form.addEventListener('submit', (ev) => {
  ev.preventDefault();
  clearErrors();
  if (!currentEvent) return;
  const snap = collectSnapshot();
  if (!validateSnapshot(snap)) {
    scrollToFirstError();
    return;
  }
  renderConfirm(snap);
  hideDialogStatus();
  dialog.showModal();
});

confirmCancelBtn.addEventListener('click', () => {
  if (_uploading) return; // アップロード中は閉じさせない
  dialog.close();
});

dialog.addEventListener('cancel', (ev) => {
  if (_uploading) ev.preventDefault();
});

confirmOkBtn.addEventListener('click', async () => {
  if (_uploading) return; // 二重押下ガード
  // 投稿完了するまでダイアログを閉じない
  await actuallySubmit(collectSnapshot());
});
