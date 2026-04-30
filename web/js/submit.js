const form = document.getElementById('submit-form');
const btn = document.getElementById('submit-btn');
const resultEl = document.getElementById('result');
const dialog = document.getElementById('confirm-dialog');
const confirmBody = document.getElementById('confirm-body');
const confirmOkBtn = document.getElementById('confirm-ok');
const confirmCancelBtn = document.getElementById('confirm-cancel');

const fieldKeys = ['song', 'effect', 'xId', 'submitName', 'submitReason'];

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

function renderConfirm(payload) {
  confirmBody.innerHTML = `
    <dl>
      <dt>X (Twitter) ID</dt>
      <dd>${payload.xId ? escapeHtml(payload.xId) : '<span class="muted">(未入力)</span>'}</dd>
      <dt>投稿者名</dt>
      <dd>${payload.submitName ? escapeHtml(payload.submitName) : '<span class="muted">(未入力)</span>'}</dd>
      <dt>楽曲名</dt>
      <dd>${payload.song ? escapeHtml(payload.song) : '<span class="muted">(未入力)</span>'}</dd>
      <dt>譜面種</dt>
      <dd>${payload.effect ? escapeHtml(effectLabel(payload.effect)) : '<span class="muted">(未選択)</span>'}</dd>
      <dt>選曲理由</dt>
      <dd>${payload.submitReason ? escapeHtml(payload.submitReason) : '<span class="muted">(なし)</span>'}</dd>
    </dl>
  `;
}

async function actuallySubmit(payload) {
  btn.disabled = true;
  resultEl.textContent = '送信中...';
  resultEl.className = 'result info';

  try {
    const res = await fetch('/api/songs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });

    if (res.ok) {
      await res.json().catch(() => null);
      resultEl.textContent = '✓ 投稿を受け付けました。ありがとうございます。';
      resultEl.className = 'result ok';
      form.reset();
    } else if (res.status === 400) {
      const data = await res.json().catch(() => ({}));
      mapServerErrors(data.errors);
      resultEl.textContent = '入力内容にエラーがあります。各項目の赤字メッセージをご確認ください。';
      resultEl.className = 'result ng';
    } else {
      resultEl.textContent = `送信に失敗しました (HTTP ${res.status})。時間をおいて再度お試しください。`;
      resultEl.className = 'result ng';
    }
  } catch (err) {
    resultEl.textContent = `通信エラー: ${err.message}`;
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
