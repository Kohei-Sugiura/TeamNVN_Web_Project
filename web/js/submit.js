const form = document.getElementById('submit-form');
const btn = document.getElementById('submit-btn');
const resultEl = document.getElementById('result');
const pastListEl = document.getElementById('past-songs-list');

const fieldKeys = ['song', 'effect', 'xId', 'submitName', 'submitReason'];

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

async function loadPastSongs() {
  try {
    const res = await fetch('/api/songs');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const items = await res.json();

    if (!items.length) {
      pastListEl.innerHTML = '<p class="empty">まだ提案された曲はありません。</p>';
      return;
    }

    const rows = items.map((it) => `
      <tr>
        <td class="song">${escapeHtml(it.song)}</td>
        <td class="effect">${escapeHtml(it.effect)}</td>
        <td class="proposer">${escapeHtml(it.submitName)} (@${escapeHtml(it.xId)})</td>
      </tr>
    `).join('');

    pastListEl.innerHTML = `
      <table class="ranking-table past-songs-table">
        <thead>
          <tr><th>楽曲名</th><th>譜面種</th><th>提案者</th></tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;
  } catch (err) {
    pastListEl.innerHTML = `<p class="error">一覧の取得に失敗しました: ${escapeHtml(err.message)}</p>`;
  }
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

form.addEventListener('submit', async (ev) => {
  ev.preventDefault();
  clearErrors();

  const payload = {
    song: form.song.value.trim(),
    effect: form.effect.value,
    xId: form.xId.value.trim(),
    submitName: form.submitName.value.trim(),
    submitReason: form.submitReason.value.trim() || null,
  };

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
      loadPastSongs();
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
});

loadPastSongs();
