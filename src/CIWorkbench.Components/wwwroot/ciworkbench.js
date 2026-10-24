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
  focusById: (id) => { const el = document.getElementById(id); if (el) el.focus(); },

  // Focus restoration: remember what opened an overlay-like pane and give focus back when it closes.
  _opener: null,
  rememberFocus: () => { const a = document.activeElement; window.ciw._opener = a && a !== document.body ? a : null; },
  restoreFocus: () => {
    const el = window.ciw._opener; window.ciw._opener = null;
    if (el && el.isConnected) el.focus();
    else { const m = document.getElementById('main'); if (m) { m.setAttribute('tabindex', '-1'); m.focus({ preventScroll: true }); } }
  },
  // A host component that exposes [JSInvokable] OnEscape() receives Esc presses that are not typed into a field.
  _shell: null,
  registerShell: (ref) => { window.ciw._shell = ref; },
};

const isTyping = (el) => !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable);

document.addEventListener('keydown', (e) => {
  // Ctrl/Cmd+K focuses the global search box when the host provides one with id="ciw-global-search".
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
    const el = document.getElementById('ciw-global-search');
    if (el && !el.disabled) { e.preventDefault(); el.focus(); el.select(); }
    return;
  }

  // Esc: leave a text field first (so typing is never hijacked), otherwise let the host close the inspector.
  if (e.key === 'Escape') {
    if (isTyping(e.target)) { e.target.blur(); return; }
    if (window.ciw._shell) window.ciw._shell.invokeMethodAsync('OnEscape').catch(() => { });
    return;
  }

  // Arrow keys move between group/workflow toggles (a simple roving list); Left/Right collapse/expand.
  const t = e.target;
  if (!(t instanceof HTMLElement) || !t.classList.contains('ciw-toggle') || e.altKey || e.ctrlKey || e.metaKey) return;
  const items = Array.from(document.querySelectorAll('.ciw-workspace .ciw-toggle')).filter((x) => x.offsetParent !== null);
  const i = items.indexOf(t);
  if (i < 0) return;
  let next = null;
  if (e.key === 'ArrowDown') next = items[Math.min(items.length - 1, i + 1)];
  else if (e.key === 'ArrowUp') next = items[Math.max(0, i - 1)];
  else if (e.key === 'Home') next = items[0];
  else if (e.key === 'End') next = items[items.length - 1];
  else if (e.key === 'ArrowRight' && t.getAttribute('aria-expanded') === 'false') { e.preventDefault(); t.click(); return; }
  else if (e.key === 'ArrowLeft' && t.getAttribute('aria-expanded') === 'true') { e.preventDefault(); t.click(); return; }
  if (next) { e.preventDefault(); next.focus(); }
});
