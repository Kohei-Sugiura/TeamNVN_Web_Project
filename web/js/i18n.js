// i18n 翻訳エンジン (vanilla, グローバル window.i18n)
// 使い方:
//   <span data-i18n="key.path">JA fallback</span>
//   <input data-i18n-placeholder="key.path">
//   i18n.t('key.path')
//
// 言語判定: URL クエリ ?lang=ja|ko (なければ ja)
// ページ遷移時にクエリを引き継ぐため i18n.localLink(href) を内部リンク href に通す。

(function () {
  const SUPPORTED = ['ja', 'ko'];
  const DEFAULT_LANG = 'ja';

  function detectLang() {
    try {
      const p = new URLSearchParams(window.location.search);
      const v = p.get('lang');
      if (v && SUPPORTED.includes(v)) return v;
    } catch { /* ignore */ }
    return DEFAULT_LANG;
  }

  const lang = detectLang();
  // <html lang> を更新
  if (document.documentElement) document.documentElement.lang = lang;

  function t(key) {
    const dict = (window.I18N_DICT && window.I18N_DICT[lang]) || {};
    if (Object.prototype.hasOwnProperty.call(dict, key)) return dict[key];
    // 言語にキーが無ければ ja にフォールバック
    const jaDict = (window.I18N_DICT && window.I18N_DICT.ja) || {};
    if (Object.prototype.hasOwnProperty.call(jaDict, key)) return jaDict[key];
    return key; // 最終フォールバック: キー自体
  }

  // 静的 HTML の data-i18n / data-i18n-* 属性を翻訳で置き換える
  function applyAll() {
    // textContent 用
    document.querySelectorAll('[data-i18n]').forEach((el) => {
      const k = el.getAttribute('data-i18n');
      el.textContent = t(k);
    });
    // 属性ベース (placeholder / title / aria-label)
    const attrMap = {
      'data-i18n-placeholder': 'placeholder',
      'data-i18n-title': 'title',
      'data-i18n-aria-label': 'aria-label',
    };
    Object.entries(attrMap).forEach(([dataKey, attr]) => {
      document.querySelectorAll('[' + dataKey + ']').forEach((el) => {
        const k = el.getAttribute(dataKey);
        el.setAttribute(attr, t(k));
      });
    });
  }

  // 内部リンクに ?lang= を付けて維持する。クエリ既存なら触らない。
  function localLink(href) {
    if (!href) return href;
    if (/^(?:[a-z]+:)?\/\//i.test(href) || href.startsWith('mailto:')) return href;
    if (lang === DEFAULT_LANG) return href; // ja の時はクエリ不要 (短い URL)
    try {
      const u = new URL(href, window.location.origin);
      if (u.searchParams.has('lang')) return u.pathname + u.search + u.hash;
      u.searchParams.set('lang', lang);
      // pathname + ?lang=...
      return u.pathname + (u.search ? u.search : '?lang=' + lang) + u.hash;
    } catch {
      // 相対パスっぽい場合の素朴対応
      const sep = href.includes('?') ? '&' : '?';
      return href + sep + 'lang=' + lang;
    }
  }

  // ページ内の通常リンク (a[href]) も自動で lang を引き継ぐ
  function patchAnchors() {
    document.querySelectorAll('a[href]').forEach((a) => {
      // 外部リンク・data-i18n-skip-link を持つものはスキップ
      if (a.hasAttribute('data-i18n-skip-link')) return;
      const href = a.getAttribute('href');
      const patched = localLink(href);
      if (patched !== href) a.setAttribute('href', patched);
    });
  }

  // 言語切替セレクタを #site-header の右上に挿入
  function injectSwitcher() {
    const header = document.querySelector('.site-header');
    if (!header || header.querySelector('.lang-switcher')) return;
    const langs = [
      { code: 'ja', label: '日本語' },
      { code: 'ko', label: '한국어' },
    ];
    const wrap = document.createElement('div');
    wrap.className = 'lang-switcher';
    langs.forEach((l) => {
      const a = document.createElement('a');
      a.className = 'lang-link' + (l.code === lang ? ' active' : '');
      a.textContent = l.label;
      // 現在の URL の lang クエリだけ書き換えてリンクを作る
      const u = new URL(window.location.href);
      if (l.code === DEFAULT_LANG) u.searchParams.delete('lang');
      else u.searchParams.set('lang', l.code);
      a.href = u.pathname + (u.search ? u.search : '') + u.hash;
      // 外部リンクと違って lang クエリは保持しない (この a 自体が切替対象)
      a.setAttribute('data-i18n-skip-link', '');
      wrap.appendChild(a);
    });
    header.appendChild(wrap);
  }

  // ネイティブの「ファイルを選択」ボタンはブラウザのロケール依存で翻訳不能。
  // <input> を <label> で包み、input をインラインスタイルで確実に隠して
  // 翻訳済みボタン(span) + ファイル名表示だけを見せる。
  // label を包んでいるので span クリックでネイティブのファイル選択が開く
  // (input.click() を呼ばないので二重起動しない)。CSS の読込状態に依存せず
  // 表示されるボタンは常に 1 つ。MSC の動的生成にも MutationObserver で追従。
  function hideNativeFileInput(input) {
    const s = input.style;
    s.position = 'absolute';
    s.width = '1px';
    s.height = '1px';
    s.padding = '0';
    s.margin = '-1px';
    s.border = '0';
    s.overflow = 'hidden';
    s.clip = 'rect(0, 0, 0, 0)';
    s.opacity = '0';
  }

  function enhanceFileInput(input) {
    if (!input || input.dataset.i18nFile === '1') return;
    if (input.type !== 'file') return;
    input.dataset.i18nFile = '1';

    const label = document.createElement('label');
    label.className = 'file-pick';
    input.parentNode.insertBefore(label, input);
    label.appendChild(input);
    hideNativeFileInput(input);

    const btn = document.createElement('span');
    btn.className = 'file-pick-btn';
    btn.textContent = t('common.file.choose');

    const name = document.createElement('span');
    name.className = 'file-name-label';
    name.textContent = t('common.file.none');

    input.addEventListener('change', () => {
      name.textContent = (input.files && input.files.length)
        ? input.files[0].name
        : t('common.file.none');
    });

    label.appendChild(btn);
    label.appendChild(name);

    // ドラッグ&ドロップのヒント文 (CSS ::after が attr で参照する) も翻訳
    const field = input.closest('.field');
    if (field) field.setAttribute('data-drop-hint', t('common.file.drop-hint'));
  }

  function enhanceAllFileInputs(root) {
    (root || document).querySelectorAll('input[type="file"]').forEach(enhanceFileInput);
  }

  function observeFileInputs() {
    if (typeof MutationObserver === 'undefined') return;
    const obs = new MutationObserver((mutations) => {
      for (const m of mutations) {
        m.addedNodes.forEach((node) => {
          if (node.nodeType !== 1) return;
          if (node.matches && node.matches('input[type="file"]')) enhanceFileInput(node);
          if (node.querySelectorAll) enhanceAllFileInputs(node);
        });
      }
    });
    obs.observe(document.body, { childList: true, subtree: true });
  }

  function init() {
    applyAll();
    patchAnchors();
    injectSwitcher();
    enhanceAllFileInputs(document);
    observeFileInputs();
  }

  // DOMContentLoaded 前後どちらでも動くように
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

  window.i18n = { lang, t, localLink, applyAll };
})();
