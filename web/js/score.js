const form = document.getElementById('score-form');
const btn = document.getElementById('submit-btn');
const resultEl = document.getElementById('result');
const eventDisplay = document.getElementById('event-display');

const fieldKeys = ['xId', 'submitName', 'score', 'exScore', 'image'];

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function formatDate(iso) {
  const d = new Date(iso);
  return d.toLocaleString('ja-JP', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit',
  });
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

function renderEventHeader(ev) {
  eventDisplay.innerHTML = `
    <div class="event-title">
      <span class="song">${escapeHtml(ev.song)}</span>
      <span class="effect">${escapeHtml(ev.effect)}</span>
    </div>
    <p class="period">
      ${escapeHtml(formatDate(ev.startDate))} ～ ${escapeHtml(formatDate(ev.endDate))}
    </p>
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
      if (res.status === 404) { showEventError('指定されたイベントは存在しません。'); return; }
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      ev = await res.json();
    } else {
      const res = await fetch('/api/events/current');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const body = await res.json();
      ev = body.event;
    }

    if (!ev) {
      showEventError('現在開催中のイベントはありません。');
      return;
    }

    const now = Date.now();
    const start = new Date(ev.startDate).getTime();
    const end = new Date(ev.endDate).getTime();
    if (now < start) {
      showEventError('このイベントはまだ開始していません。');
      return;
    }
    if (now > end) {
      showEventError('このイベントは既に終了しています。');
      return;
    }

    currentEventId = ev.weepaEventId;
    renderEventHeader(ev);
    form.hidden = false;
  } catch (err) {
    showEventError(`情報の取得に失敗しました: ${err.message}`);
  }
})();

form.addEventListener('submit', async (ev) => {
  ev.preventDefault();
  clearErrors();

  if (currentEventId == null) return;

  const fd = new FormData();
  fd.append('xId', form.xId.value.trim());
  fd.append('submitName', form.submitName.value.trim());
  fd.append('score', form.score.value);
  if (form.exScore.value !== '') {
    fd.append('exScore', form.exScore.value);
  }
  if (form.image.files[0]) {
    fd.append('image', form.image.files[0]);
  }

  btn.disabled = true;
  resultEl.textContent = '送信中...';
  resultEl.className = 'result info';

  try {
    const res = await fetch(`/api/events/${currentEventId}/scores`, {
      method: 'POST',
      body: fd,
    });

    if (res.ok) {
      resultEl.textContent = '✓ 投稿を受け付けました。ありがとうございます。';
      resultEl.className = 'result ok';
      form.reset();
    } else if (res.status === 400) {
      const data = await res.json().catch(() => ({}));
      mapServerErrors(data.errors);
      resultEl.textContent = data.message
        ?? '入力内容にエラーがあります。各項目の赤字メッセージをご確認ください。';
      resultEl.className = 'result ng';
    } else if (res.status === 413) {
      resultEl.textContent = '画像サイズが大きすぎます (上限 20 MB)。';
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
});
