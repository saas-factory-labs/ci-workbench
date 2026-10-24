// Minimal helpers used by CIWorkbench.Components. Hosts include this file with a <script> tag.
window.ciw = {
  copy: async (text) => {
    try {
      if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); return true; }
    } catch (e) { /* fall through to legacy path */ }
    try {
      const ta = document.createElement('textarea');
      ta.value = text; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.opacity = '0';
      document.body.appendChild(ta); ta.select();
      const ok = document.execCommand('copy'); document.body.removeChild(ta); return ok;
    } catch (e) { return false; }
  },
  selectActive: () => { const el = document.activeElement; if (el && el.select) el.select(); },
  // Non-sensitive UI preferences only. Never store tokens here.
  getPref: (key) => { try { return localStorage.getItem('ciw:' + key); } catch (e) { return null; } },
  setPref: (key, value) => { try { localStorage.setItem('ciw:' + key, value); } catch (e) { /* storage blocked */ } },
  focusById: (id) => { const el = document.getElementById(id); if (el) el.focus(); }
};

// Ctrl/Cmd+K focuses the global search box when the host provides one with id="ciw-global-search".
document.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
    const el = document.getElementById('ciw-global-search');
    if (el) { e.preventDefault(); el.focus(); el.select(); }
  }
});
