(async () => {
  const el = document.getElementById('api-status');
  try {
    const res = await fetch('/api/health');
    if (!res.ok) {
      throw new Error(`HTTP ${res.status}`);
    }
    const data = await res.json();
    el.textContent = data.status ?? '(unknown)';
    el.classList.add('ok');
  } catch (err) {
    el.textContent = `NG: ${err.message}`;
    el.classList.add('ng');
  }
})();
