// .field 内の <input type="file"> へドラッグ&ドロップで画像を投入できるようにする。
// 動的に追加された file input にも対応するため、document への delegated handler。
(function () {
  function findTargetField(el) {
    if (!el || el.nodeType !== 1) return null;
    const field = el.closest('.field');
    if (!field) return null;
    if (!field.querySelector('input[type="file"]')) return null;
    return field;
  }

  document.addEventListener('dragenter', (ev) => {
    const field = findTargetField(ev.target);
    if (!field) return;
    ev.preventDefault();
    field.classList.add('drag-over');
  });
  document.addEventListener('dragover', (ev) => {
    const field = findTargetField(ev.target);
    if (!field) return;
    ev.preventDefault();
    if (ev.dataTransfer) ev.dataTransfer.dropEffect = 'copy';
  });
  document.addEventListener('dragleave', (ev) => {
    const field = findTargetField(ev.target);
    if (!field) return;
    // relatedTarget が field の外なら hover 解除
    if (!field.contains(ev.relatedTarget)) {
      field.classList.remove('drag-over');
    }
  });
  document.addEventListener('drop', (ev) => {
    const field = findTargetField(ev.target);
    if (!field) {
      // ページ内の他の場所への drop は通常防止 (ブラウザがファイルを開いてしまうので)
      ev.preventDefault();
      return;
    }
    ev.preventDefault();
    field.classList.remove('drag-over');
    const input = field.querySelector('input[type="file"]');
    if (!input) return;
    const files = ev.dataTransfer && ev.dataTransfer.files;
    if (!files || files.length === 0) return;
    // 単一ファイル対応 (multiple 非対応の input なので最初の 1 つ)
    try {
      const dt = new DataTransfer();
      dt.items.add(files[0]);
      input.files = dt.files;
      input.dispatchEvent(new Event('change', { bubbles: true }));
    } catch {
      /* 古いブラウザでは DataTransfer 操作不可。無視 */
    }
  });
})();
