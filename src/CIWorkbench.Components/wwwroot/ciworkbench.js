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
  registerShell: (ref) => {
    window.ciw._shell = ref;
    // Report the two layout breakpoints the shell needs for behaviour (CSS handles the rest): phone, and wide enough to dock the inspector.
    const phone = window.matchMedia('(max-width: 760px)');
    const wide = window.matchMedia('(min-width: 1680px)');
    const report = () => ref.invokeMethodAsync('OnViewport', phone.matches, wide.matches).catch(() => { });
    phone.addEventListener('change', report); wide.addEventListener('change', report);
    report();
  },
  // Background scroll lock while a phone overlay (drawer, sheet) is open.
  lockScroll: (on) => document.documentElement.classList.toggle('ciw-scroll-lock', !!on),
  focusFirstIn: (id) => {
    const root = document.getElementById(id); if (!root) return;
    requestAnimationFrame(() => {
      const t = root.querySelector('a[href], button:not([disabled]), input, select, [tabindex]:not([tabindex="-1"])');
      if (t) t.focus({ preventScroll: true });
    });
  },

  // ---- history, back and scroll restoration (the page is a single-page app; async content defeats the browser's own restoration) ----
  _appNavs: 0, _popped: false,
  // true when this tab already holds an in-app history entry to go back to
  back: () => { if (window.ciw._appNavs > 0) { window.ciw._appNavs--; history.back(); return true; } return false; },
  restoreScroll: () => {
    const c = window.ciw;
    const key = c._scrollKey();
    if (!c._popped) { window.scrollTo(0, 0); return; }
    c._popped = false;
    let y = 0; try { y = parseInt(sessionStorage.getItem(key) || '0', 10) || 0; } catch (e) { }
    if (y <= 0) return;
    // Content renders in stages, so retry briefly until the page is tall enough.
    let tries = 0;
    const step = () => {
      window.scrollTo(0, y);
      if (Math.abs(window.scrollY - y) > 2 && tries++ < 20) setTimeout(step, 100);
    };
    step();
  },
  _scrollKey: () => 'ciw:scroll:' + location.pathname + location.search,
  _saveScroll: () => { try { sessionStorage.setItem(window.ciw._scrollKey(), String(Math.round(window.scrollY))); } catch (e) { } },
};

if ('scrollRestoration' in history) history.scrollRestoration = 'manual';
(() => {
  let timer = 0;
  window.addEventListener('scroll', () => { clearTimeout(timer); timer = setTimeout(window.ciw._saveScroll, 120); }, { passive: true });
  // Remember the position of the page we are leaving, before the URL changes.
  for (const fn of ['pushState', 'replaceState']) {
    const orig = history[fn].bind(history);
    history[fn] = (...args) => {
      const before = location.pathname + location.search;
      window.ciw._saveScroll();
      const r = orig(...args);
      if (fn === 'pushState' && (location.pathname + location.search) !== before) window.ciw._appNavs++;
      return r;
    };
  }
  window.addEventListener('popstate', () => { window.ciw._popped = true; });
  window.addEventListener('pagehide', () => window.ciw._saveScroll());
  // A reload also restores the last position of this URL.
  try { if (performance.getEntriesByType('navigation')[0]?.type === 'reload') window.ciw._popped = true; } catch (e) { }
})();

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
